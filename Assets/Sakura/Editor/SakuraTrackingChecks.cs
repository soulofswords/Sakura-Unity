using System;
using System.IO;
using System.Reflection;
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
            int checks = 0;
            void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
            Check(SakuraAvatarViewport.NormalizeCursor(new Vector2(150, 90), new Vector2(150, 90), new Vector2(320, 640)) == Vector2.zero, "Compact gaze origin follows the avatar");
            Check(SakuraAvatarViewport.NormalizeCursor(new Vector2(310, 410), new Vector2(150, 90), new Vector2(320, 640)) == Vector2.one, "Gaze uses actual stage dimensions");
            Check(Mathf.Approximately(SakuraTrackingRig.Blink(0), 0), "Open eyes must stay open");
            Check(Mathf.Approximately(SakuraTrackingRig.Blink(.6f), 1), "Blink closure threshold");
            Check(!SakuraTrackingRig.FaceRotation(new JObject { ["matrix"] = new JArray(1, 2) }, out _), "Reject malformed matrices");
            Check(!SakuraTrackingRig.FaceRotation(new JObject { ["matrix"] = new JArray(new float[16]) }, out _), "Reject degenerate matrices");
            var identity = new JArray(); var matrix = Matrix4x4.identity; for (int index = 0; index < 16; index++) identity.Add(matrix[index]);
            Check(SakuraTrackingRig.FaceRotation(new JObject { ["matrix"] = identity }, out var rotation) && Quaternion.Angle(rotation, Quaternion.identity) < .01f, "Identity rotation conversion");
            Check(!SakuraTrackingRig.ValidFrame(new JObject { ["bad"] = double.NaN }), "Reject nonfinite signals");
            Check(SakuraTrackingRig.ValidFrame(new JObject { ["face"] = new JObject { ["blendshapes"] = new JArray(new JObject { ["categoryName"] = "jawOpen", ["score"] = .5 }) } }), "Accept mobile blendshape payload");
            Check(!SakuraTrackingRig.ValidFrame(new JObject { ["face"] = new JObject { ["blendshapes"] = new JObject { ["jawOpen"] = "invalid" } } }), "Reject string scores");
            Check(SakuraTracking.PhoneTrackingStateForCommand("tracking-start") == true, "Phone start command enables tracking");
            Check(SakuraTracking.PhoneTrackingStateForCommand("companion-tracking-start") == true, "Phone companion start command enables tracking");
            Check(SakuraTracking.PhoneTrackingStateForCommand("tracking-stop") == false, "Phone stop command disables tracking");
            Check(SakuraTracking.PhoneTrackingStateForCommand("companion-tracking-stop") == false, "Phone companion stop command disables tracking");
            Check(SakuraTracking.PhoneTrackingStateForCommand("camera-start") == false, "Starting the phone camera stops face tracking");
            Check(SakuraTracking.PhoneTrackingStateForCommand("camera-stop") == null, "Stopping the phone camera does not restart face tracking");
            Check(SakuraTracking.PhoneDeckPoseForCommand("wave") == "wave", "Phone deck wave maps to a supported avatar pose");
            Check(SakuraTracking.PhoneDeckPoseForCommand("greeting_wave") == "greeting_wave", "Phone deck greeting maps to the authored gesture");
            Check(SakuraTracking.PhoneDeckPoseForCommand("stop") == "stop", "Phone deck stop maps to motion stop");
            Check(SakuraTracking.PhoneDeckPoseForCommand("run arbitrary command") == null, "Phone deck rejects unknown commands");
            Check(SakuraTracking.PhoneModelUrlForPath("category/My Model.pmx") == "/models/category/My%20Model.pmx", "Phone transfer URL encodes each path segment safely");
            Check(SakuraTracking.PhoneModelUrlForPath("../outside.pmx") == null, "Phone transfer rejects paths outside the model library");
            Check(SakuraTracking.ValidPhoneRequestId("0123456789abcdef0123456789abcdef"), "Accept server-generated phone transfer request id");
            Check(!SakuraTracking.ValidPhoneRequestId("../0123456789abcdef0123456789abcdef"), "Reject malformed phone transfer request id");
            Check(!SakuraTracking.ValidPhoneRequestId("0123456789ABCDEF0123456789ABCDEF"), "Reject non-canonical phone transfer request id");
            var root = new GameObject("Synthetic tracking check"); var mesh = new Mesh();
            try
            {
                var tracking = root.AddComponent<SakuraTracking>(); var rig = root.AddComponent<SakuraTrackingRig>();
                tracking.StartSelectedTracking("Sakura Mobile");
                Check(tracking.Source == SakuraTracking.InputSource.Phone, "Start tracking selects the Sakura Mobile source");
                Check(tracking.Status == "PC backend is unavailable.", "Start tracking requests the phone backend and reports when it is unavailable");
                var backendField = typeof(SakuraTracking).GetField("backend", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(backendField != null, "Tracking backend field is available for isolated offline check");
                var previousHands = tracking.Hands;
                backendField.SetValue(tracking, null);
                tracking.SetHandsTracking(!tracking.Hands);
                Check(tracking.Status == "Phone hand tracking unavailable: PC backend is not connected.", "Phone hand toggle reports that the remote change was not sent when the backend is unavailable");
                Check(tracking.Hands == previousHands, "Unavailable phone hand control does not change the reported tracking state");
                tracking.StopTracking();
                Check(tracking.Status == "PC backend is unavailable.", "Stop tracking preserves the missing-backend status");
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
            File.WriteAllText(Path.Combine(folder, "tracking-kernel-check.json"), new JObject { ["passed"] = true, ["checks"] = checks, ["physical_tracking_verified"] = false }.ToString());
            Debug.Log("Sakura tracking kernel checks passed (" + checks + "); physical tracking remains unverified.");
        }
    }
}
