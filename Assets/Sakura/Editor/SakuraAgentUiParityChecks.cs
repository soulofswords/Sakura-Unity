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
            Debug.Log("PASS: Agent navigation order, independent sidebar, collapsed task settings, draft preservation, management-page visibility and reserved avatar area. No tasks or tool actions were executed.");
        }

        private static void Submit(Button button)
        {
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }
    }
}
