using System;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraParameterMappingsChecks
    {
        [MenuItem("Sakura/Validation/Check Live2D parameter input mappings")]
        public static void Run()
        {
            var mapping = new SakuraParameterMappings.Mapping { input = "jawOpen", target = "ParamMouthOpenY", inputMin = 0, inputMax = 1, outputMin = -10, outputMax = 10, smoothing = 0 };
            SakuraParameterMappings.Validate(mapping);
            Require(SakuraParameterMappings.MapValue(mapping, .5f, 0, .016f) == 0, "linear midpoint");
            Require(SakuraParameterMappings.MapValue(mapping, -1, 0, .016f) == -10 && SakuraParameterMappings.MapValue(mapping, 2, 0, .016f) == 10, "input range clamped");
            mapping.inputMin = 1; mapping.inputMax = 0;
            Require(SakuraParameterMappings.MapValue(mapping, 1, 0, .016f) == -10 && SakuraParameterMappings.MapValue(mapping, 0, 0, .016f) == 10, "inverted input supported");
            mapping.inputMin = 0; mapping.inputMax = 1; mapping.smoothing = .1f;
            float smoothed = SakuraParameterMappings.MapValue(mapping, 1, 0, .016f);
            Require(smoothed > 0 && smoothed < 10, "smoothing approaches mapped output");
            var profile = new SakuraParameterMappings.Profile(); profile.mappings.Add(mapping);
            string json = JsonConvert.SerializeObject(profile);
            var loaded = SakuraParameterMappings.ParseProfile(json);
            Require(loaded.mappings.Count == 1 && loaded.mappings[0].input == "jawOpen" && loaded.mappings[0].smoothing == .1f, "profile round trip");
            Require(Rejects("[]") && Rejects("{\"version\":\"x\",\"mappings\":[]}"), "invalid profile shapes and versions rejected");
            Require(Rejects(json.Replace("\"enabled\":true", "\"enabled\":\"true\"")), "string enabled rejected");
            Require(Rejects(json.Replace("\"smoothing\":0.1", "\"smoothing\":\"0.1\"")), "string numeric field rejected");
            profile.mappings.Add(mapping);
            Require(Rejects(JsonConvert.SerializeObject(profile)), "duplicate output rejected");
            profile.mappings.Clear();
            for (int index = 0; index < 65; index++) profile.mappings.Add(new SakuraParameterMappings.Mapping { input = "MouseX", target = "Param" + index });
            Require(Rejects(JsonConvert.SerializeObject(profile)), "65th mapping rejected");
            mapping.smoothing = float.NaN;
            bool invalid = false; try { SakuraParameterMappings.Validate(mapping); } catch (ArgumentException) { invalid = true; }
            Require(invalid, "nonfinite mapping rejected");
            mapping.smoothing = 0; mapping.inputMax = mapping.inputMin;
            invalid = false; try { SakuraParameterMappings.Validate(mapping); } catch (ArgumentException) { invalid = true; }
            Require(invalid, "zero-width input range rejected");
            Require(Rejects(new string(' ', 65537)), "profile length bounded");
            Debug.Log("PASS: Live2D mapping ranges, inversion, smoothing, profile round trip, JSON types, finite values, unique outputs and 64-mapping bound. No model settings or plugin/tracker state changed.");
        }
        private static bool Rejects(string json)
        { try { SakuraParameterMappings.ParseProfile(json); return false; } catch (ArgumentException) { return true; } }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Parameter mapping check failed: " + message); }
    }
}
