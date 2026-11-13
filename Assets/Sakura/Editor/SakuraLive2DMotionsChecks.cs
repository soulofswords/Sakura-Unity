using System;
using Live2D.Cubism.Framework.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraLive2DMotionsChecks
    {
        [MenuItem("Sakura/Validation/Check authored Live2D motion playback")]
        public static void Run()
        {
            var motion = SakuraMotionRecorder.BuildMotion(new[] { "ParamAngleX" }, new[] { 0f, .5f, 1f }, new[] { new[] { 0f }, new[] { 10f }, new[] { -10f } });
            motion["UserData"] = new JArray(new JObject { ["Time"] = .5, ["Value"] = "unused native event" });
            var safe = SakuraLive2DMotions.ValidateMotion(motion.ToString(), new[] { "ParamAngleX" });
            Require(safe["UserData"] == null, "playback event data removed");
            var clip = CubismMotion3Json.LoadFrom(safe.ToString(), false).ToAnimationClip();
            try { Require(clip != null && Math.Abs(clip.length - 1) < .01f, "validated motion loads through Cubism SDK"); }
            finally { if (clip != null) UnityEngine.Object.DestroyImmediate(clip); }
            var invalid = (JObject)motion.DeepClone(); invalid["Curves"][0]["Target"] = "Transform";
            Require(Rejects(invalid), "non-parameter animation rejected");
            invalid = (JObject)motion.DeepClone(); invalid["Curves"][0]["Id"] = "../ParamAngleX";
            Require(Rejects(invalid), "curve path traversal rejected");
            invalid = (JObject)motion.DeepClone(); invalid["Curves"][0]["Id"] = "UnknownParameter";
            Require(Rejects(invalid), "unknown model parameter rejected");
            invalid = (JObject)motion.DeepClone(); invalid["Curves"][0]["Segments"] = new JArray(0, 0, 1, .2, 1);
            Require(Rejects(invalid), "truncated Bezier segment rejected");
            invalid = (JObject)motion.DeepClone(); invalid["Curves"][0]["Segments"] = new JArray(0, 0, 0, .8, 1, 0, .4, 2);
            Require(Rejects(invalid), "backward key time rejected");
            invalid = (JObject)motion.DeepClone(); invalid["Meta"]["Duration"] = double.NaN;
            Require(Rejects(invalid), "nonfinite motion duration rejected");
            invalid = (JObject)motion.DeepClone(); ((JArray)invalid["Curves"]).Add(invalid["Curves"][0].DeepClone());
            Require(Rejects(invalid), "duplicate parameter curve rejected");
            Require(SakuraLive2DMotions.SampleTime(2.5f, 1, true, out bool finished) == .5f && !finished, "loop clock wraps");
            Require(SakuraLive2DMotions.SampleTime(1, 1, false, out finished) == 1 && finished, "one-shot end stops");
            Require(SakuraLive2DMotions.SampleTime(.5f, 1, false, out finished) == .5f && !finished, "one-shot active clock");
            Debug.Log("PASS: authored Live2D motion parameter whitelist, SDK clip import, segment grammar, finite clocks, traversal rejection and looping/end semantics. No model preferences or source files changed.");
        }
        private static bool Rejects(JObject motion)
        { try { SakuraLive2DMotions.ValidateMotion(motion.ToString(), new[] { "ParamAngleX" }); return false; } catch (ArgumentException) { return true; } }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Authored motion check failed: " + message); }
    }
}
