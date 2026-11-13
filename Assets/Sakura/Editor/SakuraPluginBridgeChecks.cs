using System;
using System.Collections.Generic;
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

                var emitted = new List<(string name, JObject data)>();
                typeof(SakuraPluginBridge).GetField("eventSink", flags).SetValue(bridge, (Action<string, JObject>)((name, data) => emitted.Add((name, data))));
                var positionBefore = viewport.PluginPosition();
                reply = await Send("vts_model_move", new JObject { ["positionX"] = .1f, ["positionY"] = .1f, ["size"] = 1, ["rotation"] = 0 });
                if (!reply.Value<bool>("ok")) throw new Exception("Native plugin model move failed: " + reply.Value<string>("error"));
                if (!emitted.Exists(e => e.name == "ModelMovedEvent") || !emitted.Exists(e => e.name == "ModelConfigChangedEvent")) throw new Exception("Model move did not emit ModelMovedEvent and ModelConfigChangedEvent.");
                if (emitted.Find(e => e.name == "ModelMovedEvent").data["modelPosition"] == null) throw new Exception("ModelMovedEvent is missing modelPosition.");
                await Send("vts_model_move", positionBefore);
                emitted.Clear();
                var hotkeys = await Send("vts_hotkeys", new JObject());
                var expressionHotkey = ((JArray)hotkeys["data"]["availableHotkeys"]).FirstOrDefault(h => (h.Value<string>("hotkeyID") ?? "").StartsWith("expression:", StringComparison.Ordinal));
                if (expressionHotkey == null) throw new Exception("The loaded Live2D model exposes no expression hotkey.");
                string hotkeyId = expressionHotkey.Value<string>("hotkeyID");
                var expressionState = viewport.Expressions.Entries.Select(e => (e.Id, e.Active)).ToArray();
                reply = await Send("vts_hotkey", new JObject { ["hotkeyID"] = hotkeyId });
                if (!reply.Value<bool>("ok")) throw new Exception("Native plugin hotkey trigger failed: " + reply.Value<string>("error"));
                var hotkeyEvent = emitted.Find(e => e.name == "HotkeyTriggeredEvent");
                if (hotkeyEvent.name == null || hotkeyEvent.data.Value<string>("hotkeyID") != hotkeyId || !hotkeyEvent.data.Value<bool>("triggered")) throw new Exception("Hotkey trigger did not emit a correct HotkeyTriggeredEvent.");
                foreach (var state in expressionState) viewport.Expressions.SetActive(state.Id, state.Active);
                typeof(SakuraPluginBridge).GetField("eventSink", flags).SetValue(bridge, null);
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
        public static void ValidateEvents()
        {
            var root = new GameObject("Plugin event fixture");
            var events = new List<(string name, JObject data)>();
            try
            {
                var bridge = root.AddComponent<SakuraPluginBridge>();
                var backdrop = root.AddComponent<SakuraBackdrop>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(SakuraPluginBridge).GetField("eventSink", flags).SetValue(bridge, (Action<string, JObject>)((name, data) => events.Add((name, data))));
                var poll = typeof(SakuraPluginBridge).GetMethod("PollBackground", flags);

                poll.Invoke(bridge, null);
                if (events.Count != 0) throw new Exception("BackgroundChangedEvent fired before any background change.");

                backdrop.SetColour(new Color(.2f, .4f, .6f));
                poll.Invoke(bridge, null);
                if (events.Count != 1 || events[0].name != "BackgroundChangedEvent") throw new Exception("A background colour change did not emit BackgroundChangedEvent.");
                if (events[0].data["backgroundFileName"] == null) throw new Exception("BackgroundChangedEvent is missing backgroundFileName.");

                poll.Invoke(bridge, null);
                if (events.Count != 1) throw new Exception("An unchanged background re-emitted BackgroundChangedEvent.");

                backdrop.SetSolid(true);
                poll.Invoke(bridge, null);
                if (events.Count != 2 || events[1].name != "BackgroundChangedEvent") throw new Exception("A solid-background change did not emit BackgroundChangedEvent.");

                events.Clear();
                bridge.Emit("");
                if (events.Count != 0) throw new Exception("An empty event name was emitted.");
                bridge.Emit("ModelMovedEvent", new JObject { ["modelPosition"] = new JObject { ["positionX"] = 0 } });
                bridge.Emit("HotkeyTriggeredEvent", new JObject { ["hotkeyID"] = "expression:smile", ["triggered"] = true });
                if (events.Count != 2 || events[0].name != "ModelMovedEvent" || events[1].name != "HotkeyTriggeredEvent") throw new Exception("The plugin event seam did not capture lifecycle events.");
                if (events[0].data["modelPosition"] == null || events[1].data.Value<bool>("triggered") != true) throw new Exception("Lifecycle event payloads were not preserved.");
                Debug.Log("SAKURA_PLUGIN_EVENT_CHECKS_OK: background change emission, dedup, first-sight baseline, solid toggle, empty-name guard, model-move/hotkey payload seam. No external listener or WebSocket opened.");
            }
            finally
            {
                PlayerPrefs.DeleteKey("sakura.backdrop.colour"); PlayerPrefs.DeleteKey("sakura.backdrop.solid");
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
