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
        private VisualElement root, panel, panelBody, dock;
        private Label badge, heading, status, stageMessage;
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
            QualitySettings.vSyncCount = 0;
            var document = GetComponent<UIDocument>();
            if (document == null) document = gameObject.AddComponent<UIDocument>();
            if (document.panelSettings == null)
            {
                document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                document.panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                document.panelSettings.referenceResolution = new Vector2Int(1440, 900);
                document.panelSettings.match = .5f;
            }
            document.panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("Sakura/StudioTheme");
            root = document.rootVisualElement;
            root.Clear(); root.AddToClassList("studio");
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            var sheet = Resources.Load<StyleSheet>("Sakura/Studio");
            if (sheet != null) root.styleSheets.Add(sheet);
            backend = GetComponent<SakuraBackend>() ?? gameObject.AddComponent<SakuraBackend>();
            backend.CatalogReceived += ReceiveCatalog;
            backend.StatusChanged += SetStatus;
            backend.ReplyReceived += ReceiveReply;
            Build();
            backend.RefreshCatalog();
        }

        private void Build()
        {
            var header = Element("header", root);
            var image = new Image { image = Resources.Load<Texture2D>("Sakura/CompanionLogo") };
            image.AddToClassList("logo"); header.Add(image);
            Label("Sakura", "brand", header);
            badge = Label(CurrentMode.ToUpperInvariant(), "badge", header);
            var spacer = Element("spacer", header);
            Button("Light / dark", ToggleTheme, header);

            var workspace = Element("workspace", root);
            dock = Element("dock", workspace);
            foreach (string name in new[] { "Character", "Camera", "Voice & speech", "Model Settings", "Background", "Streaming & output", "Sakura Mobile", "Memory & privacy", "Settings" })
            {
                string key = name;
                var button = Button(name, () => ShowPanel(key), dock);
                button.AddToClassList("dock-button"); navigation[key] = button;
            }
            var stage = Element("stage", workspace);
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
            Label("Choose a character to inspect its source. Runtime avatar loading is the next step.", "stage-note", stage);

            var chat = Element("chat", stage);
            Label("Chat with Sakura", "card-title", chat);
            messages = new ScrollView(); messages.AddToClassList("messages"); chat.Add(messages);
            Label("Connect the existing backend in Settings to use your configured AI.", "message", messages.contentContainer);
            var composer = Element("row", chat);
            var input = new TextField { multiline = false }; input.AddToClassList("chat-input"); composer.Add(input);
            Button("Send", () =>
            {
                if (string.IsNullOrWhiteSpace(input.value) || backend.Busy) return;
                if (!backend.Connected) { SetStatus("Connect the backend in Settings first."); return; }
                Label(input.value, "user-message", messages.contentContainer);
                backend.SendChat(input.value); input.value = "";
            }, composer);

            panel = Element("panel", workspace);
            var panelHeader = Element("panel-header", panel);
            heading = Label("Character", "panel-title", panelHeader);
            Button("×", () => panel.style.display = DisplayStyle.None, panelHeader).AddToClassList("close");
            var scroll = new ScrollView(); scroll.AddToClassList("panel-scroll"); panel.Add(scroll);
            panelBody = scroll.contentContainer;

            var footer = Element("footer", root);
            status = Label("Reading local library…", "status", footer);
            foreach (string mode in new[] { "Companion", "VTuber", "Agent" })
            { string key = mode; Button(mode + " mode", () => SwitchMode(key), footer); }
            ShowPanel("Character");
            root.RegisterCallback<GeometryChangedEvent>(args => root.EnableInClassList("compact", root.resolvedStyle.width < 1000));
        }

        public void SwitchMode(string mode)
        {
            if (mode != "Companion" && mode != "VTuber" && mode != "Agent") throw new ArgumentException("Unknown mode");
            CurrentMode = mode;
            badge.text = mode.ToUpperInvariant();
            root.EnableInClassList("vtuber", mode == "VTuber");
            root.EnableInClassList("agent", mode == "Agent");
            root.Q(className: "chat").style.display = mode == "VTuber" ? DisplayStyle.None : DisplayStyle.Flex;
            ShowPanel(mode == "Agent" ? "Agent workspace" : "Character");
            SetStatus(mode + " Unity view. Feature parity is being verified in stages.");
        }

        public void ToggleTheme() { light = !light; root.EnableInClassList("light", light); }

        public void ShowPanel(string name)
        {
            CurrentPanel = name;
            panel.style.display = DisplayStyle.Flex;
            heading.text = name;
            panelBody.Clear();
            foreach (var pair in navigation) pair.Value.EnableInClassList("selected", pair.Key == name);
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
                Label("These controls currently drive loaded VRM avatars. Material, expression and bone tools are pending migration.", "description", panelBody);
            }
            else
            {
                Label(name, "card-title", panelBody);
                Label("This feature is available in the original Sakura app. Its native Unity implementation is pending.", "description", panelBody);
                Label("Migration preserves the existing source and personal assets until full verification and your approval.", "description", panelBody);
            }
        }

        private void BuildSettings()
        {
            var card = Element("card", panelBody);
            Label("Existing Sakura services", "card-title", card);
            Label("Development connection uses the preserved Python environment and configured accounts. Starting it can load your selected local AI.", "description", card);
            var source = new TextField("Source folder") { value = backend.SourceFolder };
            source.RegisterValueChangedCallback(args => { if (!backend.Connected) backend.SourceFolder = args.newValue; });
            card.Add(source);
            Button("Connect backend", backend.Connect, card);
            Button("Refresh local library", backend.RefreshCatalog, card);
            Label("Unity " + Application.unityVersion, "description", card);
            Label("Initial graphics budget", "card-title", panelBody);
            var frameRate = new DropdownField("Frame rate", new List<string> { "30", "60", "120" }, 1);
            frameRate.RegisterValueChangedCallback(args => Application.targetFrameRate = int.Parse(args.newValue));
            panelBody.Add(frameRate);
            Label("GTX and RTX performance targets require real model and hardware benchmarks.", "description", panelBody);
        }

        private void BuildCatalog()
        {
            var card = Element("card", panelBody);
            Label("Your character library", "card-title", card);
            var query = new TextField { value = search }; query.AddToClassList("search");
            query.tooltip = "Search by model name or folder"; card.Add(query);
            var tabs = Element("row", card);
            var result = new ScrollView(); result.AddToClassList("catalog-results"); card.Add(result);
            Action populate = () =>
            {
                result.Clear();
                var entries = catalog.Where(item => item.format == format &&
                    (item.name + " " + item.path).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).Take(150).ToArray();
                foreach (var entry in entries)
                {
                    var row = Element("model-row", result.contentContainer);
                    Button(entry.name, () =>
                    {
                        if (entry.format == "VRM") _ = viewport.LoadVrm(backend.SourceFolder, entry.path);
                        else { stageMessage.text = entry.name; SetStatus(entry.format + " · " + entry.path + " · runtime loading pending"); }
                    }, row);
                    Label(entry.path, "model-path", row);
                }
                if (entries.Length == 0) Label("No " + format + " models match this search.", "description", result.contentContainer);
            };
            foreach (string type in new[] { "PMX", "VRM", "Live2D" })
            { string kind = type; Button(type, () => { format = kind; populate(); }, tabs); }
            query.RegisterValueChangedCallback(args => { search = args.newValue; populate(); });
            Button("Refresh library", backend.RefreshCatalog, card);
            Label(catalog.Length + " model references · original files remain in Sakura", "description", card);
            populate();
        }

        private void ReceiveCatalog(CharacterEntry[] entries) { catalog = entries; if (CurrentPanel == "Character") ShowPanel("Character"); }
        private void SetStatus(string text) { if (status != null) status.text = text; }
        private void ReceiveReply(string text) { Label(text, "message", messages.contentContainer); messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0, float.MaxValue)); }
        private static VisualElement Element(string css, VisualElement parent) { var element = new VisualElement(); element.AddToClassList(css); parent.Add(element); return element; }
        private static Label Label(string text, string css, VisualElement parent) { var label = new Label(text); label.AddToClassList(css); parent.Add(label); return label; }
        private static Button Button(string text, Action action, VisualElement parent) { var button = new Button(action) { text = text }; parent.Add(button); return button; }

        private void OnDisable()
        {
            if (backend == null) return;
            backend.CatalogReceived -= ReceiveCatalog;
            backend.StatusChanged -= SetStatus;
            backend.ReplyReceived -= ReceiveReply;
        }
    }
}
