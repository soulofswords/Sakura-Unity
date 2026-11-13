using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraExpressionChecks
    {
        public static async Task VerifyReplyEmotions()
        {
            if (!Application.isPlaying) throw new Exception("Enter Play mode.");
            var root = new GameObject("Reply emotion fixture");
            var mesh = new Mesh();
            string preference = SakuraAvatarExpressions.PreferenceKey("reply-emotion-check");
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
                foreach (string name in new[] { "happy", "happy outfit remove", "mouth smile" })
                    mesh.AddBlendShapeFrame(name, 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
                var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                var controls = root.AddComponent<SakuraAvatarExpressions>(); controls.Initialize("reply-emotion-check", null);
                if (!controls.SetReplyMood("happy")) throw new Exception("Supported reply emotion was not selected.");
                await Task.Delay(200); controls.ApplyNow();
                if (renderer.GetBlendShapeWeight(0) <= 0 || renderer.GetBlendShapeWeight(1) != 0 || renderer.GetBlendShapeWeight(2) != 0)
                    throw new Exception("Reply emotion failed to fade or changed a costume/mouth control.");
                controls.StopReplyMood(); controls.ApplyNow();
                if (renderer.GetBlendShapeWeight(0) != 0) throw new Exception("Disabled reply emotion left a deformation.");
                controls.SetActive(controls.Entries[0].Id, true);
                if (controls.SetReplyMood("happy")) throw new Exception("Reply emotion took priority over a manual expression.");
                controls.Clear();
                if (controls.SetReplyMood("off") || controls.SetReplyMood("invented")) throw new Exception("Unsupported mood changed the model.");
                Debug.Log("SAKURA_REPLY_EMOTION_CHECKS_OK: smooth supported emotion, costume/mouth isolation, manual priority, off/unknown neutral.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); PlayerPrefs.DeleteKey(preference); PlayerPrefs.Save(); }
        }
        public static async Task Verify()
        {
            if (!Application.isPlaying) throw new Exception("Run expression verification in Play mode.");
            var root = new GameObject("Expression verification fixture");
            var mesh = new Mesh();
            string modelKey = "expression-check-" + Guid.NewGuid();
            string preference = SakuraAvatarExpressions.PreferenceKey(modelKey);
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.AddBlendShapeFrame("blink", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
                root.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var controls = root.AddComponent<SakuraAvatarExpressions>(); controls.Initialize(modelKey, null);
                var expression = controls.Entries.Single();
                controls.SetStrength(expression.Id, .4f); controls.SetActive(expression.Id, true);
                controls.ApplyNow();
                if (Mathf.Abs(root.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0) - 40) > .01f) throw new Exception("Expression did not reach native mesh.");
                controls.Rename(expression.Id, "My blink"); controls.Bind(expression.Id, "Ctrl+Digit1");
                if (!controls.TriggerHotkey("Ctrl+Digit1") || expression.Active) throw new Exception("Focused shortcut did not toggle expression off.");
                controls.ApplyNow();
                if (root.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0) != 0) throw new Exception("Expression off left the mesh deformed.");
                controls.Preview(expression.Id, .02f); await Task.Delay(100);
                if (expression.Active) throw new Exception("Temporary preview did not expire.");
                UnityEngine.Object.DestroyImmediate(controls);
                controls = root.AddComponent<SakuraAvatarExpressions>(); controls.Initialize(modelKey, null);
                expression = controls.Entries.Single();
                if (expression.Label != "My blink" || expression.Hotkey != "Ctrl+Digit1" || Mathf.Abs(expression.Strength - .4f) > .001f || expression.Active) throw new Exception("Per-model settings did not reload correctly.");
                if (!controls.ApplyByLabel(new Newtonsoft.Json.Linq.JObject { ["label"] = "MY BLINK" }) || !expression.Active)
                    throw new Exception("Spoken expression did not resolve its saved label.");
                if (controls.ApplyByLabel(new Newtonsoft.Json.Linq.JObject { ["label"] = "invented outfit" }))
                    throw new Exception("Unknown expression was accepted.");
                if (!controls.ApplyByLabel(new Newtonsoft.Json.Linq.JObject { ["clear"] = true }) || expression.Active)
                    throw new Exception("Spoken neutral command did not clear the expression.");
                controls.Bind(expression.Id, "Ctrl+Digit2");
                if (controls.TriggerHotkey("Ctrl+Digit1")) throw new Exception("Old shortcut remained registered.");
                bool invalid = false;
                try { controls.SetStrength(expression.Id, float.NaN); } catch (ArgumentException) { invalid = true; }
                if (!invalid) throw new Exception("Nonfinite expression strength accepted.");
                Debug.Log("SAKURA_EXPRESSION_CHECKS_OK: native mesh, strength, toggle, preview expiry, rename, shortcut persistence, invalid input.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); PlayerPrefs.DeleteKey(preference); PlayerPrefs.Save(); }
        }
    }
}
