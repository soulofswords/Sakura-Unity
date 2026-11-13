using System;
using System.Linq;
using System.Threading.Tasks;
using Live2D.Cubism.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraCubismSyncChecks
    {
        public static void Validate()
        {
            var frame = SakuraCubismSync.BuildParameterFrame(
                new[] { "ParamAngleX", "ParamMouthOpenY", "", "invalid", new string('x', 257) },
                new[] { 12.5f, .75f, 1f, float.NaN, 1f });
            if (frame["parameters"] is not JArray parameters || parameters.Count != 2) throw new Exception("Cubism frame did not filter invalid parameter data.");
            if ((string)parameters[0]["Id"] != "ParamAngleX" || (float)parameters[0]["Value"] != 12.5f || parameters[0]["id"] != null)
                throw new Exception("Cubism frame differs from the backend Id/Value schema.");
            var bounded = SakuraCubismSync.BuildParameterFrame(Enumerable.Range(0, 2100).Select(index => "Param" + index).ToArray(), new float[2100]);
            if (((JArray)bounded["parameters"]).Count != 2000) throw new Exception("Cubism frame exceeded the backend parameter limit.");
            if (((JArray)SakuraCubismSync.ReadParameters(null)["parameters"]).Count != 0) throw new Exception("Missing Live2D model produced parameter values.");
            bool refused = false;
            try { SakuraCubismSync.BuildParameterFrame(new[] { "a" }, new float[0]); }
            catch (ArgumentException) { refused = true; }
            if (!refused) throw new Exception("Mismatched Cubism parameter arrays were accepted.");
            Debug.Log("PASS: native Cubism frame schema, finite values, identifier limits, 2000-parameter bound and missing-model behavior.");
        }

        public static void VerifyLiveParameters()
        {
            Validate();
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            var model = viewport?.AvatarRoot?.GetComponent<CubismModel>();
            if (model == null) throw new InvalidOperationException("Load a Live2D model before checking native parameter capture.");
            var before = model.Parameters.Select(parameter => parameter.Value).ToArray();
            var frame = SakuraCubismSync.ReadParameters(model);
            var parameters = (JArray)frame["parameters"];
            if (parameters.Count == 0) throw new Exception("Loaded native Live2D model produced no parameters.");
            if (!before.SequenceEqual(model.Parameters.Select(parameter => parameter.Value))) throw new Exception("Cubism streaming capture modified the rendered parameters.");
            var actual = model.Parameters.Take(2000).ToDictionary(parameter => parameter.Id, parameter => parameter.Value);
            foreach (var parameter in parameters)
                if (!actual.TryGetValue((string)parameter["Id"], out float value) || value != (float)parameter["Value"])
                    throw new Exception("Native Cubism capture lost the rendered parameter values.");
            Debug.Log("PASS: real native Cubism parameter capture preserves rendered expression and tracking values. No Editor connection was opened.");
        }

        public static async Task VerifyPanel()
        {
            Validate();
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new InvalidOperationException("Enter Studio Play mode first.");
            studio.SwitchMode("VTuber"); studio.ShowPanel("Plugins");
            await Task.Delay(400);
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            if (root.Q<IntegerField>("cubism-port")?.value != 22033 || root.Q("cubism-connect") == null || root.Q("cubism-disconnect") == null || root.Q("cubism-stream") == null)
                throw new Exception("Cubism plugin controls are missing.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-cubism-sync.png");
            Debug.Log("PASS: native Cubism plugin controls. No Editor connection or parameter streaming was enabled.");
        }
    }
}
