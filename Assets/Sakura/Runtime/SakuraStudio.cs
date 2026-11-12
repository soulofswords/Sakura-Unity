using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraStudio : MonoBehaviour
    {
        public string CurrentMode { get; private set; } = "Companion";
        public string CurrentPanel { get; private set; } = "Character";
        public int CharacterCount => catalog.Length;
        public bool ControlsHidden { get; private set; }
        private VisualElement root, panel, panelBody, dock;
        private Label badge, heading, status, stageMessage;
        private Label panelEdition;
        private VisualElement footer, chatPanel;
        private ScrollView messages;
        private SakuraBackend backend;
        private SakuraAvatarViewport viewport;
        private CharacterEntry[] catalog = Array.Empty<CharacterEntry>();
        private string format = "PMX", search = "";
        private bool light;
        private readonly Dictionary<string, Button> navigation = new Dictionary<string, Button>();

        private void OnEnable()
        {
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            var document = GetComponent<UIDocument>();
            if (document == null) document = gameObject.AddComponent<UIDocument>();
            if (document.panelSettings == null)
            {
                document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                document.panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
                document.panelSettings.scale = 1;
            }
            document.panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("Sakura/StudioTheme");
            root = document.rootVisualElement;
            root.Clear(); root.AddToClassList("studio");
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            var sheet = Resources.Load<StyleSheet>("Sakura/Studio");
            if (sheet != null) root.styleSheets.Add(sheet);
            backend = GetComponent<SakuraBackend>() ?? gameObject.AddComponent<SakuraBackend>();
            backend.CatalogReceived += ReceiveCatalog;
            backend.StatusChanged += SetStatus;
            backend.ReplyReceived += ReceiveReply;
            backend.ModelImported += ReceiveImportedModel;
            Build();
            backend.RefreshCatalog();
        }

        private void Build()
        {
            var header = Element("header", root);
            header.name = "top-bar";
            var image = new Image { image = Resources.Load<Texture2D>("Sakura/CompanionLogo") };
            image.AddToClassList("logo"); header.Add(image);
            Label("Sakura", "brand", header);
            badge = Label(CurrentMode.ToUpperInvariant(), "badge", header);
            var spacer = Element("spacer", header);
            var windows = Element("window-controls", header);
            Button("—", () => SetStatus("Window controls are managed by the Unity Editor during this preview."), windows);
            Button("□", () => Screen.fullScreen = !Screen.fullScreen, windows);
            Button("×", () => { if (Application.isEditor) SetStatus("Use Unity’s Play button to leave the preview."); else Application.Quit(); }, windows);

            var workspace = Element("workspace", root);
            dock = Element("dock", workspace);
            dock.name = "companion-dock";
            string[] keys = { "Character", "Camera", "Voice & speech", "Model Settings", "Background", "Streaming & output", "Sakura Mobile", "Memory & privacy", "Settings" };
            string[] labels = { "Models", "Camera &\ntracking", "Voice &\nspeech", "Model\nSettings", "Background\n& stage\nitems", "Streaming &\noutput", "Sakura\nMobile", "Memory &\nprivacy", "Settings" };
            string[] icons = { "character", "camera", "voice", "tools", "background", "output", "phone", "memory", "settings" };
            for (int index = 0; index < keys.Length; index++)
            {
                string key = keys[index];
                var button = Button("", () => ShowPanel(key), dock);
                button.tooltip = labels[index].Replace("\n", " ");
                var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/" + icons[index]) };
                icon.AddToClassList("dock-icon"); button.Add(icon);
                Label(labels[index], "dock-label", button);
                button.AddToClassList("dock-button"); navigation[key] = button;
            }
            var stage = Element("stage", workspace);
            stage.name = "avatar-stage";
            stage.SendToBack();
            stage.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount == 2 && CurrentMode != "Agent") { ToggleChrome(); evt.StopPropagation(); }
            });
            viewport = GetComponent<SakuraAvatarViewport>() ?? gameObject.AddComponent<SakuraAvatarViewport>();
            viewport.Attach(stage);
            viewport.StatusChanged += text =>
            {
                SetStatus(text);
                if (viewport.HasAvatar)
                {
                    stageMessage.style.display = DisplayStyle.None;
                    stage.Q(className: "stage-caption").style.display = DisplayStyle.None;
                    stage.Q(className: "stage-note").style.display = DisplayStyle.None;
                }
            };
            stageMessage = Label("Your character’s new stage", "stage-heading", stage);
            Label("Native Unity renderer migration", "stage-caption", stage);
            Label("Choose a PMX, VRM or Live2D character from your library.", "stage-note", stage);

            var chat = Element("chat", workspace);
            chatPanel = chat; chat.name = "chat-panel";
            var chatHeader = Element("chat-header", chat);
            Label("●  Chat with Sakura", "chat-title", chatHeader);
            Element("spacer", chatHeader);
            Button("↔", () => SetStatus("Chat placement migration is pending."), chatHeader);
            Button("—", () => chat.EnableInClassList("minimized", !chat.ClassListContains("minimized")), chatHeader);
            Button("▤", () => messages.Clear(), chatHeader);
            Label("Screen off · Capture stopped", "capture-status", chat);
            messages = new ScrollView(); messages.AddToClassList("messages"); chat.Add(messages);
            var composer = Element("composer", chat);
            var listen = Button("♩", () => SetStatus("Native Unity microphone migration is pending."), composer);
            listen.tooltip = "Microphone";
            var input = new TextField { multiline = false }; input.AddToClassList("chat-input"); composer.Add(input);
            input.tooltip = "Type a message or use your mic…";
            Button("➤", () =>
            {
                if (string.IsNullOrWhiteSpace(input.value) || backend.Busy) return;
                if (!backend.Connected) { SetStatus("Connect the backend in Settings first."); return; }
                Label(input.value, "user-message", messages.contentContainer);
                backend.SendChat(input.value); input.value = "";
            }, composer);

            panel = Element("panel", workspace);
            panel.name = "companion-panel";
            var panelHeader = Element("panel-header", panel);
            var panelTitles = Element("panel-titles", panelHeader);
            panelEdition = Label("SAKURA COMPANION", "panel-edition", panelTitles);
            heading = Label("Models", "panel-title", panelTitles);
            Button("×", () => panel.AddToClassList("panel-closed"), panelHeader).AddToClassList("close");
            var scroll = new ScrollView(); scroll.AddToClassList("panel-scroll"); panel.Add(scroll);
            panelBody = scroll.contentContainer;
            SakuraPanelResize.Attach(panel);

            footer = Element("footer", root); footer.name = "companion-statusbar";
            BuildFooter();
            ShowPanel("Character");
            root.RegisterCallback<GeometryChangedEvent>(args =>
            {
                root.EnableInClassList("compact", args.newRect.width < 650);
                root.EnableInClassList("short", args.newRect.height < 760);
                float available = Math.Max(100, args.newRect.height - 145);
                chatPanel.style.height = Math.Min(440, Math.Min(args.newRect.height * .5f, available));
            });
        }

        public void SwitchMode(string mode)
        {
            if (mode != "Companion" && mode != "VTuber" && mode != "Agent") throw new ArgumentException("Unknown mode");
            CurrentMode = mode;
            if (viewport != null && viewport.Idle != null) viewport.Idle.Companion = mode == "Companion";
            ControlsHidden = false;
            root.RemoveFromClassList("controls-hidden");
            badge.text = mode.ToUpperInvariant();
            root.EnableInClassList("vtuber", mode == "VTuber");
            root.EnableInClassList("agent", mode == "Agent");
            root.Q(className: "chat").style.display = mode == "VTuber" ? DisplayStyle.None : DisplayStyle.Flex;
            panelEdition.text = mode == "VTuber" ? "SAKURA STUDIO" : "SAKURA COMPANION";
            BuildFooter();
            ShowPanel(mode == "Agent" ? "Agent workspace" : "Character");
        }

        public void ToggleTheme() { light = !light; root.EnableInClassList("light", light); RefreshIconColors(); }

        public void ToggleChrome()
        {
            if (CurrentMode == "Agent") return;
            ControlsHidden = !ControlsHidden;
            root.EnableInClassList("controls-hidden", ControlsHidden);
        }

        private void BuildFooter()
        {
            footer.Clear();
            status = Label(CurrentMode == "VTuber" ? "AI paused · choose your camera and start face, shoulder and arm tracking" : "Talk to Sakura…", "status", footer);
            if (CurrentMode != "Companion") Button("Companion mode", () => SwitchMode("Companion"), footer);
            if (CurrentMode != "VTuber") Button("VTuber mode", () => SwitchMode("VTuber"), footer);
            if (CurrentMode != "Agent") Button("Agent mode", () => SwitchMode("Agent"), footer);
        }

        private void RefreshIconColors()
        {
            foreach (var item in navigation)
            {
                var icon = item.Value.Q<Image>();
                ColorUtility.TryParseHtmlString(item.Value.ClassListContains("selected") ? (light ? "#a13464" : "#ff9ec8") : (light ? "#785f70" : "#ccb6c8"), out var color);
                if (icon != null) icon.tintColor = color;
            }
        }

        public void ShowPanel(string name)
        {
            CurrentPanel = name;
            panel.RemoveFromClassList("panel-closed");
            heading.text = name == "Character" ? "Models" : name == "Camera" ? "Camera & tracking" : name == "Background" ? "Background & stage items" : name;
            panelBody.Clear();
            foreach (var pair in navigation) pair.Value.EnableInClassList("selected", pair.Key == name);
            RefreshIconColors();
            if (name == "Character") BuildCatalog();
            else if (name == "Settings") BuildSettings();
            else if (name == "Model Settings")
            {
                Label("Framing", "card-title", panelBody);
                var controls = Element("row", panelBody);
                Button("−", () => viewport.Zoom(1 / 1.1f), controls);
                Button("+", () => viewport.Zoom(1.1f), controls);
                Button("Turn left", () => viewport.Rotate(-15), panelBody);
                Button("Turn right", () => viewport.Rotate(15), panelBody);
                if (viewport.Idle != null)
                {
                    var motion = viewport.Idle;
                    foreach (string option in new[] { "Auto breathing", "Auto blinking", "Idle motions" })
                    {
                        string key = option;
                        var toggle = new Toggle(key) { value = key == "Auto breathing" ? motion.Breathing : key == "Auto blinking" ? motion.Blinking : motion.IdleMotions };
                        toggle.RegisterValueChangedCallback(evt =>
                        {
                            if (motion == null || viewport.Idle != motion) return;
                            if (key == "Auto breathing") motion.Breathing = evt.newValue; else if (key == "Auto blinking") motion.Blinking = evt.newValue; else motion.IdleMotions = evt.newValue;
                            motion.Save();
                        });
                        panelBody.Add(toggle);
                    }
                }
                if (viewport.Pmx != null) new SakuraPmxPanel(viewport, backend, panelBody);
                else Label("Select a PMX model for its material, bone and appearance tools. VRM editing is pending migration.", "description", panelBody);
            }
            else
            {
                Label(name, "card-title", panelBody);
                Label("This feature is available in the original Sakura app. Its native Unity implementation is pending.", "description", panelBody);
                Label("Migration preserves the existing source and personal assets until full verification and your approval.", "description", panelBody);
            }
        }

        private void OpenPmxTool(string tool)
        {
            if (viewport.LoadedFormat != "PMX") { SetStatus("Select a PMX model first."); return; }
            backend.RunPmxTool(tool, viewport.LoadedModelPath, (json, error) => SetStatus(error ?? "Selected model sent to " + (tool == "mmd" ? "MikuMikuDance." : "PMX Editor.")));
        }

        private void BuildSettings()
        {
            new SakuraSettingsPanel(backend, panelBody, theme =>
            {
                bool target = theme == "light";
                if (target != light) ToggleTheme();
            });
        }

        private void BuildCatalog()
        {
            var card = Element("card", panelBody);
            Label("Your character library", "card-title", card);
            var query = new TextField { value = search }; query.AddToClassList("search");
            query.tooltip = "Search by model name or folder"; card.Add(query);
            query.textEdition.placeholder = "Search your characters…";
            var result = Element("search-results", card);
            Action populateSearch = () =>
            {
                result.Clear();
                if (string.IsNullOrWhiteSpace(search)) return;
                var entries = catalog.Where(item => item.format == format &&
                    (item.name + " " + item.path).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).Take(150).ToArray();
                foreach (var entry in entries)
                {
                    var row = Element("model-row", result.contentContainer);
                    Button(entry.name, () =>
                    {
                        _ = viewport.LoadModel(backend.SourceFolder, entry.path);
                    }, row);
                    Label(entry.path, "model-path", row);
                }
                if (entries.Length == 0) Label("No " + format + " models match this search.", "description", result.contentContainer);
            };
            foreach (string type in new[] { "PMX", "VRM", "Live2D" })
            {
                string kind = type;
                var select = Button("", () => ShowLocalPicker(kind), card); select.AddToClassList("model-select");
                select.name = "select-" + kind.ToLowerInvariant(); select.tooltip = kind + " characters";
                Label(type, "model-select-label", select); Label("▾", "model-select-arrow", select);
            }
            query.RegisterValueChangedCallback(args => { search = args.newValue; populateSearch(); });
            Label("Select a character above, or import a complete model package below.", "description", card);
            var imports = Element("card", panelBody);
            Label("Import a character", "card-title", imports);
            var uploadButtons = Element("row", imports);
            foreach (string type in new[] { "PMX", "VRM", "Live2D" })
            { string kind = type; Button("Upload " + type, () => backend.ImportModel(kind), uploadButtons); }
            Button("↻", backend.RefreshCatalog, imports).tooltip = "Refresh imported models";
            var cloudButtons = Element("row", imports);
            foreach (string type in new[] { "PMX", "VRM", "Live2D" })
            {
                string kind = type;
                Button("Cloud import " + type, () =>
                {
                    if (panelBody.Q("cloud-library") != null) panelBody.Q("cloud-library").RemoveFromHierarchy();
                    var host = Element("cloud-library", panelBody); host.name = "cloud-library";
                    new SakuraCloudLibrary(backend, host, kind); host.Focus();
                    panelBody.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(host);
                }, cloudButtons);
            }
            Label("PMX and Live2D need their texture folders. VRM uses a single .vrm file.", "description", imports);
            var framing = Element("card", panelBody);
            Label("Framing & customization", "card-title", framing);
            var sizeRow = Element("size-row", framing); Label("Size", "", sizeRow);
            var size = new Slider(40, 180) { value = 100 }; sizeRow.Add(size);
            var sizeValue = Label("100%", "", sizeRow);
            float previousSize = 100;
            size.RegisterValueChangedCallback(args => { viewport.Zoom(args.newValue / previousSize); previousSize = args.newValue; sizeValue.text = ((int)args.newValue) + "%"; });
            var actions = Element("row", framing);
            Button("Full model", () => viewport.ResetFraming(), actions);
            Button("Balanced", () => SetStatus("Graphics preset migration is pending."), actions);
            Button("Eyes off", () => SetStatus("Cursor and eye tracking migration is pending."), actions);
            Button("PMX Editor", () => OpenPmxTool("editor"), actions);
            Button("MikuMikuDance", () => OpenPmxTool("mmd"), actions);
            Button("Customize model", () => ShowPanel("Model Settings"), framing);
            Button("Expressions & hotkeys", () => ShowPanel("Expressions & hotkeys"), framing);
            populateSearch();
        }

        private void ShowLocalPicker(string kind)
        {
            format = kind;
            var existing = root.Q("local-picker"); existing?.RemoveFromHierarchy();
            var overlay = Element("picker-overlay", root); overlay.name = "local-picker";
            var window = Element("picker-window", overlay);
            var title = Element("row", window);
            Label(kind + " characters", "panel-title", title);
            Button("×", () => overlay.RemoveFromHierarchy(), title);
            var find = new TextField(); find.textEdition.placeholder = "Search all folders by character name…"; window.Add(find);
            var breadcrumb = Element("row", window);
            var scroll = new ScrollView(); window.Add(scroll); scroll.AddToClassList("picker-results");
            var grid = Element("picker-grid", scroll.contentContainer);
            string preference = "sakura-model-folder:" + kind;
            string folder = PlayerPrefs.GetString(preference, "");
            if (!catalog.Any(entry => entry.format == kind && entry.path.Substring(entry.path.IndexOf('/') + 1).StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))) folder = "";
            Action render = null;
            render = () =>
            {
                grid.Clear(); breadcrumb.Clear();
                if (PlayerPrefs.GetString(preference, "") != folder) { PlayerPrefs.SetString(preference, folder); PlayerPrefs.Save(); }
                Button(kind, () => { folder = ""; render(); }, breadcrumb);
                string prefix = string.IsNullOrEmpty(folder) ? "" : folder + "/";
                if (!string.IsNullOrEmpty(folder)) Button(folder + " / ←", () => { int slash = folder.LastIndexOf('/'); folder = slash < 0 ? "" : folder.Substring(0, slash); render(); }, breadcrumb);
                var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in catalog.Where(item => item.format == kind))
                {
                    string path = entry.path;
                    int rootSlash = path.IndexOf('/'); if (rootSlash >= 0) path = path.Substring(rootSlash + 1);
                    if (!string.IsNullOrWhiteSpace(find.value))
                    { if ((entry.name + " " + path).IndexOf(find.value, StringComparison.OrdinalIgnoreCase) < 0) continue; }
                    else
                    {
                        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = path.Substring(prefix.Length); int slash = relative.IndexOf('/');
                        if (slash >= 0) { folders.Add(relative.Substring(0, slash)); continue; }
                    }
                    var button = Button(entry.name, () =>
                    {
                        int slash = path.LastIndexOf('/');
                        PlayerPrefs.SetString(preference, slash < 0 ? "" : path.Substring(0, slash)); PlayerPrefs.Save();
                        _ = viewport.LoadModel(backend.SourceFolder, entry.path);
                        overlay.RemoveFromHierarchy();
                    }, grid); button.AddToClassList("picker-item");
                }
                foreach (string child in folders.Reverse())
                {
                    string next = prefix + child;
                    var button = new Button(() => { folder = next; render(); }) { text = "▣\n" + child };
                    button.AddToClassList("picker-item"); grid.Insert(0, button);
                }
            };
            find.RegisterValueChangedCallback(args => render()); render();
        }

        private void ReceiveCatalog(CharacterEntry[] entries) { catalog = entries; if (CurrentPanel == "Character") ShowPanel("Character"); }
        private void ReceiveImportedModel(string path) { _ = viewport.LoadModel(backend.SourceFolder, path); }
        private void SetStatus(string text) { if (status != null) status.text = text; }
        private void ReceiveReply(string text) { Label(text, "message", messages.contentContainer); messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0, float.MaxValue)); }
        private static VisualElement Element(string css, VisualElement parent) { var element = new VisualElement(); element.AddToClassList(css); parent.Add(element); return element; }
        private static Label Label(string text, string css, VisualElement parent) { var label = new Label(text); label.AddToClassList(css); if (css == "panel-title" || css == "brand") label.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/BricolageGrotesque")); parent.Add(label); return label; }
        private static Button Button(string text, Action action, VisualElement parent) { var button = new Button(action) { text = text }; button.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans-Semibold")); button.style.unityFontStyleAndWeight = FontStyle.Normal; parent.Add(button); return button; }

        private void OnDisable()
        {
            if (backend == null) return;
            backend.CatalogReceived -= ReceiveCatalog;
            backend.StatusChanged -= SetStatus;
            backend.ReplyReceived -= ReceiveReply;
            backend.ModelImported -= ReceiveImportedModel;
        }
    }
}
