using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraAgentUiParityChecks
    {
        public static void ValidateRequests()
        {
            if (!SakuraAgentPanel.ValidPageUrl("http://localhost:4173/") || !SakuraAgentPanel.ValidPageUrl("https://example.org/"))
                throw new Exception("Task webpage links were rejected.");
            foreach (string value in new[] { "javascript:alert(1)", "file:///C:/private", "https://user:password@example.org/", new string('x', 2049) })
                if (SakuraAgentPanel.ValidPageUrl(value)) throw new Exception("Unsafe task webpage link was accepted.");
            var extract = typeof(SakuraAgentPanel).GetMethod("ResultPageUrl", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            foreach (var value in new Newtonsoft.Json.Linq.JToken[] { new Newtonsoft.Json.Linq.JValue("plain result"), new Newtonsoft.Json.Linq.JArray(), new Newtonsoft.Json.Linq.JObject { ["url"] = new Newtonsoft.Json.Linq.JObject() } })
                if (extract.Invoke(null, new object[] { value }) != null) throw new Exception("Malformed tool output became a clickable URL.");
            foreach (bool automatic in new[] { false, true })
            {
                var request = SakuraAgentPanel.BuildTaskRequest("Draft a report", @"C:\Workspace", automatic);
                if (request["approval_mode"] != null) throw new Exception("Native tasks pin their approval policy and ignore later global policy changes.");
                if ((string)request["goal"] != "Draft a report" || (string)request["workspace"] != @"C:\Workspace" || (bool)request["auto_continue"] != automatic)
                    throw new Exception("Native task request lost its goal, workspace or continuation setting.");
            }
            foreach (string engine in new[] { "auto", "vulkan", "cuda", "cpu", "colibri" })
                foreach (string accelerator in new[] { "vulkan", "cuda" })
                {
                    var request = SakuraAgentPanel.BuildRuntimeInstallRequest(engine, accelerator);
                    string expectedBackend = engine == "colibri" ? "vulkan" : accelerator;
                    if ((string)request["backend"] != expectedBackend || (string)request["engine"] != (engine == "colibri" ? "colibri" : "llama.cpp"))
                        throw new Exception("Native runtime installation sent an unsupported engine or accelerator.");
                }
            bool refused = false;
            try { SakuraAgentPanel.BuildRuntimeInstallRequest("auto", "auto"); }
            catch (ArgumentException) { refused = true; }
            if (!refused) throw new Exception("Runtime installation accepted an unsupported automatic accelerator.");
            Debug.Log("PASS: native task requests inherit global approval policy and runtime requests use supported accelerators.");
        }

        public static async Task VerifyShell()
        {
            ValidateRequests();
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new InvalidOperationException("Enter Studio Play mode first.");
            studio.SwitchMode("Agent");
            await Task.Delay(1000);
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            var owner = root.Q("agent-workspace");
            if (owner == null || owner.styleSheets.count == 0) throw new Exception("Agent workspace stylesheet is missing.");
            var pages = owner.Query<Button>(className: "agent-page").ToList();
            string[] expected = { "Tasks", "Local AI", "Connections", "Skills", "Memory & learning", "Automations", "Task history", "Browser" };
            if (!pages.Select(button => button.text).SequenceEqual(expected)) throw new Exception("Agent navigation differs from the reference.");
            if (pages.Any(button => button.Q(className: "agent-nav-icon") == null)) throw new Exception("Agent navigation outline icons are missing.");
            if (pages.Any(button => button.resolvedStyle.paddingLeft < 35)) throw new Exception("Agent navigation text overlaps its icon.");
            var sidebar = owner.Q<ScrollView>("agent-sidebar");
            if (sidebar == null || sidebar.horizontalScrollerVisibility != ScrollerVisibility.Hidden) throw new Exception("Agent sidebar cannot scroll independently.");
            var composer = owner.Q("agent-compose");
            var options = owner.Q<Foldout>("agent-task-options");
            var goal = owner.Q<TextField>("agent-goal");
            if (composer == null || options == null || options.value || goal == null) throw new Exception("Agent composer or collapsed task settings are missing.");
            if (owner.Q<DropdownField>("agent-provider") == null || owner.Q<DropdownField>("agent-access") == null || owner.Q("agent-selected-skills") == null)
                throw new Exception("Agent planning, approval or reusable-skill controls are missing.");
            if (owner.Q("agent-run-approval") == null || owner.Q("agent-run-auto") == null) throw new Exception("Agent run-mode controls are missing.");
            goal.value = "Unsent UI verification draft";
            foreach (var page in pages.Where(button => button.text != "Tasks"))
            {
                Submit(page);
                await Task.Delay(60);
                if (!page.ClassListContains("agent-page-selected")) throw new Exception("Agent navigation selection did not follow the active page.");
                if (composer.resolvedStyle.display != DisplayStyle.None) throw new Exception("Task composer remains visible on a management page.");
                if (goal.value != "Unsent UI verification draft") throw new Exception("Changing Agent pages erased the unsent draft.");
                if (page.text == "Task history" && (owner.Q("agent-history-query") == null || owner.Q("agent-history-search") == null))
                    throw new Exception("Session history lacks its search controls.");
            }
            Submit(pages[0]);
            await Task.Delay(150);
            if (composer.resolvedStyle.display != DisplayStyle.Flex || goal.value != "Unsent UI verification draft") throw new Exception("Agent task composer did not return with its draft.");
            if (composer.worldBound.yMax > owner.worldBound.yMax + 2 || composer.worldBound.height < 80) throw new Exception("Agent composer is clipped or collapsed.");
            if (owner.worldBound.xMin < root.worldBound.width * .20f) throw new Exception("Agent workspace covers the reserved avatar area.");
            goal.value = "";
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-agent-parity.png");
            await VerifyAppearance(studio, root, owner);
            Debug.Log("PASS: Agent navigation order, independent sidebar, collapsed task settings, draft preservation, management-page visibility and reserved avatar area. No tasks or tool actions were executed.");
        }

        private static async Task VerifyAppearance(SakuraStudio studio, VisualElement root, VisualElement owner)
        {
            bool wasLight = root.ClassListContains("light");
            try
            {
                studio.ApplyTheme("dark"); await Task.Delay(150);
                ColorUtility.TryParseHtmlString("#1c1423", out var dark);
                if (!SameColor(owner.resolvedStyle.backgroundColor, dark)) throw new Exception("Agent dark palette differs from the final blossom stylesheet.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-agent-parity.png");
                studio.ApplyTheme("light"); await Task.Delay(150);
                ColorUtility.TryParseHtmlString("#f9f0f5", out var light);
                if (!SameColor(owner.resolvedStyle.backgroundColor, light)) throw new Exception("Agent light palette differs from the final blossom stylesheet.");
                var provider = owner.Q<DropdownField>("agent-provider");
                var label = provider.Q<TextElement>(className: "unity-base-popup-field__text");
                if (label == null || label.resolvedStyle.whiteSpace != WhiteSpace.Normal || label.contentRect.width < 100)
                    throw new Exception("Agent planning-model label cannot wrap within its dropdown.");
                var textSize = label.MeasureTextSize(provider.value, label.contentRect.width, VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                if (textSize.y > label.contentRect.height + 1 || label.worldBound.xMax > provider.worldBound.xMax + 1)
                    throw new Exception("Agent planning-model label is clipped.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-agent-parity-light.png");
            }
            finally { studio.ApplyTheme(wasLight ? "light" : "dark"); }
        }

        private static bool SameColor(Color left, Color right) => Mathf.Abs(left.r - right.r) < .005f && Mathf.Abs(left.g - right.g) < .005f && Mathf.Abs(left.b - right.b) < .005f;

        private static void Submit(Button button)
        {
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }
    }
}
