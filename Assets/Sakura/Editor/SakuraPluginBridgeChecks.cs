using System;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;
using System.Threading;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraPluginBridgeChecks
    {
        public static async Task VerifyViewport()
        {
            if (!Application.isPlaying) throw new Exception("Keep the existing Play session running for plugin renderer verification.");
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>(); var backend = UnityEngine.Object.FindFirstObjectByType<SakuraBackend>();
            if (viewport == null || backend == null || viewport.LoadedFormat != "Live2D" || !viewport.HasAvatar) throw new Exception("Load a Live2D avatar in the current session first.");
            var model = viewport.AvatarRoot.GetComponent<CubismModel>();
            var rig = viewport.AvatarRoot.GetComponent<SakuraModelTools>()?.Live2DPhysicsRig;
            if (model == null || model.Parameters.Length == 0 || rig == null) throw new Exception("Use a loaded Live2D avatar with native parameters and authored physics.");
            var currentAvatar = viewport.AvatarRoot; string currentPath = viewport.LoadedModelPath;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); await viewport.LoadModel(backend.SourceFolder, currentPath, cancelled.Token);
                if (viewport.AvatarRoot != currentAvatar || viewport.LoadedModelPath != currentPath) throw new Exception("Pre-cancelled plugin model import changed the current avatar.");
            }
            var parameter = model.Parameters[0]; float before = parameter.Value;
            var oldDriver = viewport.AvatarRoot.GetComponent<SakuraPluginParameterDriver>();
            var oldOwner = oldDriver == null ? null : oldDriver.Owner;
            var fixture = new GameObject("Plugin renderer fixture"); var bridge = fixture.AddComponent<SakuraPluginBridge>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(SakuraPluginBridge).GetField("backend", flags).SetValue(bridge, backend); typeof(SakuraPluginBridge).GetField("viewport", flags).SetValue(bridge, viewport);
            var scales = rig.SubRigs.Select(group => group.Output.Select(output => (output.AngleScale, output.TranslationScale)).ToArray()).ToArray();
            async Task<JObject> Send(string command, JObject args) => await bridge.Dispatch(new JObject { ["id"] = "fixture", ["command"] = command, ["args"] = args });
            try
            {
                var reply = await Send("vts_state", new JObject()); if (!reply.Value<bool>("ok") || !reply["data"].Value<bool>("modelLoaded")) throw new Exception("Plugin current model state failed.");
                float target = Mathf.Lerp(parameter.MinimumValue, parameter.MaximumValue, .75f);
                reply = await Send("vts_inject", new JObject { ["parameterValues"] = new JArray(new JObject { ["id"] = parameter.Id, ["value"] = target }) });
                bridge.OnLateUpdate();
                if (!reply.Value<bool>("ok") || Mathf.Abs(parameter.Value - target) > .0001f) throw new Exception("Native plugin injection did not reach the Cubism parameter.");
                reply = await Send("vts_physics_get", new JObject { ["_vtsCanonical"] = true });
                if (!reply.Value<bool>("ok") || !reply["data"].Value<bool>("modelHasPhysics") || ((JArray)reply["data"]["physicsGroups"]).Count == 0) throw new Exception("Native physics group metadata was not returned.");
                var args = new JObject { ["_vtsCanonical"] = true, ["_pluginIdentity"] = "fixtureA", ["_pluginName"] = "Fixture A", ["strengthOverrides"] = new JArray(new JObject { ["setBaseValue"] = true, ["value"] = 25, ["overrideSeconds"] = .5 }) };
                reply = await Send("vts_physics_set", args); if (!reply.Value<bool>("ok")) throw new Exception("Native physics override failed: " + reply.Value<string>("error"));
                for (int group = 0; group < rig.SubRigs.Length; group++)
                    for (int index = 0; index < rig.SubRigs[group].Output.Length; index++)
                        if (Mathf.Abs(rig.SubRigs[group].Output[index].AngleScale - scales[group][index].AngleScale * .5f) > .001f) throw new Exception("Physics output strength was not applied.");
                args["_pluginIdentity"] = "fixtureB"; reply = await Send("vts_physics_set", args);
                if (reply.Value<bool>("ok")) throw new Exception("Second plugin took over another plugin's active physics lease.");
                await Task.Delay(650);
                reply = await Send("vts_physics_get", new JObject { ["_vtsCanonical"] = true });
                if (reply["data"].Value<bool>("apiPhysicsOverrideActive")) throw new Exception("Physics lease did not expire.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
                var driver = viewport.AvatarRoot.GetComponent<SakuraPluginParameterDriver>();
                if (oldDriver == null && driver != null) { UnityEngine.Object.DestroyImmediate(driver); viewport.AvatarRoot.GetComponent<CubismUpdateController>()?.Refresh(); }
                else if (oldDriver != null) oldDriver.Owner = oldOwner;
                parameter.Value = before;
            }
            for (int group = 0; group < rig.SubRigs.Length; group++)
                for (int index = 0; index < rig.SubRigs[group].Output.Length; index++)
                    if (rig.SubRigs[group].Output[index].AngleScale != scales[group][index].AngleScale || rig.SubRigs[group].Output[index].TranslationScale != scales[group][index].TranslationScale) throw new Exception("Plugin cleanup did not restore authored native physics output scales.");
            Debug.Log("SAKURA_PLUGIN_RENDERER_CHECKS_OK: native current state, Cubism injection, authored physics metadata, real strength override, plugin ownership, lease expiry, cleanup restoration. No external listener or WebSocket opened.");
        }
        public static async Task Verify()
        {
            var valid = JObject.Parse("{\"id\":\"abc\",\"command\":\"vts_state\",\"args\":{}}");
            SakuraPluginBridge.ValidateMessage(valid);
            int rejected = 0;
            foreach (var message in new[]
            {
                JObject.Parse("{\"id\":\"abc\",\"command\":\"vts_state\",\"args\":[]}"),
                JObject.Parse("{\"id\":\"abc\",\"command\":\"vts_inject\",\"args\":{\"value\":1e30}}"),
                JObject.Parse("{\"id\":\"abc\",\"command\":\"vts_state/escape\",\"args\":{}}")
            })
                try { SakuraPluginBridge.ValidateMessage(message); } catch (ArgumentException) { rejected++; }
            if (rejected != 3) throw new Exception("Plugin command validation accepted malformed input.");
            var entry = new CharacterEntry { path = "live2d/My Avatar/a.model3.json", name = "Avatar", format = "Live2D" };
            if (SakuraPluginBridge.ModelId(entry) != "/models/live2d/My%20Avatar/a.model3.json") throw new Exception("Plugin catalog ID was not encoded per path segment.");
            var root = new GameObject("Plugin bridge validation fixture");
            try
            {
                var bridge = root.AddComponent<SakuraPluginBridge>();
                var reply = await bridge.Dispatch(valid);
                if (reply.Value<string>("id") != "abc" || reply.Value<bool>("ok") != true || reply["data"].Value<bool>("modelLoaded")) throw new Exception("Empty native model state failed.");
                reply = await bridge.Dispatch(JObject.Parse("{\"id\":\"x\",\"command\":\"unsupported_command\",\"args\":{}}"));
                if (reply.Value<bool>("ok") || string.IsNullOrEmpty(reply.Value<string>("error"))) throw new Exception("Unsupported command pretended success.");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); reply = await bridge.Dispatch(valid, cancelled.Token);
                    if (reply.Value<bool>("ok")) throw new Exception("Pre-cancelled plugin dispatch executed.");
                }
                Debug.Log("SAKURA_PLUGIN_BRIDGE_CHECKS_OK: bounded JSON validation, finite numbers, catalog path encoding, native empty state, correlated explicit unsupported errors. External listener and network remain off.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
