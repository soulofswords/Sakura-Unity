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
            var store = new Foldout { text = "Curated extension store", value = false }; content.Add(store);
            var storeWorkspace = Input("Workspace folder for folder-scoped extensions", parent: store);
            var storeList = new VisualElement(); store.Add(storeList);
            Button("Load extension store", () => Request("/api/agent/extension-store", null, data =>
            {
                storeList.Clear();
                foreach (var card in data["extensions"] as JArray ?? new JArray())
                {
                    string id = (string)card["id"], cardName = (string)card["name"] ?? id;
                    if (string.IsNullOrWhiteSpace(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-z0-9][a-z0-9-]{0,59}$")) continue;
                    var entry = new Foldout { text = cardName, value = false }; storeList.Add(entry);
                    Text((string)card["description"] ?? "No description.", entry, "description");
                    Text(((string)card["publisher"] ?? "Unknown publisher") + " · " + ((string)card["source"] ?? "no source"), entry, "description");
                    Button("Add configuration", () => Request("/api/agent/extension-store/add", new JObject { ["id"] = id, ["workspace"] = storeWorkspace.value ?? "" }, result =>
                    {
                        Text(result.ToString(Formatting.Indented), entry, "description");
                        status.text = "Extension configuration added. Discovery and tool calls still follow Agent approval.";
                        Refresh();
                    }), entry);
                }
            }), store).name = "agent-extension-store";
            var telegram = new Foldout { text = "Telegram gateway", value = false }; content.Add(telegram);
            var telegramEnabled = new Toggle("Enable Telegram replies"); telegram.Add(telegramEnabled);
            var telegramChats = Input("Allowed chat IDs (comma-separated)", parent: telegram);
            var telegramToken = Input("Bot token (blank keeps the saved token)", parent: telegram); telegramToken.isPasswordField = true;
            var telegramVoice = new Toggle("Answer voice memos"); telegram.Add(telegramVoice);
            var telegramStatus = new Label(); telegramStatus.style.whiteSpace = WhiteSpace.Normal; telegram.Add(telegramStatus);
            void RefreshTelegram()
            {
                Request("/api/agent/telegram", null, data =>
                {
                    telegramEnabled.SetValueWithoutNotify(data.Value<bool?>("enabled") == true);
                    telegramVoice.SetValueWithoutNotify(data.Value<bool?>("voice_enabled") == true);
                    telegramChats.SetValueWithoutNotify(string.Join(",", (data["chat_ids"] as JArray ?? new JArray()).Values<string>()));
                    telegramStatus.text = "Telegram " + (data.Value<bool?>("enabled") == true ? "enabled" : "off") + " · token " + (data.Value<bool?>("token_set") == true ? "saved" : "missing") +
                        (data.Value<int?>("received") is int received ? " · " + received + " messages received" : "") + ((string)data["last_error"] is string last && last.Length > 0 ? " · " + last : "");
                });
            }
            Button("Refresh Telegram status", RefreshTelegram, telegram);
            Button("Refresh Discord / Slack / WhatsApp status", () => Request("/api/agent/gateways", null, data =>
            {
                var rows = (data as JArray ?? new JArray()).OfType<JObject>().ToList();
                telegramStatus.text = rows.Count == 0 ? "No messaging gateways reported." : string.Join(" · ", rows.Select(row =>
                    ((string)row["provider"] ?? "gateway") + ": " + (row.Value<bool?>("enabled") == true ? (row.Value<bool?>("running") == true ? "running" : "enabled, stopped") : "off") +
                    ((string)row["last_error"] is string error && error.Length > 0 ? " — " + error : "")));
            }), telegram);
            Button("Save Telegram settings", () =>
            {
                var ids = (telegramChats.value ?? "").Split(',').Select(id => id.Trim()).Where(id => id.Length > 0).ToArray();
                Request("/api/agent/telegram", new JObject
                {
                    ["enabled"] = telegramEnabled.value,
                    ["chat_ids"] = new JArray(ids),
                    ["token"] = (telegramToken.value ?? "").Trim(),
                    ["voice_enabled"] = telegramVoice.value
                }, result =>
                {
                    telegramToken.SetValueWithoutNotify("");
                    telegramStatus.text = "Telegram settings saved.";
                    RefreshTelegram();
                });
            }, telegram);
            RefreshTelegram();
        }

        private void BuildSkills()
        {
            var skillStatus = new Label(); skillStatus.style.whiteSpace = WhiteSpace.Normal; content.Add(skillStatus);
            foreach (var item in state["skills"] as JArray ?? new JArray())
            {
                string name = (string)item["name"];
                if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "",@"^[a-z0-9][a-z0-9-]{0,59}$")) continue;
                var card = new Foldout { text = (string)item["title"] ?? name, value = false }; content.Add(card);
                Text((string)item["requirements"],card,"description");
                var instructions = Input("Procedure",(string)item["instructions"] ?? "",true,card);
                Button("Save procedure",() => Request("/api/agent/skills/" + name,new JObject { ["instructions"] = instructions.value },_ => Refresh(),"PUT"),card);
                Button("Export .skill bundle",() => Request("/api/agent/skills/" + name + "/export",new JObject(),data => skillStatus.text = "Exported " + ((string)data["file"] ?? name + ".skill") + " to " + ((string)data["folder"] ?? "the skills export folder") + "."),card);
                Button("Delete skill",() => Confirm("Delete this saved skill?",() => Request("/api/agent/skills/" + name,null,_ => Refresh(),"DELETE"),card),card);
            }
            var create = new Foldout { text = "Create or import a skill", value = false }; content.Add(create);
            var newName = Input("Skill name",parent:create);
            var procedure = Input("Procedure",multiline:true,parent:create);
            Button("Create skill",() => Request("/api/agent/skills",new JObject { ["name"] = newName.value,["instructions"] = procedure.value },_ => Refresh()),create);
            var markdown = Input("SKILL.md contents",multiline:true,parent:create);
            Button("Import Markdown",() => Request("/api/agent/skills/import",new JObject { ["markdown"] = markdown.value,["source"] = "Unity skill editor" },_ => Refresh()),create);
            Button("Export all skills",() => Request("/api/agent/skills/export-all",new JObject(),data => skillStatus.text = "Exported " + (data.Value<int?>("count") ?? 0) + " skill bundles to " + ((string)data["folder"] ?? "the skills export folder") + "."),create);
            Button("Import .skill bundles",() => Request("/api/agent/skills/import-bundles",new JObject(),data => { skillStatus.text = "Imported " + (data.Value<int?>("imported") ?? 0) + " skill bundles."; Refresh(); }),create);
            if (selected != null && !Archived && (string)selected["status"] == "completed")
                Button("Learn selected completed task",() => Request("/api/agent/skills/learn",new JObject { ["run_id"] = selected["id"],["name"] = newName.value },_ => Refresh()),create);
            var budgetFoldout = new Foldout { text = "Token Budget", value = false }; content.Add(budgetFoldout);
            var budgetLabel = new Label(); budgetLabel.style.whiteSpace = WhiteSpace.Normal; budgetFoldout.Add(budgetLabel);
            void RefreshBudget()
            {
                backend.Api("/api/companion/status", null, (json, error) =>
                {
                    if (error != null) { budgetLabel.text = error; return; }
                    try
                    {
                        var data = JObject.Parse(json)["budget"] as JObject;
                        if (data == null) { budgetLabel.text = "Companion status did not include a token budget."; return; }
                        budgetLabel.text = "Budget: " + (data.Value<int?>("used_today") ?? 0) + "/"
                            + (data.Value<int?>("daily_limit") ?? 100000) + " used · "
                            + (data.Value<int?>("remaining") ?? 0) + " remaining today"
                            + (data.Value<bool?>("exhausted") == true ? " · exhausted" : "");
                    }
                    catch (JsonException) { budgetLabel.text = "The Companion returned an invalid budget response."; }
                });
            }
            Button("Refresh Budget", RefreshBudget, budgetFoldout);
            Button("Reset Budget", () => backend.Api("/api/companion/budget", new JObject { ["reset"] = true }.ToString(), (json, error) =>
            {
                if (error != null) { budgetLabel.text = error; return; }
                RefreshBudget();
            }), budgetFoldout);
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
                var fold = new Foldout { text = (string)row["category"] + " · " + (string)row["successes"] + " runs, confidence " + ((string)row["confidence"] ?? "0.3") }; content.Add(fold);
                Text(row["tools"]?.ToString(Formatting.Indented),fold,"description");
                Button("Forget procedure",() => Confirm("Forget this learned procedure?",() => Request("/api/agent/memory/" + id,null,_ => Refresh(),"DELETE"),fold),fold);
            }
            Button("Show maintenance status",() => Request("/api/agent/memory-workbench",null,result => Text(result.ToString(Formatting.Indented),content,"description")),content);
            Button("Preview memory consolidation",() => Request("/api/agent/memory-workbench/consolidate",new JObject { ["approved"] = false },result => Text(result.ToString(Formatting.Indented),content,"description")),content);
            var query = Input("Search entity hints");
            Button("Search linked memories",() => Request("/api/agent/memory-workbench/graph?query=" + Uri.EscapeDataString(query.value ?? ""),null,result => Text(result.ToString(Formatting.Indented),content,"description")),content);
            var workbench = new Foldout { text = "Memory maintenance workbench", value = false }; content.Add(workbench);
            var workbenchEnabled = new Toggle("Run deterministic hourly maintenance") { value = false }; workbench.Add(workbenchEnabled);
            var workbenchStatus = new Label(); workbenchStatus.style.whiteSpace = WhiteSpace.Normal; workbench.Add(workbenchStatus);
            void LoadWorkbench()
            {
                Request("/api/agent/memory-workbench", null, data =>
                {
                    workbenchEnabled.SetValueWithoutNotify(data.Value<bool?>("enabled") == true);
                    workbenchStatus.text = (data.Value<bool?>("enabled") == true ? "Hourly maintenance enabled" : "Hourly maintenance off") + " · " +
                        ((data["journals"] as JArray ?? new JArray()).Count) + " daily reflection(s)" +
                        ((string)data["last_error"] is string last && last.Length > 0 ? " · " + last : "");
                });
            }
            Button("Refresh workbench", LoadWorkbench, workbench);
            Button("Save maintenance switch", () => Request("/api/agent/memory-workbench/configure", new JObject { ["enabled"] = workbenchEnabled.value }, result => { workbenchStatus.text = "Memory maintenance setting saved."; LoadWorkbench(); }), workbench);
            Button("Rebuild entity hints", () => Request("/api/agent/memory-workbench/graph", new JObject(), result => { workbenchStatus.text = "Entity hints rebuilt from current memory records."; }), workbench);
            var journalDay = Input("Journal day (YYYY-MM-DD, blank for today)", parent: workbench);
            Button("Write daily reflection", () => Request("/api/agent/memory-workbench/journal", new JObject { ["day"] = (journalDay.value ?? "").Trim() }, result => { workbenchStatus.text = (string)result["text"] ?? "Daily reflection written."; LoadWorkbench(); }), workbench);
            LoadWorkbench();
            var routing = new Foldout { text = "Measured model routing", value = false }; content.Add(routing);
            var routingEnabled = new Toggle("Choose a measured model for each coding task") { value = false }; routing.Add(routingEnabled);
            var routingCloud = new Toggle("Allow measured cloud models") { value = false }; routing.Add(routingCloud);
            var routingStatus = new Label(); routingStatus.style.whiteSpace = WhiteSpace.Normal; routing.Add(routingStatus);
            void RefreshRouting()
            {
                Request("/api/agent/model-routing", null, data =>
                {
                    routingEnabled.SetValueWithoutNotify(data.Value<bool?>("enabled") == true);
                    routingCloud.SetValueWithoutNotify(data.Value<bool?>("allow_cloud") == true);
                    int profiles = (data["profiles"] as JArray ?? new JArray()).Count;
                    routingStatus.text = "Routing " + (data.Value<bool?>("enabled") == true ? "enabled" : "off") + " · " + profiles + " measured profile(s). Benchmarks are small synthetic checks, not a general intelligence score.";
                });
            }
            Button("Refresh routing status", RefreshRouting, routing);
            Button("Save routing settings", () => Request("/api/agent/model-routing", new JObject { ["enabled"] = routingEnabled.value, ["allow_cloud"] = routingCloud.value }, result =>
            {
                routingStatus.text = "Routing settings saved."; RefreshRouting();
            }), routing);
            RefreshRouting();
            var expenses = new Foldout { text = "Expense ledger", value = false }; content.Add(expenses);
            var expenseMonth = Input("Month (YYYY-MM, blank for all)", parent: expenses);
            var expenseStatus = new Label(); expenseStatus.style.whiteSpace = WhiteSpace.Normal; expenses.Add(expenseStatus);
            void RefreshExpenses()
            {
                Request("/api/agent/expenses?month=" + Uri.EscapeDataString((expenseMonth.value ?? "").Trim()), null, data =>
                {
                    var rows = (data["items"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    var totals = data["totals_by_currency"] as JObject ?? new JObject();
                    expenseStatus.text = rows.Count == 0 ? "No expenses recorded." : rows.Count + " expense(s). Totals: " + string.Join(", ", totals.Properties().Select(property => property.Name + " " + property.Value)) + ". Returned rows only; no exchange-rate conversion or bank connection.";
                });
            }
            Button("Refresh expenses", RefreshExpenses, expenses);
            var expenseDate = Input("Date (YYYY-MM-DD)", parent: expenses);
            var expenseAmount = Input("Amount", parent: expenses);
            var expenseCurrency = Input("Currency (three letters, e.g. INR)", parent: expenses);
            var expenseCategory = Input("Category", parent: expenses);
            var expenseNote = Input("Note", parent: expenses);
            Button("Add expense", () => Request("/api/agent/expenses", new JObject
            {
                ["day"] = (expenseDate.value ?? "").Trim(),
                ["amount"] = (expenseAmount.value ?? "").Trim(),
                ["currency"] = (expenseCurrency.value ?? "").Trim().ToUpperInvariant(),
                ["category"] = (expenseCategory.value ?? "").Trim(),
                ["note"] = (expenseNote.value ?? "").Trim()
            }, result =>
            {
                expenseStatus.text = "Expense added.";
                expenseAmount.SetValueWithoutNotify(""); expenseNote.SetValueWithoutNotify("");
                RefreshExpenses();
            }), expenses);
            RefreshExpenses();
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
