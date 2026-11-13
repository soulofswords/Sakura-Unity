using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraTrackingChecks
    {
        [MenuItem("Sakura/Verify Tracking Kernels")]
        public static void Verify()
        {
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            Check(Mathf.Approximately(SakuraTrackingRig.Blink(0), 0), "Open eyes must stay open");
            Check(Mathf.Approximately(SakuraTrackingRig.Blink(.6f), 1), "Blink closure threshold");
            Check(!SakuraTrackingRig.FaceRotation(new JObject { ["matrix"] = new JArray(1, 2) }, out _), "Reject malformed matrices");
            Check(!SakuraTrackingRig.FaceRotation(new JObject { ["matrix"] = new JArray(new float[16]) }, out _), "Reject degenerate matrices");
            var identity = new JArray(); var matrix = Matrix4x4.identity; for (int index = 0; index < 16; index++) identity.Add(matrix[index]);
            Check(SakuraTrackingRig.FaceRotation(new JObject { ["matrix"] = identity }, out var rotation) && Quaternion.Angle(rotation, Quaternion.identity) < .01f, "Identity rotation conversion");
            Check(!SakuraTrackingRig.ValidFrame(new JObject { ["bad"] = double.NaN }), "Reject nonfinite signals");
            Check(SakuraTrackingRig.ValidFrame(new JObject { ["face"] = new JObject { ["blendshapes"] = new JArray(new JObject { ["categoryName"] = "jawOpen", ["score"] = .5 }) } }), "Accept mobile blendshape payload");
            Check(!SakuraTrackingRig.ValidFrame(new JObject { ["face"] = new JObject { ["blendshapes"] = new JObject { ["jawOpen"] = "invalid" } } }), "Reject string scores");
            var root = new GameObject("Synthetic tracking check"); var mesh = new Mesh();
            try
            {
                var tracking = root.AddComponent<SakuraTracking>(); var rig = root.AddComponent<SakuraTrackingRig>();
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }; mesh.triangles = new[] { 0, 1, 2 };
                mesh.AddBlendShapeFrame("あ", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, new Vector3[3], new Vector3[3]);
                var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                rig.Initialize(tracking); rig.Active = true;
                rig.Accept(new JObject { ["face"] = new JObject { ["blendshapes"] = new JObject { ["jawOpen"] = .5 } } });
                rig.SendMessage("LateUpdate"); Check(Mathf.Abs(renderer.GetBlendShapeWeight(0) - 50) < .01f, "PMX mouth alias receives actual mesh weight");
                tracking.Mouth = false; rig.SendMessage("LateUpdate"); Check(Mathf.Abs(renderer.GetBlendShapeWeight(0)) < .01f, "Mouth toggle clears the mesh weight");
                tracking.Mouth = true; rig.Accept(null); rig.SendMessage("LateUpdate"); Check(Mathf.Abs(renderer.GetBlendShapeWeight(0)) < .01f, "Lost face clears the mesh weight");
                rig.ResetTracking(); Check(Mathf.Abs(renderer.GetBlendShapeWeight(0)) < .01f, "Stop clears tracking writes");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
            const string folder = "C:/AI/Unity-Verification"; Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "tracking-kernel-check.json"), new JObject { ["passed"] = true, ["checks"] = 12, ["physical_tracking_verified"] = false }.ToString());
            Debug.Log("Sakura tracking kernel checks passed (12); physical tracking remains unverified.");
        }
    }
}
