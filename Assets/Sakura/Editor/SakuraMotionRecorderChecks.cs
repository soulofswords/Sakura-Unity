using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Live2D.Cubism.Framework.Json;

namespace Sakura.Unity.Editor
{
    public static class SakuraMotionRecorderChecks
    {
        public static void Verify()
        {
            var motion = SakuraMotionRecorder.BuildMotion(new[] { "ParamAngleX", "Static" }, new[] { 0f, .5f, 1f }, new[] { new[] { 0f, 1f }, new[] { 10f, 1f }, new[] { -10f, 1f } });
            var curves = (JArray)motion["Curves"];
            if (curves.Count != 1 || ((JArray)curves[0]["Segments"]).Count != 8 || motion["Meta"].Value<int>("TotalSegmentCount") != 2) throw new Exception("Motion3 linear curves or static-parameter filtering are invalid.");
            var clip = CubismMotion3Json.LoadFrom(motion.ToString()).ToAnimationClip();
            try { if (Math.Abs(clip.length - 1) > .01) throw new Exception("The Cubism SDK did not read the recorded motion duration."); }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
            bool refused = false;
            try { SakuraMotionRecorder.BuildMotion(new[] { "x" }, new[] { 0f, float.NaN }, new[] { new[] { 0f }, new[] { 1f } }); } catch (ArgumentException) { refused = true; }
            if (!refused) throw new Exception("Nonfinite motion times were accepted.");
            Debug.Log("PASS: native motion3 curve structure, static-parameter omission, SDK parsing and finite frame validation. No model files changed.");
        }
    }
}
