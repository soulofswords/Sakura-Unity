using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed partial class SakuraAgentPanel
    {
        private void BuildHistory()
        {
            Text("Session history", content, "card-title");
            Text("Open recent tasks to continue working. Archived sessions are read-only.", content, "description");
            var query = Input("Search past sessions"); query.name = "agent-history-query";
            var results = new VisualElement { name = "agent-history-results" }; content.Add(results);
            int revision = viewRevision;
            void ShowResults(JToken found)
            {
                if (revision != viewRevision) return;
                results.Clear();
                var rows = found["results"] as JArray ?? new JArray();
                if (rows.Count == 0) Text("No matching sessions.", results, "description");
                foreach (var row in rows)
                {
                    string id = (string)row["id"];
                    if (!ValidId(id)) continue;
                    var card = new VisualElement(); card.AddToClassList("agent-history-result"); results.Add(card);
                    Text((string)row["goal"], card, "card-title");
                    Text((string)row["status"] + " · " + (string)row["workspace"], card, "description");
                    Text((string)row["snippet"], card, "description");
                    bool recent = (state["runs"] as JArray ?? new JArray()).Any(run => (string)run["id"] == id);
                    Button(recent ? "Open task" : "Read archived session", () =>
                    {
                        if (recent) { SelectTab("Tasks"); LoadTask(id); return; }
                        Request("/api/agent/history/" + id, null, record =>
                        {
                            if (revision != viewRevision) return;
                            taskRevision++; selected = (JObject)record; snapshot = null; SelectTab("Tasks");
                        });
                    }, card);
                }
            }
            Button("Search sessions", () =>
            {
                if (string.IsNullOrWhiteSpace(query.value)) { status.text = "Enter keywords to search past sessions."; return; }
                Request("/api/agent/history/search?q=" + Uri.EscapeDataString(query.value.Trim()), null, ShowResults);
            }, content).name = "agent-history-search";
            Text("Recent tasks", content, "card-title");
            foreach (var run in (state["runs"] as JArray ?? new JArray()).OrderByDescending(run => run.Value<double?>("created_at") ?? 0))
            {
                string id = (string)run["id"];
                if (ValidId(id)) Button(((string)run["title"] ?? (string)run["goal"]) + " · " + (string)run["status"], () => { SelectTab("Tasks"); LoadTask(id); }, content);
            }
            Button("Clear task history", () => Confirm("Clear recent tasks and archived sessions on this PC?", () => Request("/api/agent/tasks", null, _ => { taskRevision++; selected = null; ClearBrowser(); ReadPolicy(); Refresh(); }, "DELETE"), content), content).AddToClassList("agent-reject");
        }

        private TextField Input(string label, string value = "", bool multiline = false, VisualElement parent = null)
        {
            var field = new TextField(label) { value = value, multiline = multiline };
            (parent ?? content).Add(field);
            return field;
        }

        private void Confirm(string prompt, Action action, VisualElement parent)
        {
            var review = Row(parent);
            Text(prompt,review,"description");
            Button("Confirm",() => { review.RemoveFromHierarchy(); action(); },review);
            Button("Cancel",() => review.RemoveFromHierarchy(),review);
        }

        private void BuildConnections()
        {
            foreach (var item in state["connections"] as JArray ?? new JArray())
            {
                string id = (string)item["id"];
                if (!ValidId(id)) continue;
                var card = new Foldout { text = (string)item["name"], value = true }; content.Add(card);
                Text((string)item["url"] ?? (string)item["command"] ?? (string)item["service"],card,"description");
                Button("Discover tools",() => Request("/api/agent/connections/" + id + "/discover",new JObject(),action =>
                {
                    var review = new Foldout { text = "Review connection", value = true }; card.Add(review);
                    Text(action["preview"]?.ToString(Formatting.Indented),review,"description");
                    string approval = (string)action["id"];
                    if (!ValidId(approval)) { status.text = "The connection did not return a valid approval."; return; }
                    foreach (bool accept in new[] { true,false })
                    {
                        bool choice = accept;
                        Button(choice ? "Approve connection" : "Reject",() => Request("/api/agent/actions/" + approval + "/decision",new JObject { ["approve"] = choice },_ => { review.RemoveFromHierarchy(); Refresh(); }),review);
                    }
                }),card);
                Button("Remove connection",() => Confirm("Remove this connection from Sakura?",() => Request("/api/agent/connections/" + id,null,_ => Refresh(),"DELETE"),card),card);
                string service = (string)item["service"];
                if (service == "google-calendar" || service == "google-drive" || service == "linkedin")
                {
                    Button("Connect account",() => Request("/api/agent/services/" + service + "/connect",new JObject(),_ => status.text = "Complete sign-in in your browser, then refresh connections."),card);
                    Button("Disconnect account",() => Confirm("Disconnect this account? Google services share their account connection.",() => Request("/api/agent/services/" + service + "/disconnect",new JObject(),_ => Refresh()),card),card);
                }
            }
            var add = new Foldout { text = "Add MCP server", value = false }; content.Add(add);
            var name = Input("Name",parent:add);
            var transport = new DropdownField("Transport",new System.Collections.Generic.List<string> { "http","stdio" },0); add.Add(transport);
            var address = Input("HTTPS URL or local HTTP MCP URL",parent:add);
            var command = Input("Executable for stdio",parent:add);
            var arguments = Input("Arguments as a JSON string array","[]",true,add);
            var token = Input("Bearer token",parent:add); token.isPasswordField = true;
            var environment = Input("Secret environment variables (JSON object)", "{}", true, add); environment.isPasswordField = true;
            add.RegisterCallback<DetachFromPanelEvent>(_ => { token.SetValueWithoutNotify(""); environment.SetValueWithoutNotify(""); });
            Button("Save connection",() =>
            {
                JArray args;
                JObject env;
                try { args = JArray.Parse(arguments.value); env = JObject.Parse(string.IsNullOrWhiteSpace(environment.value) ? "{}" : environment.value); }
                catch (JsonException) { status.text = "Use a JSON string array for arguments and a JSON object for environment variables."; return; }
                if (args.Any(argument => argument.Type != JTokenType.String) || env.Properties().Any(property => property.Value.Type != JTokenType.String)) { status.text = "Arguments and environment values must be strings."; return; }
                Request("/api/agent/connections",new JObject { ["name"] = name.value,["transport"] = transport.value,["url"] = address.value,["command"] = command.value,["args"] = args,["token"] = token.value,["env"] = env },_ => { token.SetValueWithoutNotify(""); environment.SetValueWithoutNotify(""); Refresh(); });
            },add);
            foreach (string builtin in new[] { "google-calendar","google-drive","linkedin" })
            {
                string service = builtin;
                Button("Add " + service,() => Request("/api/agent/services/" + service,new JObject(),_ => Refresh()),add);
            }
        }

        private void BuildSkills()
        {
            foreach (var item in state["skills"] as JArray ?? new JArray())
            {
                string name = (string)item["name"];
                if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "",@"^[a-z0-9][a-z0-9-]{0,59}$")) continue;
                var card = new Foldout { text = (string)item["title"] ?? name, value = false }; content.Add(card);
                Text((string)item["requirements"],card,"description");
                var instructions = Input("Procedure",(string)item["instructions"] ?? "",true,card);
                Button("Save procedure",() => Request("/api/agent/skills/" + name,new JObject { ["instructions"] = instructions.value },_ => Refresh(),"PUT"),card);
                Button("Delete skill",() => Confirm("Delete this saved skill?",() => Request("/api/agent/skills/" + name,null,_ => Refresh(),"DELETE"),card),card);
            }
            var create = new Foldout { text = "Create or import a skill", value = false }; content.Add(create);
            var newName = Input("Skill name",parent:create);
            var procedure = Input("Procedure",multiline:true,parent:create);
            Button("Create skill",() => Request("/api/agent/skills",new JObject { ["name"] = newName.value,["instructions"] = procedure.value },_ => Refresh()),create);
            var markdown = Input("SKILL.md contents",multiline:true,parent:create);
            Button("Import Markdown",() => Request("/api/agent/skills/import",new JObject { ["markdown"] = markdown.value,["source"] = "Unity skill editor" },_ => Refresh()),create);
            if (selected != null && !Archived && (string)selected["status"] == "completed")
                Button("Learn selected completed task",() => Request("/api/agent/skills/learn",new JObject { ["run_id"] = selected["id"],["name"] = newName.value },_ => Refresh()),create);
        }

        private void BuildMemory()
        {
            var learning = state["learning"] as JObject ?? new JObject();
            var enabled = new Toggle("Learn from completed tasks") { value = learning.Value<bool?>("enabled") ?? true }; content.Add(enabled);
            var notes = Input("Procedure notes",(string)learning["notes"] ?? "",true);
            var profile = Input("User preferences",(string)learning["profile"] ?? "",true);
            Button("Save memory preferences",() => Request("/api/agent/memory",new JObject { ["enabled"] = enabled.value,["notes"] = notes.value,["profile"] = profile.value },_ => Refresh()),content);
            foreach (var row in learning["procedures"] as JArray ?? new JArray())
            {
                string id = (string)row["id"];
                if (!ValidId(id)) continue;
                var fold = new Foldout { text = (string)row["category"] + " · " + (string)row["successes"] + " successful runs" }; content.Add(fold);
                Text(row["tools"]?.ToString(Formatting.Indented),fold,"description");
                Button("Forget procedure",() => Confirm("Forget this learned procedure?",() => Request("/api/agent/memory/" + id,null,_ => Refresh(),"DELETE"),fold),fold);
            }
            Button("Show maintenance status",() => Request("/api/agent/memory-workbench",null,result => Text(result.ToString(Formatting.Indented),content,"description")),content);
            Button("Preview memory consolidation",() => Request("/api/agent/memory-workbench/consolidate",new JObject { ["approved"] = false },result => Text(result.ToString(Formatting.Indented),content,"description")),content);
            var query = Input("Search entity hints");
            Button("Search linked memories",() => Request("/api/agent/memory-workbench/graph?query=" + Uri.EscapeDataString(query.value ?? ""),null,result => Text(result.ToString(Formatting.Indented),content,"description")),content);
        }

        private void BuildAutomations()
        {
            foreach (var item in state["automations"] as JArray ?? new JArray())
            {
                string id = (string)item["id"];
                if (!ValidId(id)) continue;
                var fold = new Foldout { text = (string)item["name"], value = true }; content.Add(fold);
                Text(item.ToString(Formatting.Indented),fold,"description");
                bool paused = item.Value<bool?>("paused") ?? false;
                Button(paused ? "Resume workflow" : "Pause workflow",() => Request("/api/agent/automations/" + id,new JObject { ["paused"] = !paused },_ => Refresh()),fold);
                Button("Run now",() => Request("/api/agent/automations/" + id + "/run",new JObject(),result => Text(result.ToString(Formatting.Indented),fold,"description")),fold);
                Button("Delete workflow",() => Confirm("Delete this scheduled workflow?",() => Request("/api/agent/automations/" + id,null,_ => Refresh(),"DELETE"),fold),fold);
            }
            if (selected == null || Archived) { Text("Select a verified recent task first to create its recurring workflow.",content,"description"); return; }
            var name = Input("Workflow name");
            var minutes = new IntegerField("Repeat every minutes") { value = 60 }; content.Add(minutes);
            Text("This repeats the selected task’s reviewed steps while Sakura is running. The existing backend validates eligibility and asks for execution consent.",content,"description");
            Button("Schedule selected task",() => Confirm("Approve this exact recurring task?",() => Request("/api/agent/automations",new JObject { ["run_id"] = selected["id"],["name"] = name.value,["minutes"] = minutes.value,["mode"] = "steps",["approved"] = true },_ => Refresh()),content),content);
        }
    }
}
