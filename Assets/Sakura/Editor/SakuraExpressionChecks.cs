using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraExpressionChecks
    {
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
