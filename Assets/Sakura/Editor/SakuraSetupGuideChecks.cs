using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraSetupGuideChecks
    {
        public static void Validate()
        {
            var companion = SakuraSetupGuide.StepsFor("Companion");
            var vtuber = SakuraSetupGuide.StepsFor("VTuber");
            if (companion.Count != 5 || vtuber.Count != 4) throw new Exception("Desktop setup step counts differ from the reference.");
            if (!companion.Skip(1).Select(step => step.Panel).SequenceEqual(new[] { "Character", "Voice & speech", "Camera", "Settings" }))
                throw new Exception("Companion guide navigation differs from the reference.");
            if (!vtuber.Skip(1).Select(step => step.Panel).SequenceEqual(new[] { "Character", "Camera", "Settings" }))
                throw new Exception("VTuber guide contains Companion-only steps.");
            if (SakuraSetupGuide.StepsFor("Agent").Count != 0 || SakuraSetupGuide.PreferenceKey("Companion") == SakuraSetupGuide.PreferenceKey("VTuber"))
                throw new Exception("Guide mode persistence is shared or Agent guide was enabled.");
            Debug.Log("PASS: native desktop setup steps, safe panel navigation and separate mode preferences.");
        }

        public static async Task VerifyNavigation()
        {
            Validate();
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new InvalidOperationException("Enter Studio Play mode first.");
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            string oldMode = studio.CurrentMode;
            string[] keys = { SakuraSetupGuide.PreferenceKey("Companion"), SakuraSetupGuide.PreferenceKey("VTuber") };
            string[] saved = keys.Select(key => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null).ToArray();
            SakuraSetupGuide guide = null;
            try
            {
                studio.SwitchMode("Companion");
                guide = SakuraSetupGuide.Attach(studio, root);
                guide.Open();
                await Task.Delay(150);
                if (!guide.IsOpen || guide.CurrentStep != 0 || root.Q("setup-show") != null) throw new Exception("Guide welcome or direct Next flow differs from the requested behavior.");
                foreach (var step in SakuraSetupGuide.StepsFor("Companion").Skip(1))
                {
                    Submit(root.Q<Button>("setup-next"));
                    await Task.Delay(200);
                    if (studio.CurrentPanel != step.Panel) throw new Exception("Next did not open " + step.Panel);
                    if (!guide.IsOpen || root.Q("sakura-setup-focus").resolvedStyle.display != DisplayStyle.Flex) throw new Exception("Next did not retain the guide and highlight its target.");
                }
                Submit(root.Q<Button>("setup-back"));
                if (studio.CurrentPanel != "Camera") throw new Exception("Back did not reopen the matching panel.");
                Submit(root.Q<Button>("setup-skip"));
                if (guide.IsOpen || JObject.Parse(PlayerPrefs.GetString(keys[0])).Value<bool>("done") != true) throw new Exception("Skipping setup did not persist completion.");
                studio.SwitchMode("VTuber");
                await Task.Delay(200);
                guide.Open();
                for (int index = 0; index < SakuraSetupGuide.StepsFor("VTuber").Count; index++) Submit(root.Q<Button>("setup-next"));
                if (guide.IsOpen || JObject.Parse(PlayerPrefs.GetString(keys[1])).Value<bool>("done") != true) throw new Exception("VTuber setup did not finish independently.");
                guide.Open(); studio.SwitchMode("Agent");
                if (guide.IsOpen) throw new Exception("Entering Agent mode retained the desktop guide.");
                studio.SwitchMode("Companion");
                await Task.Delay(200);
                if (guide.IsOpen) throw new Exception("Completed setup reopened without an explicit request.");
                guide.Open();
                if (!guide.IsOpen || guide.CurrentStep != 0) throw new Exception("Explicit setup replay did not start at the welcome step.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-setup-guide.png");
                Debug.Log("PASS: setup Next auto-navigation/highlight, Back, Skip/Finish persistence, explicit replay and Agent closure. Original guide preferences restored.");
            }
            finally
            {
                guide?.Close();
                for (int index = 0; index < keys.Length; index++)
                    if (saved[index] == null) PlayerPrefs.DeleteKey(keys[index]); else PlayerPrefs.SetString(keys[index], saved[index]);
                PlayerPrefs.Save();
                studio.SwitchMode(oldMode);
            }
        }

        private static void Submit(Button button)
        {
            using var submit = NavigationSubmitEvent.GetPooled(); submit.target = button; button.SendEvent(submit);
        }
    }
}
