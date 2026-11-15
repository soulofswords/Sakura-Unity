using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraStudio : MonoBehaviour
    {
        public string CurrentMode { get; private set; } = "Companion";
        public string CurrentPanel { get; private set; } = "Character";
        public int CharacterCount => catalog.Length;
        public bool IsVtuberEdition => vtuberEdition;
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
        private Button panelClose;
        private Image closeIcon;
        private Image settingsGear;
        private VisualElement splash, splashFill, voiceModal;
        private Image splashLogo;
        private Label voiceMessage, voiceError;
        private Button voiceClose;
        private bool splashDismissed, voicePrepStarted;
        private IVisualElementScheduledItem splashAnimation, voicePoll;
        private Button microphoneButton;
        private Button compactMicrophone, transparencyButton;
        private Button positionLockButton, handsButton, hideControlsButton;
        private Label dockHoverLabel;
        private CharacterEntry[] catalog = Array.Empty<CharacterEntry>();
        private string format = "PMX", search = "";
        private bool lightTheme;
        private bool vtuberEdition;
        private bool initialCatalog = true;
        private bool propsLibrary;
        private float chatExpandedHeight = 520;
        private int replyVisualRevision;
        private int panelAnimation, chromeAnimation;
        private readonly Dictionary<string, Button> navigation = new Dictionary<string, Button>();
        private readonly Dictionary<Button, Texture2D[]> pictureIconVariants = new Dictionary<Button, Texture2D[]>();
        private readonly Dictionary<string, Label> catalogSelectionLabels = new Dictionary<string, Label>();
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
            (GetComponent<SakuraReminders>() ?? gameObject.AddComponent<SakuraReminders>()).Initialize(backend, root);
            tracking = GetComponent<SakuraTracking>() ?? gameObject.AddComponent<SakuraTracking>();
            tracking.Initialize(backend, viewport);
            tracking.PhoneModelRequested += OpenPhoneModelPicker;
            tracking.PhoneLink();
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
            // The standalone player starts with the same three-way chooser as the
            // existing desktop launcher. Verification builds skip it so automated
            // checks still enter the Studio directly.
            string[] arguments = Environment.GetCommandLineArgs();
            bool verification = arguments.Any(argument => argument == "--sakura-verify" || argument == "--sakura-verify-hold");
            if (!Application.isEditor && !verification)
                SakuraStartupChooser.Attach(this, backend, () => SakuraSetupGuide.Attach(this, root));
            else
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
            string startupModel = PlayerPrefs.GetString("sakura-selected-model", "");
            if (!SakuraInstances.IsSecondary && !string.IsNullOrWhiteSpace(startupModel)
                && System.IO.File.Exists(System.IO.Path.Combine(backend.SourceFolder, "models", startupModel)))
                _ = viewport.LoadModel(backend.SourceFolder, startupModel);
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
            settingsGear = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/settings"), pickingMode = PickingMode.Ignore };
            settingsGear.AddToClassList("panel-gear"); settingsGear.style.display = DisplayStyle.None; panelHeader.Add(settingsGear);
            var panelTitles = Element("panel-titles", panelHeader);
            panelEdition = Label("SAKURA COMPANION", "panel-edition", panelTitles);
            heading = Label("Models", "panel-title", panelTitles);
            panelClose = Button("", ClosePanel, panelHeader); panelClose.AddToClassList("close");
            closeIcon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/close-x"), pickingMode = PickingMode.Ignore }; closeIcon.AddToClassList("close-icon"); panelClose.Add(closeIcon);
            var scroll = new ScrollView(); scroll.AddToClassList("panel-scroll"); panel.Add(scroll);
            panelBody = scroll.contentContainer;
            panelBody.name = "companion-panel-body";
            SakuraPanelResize.Attach(panel);

            footer = Element("footer", root); footer.name = "companion-statusbar";
            BuildFooter();
            ShowPanel("Character");
            ClosePanel();
            root.RegisterCallback<GeometryChangedEvent>(args =>
            {
                root.EnableInClassList("compact", args.newRect.width < 760);
                root.EnableInClassList("short", args.newRect.height < 760);
                float available = Math.Max(100, args.newRect.height - 145);
                chatExpandedHeight = Math.Min(520, Math.Min(args.newRect.height * .5f, available));
                ResizeChat();
            });
            BuildVoiceModal();
            BuildSplash();
        }

        private static readonly Dictionary<string, string> DockInks = new Dictionary<string, string>
        {
            ["pink"] = "#b7688d", ["blue"] = "#3888b3", ["green"] = "#76a85c", ["indigo"] = "#6480b5",
            ["orange"] = "#b9894a", ["purple"] = "#8d6abb", ["slate"] = "#5a76a8", ["periwinkle"] = "#797cb0",
            ["lock"] = "#9c7a16", ["hide"] = "#61718c"
        };

        private void BuildDock()
        {
            positionLockButton = null; hideControlsButton = null;
            dock.Clear(); navigation.Clear(); pictureIconVariants.Clear();
            bool vtuber = CurrentMode == "VTuber";
            string[] keys = vtuber
                ? new[] { "Character", "Camera", "Expressions & hotkeys", "Background", "Streaming & output", "Plugins", "Sakura Mobile", "Settings", "Collab" }
                : new[] { "Character", "Camera", "Voice & speech", "Model Settings", "Background", "Streaming & output", "Sakura Mobile", "Memory & privacy", "Settings" };
            string[] labels = { "Models", "Camera &\ntracking", "Voice &\nspeech", "Model\nSettings", "Background\n& stage\nitems", "Streaming &\noutput", "Sakura\nMobile", "Memory &\nprivacy", "Settings" };
            string[] icons = { "character", "camera", "voice", "tools", "background", "output", "phone", "memory", "settings" };
            string[] tints = { "pink", "blue", "blue", "green", "green", "indigo", "orange", "purple", "pink" };
            if (vtuber)
            {
                labels = new[] { "Models", "Camera &\ntracking", "Expressions &\nhotkeys", "Background", "Streaming &\nvoice", "Plugins", "Sakura\nMobile", "Settings", "Collab" };
                icons = new[] { "glyph-character", "glyph-camera", "expressions", "glyph-background", "glyph-output", "plugins", "glyph-phone", "glyph-settings", "collab" };
                tints = new[] { "pink", "blue", "purple", "green", "indigo", "slate", "orange", "pink", "periwinkle" };
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
                bool picture = !vtuber;
                string iconName = (picture ? "dock-" : "") + icons[index];
                var baseIcon = Resources.Load<Texture2D>("Sakura/Icons/" + iconName);
                var selectedIcon = picture ? Resources.Load<Texture2D>("Sakura/Icons/" + iconName + "-selected") : null;
                var icon = new Image { image = baseIcon };
                if (picture) pictureIconVariants[button] = new[] { baseIcon, selectedIcon ?? baseIcon };
                icon.AddToClassList("dock-icon"); if (picture) icon.AddToClassList("dock-picture-icon"); button.Add(icon);
                button.AddToClassList("dock-button");
                if (picture) { button.AddToClassList("dock-picture"); button.userData = "picture"; }
                else { button.AddToClassList("dock-tint-" + tints[index]); button.userData = tints[index]; }
                RegisterDockHover(button);
                navigation[key] = button;
            }
            if (vtuber)
            {
                var modelLock = Button("", () => {}, scroll.contentContainer); modelLock.name = "model-position-lock";
                modelLock.AddToClassList("dock-button"); modelLock.AddToClassList("dock-tint-lock"); modelLock.AddToClassList("dock-lock");
                modelLock.userData = "lock";
                var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/lock") }; icon.AddToClassList("dock-icon"); modelLock.Add(icon);
                modelLock.tooltip = "Lock model position";
                RegisterDockHover(modelLock);
                positionLockButton = modelLock;
                modelLock.clicked += () =>
                {
                    viewport.PositionLocked = !viewport.PositionLocked;
                    modelLock.EnableInClassList("selected", viewport.PositionLocked);
                    modelLock.tooltip = viewport.PositionLocked ? "Unlock model position" : "Lock model position";
                    RefreshIconColors();
                };
                var hide = Button("", ToggleChrome, dock); hide.AddToClassList("dock-button"); hide.AddToClassList("dock-tint-hide"); hide.AddToClassList("dock-hide"); hide.name = "hide-controls";
                hide.userData = "hide";
                var eye = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/hide") }; eye.AddToClassList("dock-icon"); hide.Add(eye);
                hide.tooltip = "Hide controls";
                RegisterDockHover(hide);
                hideControlsButton = hide;
            }
            dockHoverLabel = new Label { pickingMode = PickingMode.Ignore };
            dockHoverLabel.AddToClassList("dock-hover-label"); dock.Add(dockHoverLabel);
            RefreshIconColors();
        }

        private void RegisterDockHover(Button button)
        {
            button.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (dockHoverLabel == null) return;
                dockHoverLabel.text = button.tooltip;
                dockHoverLabel.style.top = button.worldBound.center.y - dock.worldBound.y;
                dockHoverLabel.style.display = DisplayStyle.Flex;
            });
            button.RegisterCallback<PointerLeaveEvent>(_ => { if (dockHoverLabel != null) dockHoverLabel.style.display = DisplayStyle.None; });
        }

        private void BuildChat(VisualElement workspace)
        {
            var chat = Element("chat", workspace);
            chatPanel = chat; chat.name = "chat-panel";
            var chatHeader = Element("chat-header", chat);
            var titleRow = Element("chat-title-row", chatHeader);
            var bubble = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/chat-bubble"), pickingMode = PickingMode.Ignore };
            bubble.AddToClassList("chat-title-icon"); titleRow.Add(bubble);
            Label("Chat with Sakura", "chat-title", titleRow);
            Element("spacer", chatHeader);
            var refresh = Button("", RefreshMemory, chatHeader); refresh.tooltip = "Refresh conversation memory";
            refresh.Add(new Image { image = Resources.Load<Texture2D>("Sakura/Icons/chat-refresh"), pickingMode = PickingMode.Ignore });
            var minimize = Button("—", () => { chat.EnableInClassList("minimized", !chat.ClassListContains("minimized")); ResizeChat(); }, chatHeader);
            minimize.name = "chat-minimize";
            var clear = Button("", ConfirmClearMemory, chatHeader); clear.tooltip = "Clear conversation memory";
            clear.Add(new Image { image = Resources.Load<Texture2D>("Sakura/Icons/chat-trash"), pickingMode = PickingMode.Ignore });
            captureStatus = Label("Screen off · Capture stopped", "capture-status", chat);
            messages = new ScrollView(); messages.AddToClassList("messages"); chat.Add(messages);
            var attachmentTray = Element("chat-attachment-tray", chat);
            attachmentTray.style.flexShrink = 0;
            attachmentTray.style.flexDirection = FlexDirection.Row;
            attachmentTray.style.flexWrap = Wrap.Wrap;
            void RenderAttachments()
            {
                attachmentTray.Clear();
                attachmentTray.style.display = backend.ChatAttachments.Count > 0 || backend.AttachmentBusy ? DisplayStyle.Flex : DisplayStyle.None;
                foreach (var attachment in backend.ChatAttachments)
                {
                    var item = attachment;
                    var chip = Button((string)item["name"] + " ×", () => backend.RemoveChatAttachment(item), attachmentTray);
                    chip.tooltip = "Remove attachment";
                }
                if (backend.AttachmentBusy) Label("Reading file…", "description", attachmentTray);
            }
            backend.AttachmentsChanged += RenderAttachments;
            attachmentTray.RegisterCallback<DetachFromPanelEvent>(_ => backend.AttachmentsChanged -= RenderAttachments);
            RenderAttachments();
            var composer = Element("composer", chat);
            var attach = Button("", backend.PickChatAttachment, composer);
            attach.name = "chat-attach"; attach.tooltip = "Attach up to three files, 10 MB each";
            attach.Add(new Image { image = Resources.Load<Texture2D>("Sakura/Icons/chat-attach"), pickingMode = PickingMode.Ignore });
            var input = new TextField { multiline = false }; input.AddToClassList("chat-input"); composer.Add(input);
            input.schedule.Execute(() => { if (input.panel != null) input.Focus(); }).StartingIn(700);
            microphoneButton = Button("", () => { if (voice.Listening) voice.StopListening(); else voice.StartListening(); }, composer);
            microphoneButton.name = "chat-microphone";
            microphoneButton.tooltip = "Microphone OFF · click to listen";
            microphoneButton.Add(new Image { image = Resources.Load<Texture2D>("Sakura/Icons/chat-mic"), pickingMode = PickingMode.Ignore });
            input.tooltip = "Type a message or use your mic…";
            input.textEdition.placeholder = "Type a message or use your mic…";
            // The field commits its text on Return, which can leave value empty on the
            // first press, so track edits separately and send that text if needed.
            string typed = null;
            void SendMessage()
            {
                string text = (input.value ?? string.Empty).Trim();
                if (text.Length == 0) text = (typed ?? string.Empty).Trim();
                if (text.Length == 0 || backend.Busy || backend.AttachmentBusy) return;
                Label(text, "user-message", messages.contentContainer);
                typed = null;
                backend.SendChat(text); input.value = "";
                input.schedule.Execute(() => { if (input.panel != null) input.Focus(); });
            }
            var send = Button("", SendMessage, composer);
            send.Add(new Image { image = Resources.Load<Texture2D>("Sakura/Icons/chat-send"), pickingMode = PickingMode.Ignore });
            input.RegisterValueChangedCallback(evt => typed = evt.newValue);
            input.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                SendMessage(); evt.StopPropagation();
            }, TrickleDown.TrickleDown);

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
            chromeAnimation++;
            root.RemoveFromClassList("controls-hidden");
            ResetChromeVisuals();
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
            AnimateChrome();
            root.EnableInClassList("panel-open", !ControlsHidden && panel != null && !panel.ClassListContains("panel-closed"));
        }

        private void ResetChromeVisuals()
        {
            foreach (var element in new[] { root.Q(className: "header"), dock, panel, footer, chatPanel })
            {
                if (element == null) continue;
                element.style.opacity = 1;
                element.style.translate = Translate.None();
            }
        }

        private void AnimateChrome()
        {
            bool show = !ControlsHidden;
            var items = new List<(VisualElement Element, float Dx, float Dy)>();
            void Add(VisualElement element, float dx, float dy)
            {
                if (element == null) return;
                if (!show && element.ClassListContains("panel-closed")) return;
                if (!show && element.style.display == DisplayStyle.None) return;
                items.Add((element, dx, dy));
            }
            Add(root.Q(className: "header"), 0, -12);
            Add(dock, -12, 0);
            Add(panel, 24, 0);
            Add(footer, 0, 12);
            Add(chatPanel, 0, 10);
            if (items.Count == 0) { root.EnableInClassList("controls-hidden", !show); return; }

            int revision = ++chromeAnimation;
            float started = Time.unscaledTime;
            if (show)
            {
                root.RemoveFromClassList("controls-hidden");
                foreach (var item in items) { item.Element.style.opacity = 0; item.Element.style.translate = new Translate(item.Dx, item.Dy); }
            }
            root.schedule.Execute(() =>
            {
                if (this == null || revision != chromeAnimation) return;
                float t = Mathf.Clamp01((Time.unscaledTime - started) / .18f);
                float p = t * t * (3 - 2 * t);
                foreach (var item in items)
                {
                    item.Element.style.opacity = show ? p : 1 - p;
                    item.Element.style.translate = new Translate(item.Dx * (1 - p), item.Dy * (1 - p));
                }
                if (t < 1) return;
                foreach (var item in items)
                {
                    item.Element.style.opacity = 1;
                    item.Element.style.translate = Translate.None();
                }
                if (!show) root.EnableInClassList("controls-hidden", true);
            }).Every(16);
        }

        private void BuildFooter()
        {
            footer.Clear();
            status = Label(CurrentMode == "VTuber" ? "AI paused · choose your camera and start face, shoulder and arm tracking" : "Talk to Sakura…", "status", footer);
            if (!vtuberEdition)
            {
                var companion = Button("Companion mode", () => SwitchMode("Companion"), footer); companion.style.display = CurrentMode == "Companion" ? DisplayStyle.None : DisplayStyle.Flex; companion.EnableInClassList("mode-active", CurrentMode == "Companion");
                var vtuber = Button("VTuber mode", () => SwitchMode("VTuber"), footer); vtuber.style.display = CurrentMode == "VTuber" ? DisplayStyle.None : DisplayStyle.Flex; vtuber.EnableInClassList("mode-active", CurrentMode == "VTuber");
                var agent = Button("Agent mode", () => SwitchMode("Agent"), footer); agent.style.display = CurrentMode == "Agent" ? DisplayStyle.None : DisplayStyle.Flex; agent.EnableInClassList("mode-active", CurrentMode == "Agent");
            }
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
            foreach (var item in navigation) TintDockIcon(item.Value);
            if (positionLockButton != null) TintDockIcon(positionLockButton);
            if (hideControlsButton != null) TintDockIcon(hideControlsButton);
        }

        private void TintDockIcon(Button button)
        {
            var icon = button.Q<Image>();
            if (icon == null) return;
            string tint = button.userData as string ?? "blue";
            if (tint == "picture")
            {
                if (pictureIconVariants.TryGetValue(button, out var variants))
                    icon.image = button.ClassListContains("selected") ? variants[1] : variants[0];
                icon.tintColor = Color.white; return;
            }
            string hex = button.ClassListContains("selected") ? "#168fd5" : DockInks.TryGetValue(tint, out string ink) ? ink : "#3184bb";
            ColorUtility.TryParseHtmlString(hex, out var color);
            icon.tintColor = color;
        }

        public void ShowPanel(string name)
        {
            bool wasClosed = panel.ClassListContains("panel-closed");
            CurrentPanel = name;
            panelAnimation++;
            panel.RemoveFromClassList("panel-closed");
            panel.style.opacity = 1;
            panel.style.translate = Translate.None();
            if (root != null) root.EnableInClassList("panel-open", true);
            bool modelSettings = name == "Model Settings" || name == "Expressions & hotkeys";
            heading.text = modelSettings ? "Model Settings" : name == "Character" ? "Models" : name == "Camera" ? "Camera & tracking" : name == "Background" ? "Background & stage items" : name;
            panelEdition.style.display = modelSettings || name == "Settings" ? DisplayStyle.None : DisplayStyle.Flex;
            bool settings = name == "Settings";
            bool closeAccent = settings || modelSettings || name == "Voice & speech";
            panelClose.EnableInClassList("close-accent", closeAccent);
            ColorUtility.TryParseHtmlString(closeAccent ? "#8fc3ff" : "#b5c1d0", out var closeTint);
            closeIcon.tintColor = closeTint;
            closeIcon.style.width = closeAccent ? 16 : 9;
            closeIcon.style.height = closeAccent ? 16 : 9;
            settingsGear.style.display = settings ? DisplayStyle.Flex : DisplayStyle.None;
            panel.EnableInClassList("model-settings-open",modelSettings);
            panelBody.Clear();
            foreach (var pair in navigation)
            {
                pair.Value.EnableInClassList("selected", pair.Key == name);
                pair.Value.EnableInClassList("selected-picture", name == "Settings" && pair.Key == name);
            }
            RefreshIconColors();
            if (name == "Character") BuildCatalog();
            else if (name == "Settings") BuildSettings();
            else if (name == "Expressions & hotkeys") new SakuraModelPanel(viewport, backend, panelBody, "Expressions & hotkeys");
            else if (name == "Camera" || name == "Sakura Mobile")
            {
                if (name == "Camera" && CurrentMode == "Companion") new SakuraCompanionCameraPanel(tracking, backend, panelBody);
                else new SakuraTrackingPanel(tracking, panelBody, name == "Sakura Mobile");
                if (name == "Camera") GetComponent<SakuraVmc>().BuildPanel(panelBody);
                if (name == "Camera" && CurrentMode == "Companion") SakuraLiveChat.BuildPanel(liveChat, panelBody, false);
                if (name == "Sakura Mobile") tracking.PhoneLink();
            }
            else if (name == "Streaming & output")
            {
                new SakuraOutputPanel(output, panelBody);
                if (CurrentMode == "VTuber")
                {
                    SakuraLiveChat.BuildPanel(liveChat, panelBody, true);
                    (GetComponent<SakuraVoiceChanger>() ?? gameObject.AddComponent<SakuraVoiceChanger>()).BuildPanel(panelBody);
                }
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
                new SakuraPerformancePanel(backend, viewport, voice, panelBody);
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
            if (wasClosed) AnimatePanelIn();
        }

        private void AnimatePanelIn()
        {
            int revision = ++panelAnimation;
            float started = Time.unscaledTime;
            panel.style.opacity = 0;
            panel.style.translate = new Translate(new Length(26, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel));
            panel.schedule.Execute(() =>
            {
                if (this == null || revision != panelAnimation || panel == null) return;
                float t = Mathf.Clamp01((Time.unscaledTime - started) / .18f);
                float p = 1 - Mathf.Pow(1 - t, 3);
                panel.style.opacity = p;
                panel.style.translate = new Translate(new Length(26 * (1 - p), LengthUnit.Pixel), new Length(0, LengthUnit.Pixel));
                if (t < 1) return;
                panel.style.opacity = 1;
                panel.style.translate = Translate.None();
            }).Every(16);
        }

        private void ClosePanel()
        {
            if (panel.ClassListContains("panel-closed")) return;
            PanelsHidden?.Invoke();
            foreach (var button in navigation.Values) { button.RemoveFromClassList("selected"); button.RemoveFromClassList("selected-picture"); }
            RefreshIconColors();
            int revision = ++panelAnimation;
            float started = Time.unscaledTime;
            panel.style.opacity = 1;
            panel.style.translate = new Translate(new Length(0, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel));
            panel.schedule.Execute(() =>
            {
                if (this == null || revision != panelAnimation || panel == null) return;
                float t = Mathf.Clamp01((Time.unscaledTime - started) / .15f);
                float p = t * t * (3 - 2 * t);
                panel.style.opacity = 1 - p;
                panel.style.translate = new Translate(new Length(24 * p, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel));
                if (t < 1) return;
                panel.AddToClassList("panel-closed");
                panel.style.opacity = 1;
                panel.style.translate = Translate.None();
                if (root != null) root.EnableInClassList("panel-open", false);
            }).Every(16);
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
            Button("Image & Live2D items", () => ShowPanel("Background"), stages);
            Label("Open the background panel to add scenery and items alongside your character.", "description", card);
        }

        private void ShowStageLibrary(bool props)
        {
            propsLibrary = props;
            ShowPanel("Background");
        }

        private void Update()
        {
            if (!splashDismissed && backend != null && backend.Connected && !initialCatalog) DismissSplash();
            if (!voicePrepStarted && backend != null && backend.Connected && !vtuberEdition && !SakuraInstances.IsSecondary) { voicePrepStarted = true; StartVoicePrep(); }
            SakuraBrowserPreview.UpdateAll();
            if (avatarActions != null) avatarActions.TrackingOwnsMotion = tracking != null && tracking.Source != SakuraTracking.InputSource.Off;
            if (handsButton != null && tracking != null) handsButton.text = tracking.Hands ? "Hands on" : "Hands off";
            var screen = GetComponent<SakuraScreenFeatures>();
            if (captureStatus != null && screen != null) captureStatus.text = screen.Status;
            if (positionLockButton != null && viewport != null)
            {
                positionLockButton.EnableInClassList("selected", viewport.PositionLocked);
                positionLockButton.tooltip = viewport.PositionLocked ? "Unlock model position" : "Lock model position";
            }
        }

        private void BuildSettings()
        {
            new SakuraSettingsPanel(backend, panelBody, ApplyTheme, "General", true, modelsHost =>
            {
                var converter = new Foldout { text = "VRM ↔ PMX Converter", value = false };
                modelsHost.Add(converter);
                new SakuraModelConverterPanel(backend, catalog, viewport.LoadedModelPath, converter, backend.RefreshCatalog);
            });
            Button("Replay quick setup", () => SakuraSetupGuide.Attach(this, root).Open(), panelBody);
        }

        private void BuildSplash()
        {
            splash = Element("splash", root); splash.name = "startup-splash";
            splashLogo = new Image { image = Resources.Load<Texture2D>(vtuberEdition ? "Sakura/VTuberLogo" : "Sakura/CompanionLogo"), pickingMode = PickingMode.Ignore };
            splashLogo.AddToClassList("splash-logo"); splash.Add(splashLogo);
            Label(vtuberEdition ? "Sakura Studio" : "Sakura Companion", "splash-title", splash);
            var track = Element("splash-track", splash);
            splashFill = Element("splash-track-fill", track);
            Label("Preparing your workspace…", "splash-label", splash);
            splashAnimation = splash.schedule.Execute(() =>
            {
                float t = Time.realtimeSinceStartup;
                float wave = Mathf.Sin(t * (2 * Mathf.PI / 1.8f));
                splashLogo.style.opacity = 0.8f + 0.2f * wave;
                float s = 0.98f + 0.02f * wave;
                splashLogo.style.scale = new Scale(new Vector3(s, s, 1));
                float p = (t % 1.2f) / 1.2f;
                splashFill.style.translate = new Translate(Mathf.Lerp(-126, 415, p), 0);
            }).Every(40);
        }

        private void DismissSplash()
        {
            splashDismissed = true;
            splashAnimation?.Pause();
            splash?.RemoveFromHierarchy();
            splash = null;
        }

        private void BuildVoiceModal()
        {
            voiceModal = Element("voice-modal", root); voiceModal.name = "kokoro-loading-modal";
            voiceModal.style.display = DisplayStyle.None;
            var card = Element("voice-card", voiceModal);
            var header = Element("voice-card-header", card);
            Label("Preparing local voices", "voice-card-title", header);
            var body = Element("voice-card-body", card);
            voiceMessage = Label("Loading Kokoro speech…", "voice-message", body);
            var bar = Element("voice-bar", body); Element("voice-bar-fill", bar);
            Label("Speech runs on your PC. If files are missing, the first setup downloads about 121 MB. No API key is needed.", "voice-hint", body);
            voiceError = Label("", "voice-error", body);
            voiceClose = Button("Close", () => { voicePoll?.Pause(); voiceModal.style.display = DisplayStyle.None; }, body);
            voiceClose.AddToClassList("voice-close");
        }

        private void StartVoicePrep()
        {
            backend.Api("/api/unity/start-services", "{}", (_, __) => { });
            backend.Api("/api/tts/kokoro", null, (json, error) =>
            {
                if (this == null || !isActiveAndEnabled || error != null) return;
                var state = JObject.Parse(json);
                if ((string)state["phase"] == "ready") return;
                voiceModal.style.display = DisplayStyle.Flex;
                voiceClose.style.display = DisplayStyle.None;
                voiceError.text = "";
                voiceMessage.text = (string)state["message"] ?? "Loading local Kokoro voices…";
                voicePoll = root.schedule.Execute(PollVoiceStatus).Every(500);
                backend.Api("/api/tts/kokoro", "{}", (postJson, postError) =>
                {
                    if (this == null || !isActiveAndEnabled) return;
                    voicePoll?.Pause();
                    if (postError != null) { ShowVoiceError(postError); return; }
                    var result = JObject.Parse(postJson);
                    if ((string)result["phase"] == "ready") voiceModal.style.display = DisplayStyle.None;
                    else ShowVoiceError((string)result["error"] ?? (string)result["message"] ?? "Kokoro could not load.");
                });
            });
        }

        private void PollVoiceStatus()
        {
            if (this == null || !isActiveAndEnabled || voiceModal == null || voiceModal.style.display == DisplayStyle.None) return;
            backend.Api("/api/tts/kokoro", null, (json, error) =>
            {
                if (this == null || !isActiveAndEnabled || error != null || voiceModal.style.display == DisplayStyle.None) return;
                var state = JObject.Parse(json);
                string message = (string)state["message"] ?? "";
                if ((string)state["phase"] == "downloading") message += " " + (state.Value<int?>("progress") ?? 0) + "%";
                voiceMessage.text = message;
                if ((string)state["phase"] == "ready") { voicePoll?.Pause(); voiceModal.style.display = DisplayStyle.None; }
            });
        }

        private void ShowVoiceError(string message)
        {
            if (voiceModal == null) return;
            voiceModal.style.display = DisplayStyle.Flex;
            voiceError.text = message;
            voiceClose.style.display = DisplayStyle.Flex;
        }

        private void BuildCatalog()
        {
            catalogSelectionLabels.Clear();
            var card = Element("card", panelBody);
            Label("Your character library", "card-title", card);
            if (catalog.Length == 0)
            {
                var migration = new Foldout { text = "Bring my existing Sakura library", value = true };
                card.Add(migration);
                SakuraLibraryMigrationPanel.Build(migration, backend);
            }
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
                        if (entry.path == viewport.LoadedModelPath) return;
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
                catalogSelectionLabels[kind] = Label(loaded == null ? type : type + " · " + loaded.name, "model-select-label", select); Label("▾", "model-select-arrow", select);
            }
            query.RegisterValueChangedCallback(args => { search = args.newValue; populateSearch(); });
            Label("Select a character above, or import a complete model package below.", "description", card);
            var imports = Element("card", panelBody);
            Label("Import a character", "card-title", imports);
            var uploadButtons = Element("row", imports);
            foreach (string type in new[] { "PMX", "VRM", "Live2D" })
            { string kind = type; Button("Upload " + type, () => backend.ImportModel(kind), uploadButtons); }
            var refresh = Button("", backend.RefreshCatalog, imports);
            refresh.tooltip = "Refresh imported models"; refresh.style.width = 42; refresh.style.alignSelf = Align.FlexStart; refresh.style.marginBottom = 8;
            var refreshIcon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/refresh-blue"), pickingMode = PickingMode.Ignore };
            refreshIcon.style.width = 18; refreshIcon.style.height = 18; refresh.Add(refreshIcon);
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
            var sizeRow = Element("size-row", framing); Label("Size", "size-label", sizeRow);
            var size = new SakuraSlider(40, 180) { value = 100, name = "character-zoom-slider" };
            size.AddToClassList("character-zoom-slider");
            size.tooltip = "Drag to zoom the character. Double-click for 100%. Scrolling over the character also zooms.";
            sizeRow.Add(size);
            var sizeValue = Label("100%", "size-value", sizeRow);
            size.SetValueWithoutNotify(viewport.ZoomPercent); sizeValue.text = Mathf.RoundToInt(viewport.ZoomPercent) + "%";
            size.RegisterValueChangedCallback(args =>
            {
                viewport.SetZoomPercent(args.newValue);
                sizeValue.text = Mathf.RoundToInt(viewport.ZoomPercent) + "%";
            });
            size.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount != 2) return;
                viewport.SetZoomPercent(100);
                size.SetValueWithoutNotify(100);
                sizeValue.text = "100%";
                evt.StopPropagation();
            });
            // Wheel zoom and "Full model" can change the camera without touching this slider.
            // Keep the thumb and percentage honest instead of letting them drift out of date.
            size.schedule.Execute(() =>
            {
                if (size.panel == null) return;
                float percent = Mathf.Round(viewport.ZoomPercent);
                if (Mathf.Abs(size.value - percent) < .5f) return;
                size.SetValueWithoutNotify(percent);
                sizeValue.text = Mathf.RoundToInt(percent) + "%";
            }).Every(120);
            var actions = Element("row", framing);
            Button("Full model", () => viewport.ResetFraming(), actions);
            var quality = Button(output == null ? "Balanced" : output.Preset.ToString(), () => {}, actions);
            quality.clicked += () =>
            {
                if (output == null) return;
                output.SetQuality((SakuraOutput.Quality)(((int)output.Preset + 1) % 3)); quality.text = output.Preset.ToString();
            };
            var eyes = Button("", () => {}, actions); eyes.AddToClassList("icon-text-button");
            var eyesIcon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/eye"), pickingMode = PickingMode.Ignore };
            eyesIcon.AddToClassList("button-icon"); eyes.Add(eyesIcon);
            var eyesText = new Label(tracking != null && tracking.Source == SakuraTracking.InputSource.Cursor ? "Eyes on" : "Eyes off"); eyes.Add(eyesText);
            eyes.clicked += () =>
            {
                if (tracking == null) return;
                tracking.SetSource(tracking.Source == SakuraTracking.InputSource.Cursor ? SakuraTracking.InputSource.Off : SakuraTracking.InputSource.Cursor);
                eyesText.text = tracking.Source == SakuraTracking.InputSource.Cursor ? "Eyes on" : "Eyes off";
            };
            Button("PMX Editor", () => OpenPmxTool("editor"), actions);
            Button("MikuMikuDance", () => OpenPmxTool("mmd"), actions);
            populateSearch();
        }

        private void OpenPhoneModelPicker(string requestId) => ShowLocalPicker("PMX", requestId);

        private void ShowLocalPicker(string kind, string phoneRequestId = null)
        {
            format = kind;
            var existing = root.Q("local-picker"); existing?.RemoveFromHierarchy();
            var overlay = Element("picker-overlay", root); overlay.name = "local-picker";
            var previews = SakuraLocalPreviews.Attach(backend.SourceFolder, overlay);
            var window = Element("picker-window", overlay);
            var title = Element("row", window);
            Label(phoneRequestId == null ? kind + " characters" : "Choose a model for Sakura Mobile", "panel-title", title);
            Button("×", () => overlay.RemoveFromHierarchy(), title);
            if (phoneRequestId != null)
            {
                var formats = Element("row", window);
                foreach (string type in new[] { "PMX", "VRM", "Live2D" })
                { string selectedType = type; var tab = Button(type, () => ShowLocalPicker(selectedType, phoneRequestId), formats); if (selectedType == kind) tab.AddToClassList("selected"); }
                var cloud = Button("Cloud library", () =>
                {
                    window.Q("picker-find").style.display = DisplayStyle.None; window.Q("picker-breadcrumb").style.display = DisplayStyle.None; window.Q("picker-scroll").style.display = DisplayStyle.None;
                    var host = Element("phone-cloud", window);
                    host.Add(new Button(() => ShowLocalPicker(kind, phoneRequestId)) { text = "Back to PC library" });
                    new SakuraCloudLibrary(backend, host, kind.ToLowerInvariant(), path => tracking.StageModelForPhone(path, phoneRequestId, (success, _) =>
                    {
                        if (this == null || overlay == null) return;
                        if (success) overlay.RemoveFromHierarchy();
                    }));
                }, formats);
                Label("Select a model from this PC or download one from your Sakura cloud library. This does not change the model on the PC.", "description", window);
            }
            var find = new TextField { name = "picker-find" }; find.textEdition.placeholder = "Search all folders by character name…"; window.Add(find);
            var breadcrumb = Element("row", window); breadcrumb.name = "picker-breadcrumb";
            var scroll = new ScrollView { name = "picker-scroll" }; window.Add(scroll); scroll.AddToClassList("picker-results");
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
                    Button button = null;
                    button = Button(entry.name, () =>
                    {
                        if (phoneRequestId != null)
                        {
                            button?.SetEnabled(false);
                            tracking.StageModelForPhone(entry.path, phoneRequestId, (success, _) =>
                            {
                                if (this == null || overlay == null) return;
                                if (success) overlay.RemoveFromHierarchy(); else button?.SetEnabled(true);
                            });
                            return;
                        }
                        int slash = path.LastIndexOf('/');
                        PlayerPrefs.SetString(preference, slash < 0 ? "" : path.Substring(0, slash)); PlayerPrefs.Save();
                        if (entry.path != viewport.LoadedModelPath) _ = viewport.LoadModel(backend.SourceFolder, entry.path);
                        overlay.RemoveFromHierarchy(); ClosePanel();
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
            if (CurrentPanel == "Character" && !panel.ClassListContains("panel-closed")) ShowPanel("Character");
            if (!initialCatalog) return;
            initialCatalog = false;
            if (!SakuraInstances.IsSecondary && !viewport.HasAvatar && !viewport.Loading)
            {
                string preferred = PlayerPrefs.GetString("sakura-selected-model", "");
                if (catalog.Any(item => item.path == preferred)) _ = viewport.LoadModel(backend.SourceFolder, preferred);
            }
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
            if (!panel.ClassListContains("panel-closed"))
            {
                if (CurrentPanel == "Character") RefreshCatalogSelectionLabels();
                else if (CurrentPanel == "Model Settings" || CurrentPanel == "Expressions & hotkeys") ShowPanel(CurrentPanel);
            }
        }
        private void RefreshCatalogSelectionLabels()
        {
            foreach (var pair in catalogSelectionLabels)
            {
                var loaded = catalog.FirstOrDefault(entry => entry.path == viewport.LoadedModelPath && entry.format == pair.Key);
                pair.Value.text = loaded == null ? pair.Key : pair.Key + " · " + loaded.name;
            }
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
        private void ReceiveImportedModel(string path) { ClosePanel(); _ = viewport.LoadModel(backend.SourceFolder, path); }
        private void SetStatus(string text) { if (status != null) status.text = text; }
        private void ReceiveReply(string text) { Label(text, "message", messages.contentContainer); SetStatus(text); messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0, float.MaxValue)); }
        private void ShowEvidence(JObject reply)
        {
            if (reply == null || messages == null || reply.Value<string>("response_kind") == "conversation") return;
            var statements = reply["statements"] as JArray ?? new JArray();
            var sources = reply["sources"] as JArray ?? new JArray();
            if (statements.Count == 0 && sources.Count == 0) return;
            var labels = new List<string>();
            foreach (var statement in statements.OfType<JObject>())
            {
                string classification = statement.Value<string>("classification");
                if (!string.IsNullOrEmpty(classification) && !labels.Contains(classification)) labels.Add(classification);
            }
            var details = new Foldout { text = "Evidence · " + (labels.Count == 0 ? "Unknown" : string.Join(" · ", labels)), value = false };
            details.AddToClassList("response-evidence");
            foreach (var statement in statements.OfType<JObject>())
            {
                string classification = statement.Value<string>("classification");
                var row = new Label((string.IsNullOrEmpty(classification) ? "Unknown" : classification) + ": " + (statement.Value<string>("text") ?? ""));
                row.style.whiteSpace = WhiteSpace.Normal; details.Add(row);
            }
            foreach (var source in sources.OfType<JObject>())
            {
                string kind = source.Value<string>("kind"), title = source.Value<string>("title"), url = source.Value<string>("url");
                if (kind == "tool")
                {
                    var receipt = new Label("Source: " + (title ?? "Local tool") + (string.IsNullOrEmpty(url) ? "" : " (" + url + ")"));
                    receipt.style.whiteSpace = WhiteSpace.Normal; details.Add(receipt);
                }
                else if (kind == "external" && Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) && uri.Scheme == "https")
                {
                    var link = new Button(() => Application.OpenURL(uri.AbsoluteUri)) { text = string.IsNullOrEmpty(title) ? uri.Host : title };
                    details.Add(link);
                }
            }
            messages.contentContainer.Add(details);
            messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0, float.MaxValue));
        }
        private void ReceiveTranscript(string text) { Label(text, "user-message", messages.contentContainer); }
        private void ApplyAvatarReply(Newtonsoft.Json.Linq.JObject reply)
        {
            ShowEvidence(reply);
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
                if (button == compactMicrophone) button.text = enabled ? "On" : "Mic";
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
            splashAnimation?.Pause();
            voicePoll?.Pause();
            if (nativeWindow != null) nativeWindow.Changed -= UpdateWindowTransparency;
            PanelsHidden?.Invoke();
            if (backend == null) return;
            if (viewport != null) viewport.ModelChanged -= ReceiveModelChanged;
            backend.CatalogReceived -= ReceiveCatalog;
            backend.StatusChanged -= SetStatus;
            backend.ReplyReceived -= ReceiveReply;
            backend.ModelImported -= ReceiveImportedModel;
            backend.ReplyDataReceived -= ApplyAvatarReply;
            if (tracking != null) tracking.PhoneModelRequested -= OpenPhoneModelPicker;
            if (voice != null) { voice.TranscriptReceived -= ReceiveTranscript; voice.StatusChanged -= SetStatus; voice.ListeningChanged -= UpdateMicrophone; }
        }
    }
}
