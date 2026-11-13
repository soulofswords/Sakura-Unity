using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraVtubeHotkeyChecks
    {
        public static void VerifyNativeBindings()
        {
            if (!Application.isPlaying) throw new Exception("Run native shortcut verification in Play mode.");
            var root = new GameObject("Native shortcut fixture"); var mesh = new Mesh();
            string model = "hotkey-check-" + Guid.NewGuid(); string preference = SakuraAvatarExpressions.PreferenceKey(model);
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
                mesh.AddBlendShapeFrame("blink", 100, new Vector3[3], new Vector3[3], new Vector3[3]); root.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var controller = root.AddComponent<SakuraAvatarExpressions>(); controller.Initialize(model, null); var expression = controller.Entries.Single();
                if (expression.Hotkey != "Ctrl+Digit1") throw new Exception("Fallback automatic shortcut was not assigned.");
                bool collision = false; try { controller.BindAction("clear-expressions", "Ctrl+Digit1"); } catch (ArgumentException) { collision = true; }
                if (!collision) throw new Exception("Action reused an expression shortcut.");
                controller.BindAction("clear-expressions", "Alt+KeyR"); controller.SetActive(expression.Id, true);
                if (!controller.TriggerHotkey("alt+keyr") || expression.Active) throw new Exception("Clear-expressions shortcut failed.");
                controller.Bind(expression.Id, ""); UnityEngine.Object.DestroyImmediate(controller);
                controller = root.AddComponent<SakuraAvatarExpressions>(); controller.Initialize(model, null);
                if (!string.IsNullOrEmpty(controller.Entries.Single().Hotkey) || controller.Actions.First(entry => entry.Id == "clear-expressions").Hotkey != "Alt+KeyR") throw new Exception("Manual clear or action shortcut did not persist.");
                Debug.Log("SAKURA_NATIVE_HOTKEY_CHECKS_OK: fallback assignment, cross-action duplicate guard, native clear action, normalized dispatch, manual-clear persistence.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); PlayerPrefs.DeleteKey(preference); PlayerPrefs.Save(); }
        }
        public static void Verify()
        {
            string folder = Path.Combine(Application.temporaryCachePath, "vtube-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string model = Path.Combine(folder, "avatar.model3.json"); File.WriteAllText(model, "{\"FileReferences\":{\"Expressions\":[]}}");
                Directory.CreateDirectory(Path.Combine(folder, "Exp3")); File.WriteAllText(Path.Combine(folder, "Exp3", "smile.exp3.json"), "{}");
                File.WriteAllText(Path.Combine(folder, "a.vtube.json"), "{\"Hotkeys\":[{\"Action\":\"ToggleExpression\",\"Name\":\"Smile\",\"File\":\"smile.exp3.json\",\"Triggers\":{\"Trigger1\":\"alt\",\"Trigger2\":\"n1\"}},{\"Action\":\"RemoveAllExpressions\",\"Triggers\":{\"Trigger1\":\"Alt\",\"Trigger2\":\"R\"}},{\"Action\":\"ToggleExpression\",\"File\":\"../escape.exp3.json\",\"Triggers\":{\"Trigger1\":\"Q\"}}]}");
                var profile = SakuraVtubeHotkeys.Load(model);
                if (!profile.Present || profile.Bindings.Count != 2 || profile.Bindings[0].File != "Exp3/smile.exp3.json" || profile.Bindings[0].Hotkey != "Alt+Digit1" || profile.Expressions.Single().Name != "Smile") throw new Exception("VTube expression file/name/trigger import failed.");
                if (profile.Bindings[1].Hotkey != "Alt+KeyR") throw new Exception("RemoveAllExpressions import failed.");
                File.WriteAllText(Path.Combine(folder, "wave.motion3.json"), "{}");
                File.WriteAllText(Path.Combine(folder, "motion.vtube.json"), "{\"Hotkeys\":[{\"Action\":\"TriggerAnimation\",\"Name\":\"Wave\",\"File\":\"wave.motion3.json\",\"Triggers\":{\"Trigger1\":\"ctrl\",\"Trigger2\":\"w\"}}]}");
                profile = SakuraVtubeHotkeys.Load(model);
                if (!profile.Bindings.Any(binding => binding.Action == "TriggerAnimation" && binding.File == "wave.motion3.json" && binding.Hotkey == "Ctrl+KeyW")) throw new Exception("VTube authored-motion hotkey was discarded.");
                File.Delete(Path.Combine(folder, "motion.vtube.json"));
                if (SakuraAvatarExpressions.NormalizeHotkey("shift+ctrl+keya") != "Ctrl+Shift+KeyA" || SakuraAvatarExpressions.NormalizeHotkey("Alt+ArrowLeft") != "Alt+LeftArrow") throw new Exception("Shortcut normalization failed.");
                if (SakuraVtubeHotkeys.TriggerCode(JObject.Parse("{\"Trigger1\":\"Q\",\"Trigger2\":\"R\"}")) != null) throw new Exception("Multiple trigger keys accepted.");
                Directory.CreateDirectory(Path.Combine(folder, "Other")); File.WriteAllText(Path.Combine(folder, "Other", "smile.exp3.json"), "{}");
                profile = SakuraVtubeHotkeys.Load(model); if (profile.Bindings.Any(entry => entry.File != null)) throw new Exception("Ambiguous basename matched an expression.");
                File.WriteAllText(Path.Combine(folder, "a.vtube.json"), "[]");
                profile = SakuraVtubeHotkeys.Load(model); if (!profile.Present || profile.Bindings.Count != 0) throw new Exception("Malformed profile enabled fallback hotkeys.");
                Debug.Log("SAKURA_VTUBE_HOTKEY_CHECKS_OK: sidecar presence, unreferenced discovery, case-normalized keys, reset action, unique basename fallback, traversal refusal, malformed sidecar, shared normalization. No source assets changed.");
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
