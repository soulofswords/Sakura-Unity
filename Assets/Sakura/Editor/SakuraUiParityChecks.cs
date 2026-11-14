using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraUiParityChecks
    {
        public static async Task VerifyShell()
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new InvalidOperationException("Enter Studio Play mode first.");
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            studio.SwitchMode("Companion");
            var fixtureViewport = studio.GetComponent<SakuraAvatarViewport>();
            var importDeadline = DateTime.UtcNow.AddSeconds(90);
            while (fixtureViewport.Loading && DateTime.UtcNow < importDeadline) await Task.Delay(100);
            if (fixtureViewport.Loading) throw new Exception("Startup model import did not finish within 90 seconds.");
            await fixtureViewport.LoadModel(studio.GetComponent<SakuraBackend>().SourceFolder, "pmx/Ringo/Ringo.pmx");
            if (fixtureViewport.Pmx == null) throw new Exception(fixtureViewport.LastImportError ?? "Shell check requires the Ringo PMX fixture.");
            studio.ApplyTheme("dark");
            Submit(root.Q<Button>("chat-minimize"));
            await Task.Delay(150);
            if (Mathf.Abs(root.Q("chat-panel").layout.height - 54) > 1) throw new Exception("Minimized chat retained its expanded inline height.");
            Submit(root.Q<Button>("chat-minimize"));
            await Task.Delay(150);
            if (root.Q("chat-panel").layout.height <= 54) throw new Exception("Chat did not expand again.");
            var dock = root.Q("companion-dock");
            string[] companion = { "Models", "Camera & tracking", "Voice & speech", "Model Settings", "Background & stage items", "Streaming & output", "Sakura Mobile", "Memory & privacy", "Settings" };
            if (!dock.Query<Button>(className:"dock-button").ToList().Select(button => button.tooltip).SequenceEqual(companion))
                throw new Exception("Companion navigation differs from the reference.");
            var firstDock = dock.Query<Button>(className:"dock-button").First();
            if (firstDock.resolvedStyle.borderTopLeftRadius < 25) throw new Exception("Dock buttons are not circular.");
            if (firstDock.Q(className:"dock-label") != null) throw new Exception("Dock buttons still carry inline text labels.");
            if (!firstDock.ClassListContains("dock-tint-pink")) throw new Exception("The Models dock button is missing its reference pink tint class.");
            if (dock.Q(className:"dock-hover-label") == null) throw new Exception("Dock hover label is missing.");
            studio.ShowPanel("Voice & speech");
            await Task.Delay(1500);
            var tabs = root.Q(className:"settings-tabs");
            if (tabs == null || tabs.resolvedStyle.display != DisplayStyle.None) throw new Exception("Voice pane contains an extra settings tab strip.");
            if (root.Q("conversation-friend") == null || root.Q("conversation-teacher") == null) throw new Exception("Conversation switches are missing.");
            var language = root.Q<DropdownField>("voice-language");
            var preset = root.Q<DropdownField>("voice-preset");
            if (language == null || preset == null || !language.enabledSelf) throw new Exception("Voice language choices were not loaded.");
            string oldLanguage = language.value, oldVoice = preset.value;
            language.value = "Hindi";
            if (!preset.value.Contains("Hindi")) throw new Exception("Hindi did not select a Hindi voice.");
            language.value = "Hinglish";
            if (!preset.value.Contains("Hinglish")) throw new Exception("Hinglish did not select a Hinglish voice.");
            language.value = oldLanguage;
            if (preset.value != oldVoice) throw new Exception("Language selection lost the original voice.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-voice-parity.png");
            studio.ShowPanel("Expressions & hotkeys");
            var expression = root.Q<Foldout>(className:"expression-card");
            if (expression == null) throw new Exception("Expression cards are missing.");
            expression.value = true;
            await Task.Delay(150);
            if (expression.Q<Label>(className:"unity-foldout__text").worldBound.width < 10) throw new Exception("Expression title was clipped by the foldout input.");
            if (expression.Q(className:"expression-expand").worldBound.center.x < expression.worldBound.center.x) throw new Exception("Expression expansion control moved to the left edge.");
            if (expression.Q(className:"expression-expand") is not Label indicator || indicator.text != "\u2212") throw new Exception("Expression expansion indicator failed.");
            if (!expression.Query<Button>().ToList().Any(button => button.text == "Set hotkey")) throw new Exception("Expression actions did not open.");
            var expressions = studio.GetComponent<SakuraAvatarViewport>().Expressions;
            Submit(expression.Query<Button>().ToList().First(button => button.text == "Set hotkey"));
            if (!expressions.SuspendHotkeys) throw new Exception("Shortcut recorder did not start.");
            Submit(root.Q<Button>(className:"close"));
            if (expressions.SuspendHotkeys) throw new Exception("Closing the panel left shortcut recording active.");
            studio.ShowPanel("Expressions & hotkeys");
            expression = root.Q<Foldout>(className:"expression-card"); expression.value = true;
            Submit(expression.Query<Button>().ToList().First(button => button.text == "Set hotkey"));
            studio.ToggleChrome();
            if (expressions.SuspendHotkeys) throw new Exception("Hiding controls left shortcut recording active.");
            studio.ToggleChrome();
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-expressions-parity.png");
            studio.SwitchMode("VTuber");
            string[] vtuber = { "Models", "Camera & tracking", "Expressions & hotkeys", "Background", "Streaming & voice", "Plugins", "Sakura Mobile", "Settings", "Collab" };
            var buttons = dock.Query<Button>(className:"dock-button").ToList();
            if (!buttons.Take(9).Select(button => button.tooltip).SequenceEqual(vtuber) || buttons.Count != 11) throw new Exception("VTuber navigation differs from the reference.");
            var scroll = dock.Q<ScrollView>();
            if (scroll.horizontalScrollerVisibility != ScrollerVisibility.Hidden || scroll.verticalScrollerVisibility != ScrollerVisibility.Hidden) throw new Exception("Default scrollbars are visible in the toolbar.");
            var viewport = studio.GetComponent<SakuraAvatarViewport>();
            bool locked = viewport.PositionLocked;
            Submit(root.Q<Button>("model-position-lock"));
            if (viewport.PositionLocked == locked) throw new Exception("VTuber position lock did not toggle.");
            Submit(root.Q<Button>("model-position-lock"));
            var tracking = studio.GetComponent<SakuraTracking>();
            var hands = root.Q<Button>("footer-hands-toggle");
            if (hands == null) throw new Exception("VTuber footer hand-tracking toggle is missing.");
            bool handsEnabled = tracking.Hands;
            Submit(hands);
            if (tracking.Hands == handsEnabled || hands.text != (tracking.Hands ? "Hands on" : "Hands off"))
                throw new Exception("VTuber footer hand-tracking toggle did not update its state and label.");
            Submit(root.Q<Button>("footer-hands-toggle"));
            if (tracking.Hands != handsEnabled) throw new Exception("VTuber footer hand-tracking toggle did not restore its original state.");
            studio.ShowPanel("Streaming & output");
            var streaming = root.Q("companion-panel-body");
            if (streaming == null || !streaming.Query<Label>().ToList().Any(label => label.text == "Microphone voice changer") ||
                !streaming.Query<Button>().ToList().Any(button => button.text == "Start microphone voice changer"))
                throw new Exception("VTuber Streaming & voice is missing the microphone voice-changer controls.");
            if (streaming.Query<Button>().ToList().Any(button => button.text == "Start microphone voice changer") &&
                streaming.Query<Label>().ToList().All(label => label.text != "Converted audio plays through Unity’s current Windows output device. To send it to OBS or a call app, select your virtual cable as Sakura’s output in Windows Volume mixer, then select the cable’s recording endpoint in that app. Use headphones to avoid speaker feedback."))
                throw new Exception("Voice-changer routing guidance is missing.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-vtuber-parity.png");
            studio.ShowPanel("Settings");
            if (root.Query<Button>(className:"settings-tab").ToList().Any(button => button.text == "AI & LLM" || button.text == "Assistant"))
                throw new Exception("Companion-only settings appeared in VTuber mode.");
            Submit(root.Query<Button>(className:"settings-tab").ToList().First(button => button.text == "Theme"));
            await Task.Delay(250);
            if (root.Query<Label>(className:"card-title").ToList().Any(label => label.text == "Computer access"))
                throw new Exception("Companion-only computer access appeared in the VTuber Theme tab.");
            studio.SwitchMode("Companion");
            studio.ShowPanel("Settings");
            Submit(root.Query<Button>(className:"settings-tab").ToList().First(button => button.text == "Theme"));
            await Task.Delay(250);
            string[] themeCards = { "Appearance", "About Sakura", "Graphics", "Computer access" };
            if (!root.Query<Label>(className:"card-title").ToList().Select(label => label.text).SequenceEqual(themeCards))
                throw new Exception("Theme tab cards differ from the reference order.");
            if (!root.Query<DropdownField>().ToList().Any(field => field.label == "Rendering quality"))
                throw new Exception("Graphics rendering-quality select is missing.");
            if (!root.Query<Button>().ToList().Any(button => button.text == "Sakura website: guides, features and setup") ||
                !root.Query<Button>().ToList().Any(button => button.text == "Release history and downloads"))
                throw new Exception("About Sakura website/release links are missing.");
            var versionDeadline = DateTime.UtcNow.AddSeconds(20);
            bool versionLoaded = false;
            while (DateTime.UtcNow < versionDeadline && !versionLoaded)
            {
                versionLoaded = root.Query<Label>(className:"description").ToList().Any(label => label.text.Contains("· Version"));
                if (!versionLoaded) await Task.Delay(200);
            }
            if (!versionLoaded) throw new Exception("About Sakura did not read the installed version from /api/version.");
            studio.ShowPanel("Character");
            var importsCard = root.Query<Label>(className:"card-title").ToList().FirstOrDefault(label => label.text == "Import a character")?.parent;
            if (importsCard == null || !importsCard.Query<Button>().ToList().Any(button => button.text == "Upload PMX"))
                throw new Exception("The local PMX import button is missing from the Models pane.");
            bool connectedBefore = await LibraryConnected(studio.GetComponent<SakuraBackend>());
            Submit(importsCard.Query<Button>().ToList().First(button => button.text == "Cloud import PMX"));
            await Task.Delay(400);
            var cloud = root.Q("cloud-library");
            if (cloud == null || cloud.Q<TextField>("library-email") == null || cloud.Q<TextField>("library-password") == null)
                throw new Exception("Cloud import PMX did not open the email sign-in card.");
            if (!connectedBefore && cloud.Q<Foldout>(className:"cloud-account").value != true)
                throw new Exception("The email sign-in form was not presented to a signed-out user.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-cloud-signin-parity.png");
            if (await LibraryConnected(studio.GetComponent<SakuraBackend>()) != connectedBefore)
                throw new Exception("Opening the cloud library changed the saved sign-in state.");
            studio.ShowPanel("Character");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-models-parity.png");
            studio.ToggleTheme();
            try
            {
                if (!root.ClassListContains("light")) throw new Exception("Light theme was not applied.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-light-parity.png");
            }
            finally { studio.ToggleTheme(); }
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-compact-parity.png",460,900);
            System.IO.File.WriteAllText(@"C:\AI\Unity-Verification\pc-ui-parity-check.json", "{\"companionNavigation\":true,\"vtuberNavigation\":true,\"voiceDirectPane\":true,\"expressionExpansion\":true,\"positionLock\":true,\"vtuberAiSettingsExcluded\":true,\"circularTintedDock\":true,\"webThemeTabOrder\":true,\"cloudImportSignIn\":true,\"fullFeatureParity\":false}");
            Debug.Log("PASS: mode-specific navigation, circular tinted dock, direct Voice pane, expression cards, position lock, toolbar scrolling, web Theme tab order and VTuber settings isolation.");
        }

        public static async Task VerifyCompactLayout()
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new InvalidOperationException("Enter Studio Play mode first.");
            var document = studio.GetComponent<UIDocument>(); var previous = document.panelSettings.targetTexture;
            var target = new RenderTexture(460, 740, 24); target.Create(); document.panelSettings.targetTexture = target;
            string mode = studio.CurrentMode;
            try
            {
                foreach (string choice in new[] { "Companion", "VTuber" })
                {
                    studio.SwitchMode(choice); studio.ShowPanel("Character"); await Task.Delay(650);
                    var root = document.rootVisualElement;
                    var panel = root.Q("companion-panel"); var footer = root.Q("companion-statusbar");
                    if (panel.worldBound.yMax >= footer.worldBound.yMin) throw new Exception(choice + " panel overlaps compact footer: " + panel.worldBound + " / " + footer.worldBound + " root=" + root.worldBound + " classes=" + string.Join(",",root.GetClasses()));
                    if (choice == "Companion" && root.Q("chat-panel").worldBound.Overlaps(footer.worldBound)) throw new Exception("Compact chat overlaps mode controls.");
                    var buttons = footer.Query<Button>().ToList();
                    foreach (var button in buttons)
                    {
                        if (button.worldBound.xMin < 0 || button.worldBound.xMax > root.worldBound.xMax || button.worldBound.yMax > root.worldBound.yMax) throw new Exception("Compact mode action is outside the window.");
                        if (choice == "Companion" && button.worldBound.Overlaps(root.Q("compact-microphone").worldBound)) throw new Exception("Compact mode action overlaps microphone.");
                    }
                    for (int i = 0; i < buttons.Count; i++) for (int j = i + 1; j < buttons.Count; j++)
                        if (buttons[i].worldBound.Overlaps(buttons[j].worldBound)) throw new Exception("Compact mode buttons overlap.");
                }
                Debug.Log("PASS: compact Companion/VTuber panels, mode controls and microphone have separate visible bounds.");
            }
            finally { studio.SwitchMode(mode); document.panelSettings.targetTexture = previous; target.Release(); UnityEngine.Object.Destroy(target); }
        }

        public static async Task VerifyModelSettings()
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new InvalidOperationException("Enter Studio Play mode first.");
            studio.SwitchMode("Companion");
            var viewport = studio.GetComponent<SakuraAvatarViewport>();
            await viewport.LoadModel(@"F:\AI\assistant\Sakura","pmx/Ringo/Ringo.pmx");
            if (viewport.Pmx == null) throw new Exception(viewport.LastImportError ?? "PMX was not loaded.");
            studio.ShowPanel("Model Settings");
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            var tabs = root.Query<Button>(className:"model-tab").ToList();
            string[] expected = { "Texture remapping","Texture coloring","Expressions & hotkeys","Bones","Mouth" };
            if (!tabs.Select(button => button.text).SequenceEqual(expected)) throw new Exception("PMX tabs differ from the reference app.");
            if (root.Q<IntegerField>("model-zoom-percent") == null) throw new Exception("Numeric model zoom is missing.");
            await Task.Delay(250);
            if (tabs.Last().worldBound.xMax > root.Q("companion-panel").worldBound.xMax) throw new Exception("The final PMX tab is clipped.");
            if (viewport.PickPart(new Vector2(.5f,.65f),false) < 0) throw new Exception("Material picking did not hit the model torso.");
            if (viewport.PickPart(new Vector2(0,0),false) >= 0) throw new Exception("Empty stage was treated as a model hit.");
            Submit(tabs[1]);
            await Task.Delay(150);
            if (root.Query<IntegerField>(className:"colour-channel").ToList().Count != 3 || root.Q(className:"colour-wheel") == null)
                throw new Exception("Native colour wheel and RGB fields are missing.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-model-colouring-parity.png");
            Submit(tabs[0]);
            await Task.Delay(100);
            if (!root.Query<Button>().ToList().Any(button => button.text == "Apply replacement")) throw new Exception("Texture selection/apply flow differs from the reference.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-model-remapping-parity.png");
            Debug.Log("PASS: reference PMX tab order, numeric zoom, unclipped tabs, model-only material picking, colour wheel/RGB and separate texture selection/apply.");
        }

        private static Task<bool> LibraryConnected(SakuraBackend backend)
        {
            var completion = new TaskCompletionSource<bool>();
            backend.Api("/api/model-library/connection", null, (json, error) =>
            {
                if (error != null) { completion.SetException(new Exception(error)); return; }
                completion.SetResult(Newtonsoft.Json.Linq.JObject.Parse(json).Value<bool>("configured"));
            });
            return completion.Task;
        }

        private static void Submit(Button button)
        {
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }
    }
}
