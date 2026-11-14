using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using WebViewToolkit.UIToolkit;
#endif

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
            foreach (string name in new[] { "Tasks", "Local AI", "Connections", "Integrations", "Skills", "Memory & learning", "Automations", "Task history", "Browser" })
            {
                string page = name;
                var button = Button(page, () => SelectTab(page), navigation);
                button.AddToClassList("agent-page"); AddNavigationIcon(button, page); pageButtons[page] = button;
            }
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
        public void OpenTask(string id) { if (!alive || !ValidId(id)) return; SelectTab("Tasks"); LoadTask(id); }
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
            foreach (var item in pageButtons)
            {
                item.Value.EnableInClassList("agent-page-selected", item.Key == tab);
                item.Value.Q(className: "agent-nav-icon")?.MarkDirtyRepaint();
            }
            composer.style.display = tab == "Tasks" && !Archived ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateInspectorVisibility();
            if (tab == "Browser") { BuildBrowser(); return; }
            if (tab == "Connections") { BuildConnections(); return; }
            if (tab == "Integrations") { BuildIntegrations(); return; }
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
                string pageUrl = ResultPageUrl(step["result"]);
                if (!Archived && (string)step["status"] == "completed" && ValidPageUrl(pageUrl))
                {
                    bool alreadyOpen = (string)step["tool"] == "local_app.open" || (string)step["tool"] == "browser.open";
                    Button("Open page inside Sakura", () =>
                    {
                        localBrowser = new Uri(pageUrl).IsLoopback;
                        if (alreadyOpen) { SelectTab("Browser"); ReadPreview(); }
                        else Propose(localBrowser ? "local_app.open" : "browser.open", new JObject { ["url"] = pageUrl });
                    }, fold);
                }
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
                var search = Input("Search Hugging Face GGUF models", parent: import);
                var searchResults = new VisualElement(); import.Add(searchResults);
                Button("Search model repositories", () => Request("/api/agent/local-ai/search?q=" + Uri.EscapeDataString((search.value ?? "").Trim()), null, data =>
                {
                    searchResults.Clear();
                    foreach (var repo in (data["models"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string id = (string)repo["repo"];
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; searchResults.Add(row);
                        var label = new Label(id + " · " + (repo["downloads"] ?? 0) + " downloads"); label.style.flexGrow = 1; row.Add(label);
                        row.Add(new Button(() => Request("/api/agent/local-ai/files?repo=" + Uri.EscapeDataString(id), null, detail =>
                        {
                            var files = (detail["files"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                            var fileList = new VisualElement(); searchResults.Add(fileList);
                            foreach (var file in files)
                            {
                                string filename = (string)file["file"];
                                if (string.IsNullOrWhiteSpace(filename)) continue;
                                var fileRow = new VisualElement(); fileRow.style.flexDirection = FlexDirection.Row; fileList.Add(fileRow);
                                var fileLabel = new Label(filename + " · " + (((double?)file["bytes"] ?? 0) / 1e9).ToString("0.0") + " GB" + (file.Value<bool?>("projector") == true ? " · vision projector" : "")); fileLabel.style.flexGrow = 1; fileRow.Add(fileLabel);
                                fileRow.Add(new Button(() => Request("/api/agent/local-ai/download", new JObject { ["repo"] = id, ["file"] = filename }, _ => status.text = "Model download started. Refresh Local AI to check progress.")) { text = "Download" });
                            }
                        })) { text = "Show files" });
                    }
                }), import);
            });
        }
        private void BuildBrowser()
        {
            if(selected==null) { Text("Select a task first. Browser interactions belong to that task and use its approvals.",content,"description"); return; }
            if (Archived) { Text("Open a recent task to use the browser. Archived sessions are read-only.",content,"description"); return; }
            snapshot = null;
            var browserSteps = (selected["steps"] as JArray ?? new JArray()).Reverse().ToArray();
            var opened = browserSteps.FirstOrDefault(step => (string)step["status"] == "completed" && ((string)step["tool"] == "local_app.open" || (string)step["tool"] == "browser.open"));
            var lastBrowser = browserSteps.FirstOrDefault(step => (string)step["status"] == "completed"
                && (((string)step["tool"] ?? "").StartsWith("local_app.", StringComparison.Ordinal) || ((string)step["tool"] ?? "").StartsWith("browser.", StringComparison.Ordinal)));
            if (((string)lastBrowser?["tool"] ?? "").EndsWith(".close", StringComparison.Ordinal)) { ClearBrowser(); opened = null; }
            if (opened != null) localBrowser = (string)opened["tool"] == "local_app.open";
            string currentUrl = ResultPageUrl(opened?["result"]) ?? (string)opened?["arguments"]?["url"] ?? "";
            var url=new TextField("Website address") { value = currentUrl }; content.Add(url);
            var buttons=Row(content);
            Button("Open page",()=>
            { localBrowser=Uri.TryCreate(url.value,UriKind.Absolute,out var target)&&target.IsLoopback; Propose(localBrowser?"local_app.open":"browser.open",new JObject {["url"]=url.value}); },buttons);
            Button("Read page",()=>Propose(localBrowser?"local_app.inspect":"browser.read",new JObject()),buttons);
            Button("Refresh preview",ReadPreview,buttons);
            Button("Close page",()=>Propose(localBrowser?"local_app.close":"browser.close",new JObject()),buttons);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (localBrowser && opened != null && ValidPageUrl(currentUrl)
                && Uri.TryCreate(currentUrl, UriKind.Absolute, out var liveTarget) && liveTarget.IsLoopback)
            {
                var liveHost = new VisualElement(); content.Add(liveHost);
                Text("Embedded live view of this local app, in a separate session. Closing it keeps the app's server running.", liveHost, "description");
                WebViewElement liveView = null;
                Label liveStatus = new Label(); liveStatus.style.whiteSpace = WhiteSpace.Normal; liveHost.Add(liveStatus);
                Button liveToggle = null;
                liveToggle = Button("Open live view", () =>
                {
                    if (liveView != null) { liveHost.Remove(liveView); liveView = null; liveToggle.text = "Open live view"; return; }
                    try
                    {
                        liveStatus.text = "Starting embedded browser…";
                        var opening = new WebViewElement { InitialUrl = currentUrl };
                        new SakuraBrowserPreview(opening, message => liveStatus.text = message);
                        liveView = opening;
                        opening.NavigationCompleted += (_, success) =>
                        {
                            if (liveView != opening) return;
                            liveStatus.text = success ? "Local app loaded." : "The local app did not load. Check that its server is still running, then reopen the live view.";
                        };
                        opening.style.height = 380; opening.style.width = new Length(100, LengthUnit.Percent);
                        liveHost.Add(opening); liveToggle.text = "Close live view";
                        liveHost.schedule.Execute(() =>
                        {
                            if (liveView == opening && !opening.IsReady)
                                liveStatus.text = "Embedded browser could not start. Check that Microsoft Edge WebView2 Runtime is installed.";
                        }).StartingIn(20000);
                    }
                    catch (Exception error)
                    {
                        liveView?.RemoveFromHierarchy(); liveView = null;
                        liveStatus.text = "Embedded browser failed: " + error.Message;
                    }
                }, buttons);
            }
#endif
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
            if (opened != null && preview == null) ReadPreview();
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
        public static bool ValidPageUrl(string url) => url != null && url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == "http" || parsed.Scheme == "https") && string.IsNullOrEmpty(parsed.UserInfo);
        static string ResultPageUrl(JToken result)
        {
            var value = result is JObject record ? record["url"] ?? record["preview_url"] : null;
            return value?.Type == JTokenType.String ? (string)value : null;
        }
        private static void AddNavigationIcon(Button button, string page)
        {
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("agent-nav-icon"); button.Insert(0, icon);
            icon.generateVisualContent += context =>
            {
                var painter = context.painter2D;
                const float scale = 17f / 24;
                painter.strokeColor = button.resolvedStyle.color; painter.lineWidth = 1.7f * scale;
                painter.lineCap = LineCap.Round; painter.lineJoin = LineJoin.Round;
                Vector2 Point(float x, float y) => new Vector2(x * scale, y * scale);
                void Path(params Vector2[] points)
                {
                    painter.BeginPath(); painter.MoveTo(points[0]);
                    foreach (var point in points.Skip(1)) painter.LineTo(point);
                    painter.Stroke();
                }
                void Box(float x, float y, float width, float height, float radius)
                {
                    float curve = radius * .552285f;
                    painter.BeginPath(); painter.MoveTo(Point(x + radius, y)); painter.LineTo(Point(x + width - radius, y));
                    painter.BezierCurveTo(Point(x + width - radius + curve, y), Point(x + width, y + radius - curve), Point(x + width, y + radius));
                    painter.LineTo(Point(x + width, y + height - radius));
                    painter.BezierCurveTo(Point(x + width, y + height - radius + curve), Point(x + width - radius + curve, y + height), Point(x + width - radius, y + height));
                    painter.LineTo(Point(x + radius, y + height));
                    painter.BezierCurveTo(Point(x + radius - curve, y + height), Point(x, y + height - radius + curve), Point(x, y + height - radius));
                    painter.LineTo(Point(x, y + radius));
                    painter.BezierCurveTo(Point(x, y + radius - curve), Point(x + radius - curve, y), Point(x + radius, y));
                    painter.ClosePath(); painter.Stroke();
                }
                switch (page)
                {
                    case "Tasks":
                        Path(Point(4, 4), Point(20, 4), Point(20, 20), Point(4, 20), Point(4, 4));
                        Path(Point(8, 9), Point(16, 9)); Path(Point(8, 13), Point(14, 13)); Path(Point(8, 17), Point(12, 17));
                        break;
                    case "Local AI":
                        Box(5, 5, 14, 14, 3);
                        foreach (float pin in new[] { 9f, 15f })
                        {
                            Path(Point(pin, 1), Point(pin, 5)); Path(Point(pin, 19), Point(pin, 23));
                            Path(Point(1, pin), Point(5, pin)); Path(Point(19, pin), Point(23, pin));
                        }
                        break;
                    case "Connections":
                        Path(Point(8, 12), Point(16, 12));
                        Path(Point(9, 7), Point(9, 4), Point(4, 4), Point(4, 9), Point(7, 9));
                        Path(Point(17, 9), Point(20, 9), Point(20, 4), Point(15, 4), Point(15, 7));
                        Path(Point(7, 15), Point(4, 15), Point(4, 20), Point(9, 20), Point(9, 17));
                        Path(Point(15, 17), Point(15, 20), Point(20, 20), Point(20, 15), Point(17, 15));
                        break;
                    case "Integrations":
                        Box(4, 4, 16, 16, 3);
                        Path(Point(8, 4), Point(8, 20)); Path(Point(4, 10), Point(20, 10));
                        Path(Point(11, 14), Point(17, 14));
                        break;
                    case "Skills":
                        Path(Point(5, 20), Point(9, 19), Point(20, 8), Point(16, 4), Point(5, 15), Point(5, 20));
                        Path(Point(13, 7), Point(17, 11));
                        break;
                    case "Memory & learning":
                        painter.BeginPath(); painter.MoveTo(Point(8, 4));
                        painter.BezierCurveTo(Point(5, 3), Point(4, 6), Point(4, 8)); painter.LineTo(Point(4, 10));
                        painter.BezierCurveTo(Point(1, 11), Point(1, 16), Point(4, 17));
                        painter.BezierCurveTo(Point(4, 22), Point(12, 23), Point(12, 19)); painter.LineTo(Point(12, 6));
                        painter.BezierCurveTo(Point(12, 3), Point(10, 2), Point(8, 4)); painter.ClosePath(); painter.Stroke();
                        painter.BeginPath(); painter.MoveTo(Point(12, 6));
                        painter.BezierCurveTo(Point(14, 2), Point(20, 4), Point(20, 8)); painter.LineTo(Point(20, 10));
                        painter.BezierCurveTo(Point(23, 11), Point(23, 16), Point(20, 17));
                        painter.BezierCurveTo(Point(20, 22), Point(12, 23), Point(12, 19)); painter.Stroke();
                        Path(Point(8, 9), Point(8, 13)); Path(Point(16, 9), Point(16, 13));
                        break;
                    case "Automations":
                        painter.BeginPath(); painter.Arc(Point(12, 12), 8 * scale, 0, 360); painter.Stroke();
                        Path(Point(12, 7), Point(12, 12), Point(15, 14));
                        Path(Point(4, 4), Point(2, 8)); Path(Point(20, 4), Point(22, 8));
                        break;
                    case "Task history":
                        painter.BeginPath(); painter.Arc(Point(12, 12), 8 * scale, 180, 230, ArcDirection.CounterClockwise); painter.Stroke();
                        Path(Point(4, 7), Point(4, 12), Point(9, 12)); Path(Point(12, 8), Point(12, 13), Point(15, 15));
                        break;
                    case "Browser":
                        Box(3, 3, 18, 18, 3); Path(Point(3, 8), Point(21, 8));
                        Path(Point(7, 5), Point(8, 5)); Path(Point(10, 5), Point(11, 5));
                        break;
                }
            };
        }
        private static VisualElement Row(VisualElement parent){var row=new VisualElement();row.AddToClassList("row");parent.Add(row);return row;}
        private static Label Text(string text,VisualElement parent,string style){var label=new Label(text??"");label.AddToClassList(style);parent.Add(label);return label;}
        private static Button Button(string text,Action clicked,VisualElement parent){var button=new Button(clicked){text=text};parent.Add(button);return button;}
    }
}
