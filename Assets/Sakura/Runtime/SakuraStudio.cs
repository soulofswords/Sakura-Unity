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
        public event Action PanelsHidden;
        private VisualElement root, panel, panelBody, dock;
        private Label badge, heading, status, stageMessage;
        private Label panelEdition;
        private Label captureStatus;
        private VisualElement footer, chatPanel;
        private ScrollView messages;
        private SakuraBackend backend;
        private SakuraAvatarViewport viewport;
        private SakuraVoice voice;
        private SakuraTracking tracking;
        private SakuraOutput output;
        private SakuraAvatarActions avatarActions;
        private SakuraWindow nativeWindow;
        private SakuraLiveChat liveChat;
        private SakuraAgentPanel agentPanel;
        private Image appLogo;
        private Button microphoneButton;
        private Button compactMicrophone, transparencyButton;
        private Button positionLockButton, handsButton;
        private Label positionLockCaption;
        private CharacterEntry[] catalog = Array.Empty<CharacterEntry>();
        private string format = "PMX", search = "";
        private string catalogView = "Library";
        private bool lightTheme;
        private bool vtuberEdition;
        private bool initialCatalog = true;
        private bool propsLibrary;
        private float chatExpandedHeight = 440;
        private int replyVisualRevision;
        private readonly Dictionary<string, Button> navigation = new Dictionary<string, Button>();
        public VisualElement StageElement { get; private set; }

        private void OnEnable()
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                enabled = false;
                gameObject.AddComponent<SakuraMobile>();
                return;
            }
            Application.targetFrameRate = 60;
            nativeWindow = GetComponent<SakuraWindow>() ?? gameObject.AddComponent<SakuraWindow>();
            nativeWindow.Changed += UpdateWindowTransparency;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            var document = GetComponent<UIDocument>();
            if (document == null) document = gameObject.AddComponent<UIDocument>();
            if (document.panelSettings == null)
            {
                var settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("Sakura/StudioTheme");
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.scale = 1;
                document.panelSettings = settings;
            }
            document.panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("Sakura/StudioTheme");
            root = document.rootVisualElement;
            root.Clear(); root.AddToClassList("studio");
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            var sheet = Resources.Load<StyleSheet>("Sakura/Studio");
            if (sheet != null) root.styleSheets.Add(sheet);
            backend = GetComponent<SakuraBackend>() ?? gameObject.AddComponent<SakuraBackend>();
            vtuberEdition = SakuraInstances.IsVtuber;
            backend.VtuberEdition = vtuberEdition;
            backend.CatalogReceived += ReceiveCatalog;
            backend.StatusChanged += SetStatus;
            backend.ReplyReceived += ReceiveReply;
            backend.ModelImported += ReceiveImportedModel;
            voice = GetComponent<SakuraVoice>() ?? gameObject.AddComponent<SakuraVoice>();
            voice.TranscriptReceived += ReceiveTranscript;
            voice.StatusChanged += SetStatus;
            voice.ListeningChanged += UpdateMicrophone;
            liveChat = GetComponent<SakuraLiveChat>() ?? gameObject.AddComponent<SakuraLiveChat>();
            Build();
            tracking = GetComponent<SakuraTracking>() ?? gameObject.AddComponent<SakuraTracking>();
            tracking.Initialize(backend, viewport);
            _ = GetComponent<SakuraLipSync>() ?? gameObject.AddComponent<SakuraLipSync>();
            (GetComponent<SakuraMotionRecorder>() ?? gameObject.AddComponent<SakuraMotionRecorder>()).Initialize(backend, viewport);
            (GetComponent<SakuraExportWatch>() ?? gameObject.AddComponent<SakuraExportWatch>()).Initialize(backend, viewport);
            (GetComponent<SakuraPluginBridge>() ?? gameObject.AddComponent<SakuraPluginBridge>()).Initialize(backend, viewport);
            (GetComponent<SakuraParameterMappings>() ?? gameObject.AddComponent<SakuraParameterMappings>()).Initialize(backend, viewport);
            (GetComponent<SakuraVmc>() ?? gameObject.AddComponent<SakuraVmc>()).Initialize(viewport);
            output = GetComponent<SakuraOutput>() ?? gameObject.AddComponent<SakuraOutput>();
            output.Initialize(backend, viewport);
            var stage = GetComponent<SakuraStage>() ?? gameObject.AddComponent<SakuraStage>();
            stage.Initialize(backend.SourceFolder, !SakuraInstances.IsSecondary);
            avatarActions = GetComponent<SakuraAvatarActions>() ?? gameObject.AddComponent<SakuraAvatarActions>();
            avatarActions.Viewport = viewport;
            avatarActions.Stage = stage;
            backend.ReplyDataReceived += ApplyAvatarReply;
            if (vtuberEdition) SwitchMode("VTuber");
            backend.RefreshCatalog();
            SakuraSetupGuide.Attach(this, root);
        }

        private void Build()
        {
            var header = Element("header", root);
            header.name = "top-bar";
            header.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button == 0 && evt.target is not UnityEngine.UIElements.Button && (evt.target as VisualElement)?.GetFirstAncestorOfType<UnityEngine.UIElements.Button>() == null) nativeWindow.Drag();
            });
            appLogo = new Image { image = Resources.Load<Texture2D>(vtuberEdition ? "Sakura/VTuberLogo" : "Sakura/CompanionLogo") };
            appLogo.AddToClassList("logo"); header.Add(appLogo);
            Label("Sakura", "brand", header);
            badge = Label(CurrentMode.ToUpperInvariant(), "badge", header);
            var spacer = Element("spacer", header);
            var windows = Element("window-controls", header);
            Button("—", nativeWindow.Minimize, windows);
            Button("□", nativeWindow.Maximize, windows);
            Button("×", () => { if (Application.isEditor) SetStatus("Use Unity’s Play button to leave the preview."); else Application.Quit(); }, windows);

            var workspace = Element("workspace", root);
            dock = Element("dock", workspace);
            dock.name = "companion-dock";
            BuildDock();
            StageElement = Element("stage", workspace);
            var stage = StageElement;
            stage.name = "avatar-stage";
            stage.SendToBack();
            stage.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount == 2 && CurrentMode != "Agent") { ToggleChrome(); evt.StopPropagation(); }
            });
            viewport = GetComponent<SakuraAvatarViewport>() ?? gameObject.AddComponent<SakuraAvatarViewport>();
            viewport.Attach(stage);
            var stageItems = GetComponent<SakuraStageItems>() ?? gameObject.AddComponent<SakuraStageItems>();
            stageItems.Initialize(backend, viewport); stageItems.AttachStage(stage);
            var collab = GetComponent<SakuraNativeCollab>() ?? gameObject.AddComponent<SakuraNativeCollab>();
            collab.Initialize(backend, viewport);
            (GetComponent<SakuraBackdrop>() ?? gameObject.AddComponent<SakuraBackdrop>()).Attach(stage);
            viewport.ModelChanged += ReceiveModelChanged;
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

            BuildChat(workspace);
            BuildQuickControls(workspace);

            panel = Element("panel", workspace);
            panel.name = "companion-panel";
            var panelHeader = Element("panel-header", panel);
            var panelTitles = Element("panel-titles", panelHeader);
            panelEdition = Label("SAKURA COMPANION", "panel-edition", panelTitles);
            heading = Label("Models", "panel-title", panelTitles);
            Button("×", ClosePanel, panelHeader).AddToClassList("close");
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
                chatExpandedHeight = Math.Min(440, Math.Min(args.newRect.height * .5f, available));
                ResizeChat();
            });
        }

        private void BuildDock()
        {
            positionLockButton = null; positionLockCaption = null;
            dock.Clear(); navigation.Clear();
            bool vtuber = CurrentMode == "VTuber";
            string[] keys = vtuber
                ? new[] { "Character", "Camera", "Expressions & hotkeys", "Background", "Streaming & output", "Plugins", "Sakura Mobile", "Settings", "Collab" }
                : new[] { "Character", "Camera", "Voice & speech", "Model Settings", "Background", "Streaming & output", "Sakura Mobile", "Memory & privacy", "Settings" };
            string[] labels = { "Models", "Camera &\ntracking", "Voice &\nspeech", "Model\nSettings", "Background\n& stage\nitems", "Streaming &\noutput", "Sakura\nMobile", "Memory &\nprivacy", "Settings" };
            string[] icons = { "character", "camera", "voice", "tools", "background", "output", "phone", "memory", "settings" };
            if (vtuber)
            {
                labels = new[] { "Models", "Camera &\ntracking", "Expressions &\nhotkeys", "Background", "Streaming &\nvoice", "Plugins", "Sakura\nMobile", "Settings", "Collab" };
                icons = new[] { "character", "camera", "expressions", "background", "output", "plugins", "phone", "settings", "collab" };
            }
            var scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden
            };
            scroll.AddToClassList("dock-scroll"); dock.Add(scroll);
            for (int index = 0; index < keys.Length; index++)
            {
                string key = keys[index];
                var button = Button("", () =>
                {
                    if (CurrentPanel == key && !panel.ClassListContains("panel-closed")) ClosePanel();
                    else ShowPanel(key);
                }, scroll.contentContainer);
                button.tooltip = labels[index].Replace("\n", " ");
                var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/" + icons[index]) };
                icon.AddToClassList("dock-icon"); button.Add(icon);
                Label(labels[index], "dock-label", button);
                button.AddToClassList("dock-button"); navigation[key] = button;
            }
            if (vtuber)
            {
                var modelLock = Button("", () => {}, scroll.contentContainer); modelLock.name = "model-position-lock";
                modelLock.AddToClassList("dock-button");
                var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/lock") }; icon.AddToClassList("dock-icon"); modelLock.Add(icon);
                var caption = Label("Lock model", "dock-label", modelLock);
                positionLockButton = modelLock; positionLockCaption = caption;
                modelLock.clicked += () =>
                {
                    viewport.PositionLocked = !viewport.PositionLocked;
                    modelLock.EnableInClassList("selected", viewport.PositionLocked);
                    caption.text = viewport.PositionLocked ? "Unlock model" : "Lock model";
                };
                var hide = Button("", ToggleChrome, dock); hide.AddToClassList("dock-button"); hide.name = "hide-controls";
                var eye = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/hide") }; eye.AddToClassList("dock-icon"); hide.Add(eye);
                Label("Hide controls", "dock-label", hide);
            }
            RefreshIconColors();
        }

        private void BuildChat(VisualElement workspace)
        {
            var chat = Element("chat", workspace);
            chatPanel = chat; chat.name = "chat-panel";
            var chatHeader = Element("chat-header", chat);
            Label("●  Chat with Sakura", "chat-title", chatHeader);
            Element("spacer", chatHeader);
            Button("↻", RefreshMemory, chatHeader).tooltip = "Refresh conversation memory";
            var minimize = Button("—", () => { chat.EnableInClassList("minimized", !chat.ClassListContains("minimized")); ResizeChat(); }, chatHeader);
            minimize.name = "chat-minimize";
            Button("▤", ConfirmClearMemory, chatHeader).tooltip = "Clear conversation memory";
            captureStatus = Label("Screen off · Capture stopped", "capture-status", chat);
            messages = new ScrollView(); messages.AddToClassList("messages"); chat.Add(messages);
            var composer = Element("composer", chat);
            var input = new TextField { multiline = false }; input.AddToClassList("chat-input"); composer.Add(input);
            microphoneButton = Button("♩", () => { if (voice.Listening) voice.StopListening(); else voice.StartListening(); }, composer);
            microphoneButton.name = "chat-microphone";
            microphoneButton.tooltip = "Microphone OFF · click to listen";
            input.tooltip = "Type a message or use your mic…";
            input.textEdition.placeholder = "Type a message or use your mic…";
            void SendMessage()
            {
                if (string.IsNullOrWhiteSpace(input.value) || backend.Busy) return;
                Label(input.value, "user-message", messages.contentContainer);
                backend.SendChat(input.value); input.value = "";
            }
            Button("➤", SendMessage, composer);
            input.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return) { SendMessage(); evt.StopPropagation(); } });

        }
        private void BuildQuickControls(VisualElement workspace)
        {
            var controls = Element("quick-controls", workspace);
            transparencyButton = Button("◐", () => { nativeWindow.SetTransparent(!nativeWindow.TransparencyRequested); SetStatus(nativeWindow.Status); }, controls);
            transparencyButton.name = "compact-transparency"; transparencyButton.tooltip = "Toggle transparent desktop stage";
            compactMicrophone = Button("Mic", () => { if (voice.Listening) voice.StopListening(); else voice.StartListening(); }, controls);
            compactMicrophone.name = "compact-microphone";
            var settings = Button("", () => ShowPanel("Settings"), workspace); settings.name = "compact-settings"; settings.AddToClassList("quick-settings"); settings.tooltip = "Settings";
            var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/settings"), pickingMode = PickingMode.Ignore }; icon.style.width = icon.style.height = 20; settings.Add(icon);
            UpdateMicrophone(voice.Listening);
        }
        private void UpdateWindowTransparency()
        {
            root?.EnableInClassList("window-transparent", nativeWindow.Transparent);
            transparencyButton?.EnableInClassList("mode-active", nativeWindow.Transparent);
        }

        public void SwitchMode(string mode)
        {
            if (mode != "Companion" && mode != "VTuber" && mode != "Agent") throw new ArgumentException("Unknown mode");
            if (vtuberEdition && mode != "VTuber") return;
            nativeWindow?.SetAgentMode(mode == "Agent");
            replyVisualRevision++;
            viewport?.Expressions?.StopReplyMood();
            PanelsHidden?.Invoke();
            CurrentMode = mode;
            if (liveChat.Active) liveChat.Disconnect();
            backend.SetMode(mode);
            if (mode != "Companion") avatarActions?.DisableRoom();
            if (viewport != null && viewport.Idle != null) viewport.Idle.Companion = mode == "Companion";
            ControlsHidden = false;
            root.RemoveFromClassList("controls-hidden");
            badge.text = mode.ToUpperInvariant();
            root.EnableInClassList("vtuber", mode == "VTuber");
            root.EnableInClassList("agent", mode == "Agent");
            root.Q(className: "chat").style.display = mode == "Companion" ? DisplayStyle.Flex : DisplayStyle.None;
            panelEdition.text = mode == "VTuber" ? "SAKURA STUDIO" : "SAKURA COMPANION";
            appLogo.image = Resources.Load<Texture2D>(mode == "VTuber" ? "Sakura/VTuberLogo" : "Sakura/CompanionLogo");
            BuildDock();
            BuildFooter();
            root.Q("agent-workspace")?.RemoveFromHierarchy();
            if (mode == "Agent")
            {
                CurrentPanel = "Agent workspace";
                agentPanel = new SakuraAgentPanel(backend, root.Q(className: "workspace"), () => SwitchMode("Companion"));
            }
            else ShowPanel("Character");
        }

        public void ToggleTheme() => ApplyTheme(lightTheme ? "dark" : "light");

        public void ApplyTheme(string theme)
        {
            bool systemLight = theme == "system" && SakuraWindow.SystemUsesLightTheme();
            lightTheme = theme == "light" || theme == "system" && systemLight;
            root.EnableInClassList("light", lightTheme); RefreshIconColors();
        }

        private void ResizeChat()
        {
            if (chatPanel != null) chatPanel.style.height = chatPanel.ClassListContains("minimized") ? 54 : chatExpandedHeight;
        }

        public void ToggleChrome()
        {
            if (CurrentMode == "Agent") return;
            ControlsHidden = !ControlsHidden;
            if (ControlsHidden) PanelsHidden?.Invoke();
            root.EnableInClassList("controls-hidden", ControlsHidden);
        }

        private void BuildFooter()
        {
            footer.Clear();
            status = Label(CurrentMode == "VTuber" ? "AI paused · choose your camera and start face, shoulder and arm tracking" : "Talk to Sakura…", "status", footer);
            if (CurrentMode != "Companion" && !vtuberEdition) Button("Companion mode", () => SwitchMode("Companion"), footer);
            if (CurrentMode != "VTuber") Button("VTuber mode", () => SwitchMode("VTuber"), footer);
            if (CurrentMode != "Agent" && !vtuberEdition) Button("Agent mode", () => SwitchMode("Agent"), footer);
            if (CurrentMode == "VTuber")
            {
                Button("Start tracking", () => tracking.StartTracking(), footer);
                Button("Stop", tracking.StopTracking, footer);
                Button("Calibrate", () => tracking.Calibrate(), footer);
                handsButton = Button(tracking.Hands ? "Hands on" : "Hands off", () => {
                    tracking.SetHandsTracking(!tracking.Hands);
                    handsButton.text = tracking.Hands ? "Hands on" : "Hands off";
                }, footer);
                handsButton.name = "footer-hands-toggle";
                Button("New instance", () => SetStatus(SakuraInstances.Open()), footer);
            }
        }

        private void RefreshIconColors()
        {
            foreach (var item in navigation)
            {
                var icon = item.Value.Q<Image>();
                ColorUtility.TryParseHtmlString(item.Value.ClassListContains("selected") ? (lightTheme ? "#a13464" : "#ff9ec8") : (lightTheme ? "#785f70" : "#ccb6c8"), out var color);
                if (icon != null) icon.tintColor = color;
            }
        }

        public void ShowPanel(string name)
        {
            CurrentPanel = name;
            panel.RemoveFromClassList("panel-closed");
            bool modelSettings = name == "Model Settings" || name == "Expressions & hotkeys";
            heading.text = modelSettings ? "Model Settings" : name == "Character" ? "Models" : name == "Camera" ? "Camera & tracking" : name == "Background" ? "Background & stage items" : name;
            panelEdition.style.display = modelSettings || name == "Settings" ? DisplayStyle.None : DisplayStyle.Flex;
            panel.EnableInClassList("model-settings-open",modelSettings);
            panelBody.Clear();
            foreach (var pair in navigation) pair.Value.EnableInClassList("selected", pair.Key == name);
            RefreshIconColors();
            if (name == "Character") BuildCatalog();
            else if (name == "Settings") BuildSettings();
            else if (name == "Expressions & hotkeys") new SakuraModelPanel(viewport, backend, panelBody, "Expressions & hotkeys");
            else if (name == "Camera" || name == "Sakura Mobile")
            {
                new SakuraTrackingPanel(tracking, panelBody, name == "Sakura Mobile");
                if (name == "Camera") GetComponent<SakuraVmc>().BuildPanel(panelBody);
                if (name == "Camera" && CurrentMode == "Companion") SakuraLiveChat.BuildPanel(liveChat, panelBody, false);
                if (name == "Sakura Mobile") tracking.PhoneLink();
            }
            else if (name == "Streaming & output")
            {
                new SakuraOutputPanel(output, panelBody);
                if (CurrentMode == "VTuber") SakuraLiveChat.BuildPanel(liveChat, panelBody, true);
            }
            else if (name == "Background") new SakuraStagePanel(backend, viewport, panelBody, propsLibrary);
            else if (name == "Collab") GetComponent<SakuraNativeCollab>().BuildPanel(panelBody);
            else if (name == "Memory & privacy") BuildMemoryPrivacy();
            else if (name == "Voice & speech")
            {
                new SakuraSettingsPanel(backend, panelBody, ApplyTheme, "Voice", false);
                var inputCard = Element("card", panelBody); Label("Microphone", "card-title", inputCard);
                Button(voice.Listening ? "Microphone ON · turn off" : "Microphone OFF · turn on", () => { if (voice.Listening) voice.StopListening(); else voice.StartListening(); ShowPanel(name); }, inputCard);
                var devices = Microphone.devices.ToList();
                if (devices.Count > 0)
                {
                    devices.Insert(0, "Default microphone");
                    var device = new DropdownField("Microphone", devices, Math.Max(0, devices.IndexOf(voice.MicrophoneDevice)));
                    device.RegisterValueChangedCallback(evt => { voice.StopListening(); voice.MicrophoneDevice = device.index == 0 ? "" : device.value; }); inputCard.Add(device);
                }
                Label("Speech recognition uses your saved language and recognised-voice settings. Configure the optional offline wake pack under Settings → General → Hands-free listening.", "description", inputCard);
            }
            else if (name == "Model Settings")
            {
                new SakuraModelPanel(viewport,backend,panelBody);
            }
            else if (name == "Plugins")
            {
                (GetComponent<SakuraPluginBridge>() ?? gameObject.AddComponent<SakuraPluginBridge>()).BuildPanel(panelBody);
                SakuraCubismSync.BuildPanel(backend, viewport, panelBody);
                GetComponent<SakuraExportWatch>().BuildPanel(panelBody);
            }
            else
            {
                Label(name, "card-title", panelBody);
                Label("This feature is available in the original Sakura app. Its native Unity implementation is pending.", "description", panelBody);
                Label("Migration preserves the existing source and personal assets until full verification and your approval.", "description", panelBody);
            }
        }

        private void ClosePanel()
        {
            PanelsHidden?.Invoke();
            panel.AddToClassList("panel-closed");
            foreach (var button in navigation.Values) button.RemoveFromClassList("selected");
            RefreshIconColors();
        }

        private void OpenPmxTool(string tool)
        {
            if (viewport.LoadedFormat != "PMX") { SetStatus("Select a PMX model first."); return; }
            backend.RunPmxTool(tool, viewport.LoadedModelPath, (json, error) => SetStatus(error ?? "Selected model sent to " + (tool == "mmd" ? "MikuMikuDance." : "PMX Editor.")));
        }

        private void RefreshMemory()
        {
            backend.Api("/api/unity/start-services", "{}", (_, error) =>
            {
                if (error != null) { SetStatus(error); return; }
                backend.Api("/api/refresh-memory", "{}", (json, failure) => SetStatus(failure ?? "Memory refreshed."));
            });
        }

        private void ConfirmClearMemory()
        {
            root.Q("confirm-clear-memory")?.RemoveFromHierarchy();
            var overlay = Element("picker-overlay", root); overlay.name = "confirm-clear-memory";
            var card = Element("confirmation-card", overlay);
            Label("Clear memory?", "panel-title", card);
            Label("This clears saved conversation memory on this computer. Imported models, voices and artwork are kept.", "description", card);
            var actions = Element("row", card);
            Button("Cancel", () => overlay.RemoveFromHierarchy(), actions);
            var confirm = Button("Clear memory", () => {}, actions);
            confirm.clicked += () =>
            {
                confirm.SetEnabled(false); voice.StopSpeaking();
                backend.Api("/api/unity/start-services", "{}", (_, error) =>
                {
                    if (error != null) { SetStatus(error); confirm.SetEnabled(true); return; }
                    backend.Api("/api/clear", "{}", (json, failure) =>
                    {
                        if (failure != null) { SetStatus(failure); confirm.SetEnabled(true); return; }
                        messages.Clear(); overlay.RemoveFromHierarchy(); ReceiveReply("Memory cleared. What would you like to talk about next?");
                    });
                });
            };
        }

        private void BuildMemoryPrivacy()
        {
            var card = Element("card", panelBody);
            Label("Memory & privacy", "card-title", card);
            Button("Clear memory", ConfirmClearMemory, card);
            Label("Clearing memory forgets saved conversations on this computer. Imported models, voices and artwork are kept.", "description", card);
            card = Element("card", panelBody);
            Label("Movement & dance", "card-title", card);
            var motions = new DropdownField("Motion", new List<string> { "groove", "step", "wave", "twist", "disco", "march" }, 0); card.Add(motions);
            var actions = Element("row", card);
            Button("Dance", () =>
            {
                avatarActions.TrackingOwnsMotion = tracking.Source != SakuraTracking.InputSource.Off;
                if (!avatarActions.Gesture(motions.value)) SetStatus("Choose a rigged character and stop camera tracking first.");
            }, actions);
            Button("Stop dance", avatarActions.Stop, actions);
            card = Element("card", panelBody);
            (GetComponent<SakuraScreenFeatures>() ?? gameObject.AddComponent<SakuraScreenFeatures>()).BuildPanel(backend, card);
            card = Element("card", panelBody);
            Label("Stage & extras", "card-title", card);
            var stages = Element("row", card);
            Button("3D stages", () => ShowStageLibrary(false), stages);
            Button("Tools & Props", () => ShowStageLibrary(true), stages);
            Label("Open the background panel to add scenery and items alongside your character.", "description", card);
        }

        private void ShowStageLibrary(bool props)
        {
            propsLibrary = props;
            ShowPanel("Background");
        }

        private void Update()
        {
            if (avatarActions != null) avatarActions.TrackingOwnsMotion = tracking != null && tracking.Source != SakuraTracking.InputSource.Off;
            if (handsButton != null && tracking != null) handsButton.text = tracking.Hands ? "Hands on" : "Hands off";
            var screen = GetComponent<SakuraScreenFeatures>();
            if (captureStatus != null && screen != null) captureStatus.text = screen.Status;
            if (positionLockButton != null && viewport != null)
            {
                positionLockButton.EnableInClassList("selected", viewport.PositionLocked);
                positionLockCaption.text = viewport.PositionLocked ? "Unlock model" : "Lock model";
            }
        }

        private void BuildSettings()
        {
            new SakuraSettingsPanel(backend, panelBody, ApplyTheme);
            Button("Replay quick setup", () => SakuraSetupGuide.Attach(this, root).Open(), panelBody);
        }

        private void BuildCatalog()
        {
            var views = Element("row", panelBody); views.AddToClassList("model-view-tabs");
            var libraryTab = Button("Character library", () => { catalogView = "Library"; ShowPanel("Character"); }, views);
            libraryTab.name = "models-library-tab"; libraryTab.EnableInClassList("selected", catalogView == "Library");
            var converterTab = Button("Converter", () => { catalogView = "Converter"; ShowPanel("Character"); }, views);
            converterTab.name = "models-converter-tab"; converterTab.EnableInClassList("selected", catalogView == "Converter");
            if (catalogView == "Converter")
            {
                new SakuraModelConverterPanel(backend, catalog, viewport.LoadedModelPath, panelBody, backend.RefreshCatalog);
                return;
            }
            var card = Element("card", panelBody);
            Label("Your character library", "card-title", card);
            var migration = new Foldout { text = catalog.Length == 0 ? "Bring my existing Sakura library" : "Import a previous Sakura library", value = catalog.Length == 0 };
            card.Add(migration);
            SakuraLibraryMigrationPanel.Build(migration, backend);
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
                var loaded = catalog.FirstOrDefault(entry => entry.path == viewport.LoadedModelPath && entry.format == kind);
                Label(loaded == null ? type : type + " · " + loaded.name, "model-select-label", select); Label("▾", "model-select-arrow", select);
            }
            query.RegisterValueChangedCallback(args => { search = args.newValue; populateSearch(); });
            Label("Select a character above, or import a complete model package below.", "description", card);
            var imports = Element("card", panelBody);
            Label("Import a character", "card-title", imports);
            var uploadButtons = Element("row", imports);
            foreach (string type in new[] { "PMX", "VRM", "Live2D" })
            { string kind = type; Button("Upload " + type, () => backend.ImportModel(kind), uploadButtons); }
            var refresh = Button("↻", backend.RefreshCatalog, imports);
            refresh.tooltip = "Refresh imported models"; refresh.style.width = 42; refresh.style.alignSelf = Align.FlexStart; refresh.style.marginBottom = 8;
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
            size.SetValueWithoutNotify(viewport.ZoomPercent); sizeValue.text = Mathf.RoundToInt(viewport.ZoomPercent) + "%";
            size.RegisterValueChangedCallback(args => { viewport.SetZoomPercent(args.newValue); sizeValue.text = ((int)args.newValue) + "%"; });
            var actions = Element("row", framing);
            Button("Full model", () => viewport.ResetFraming(), actions);
            var quality = Button(output == null ? "Balanced" : output.Preset.ToString(), () => {}, actions);
            quality.clicked += () =>
            {
                if (output == null) return;
                output.SetQuality((SakuraOutput.Quality)(((int)output.Preset + 1) % 3)); quality.text = output.Preset.ToString();
            };
            var eyes = Button(tracking != null && tracking.Source == SakuraTracking.InputSource.Cursor ? "Eyes on" : "Eyes off", () => {}, actions);
            eyes.clicked += () =>
            {
                if (tracking == null) return;
                tracking.SetSource(tracking.Source == SakuraTracking.InputSource.Cursor ? SakuraTracking.InputSource.Off : SakuraTracking.InputSource.Cursor);
                eyes.text = tracking.Source == SakuraTracking.InputSource.Cursor ? "Eyes on" : "Eyes off";
            };
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
            var previews = SakuraLocalPreviews.Attach(backend.SourceFolder, overlay);
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
                    previews.Bind(button, entry.path);
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

        private void ReceiveCatalog(CharacterEntry[] entries)
        {
            catalog = entries;
            if (CurrentPanel == "Character") ShowPanel("Character");
            if (!initialCatalog) return;
            initialCatalog = false;
            backend.Api("/api/settings",null,(json,error) =>
            {
                if (this == null || !isActiveAndEnabled || error != null) return;
                try
                {
                    var settings = Newtonsoft.Json.Linq.JObject.Parse(json);
                    ApplyTheme((string)settings["ui_theme"] ?? "system");
                    if (SakuraInstances.IsSecondary) return;
                    if (viewport.Loading || viewport.HasAvatar) return;
                    string preferred = PlayerPrefs.GetString("sakura-selected-model","");
                    string original = (string)settings["selected_model"] ?? "";
                    original = original.Replace('\\','/');
                    if (original.StartsWith("models/",StringComparison.OrdinalIgnoreCase)) original = original.Substring(7);
                    string chosen = catalog.Any(item => item.path == preferred) ? preferred : original;
                    if (catalog.Any(item => item.path == chosen)) _ = viewport.LoadModel(backend.SourceFolder,chosen);
                }
                catch (Newtonsoft.Json.JsonException) { SetStatus("Choose your character from Models."); }
            });
        }
        private void ReceiveModelChanged()
        {
            if (!SakuraInstances.IsSecondary) { PlayerPrefs.SetString("sakura-selected-model",viewport.LoadedModelPath); PlayerPrefs.Save(); }
            RefreshModelPhysics();
            if (CurrentPanel == "Character" || CurrentPanel == "Model Settings" || CurrentPanel == "Expressions & hotkeys") ShowPanel(CurrentPanel);
        }
        public void RefreshModelPhysics()
        {
            var model = viewport.AvatarRoot;
            if (model == null) return;
            backend.Api("/api/settings", null, (json, error) => {
                if (error != null || model == null || viewport.AvatarRoot != model) return;
                try {
                    bool enabled = Newtonsoft.Json.Linq.JObject.Parse(json).Value<bool?>("physics_enabled") ?? true;
                    model.GetComponent<SakuraPmxPhysics>()?.SetSimulation(enabled);
                    model.GetComponent<SakuraModelTools>()?.SetPhysicsEnabled(enabled);
                } catch (Newtonsoft.Json.JsonException failure) { SetStatus("Could not read physics settings: " + failure.Message); }
            });
        }
        private void ReceiveImportedModel(string path) { _ = viewport.LoadModel(backend.SourceFolder, path); }
        private void SetStatus(string text) { if (status != null) status.text = text; }
        private void ReceiveReply(string text) { Label(text, "message", messages.contentContainer); SetStatus(text); messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0, float.MaxValue)); }
        private void ReceiveTranscript(string text) { Label(text, "user-message", messages.contentContainer); }
        private void ApplyAvatarReply(Newtonsoft.Json.Linq.JObject reply)
        {
            if (CurrentMode == "Companion" && avatarActions != null)
            {
                int revision = ++replyVisualRevision;
                string taskId = (string)reply["agent_task"]?["id"];
                if (SakuraAgentPanel.ValidId(taskId))
                {
                    var taskControls = Element("row", messages.contentContainer);
                    Button("Open task in Agent", () => { SwitchMode("Agent"); agentPanel?.OpenTask(taskId); }, taskControls);
                    messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0, float.MaxValue));
                }
                var picker = reply["avatar_picker"];
                if (picker?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && picker.ToObject<bool>()
                    || picker?.Type == Newtonsoft.Json.Linq.JTokenType.String && !string.IsNullOrWhiteSpace((string)picker))
                {
                    search = reply.Value<string>("avatar_query") ?? (reply["avatar_picker"].Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)reply["avatar_picker"] : "");
                    ShowPanel("Character");
                    if (reply.Value<bool?>("avatar_cloud") == true)
                    {
                        var host = Element("cloud-library", panelBody); host.name = "cloud-library";
                        new SakuraCloudLibrary(backend, host, format);
                        panelBody.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(host);
                    }
                }
                if (reply["avatar_expression"] is Newtonsoft.Json.Linq.JObject expression
                    && viewport.AvatarRoot?.GetComponent<SakuraAvatarExpressions>()?.ApplyByLabel(expression) != true)
                    SetStatus("That expression is unavailable or has an ambiguous name. Choose one in Expressions & hotkeys.");
                if (reply["avatar_expression"] == null && viewport.AvatarRoot != null)
                {
                    var model = viewport.AvatarRoot;
                    backend.Api("/api/settings", null, (json, error) =>
                    {
                        if (error != null || this == null || CurrentMode != "Companion" || revision != replyVisualRevision || model != viewport.AvatarRoot) return;
                        var settings = Newtonsoft.Json.Linq.JObject.Parse(json);
                        string choice = settings.Value<string>("avatar_emotion") ?? "auto";
                        model.GetComponent<SakuraAvatarExpressions>()?.SetReplyMood(choice == "auto" ? reply.Value<string>("emotion") : choice);
                    });
                }
                avatarActions.TrackingOwnsMotion = tracking != null && tracking.Source != SakuraTracking.InputSource.Off;
                avatarActions.Dispatch(reply);
            }
        }
        private void UpdateMicrophone(bool enabled)
        {
            foreach (var button in new[] { microphoneButton, compactMicrophone })
            {
                if (button == null) continue;
                button.text = button == compactMicrophone ? enabled ? "On" : "Mic" : enabled ? "●" : "♩";
                button.tooltip = enabled ? "Microphone ON · click to turn off" : "Microphone OFF · click to listen";
                button.style.borderTopColor = button.style.borderBottomColor = button.style.borderLeftColor = button.style.borderRightColor = enabled ? new Color(1, .25f, .4f) : StyleKeyword.Null;
                button.EnableInClassList("microphone-active", enabled);
            }
        }
        private static VisualElement Element(string css, VisualElement parent) { var element = new VisualElement(); element.AddToClassList(css); parent.Add(element); return element; }
        private static Label Label(string text, string css, VisualElement parent) { var label = new Label(text); label.AddToClassList(css); if (css == "panel-title" || css == "brand") label.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/BricolageGrotesque")); parent.Add(label); return label; }
        private static Button Button(string text, Action action, VisualElement parent) { var button = new Button(action) { text = text }; button.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans-Semibold")); button.style.unityFontStyleAndWeight = FontStyle.Normal; parent.Add(button); return button; }

        private void OnDisable()
        {
            if (nativeWindow != null) nativeWindow.Changed -= UpdateWindowTransparency;
            PanelsHidden?.Invoke();
            if (backend == null) return;
            if (viewport != null) viewport.ModelChanged -= ReceiveModelChanged;
            backend.CatalogReceived -= ReceiveCatalog;
            backend.StatusChanged -= SetStatus;
            backend.ReplyReceived -= ReceiveReply;
            backend.ModelImported -= ReceiveImportedModel;
            backend.ReplyDataReceived -= ApplyAvatarReply;
            if (voice != null) { voice.TranscriptReceived -= ReceiveTranscript; voice.StatusChanged -= SetStatus; voice.ListeningChanged -= UpdateMicrophone; }
        }
    }
}
