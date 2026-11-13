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
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-vtuber-parity.png");
            studio.ShowPanel("Settings");
            if (root.Query<Button>(className:"settings-tab").ToList().Any(button => button.text == "AI & LLM" || button.text == "Assistant"))
                throw new Exception("Companion-only settings appeared in VTuber mode.");
            studio.SwitchMode("Companion"); studio.ShowPanel("Character");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-models-parity.png");
            studio.ToggleTheme();
            try
            {
                if (!root.ClassListContains("light")) throw new Exception("Light theme was not applied.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-light-parity.png");
            }
            finally { studio.ToggleTheme(); }
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-compact-parity.png",460,900);
            System.IO.File.WriteAllText(@"C:\AI\Unity-Verification\pc-ui-parity-check.json", "{\"companionNavigation\":true,\"vtuberNavigation\":true,\"voiceDirectPane\":true,\"expressionExpansion\":true,\"positionLock\":true,\"vtuberAiSettingsExcluded\":true,\"fullFeatureParity\":false}");
            Debug.Log("PASS: mode-specific navigation, direct Voice pane, expression cards, position lock, toolbar scrolling and VTuber settings isolation.");
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

        private static void Submit(Button button)
        {
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }
    }
}
