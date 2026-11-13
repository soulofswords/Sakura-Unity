using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed partial class SakuraAgentPanel
    {
        private readonly SakuraBackend backend;
        private readonly VisualElement owner, navigation, content, taskList;
        private readonly Label status;
        private readonly TextField goal, workspace;
        private readonly DropdownField provider, access;
        private readonly Toggle autoContinue;
        private readonly VisualElement composer, inspector;
        private readonly Dictionary<string, Button> pageButtons = new Dictionary<string, Button>();
        private readonly List<string> attachedSkills = new List<string>();
        private readonly VisualElement skillChoices;
        private readonly Label pageTitle;
        private readonly Button approvalMode, automaticMode;
        private readonly List<string> providerIds = new List<string> { "current" };
        private JObject state = new JObject(), selected;
        private string tab = "Tasks";
        private bool alive = true, busy, refreshing;
        private Texture2D preview;
        private readonly Image browserImage;
        private JObject snapshot;
        private bool localBrowser;
        private Action<string> pendingReply;
        private int taskRevision, viewRevision;
        private bool taskLoading;
        private bool policyBusy;
        private int policyRevision;
        private string approvalPolicy = "ask";

        public SakuraAgentPanel(SakuraBackend backend, VisualElement parent, Action close)
        {
            this.backend = backend;
            owner = new VisualElement { name = "agent-workspace" }; owner.AddToClassList("agent-workspace"); parent.Add(owner);
            var stylesheet = Resources.Load<StyleSheet>("Sakura/Agent");
            if (stylesheet != null) owner.styleSheets.Add(stylesheet);
            var sidebar = new ScrollView { name = "agent-sidebar", horizontalScrollerVisibility = ScrollerVisibility.Hidden }; sidebar.AddToClassList("agent-navigation"); owner.Add(sidebar);
            navigation = sidebar.contentContainer;
            Text("Sakura Agent", navigation, "card-title");
            Button("+ New task", NewTask, navigation).name = "agent-new-task";
            Button("Search sessions", () => SelectTab("Task history"), navigation);
            foreach (string name in new[] { "Tasks", "Local AI", "Connections", "Skills", "Memory & learning", "Automations", "Task history", "Browser" })
            { string page = name; var button = Button(page, () => SelectTab(page), navigation); button.AddToClassList("agent-page"); pageButtons[page] = button; }
            Text("Recent tasks", navigation, "agent-sidebar-heading");
            taskList = new VisualElement { name = "agent-recent-tasks" }; navigation.Add(taskList);
            Button("Refresh connections & tasks", Refresh, navigation);
            var main = new VisualElement(); main.AddToClassList("agent-main"); owner.Add(main);
            var top = Row(main); top.AddToClassList("agent-header"); pageTitle = Text("Sakura Agent", top, "card-title"); Button("Companion mode", close, top);
            status = Text("Connecting to the existing Agent runtime…", main, "description"); status.name = "agent-status";
            var work = Row(main); work.AddToClassList("agent-work-area");
            var scroll = new ScrollView { name = "agent-content-scroll" }; scroll.AddToClassList("agent-content-scroll"); work.Add(scroll);
            content = scroll.contentContainer;
            inspector = new ScrollView { name = "agent-inspector" }; inspector.AddToClassList("agent-inspector"); work.Add(inspector);
            composer = new VisualElement { name = "agent-compose" }; composer.AddToClassList("agent-composer"); main.Add(composer);
            goal = new TextField { multiline = true }; goal.name = "agent-goal"; goal.textEdition.placeholder = "What would you like to work on?"; composer.Add(goal);
            var options = new Foldout { text = "Task settings · workspace & skills", value = false, name = "agent-task-options" }; composer.Add(options);
            var optionsScroll = new ScrollView(); optionsScroll.AddToClassList("agent-task-options-scroll"); options.Add(optionsScroll);
            workspace = new TextField("Workspace folder on this PC") { value = PlayerPrefs.GetString("sakura-agent-workspace", "") }; workspace.name = "agent-workspace-path"; optionsScroll.Add(workspace);
            Text("Reusable skills", optionsScroll, "description");
            skillChoices = new VisualElement { name = "agent-selected-skills" }; optionsScroll.Add(skillChoices);
            Text("Choose up to eight skills, or leave the selection empty for automatic matching.", optionsScroll, "description");
            Button("Use Teacher mode for tasks", () => backend.Api("/api/settings", "{\"conversation_mode\":\"teacher\"}", (_, error) => { if(alive) status.text = error ?? "Teacher mode enabled."; }), optionsScroll);
            var buttons = Row(composer);
            access = new DropdownField(new List<string> { "Ask for approval", "Approve for me", "Full access" }, 0); access.name = "agent-access"; access.tooltip = "Action approval level"; buttons.Add(access);
            access.RegisterValueChangedCallback(_ => ChangePolicy(Access, null));
            autoContinue = new Toggle { value = false }; autoContinue.name = "agent-auto-continue"; autoContinue.style.display = DisplayStyle.None; buttons.Add(autoContinue);
            var execution = Row(buttons); execution.AddToClassList("agent-execution-mode");
            approvalMode = Button("Approval", () => ChangePolicy("ask", false), execution); approvalMode.name = "agent-run-approval";
            automaticMode = Button("Auto", () => ChangePolicy("full", true), execution); automaticMode.name = "agent-run-auto";
            approvalMode.tooltip = "Review each action before it runs";
            automaticMode.tooltip = "Automatically plan and run task steps with full access";
            provider = new DropdownField(new List<string> { "Current Companion model" }, 0); provider.name = "agent-provider"; provider.tooltip = "Planning AI"; buttons.Add(provider);
            Button("Send", SendMessage, buttons).name = "agent-send";
            var start = Button("Start task", StartTask, buttons); start.name = "agent-start"; start.AddToClassList("agent-primary");
            goal.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return && (evt.ctrlKey || evt.commandKey)) { StartTask(); evt.StopPropagation(); } });
            browserImage = new Image { scaleMode = ScaleMode.ScaleToFit }; browserImage.style.height = 360;
            owner.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; if(pendingReply != null) backend.ReplyReceived -= pendingReply; if(preview != null) UnityEngine.Object.Destroy(preview); });
            owner.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                owner.EnableInClassList("agent-tight", evt.newRect.width < 940);
                UpdateInspectorVisibility();
            });
            owner.schedule.Execute(() => { if (alive && !busy && !taskLoading && selected != null && !Archived && (tab == "Tasks" || tab == "Browser")) LoadTask((string)selected["id"], false); }).Every(2500);
            Render();
            Refresh();
            ReadPolicy();
        }

        private bool Archived => selected?.Value<bool?>("archived") == true;
        private void SelectTab(string page) { viewRevision++; tab = page; Render(); }
        private void NewTask()
        {
            taskRevision++; taskLoading = false; selected = null; ClearBrowser();
            goal.value = ""; SelectTab("Tasks"); goal.Focus();
        }
        private void ClearBrowser()
        {
            snapshot = null; localBrowser = false;
            browserImage.image = null;
            if (preview != null) UnityEngine.Object.Destroy(preview);
            preview = null;
        }
        private JObject PlannerOptions() => new JObject { ["provider"] = Provider, ["skill"] = new JArray(attachedSkills) };
        private void UpdateInspectorVisibility()
        {
            inspector.style.display = tab == "Tasks" && selected != null && !owner.ClassListContains("agent-tight") ? DisplayStyle.Flex : DisplayStyle.None;
        }
        private void ReadPolicy()
        {
            int revision = policyRevision;
            backend.AgentApi("/api/agent/approval-policy", null, (json, error) =>
            {
                if (!alive || revision != policyRevision) return;
                if (error != null) { status.text = error; return; }
                try { ApplyPolicy((string)JObject.Parse(json)["mode"], null, true); }
                catch (JsonException) { status.text = "The Agent returned an invalid approval policy."; }
            });
        }
        private void ChangePolicy(string mode, bool? continuation)
        {
            if (policyBusy) return;
            policyBusy = true; policyRevision++;
            access.SetEnabled(false); approvalMode.SetEnabled(false); automaticMode.SetEnabled(false);
            backend.AgentApi("/api/agent/approval-policy", new JObject { ["mode"] = mode }.ToString(), (json, error) =>
            {
                if (!alive) return;
                policyBusy = false;
                access.SetEnabled(true); approvalMode.SetEnabled(true); automaticMode.SetEnabled(true);
                if (error != null) { ApplyPolicy(approvalPolicy, null); status.text = error; return; }
                try { ApplyPolicy((string)JObject.Parse(json)["mode"], continuation); status.text = "Approval settings saved."; }
                catch (JsonException) { ApplyPolicy(approvalPolicy, null); status.text = "The Agent returned an invalid approval policy."; }
            });
        }
        private void ApplyPolicy(string mode, bool? continuation, bool initial = false)
        {
            approvalPolicy = mode == "balanced" || mode == "full" ? mode : "ask";
            access.SetValueWithoutNotify(access.choices[Array.IndexOf(new[] { "ask", "balanced", "full" }, approvalPolicy)]);
            if (continuation.HasValue || initial) autoContinue.SetValueWithoutNotify(continuation ?? approvalPolicy == "full");
            approvalMode.EnableInClassList("agent-mode-selected", !autoContinue.value && approvalPolicy == "ask");
            automaticMode.EnableInClassList("agent-mode-selected", autoContinue.value && approvalPolicy == "full");
        }

        private void Request(string route, JObject body, Action<JToken> done, string method = null)
        {
            backend.AgentApi(route, body?.ToString(Formatting.None), (json, error) =>
            {
                if (!alive) return;
                if (error != null) { busy = false; refreshing = false; taskLoading = false; status.text = error; return; }
                try { done(JToken.Parse(json)); }
                catch (JsonException) { busy = false; refreshing = false; taskLoading = false; status.text = "The Agent returned an invalid response."; }
            }, method);
        }
        private void Refresh()
        {
            if (!alive || refreshing) return; refreshing = true;
            int revision = viewRevision;
            Request("/api/agent/state", null, value =>
            {
                state = (JObject)value; refreshing = false;
                taskList.Clear();
                foreach (var run in (state["runs"] as JArray ?? new JArray()).OrderByDescending(run => run.Value<bool?>("pinned") == true).ThenByDescending(run => run.Value<double?>("created_at") ?? 0).Take(30))
                {
                    string id = (string)run["id"], title = (string)run["title"] ?? (string)run["goal"] ?? "Task";
                    if (!ValidId(id)) continue;
                    var entry = Button((run.Value<bool?>("pinned") == true ? "● " : "") + title + "\n" + (string)run["status"], () => { SelectTab("Tasks"); LoadTask(id); }, taskList);
                    entry.AddToClassList("agent-task-row");
                }
                string chosen = Provider;
                providerIds.Clear(); providerIds.Add("current"); var labels = new List<string> { "Current Companion model" };
                foreach (var row in state["providers"] as JArray ?? new JArray())
                { string id = (string)row["id"]; if(string.IsNullOrEmpty(id) || providerIds.Contains(id)) continue; providerIds.Add(id); labels.Add((string)row["label"] + ((bool?)row["ready"] == false ? " · unavailable" : "")); }
                provider.choices = labels; provider.index = Math.Max(0, providerIds.IndexOf(chosen));
                skillChoices.Clear();
                foreach (var skill in state["skills"] as JArray ?? new JArray())
                {
                    string name = (string)skill["name"];
                    if (string.IsNullOrEmpty(name)) continue;
                    var choice = new Toggle((string)skill["title"] ?? name) { value = attachedSkills.Contains(name) }; skillChoices.Add(choice);
                    choice.RegisterValueChangedCallback(change =>
                    {
                        if (!change.newValue) attachedSkills.Remove(name);
                        else if (attachedSkills.Count < 8) attachedSkills.Add(name);
                        else { choice.SetValueWithoutNotify(false); status.text = "Choose up to eight skills."; }
                    });
                }
                status.text = "Connected · tools run on this PC.";
                if (revision == viewRevision) Render();
            });
        }
        private string Provider => providerIds[Mathf.Clamp(provider.index, 0, providerIds.Count - 1)];
        private string Access => new[] { "ask", "balanced", "full" }[Mathf.Clamp(access.index,0,2)];
        public static JObject BuildTaskRequest(string goal, string workspace, bool autoContinue)
        {
            return new JObject { ["goal"] = goal, ["workspace"] = workspace, ["auto_continue"] = autoContinue };
        }
        public static JObject BuildRuntimeInstallRequest(string engine, string accelerator)
        {
            if (accelerator != "vulkan" && accelerator != "cuda") throw new ArgumentException("Choose Vulkan or CUDA for runtime installation.", nameof(accelerator));
            return new JObject { ["engine"] = engine == "colibri" ? "colibri" : "llama.cpp", ["backend"] = engine == "colibri" ? "vulkan" : accelerator };
        }
        private void StartTask()
        {
            if (busy || policyBusy || backend.Busy || pendingReply != null) return;
            if(string.IsNullOrWhiteSpace(goal.value) || string.IsNullOrWhiteSpace(workspace.value)) { owner.Q<Foldout>("agent-task-options").value = true; status.text = "Describe the task and choose an existing workspace folder."; return; }
            busy = true; status.text = "Creating task…";
            Request("/api/agent/tasks", BuildTaskRequest(goal.value, workspace.value, autoContinue.value), value =>
            {
                selected = (JObject)value; tab = "Tasks"; busy = false;
                PlayerPrefs.SetString("sakura-agent-workspace", workspace.value); PlayerPrefs.Save(); goal.value = "";
                taskRevision++; viewRevision++; Render(); Plan("plan", PlannerOptions()); Refresh();
            });
        }
        private void SendMessage()
        {
            if (string.IsNullOrWhiteSpace(goal.value) || backend.Busy || busy || pendingReply != null || Archived) return;
            string text = goal.value; goal.value = "";
            status.text = "Message sent to the current Companion model. Task planning uses Planning AI.";
            Text(text, content, "agent-user-message");
            string task = (string)selected?["id"];
            int revision = viewRevision;
            Action<string> handler = null;
            handler = reply =>
            {
                backend.ReplyReceived -= handler; pendingReply = null;
                if(alive && revision == viewRevision && (string)selected?["id"] == task) Text(reply, content, "agent-assistant-message");
                if(ValidId(task)) Request("/api/agent/tasks/"+task+"/message",new JObject {["text"]=text,["reply"]=reply},_=>{});
            };
            pendingReply = handler;
            backend.ReplyReceived += pendingReply;
            backend.SendChat(text, task);
        }
        private void LoadTask(string id, bool render = true)
        {
            if (!ValidId(id) || !render && taskLoading) return;
            taskLoading = true;
            int revision = ++taskRevision, view = viewRevision;
            Request("/api/agent/tasks/" + id, null, value =>
            {
                if (revision != taskRevision) return;
                taskLoading = false;
                string previous = selected?.ToString(Formatting.None);
                if ((string)selected?["id"] != id) ClearBrowser();
                selected = (JObject)value;
                if (view == viewRevision && pendingReply == null && (render || selected.ToString(Formatting.None) != previous)) Render();
            });
        }
        private void Plan(string action, JObject body = null)
        {
            if (busy || selected == null || Archived) return; busy = true; status.text = "Working…";
            string id = (string)selected["id"];
            int revision = taskRevision;
            Request("/api/agent/tasks/" + id + "/" + action, body ?? new JObject(), value =>
            { busy = false; if (revision != taskRevision || (string)selected?["id"] != id) return; if((string)value["id"] == id) selected = (JObject)value; else LoadTask(id); status.text = "Ready"; Render(); if (action == "pin" || action == "rename") Refresh(); });
        }
        private void Render()
        {
            viewRevision++;
            content.Clear(); inspector.Clear();
            pageTitle.text = tab == "Tasks" ? "Sakura Agent" : tab;
            foreach (var item in pageButtons) item.Value.EnableInClassList("agent-page-selected", item.Key == tab);
            composer.style.display = tab == "Tasks" && !Archived ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateInspectorVisibility();
            if (tab == "Browser") { BuildBrowser(); return; }
            if (tab == "Connections") { BuildConnections(); return; }
            if (tab == "Skills") { BuildSkills(); return; }
            if (tab == "Memory & learning") { BuildMemory(); return; }
            if (tab == "Automations") { BuildAutomations(); return; }
            if (tab == "Local AI") { BuildLocalAi(); return; }
            if (tab == "Task history") { BuildHistory(); return; }
            if (selected == null) { Text("What would you like to work on?",content,"agent-empty-title"); Text("Talk to Sakura below, or choose a workspace in Task settings to start a task.",content,"description"); return; }
            Text((string)selected["title"] ?? (string)selected["goal"], content, "agent-task-title");
            Text((Archived ? "Archived · " : "") + (string)selected["status"] + " · " + (string)selected["workspace"], content,"description");
            if (!string.IsNullOrEmpty((string)selected["summary"])) Text((string)selected["summary"], content,"agent-assistant-message");
            BuildInspector();
            var actions = Row(content);
            actions.AddToClassList("agent-task-controls");
            if (!Archived)
            {
                Button("Suggest next step", () => Plan("plan",PlannerOptions()),actions);
                Button("Pause",()=>Plan("pause"),actions);
                Button("Resume +25",()=> { var options = PlannerOptions(); options["additional_steps"] = 25; Plan("resume",options); },actions);
                Button("Cancel task",()=>Plan("cancel"),actions);
                Button(selected.Value<bool?>("pinned") == true ? "Unpin" : "Pin", () => Plan("pin", new JObject { ["pinned"] = selected.Value<bool?>("pinned") != true }),actions);
                var rename = new Foldout { text = "Rename task" }; content.Add(rename);
                var title = Input("Task name", (string)selected["title"] ?? (string)selected["goal"], parent: rename);
                Button("Save name", () => Plan("rename", new JObject { ["title"] = title.value }), rename);
            }
            Button("Start a similar task", () => { string text = (string)selected["goal"], folder = (string)selected["workspace"]; NewTask(); goal.value = text; workspace.value = folder; },actions);
            string taskId = (string)selected["id"];
            bool archived = Archived;
            Button(archived ? "Forget archived session" : "Delete task", () => Confirm("Delete this saved task record?", () => Request((archived ? "/api/agent/history/" : "/api/agent/tasks/") + taskId, null, _ => { if ((string)selected?["id"] == taskId) NewTask(); Refresh(); }, "DELETE"), content),actions);
            foreach (var turn in selected["messages"] as JArray ?? new JArray())
                Text((string)turn["text"], content, (string)turn["role"] == "user" ? "agent-user-message" : "agent-assistant-message");
            var steps = selected["steps"] as JArray ?? new JArray();
            var activity = new Foldout { text = "Activity & results · " + steps.Count + " steps", value = false, name = "agent-activity" }; content.Add(activity);
            foreach (var step in steps)
            {
                bool pending = (string)step["status"] == "pending" && !Archived;
                var fold = new Foldout { text = (string)step["tool"] + " · " + (string)step["status"], value = pending };
                (pending ? content : activity).Add(fold);
                if (pending) fold.AddToClassList("agent-approval");
                Text((string)step["reason"] ?? "",fold,"description");
                Text(step["arguments"]?.ToString(Formatting.Indented) ?? "",fold,"agent-code");
                Text(step["result"]?.ToString(Formatting.Indented) ?? (string)step["error"] ?? "",fold,"agent-code");
                if(pending)
                {
                    string id=(string)step["id"]; var choices=Row(fold);
                    foreach(bool approve in new[]{true,false})
                    { bool decision=approve; var choice = Button(approve?"Approve & run":"Reject",()=>Decide(id, taskId, decision),choices); choice.AddToClassList(approve ? "agent-approve" : "agent-reject"); choice.SetEnabled(!busy); }
                }
            }
        }

        private void Decide(string action, string task, bool approve)
        {
            if (busy || !ValidId(action)) return;
            busy = true; status.text = approve ? "Waiting for action approval…" : "Rejecting action…";
            Request("/api/agent/actions/" + action + "/decision",new JObject { ["approve"] = approve }, value =>
            {
                busy = false;
                if ((string)selected?["id"] != task) return;
                if (value["run"] is JObject run) selected = run;
                status.text = "Ready"; Render();
            });
        }

        private void BuildInspector()
        {
            Text("Environment", inspector, "agent-sidebar-heading");
            Text("Workspace", inspector, "description"); Text((string)selected["workspace"], inspector, "agent-inspector-value");
            Text("Status", inspector, "description"); Text((string)selected["status"], inspector, "agent-inspector-value");
            Text("Background processes", inspector, "agent-sidebar-heading");
            foreach (var step in (selected["steps"] as JArray ?? new JArray()).Reverse().Take(8))
                Text((string)step["tool"] + "\n" + (string)step["status"], inspector, "agent-process");
            if (selected["usage"] is JObject usage)
            {
                var detail = new Foldout { text = "Inference usage" }; inspector.Add(detail);
                Text(usage.ToString(Formatting.Indented), detail, "agent-code");
            }
        }
        private void BuildLocalAi()
        {
            Text("Local AI",content,"card-title");
            Text("Run installed models on this PC and use them for Agent planning.",content,"description");
            int revision = viewRevision;
            Request("/api/agent/local-ai",null,value=>
            {
                if(tab!="Local AI" || revision != viewRevision)return;
                var models = (value["models"] as JArray ?? new JArray()).Where(row => row.Value<bool?>("projector") != true).ToArray();
                var projectors = (value["models"] as JArray ?? new JArray()).Where(row => row.Value<bool?>("projector") == true).ToArray();
                Text("Loaded model · " + ((string)value["loaded"] ?? "None") + "\n" + ((string)value["job"]?["message"] ?? ""),content,"description");
                var engine = new DropdownField("Inference engine",new List<string> { "auto", "vulkan", "cuda", "cpu", "colibri" },0); content.Add(engine);
                var runtimeAccelerator = new DropdownField("Runtime installer accelerator",new List<string> { "vulkan", "cuda" },(string)value["backend"] == "cuda" ? 1 : 0); content.Add(runtimeAccelerator);
                runtimeAccelerator.tooltip = "Colibri installs its Vulkan runtime. Model loading separately supports automatic selection and CPU execution.";
                Button(value.Value<bool?>("runtime_installed") == true ? "Reinstall inference engine" : "Install inference engine",()=>Request("/api/agent/local-ai/runtime",BuildRuntimeInstallRequest(engine.value, runtimeAccelerator.value),_=>status.text="Inference engine installation started. Refresh to check progress."),content);
                if (models.Length > 0)
                {
                    var model = new DropdownField("Installed model",models.Select(row => (string)row["name"] ?? (string)row["id"]).ToList(),0); content.Add(model);
                    var projector = new DropdownField("Vision projector",new[] { "Text only" }.Concat(projectors.Select(row => (string)row["name"])).ToList(),0); content.Add(projector);
                    var context = new IntegerField("Context tokens") { value = 8192 }; content.Add(context);
                    var gpu = new IntegerField("GPU layers") { value = 999 }; content.Add(gpu);
                    var controls = Row(content);
                    Button("Load model",()=>Request("/api/agent/local-ai/load",new JObject { ["id"] = models[model.index]["id"], ["projector"] = projector.index == 0 ? "" : (string)projectors[projector.index - 1]["id"], ["backend"] = engine.value == "colibri" ? "vulkan" : engine.value, ["gpu_layers"] = gpu.value, ["context"] = context.value },_=>status.text="Model loading started. Refresh to check its status."),controls);
                    Button("Unload model",()=>Request("/api/agent/local-ai/unload",new JObject(),_=>status.text="Model unloaded."),controls);
                }
                else Text("No models installed. Add local weights below.",content,"description");
                Button("Refresh model status",()=> { viewRevision++; Render(); },content);
                var import = new Foldout { text = "Add local model", value = false }; content.Add(import);
                var path = Input("GGUF model path",parent:import);
                var name = Input("Model name",parent:import);
                var isProjector = new Toggle("Vision projector"); import.Add(isProjector);
                Button("Add model",()=>Request("/api/agent/local-ai/import",new JObject { ["path"] = path.value, ["name"] = name.value, ["projector"] = isProjector.value },_=> { viewRevision++; Render(); }),import);
            });
        }
        private void BuildBrowser()
        {
            if(selected==null) { Text("Select a task first. Browser interactions belong to that task and use its approvals.",content,"description"); return; }
            if (Archived) { Text("Open a recent task to use the browser. Archived sessions are read-only.",content,"description"); return; }
            snapshot = null;
            var browserSteps = (selected["steps"] as JArray ?? new JArray()).Reverse().ToArray();
            var opened = browserSteps.FirstOrDefault(step => (string)step["status"] == "completed" && ((string)step["tool"] == "local_app.open" || (string)step["tool"] == "browser.open"));
            if (opened != null) localBrowser = (string)opened["tool"] == "local_app.open";
            var url=new TextField("Website address"); content.Add(url);
            var buttons=Row(content);
            Button("Open page",()=>
            { localBrowser=Uri.TryCreate(url.value,UriKind.Absolute,out var target)&&target.IsLoopback; Propose(localBrowser?"local_app.open":"browser.open",new JObject {["url"]=url.value}); },buttons);
            Button("Read page",()=>Propose(localBrowser?"local_app.inspect":"browser.read",new JObject()),buttons);
            Button("Refresh preview",ReadPreview,buttons);
            Button("Close page",()=>Propose(localBrowser?"local_app.close":"browser.close",new JObject()),buttons);
            content.Add(browserImage); if(preview!=null)browserImage.image=preview;
            var controls=new TextField("Numbered control"); content.Add(controls);
            var text=new TextField("Text to fill"); content.Add(text);
            void Interact(bool fill)
            {
                if(snapshot==null||!int.TryParse(controls.value,out int index)) {status.text="Read the page and select one of its numbered controls first.";return;}
                var args=new JObject {["snapshot"]=snapshot["snapshot"],["element"]=index};if(fill)args["text"]=text.value;
                Propose((localBrowser?"local_app.":"browser.")+(fill?"fill":"click"),args);
            }
            Button("Click control",()=>Interact(false),content); Button("Fill control",()=>Interact(true),content);
            foreach(var step in browserSteps)
            {
                if(((string)step["tool"] ?? "").StartsWith(localBrowser ? "local_app." : "browser.",StringComparison.Ordinal) && step["result"] is JObject result && result["snapshot"]!=null)
                {snapshot=result; Text(result.ToString(Formatting.Indented),content,"description");break;}
            }
        }
        private void Propose(string tool,JObject arguments)
        {
            if (busy || selected == null || Archived) return;
            string id = (string)selected["id"]; int revision = viewRevision;
            busy = true;
            Request("/api/agent/tasks/"+id+"/propose",new JObject {["tool"]=tool,["arguments"]=arguments,["reason"]="Requested in the native Agent panel."},value=>
            {
                busy = false;
                if ((string)selected?["id"] != id || revision != viewRevision) return;
                selected=(JObject)value; SelectTab("Tasks");
            });
        }
        private void ReadPreview()
        {
            string id = (string)selected["id"]; bool local = localBrowser;
            backend.AgentPreview(id,local,(texture,error)=>
            {if(!alive || (string)selected?["id"] != id || local != localBrowser){if(texture!=null)UnityEngine.Object.Destroy(texture);return;} if(error!=null){status.text=error;return;} if(preview!=null)UnityEngine.Object.Destroy(preview);preview=texture;browserImage.image=texture;});
        }
        public static bool ValidId(string id)=>id!=null&&System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,80}\z");
        private static VisualElement Row(VisualElement parent){var row=new VisualElement();row.AddToClassList("row");parent.Add(row);return row;}
        private static Label Text(string text,VisualElement parent,string style){var label=new Label(text??"");label.AddToClassList(style);parent.Add(label);return label;}
        private static Button Button(string text,Action clicked,VisualElement parent){var button=new Button(clicked){text=text};parent.Add(button);return button;}
    }
}
