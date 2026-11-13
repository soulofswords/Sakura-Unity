using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraAvatarReplyChecks
    {
        public static async Task Verify()
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (!Application.isPlaying || studio == null) throw new Exception("Enter Studio Play mode.");
            if (!SakuraBackend.IsRoomQuestion("How do you like your room?") || !SakuraBackend.IsRoomQuestion("कमरा कैसा है?") || SakuraBackend.IsRoomQuestion("hello"))
                throw new Exception("Room inspection routing differs from the reference.");
            var backend = studio.GetComponent<SakuraBackend>();
            var viewport = studio.GetComponent<SakuraAvatarViewport>();
            float importDeadline = Time.realtimeSinceStartup + 40;
            while (viewport.Loading && Time.realtimeSinceStartup < importDeadline) await Task.Delay(100);
            if (viewport.Loading) throw new Exception("The initial model import did not settle before reply checks.");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var busy = typeof(SakuraBackend).GetField("<Busy>k__BackingField", flags);
            var generation = typeof(SakuraBackend).GetField("chatGeneration", flags);
            int before = (int)generation.GetValue(backend);
            busy.SetValue(backend, true); backend.InterruptChat();
            if (backend.Busy || (int)generation.GetValue(backend) != before + 1)
                throw new Exception("Interrupt did not release chat ownership and invalidate late replies.");
            studio.SwitchMode("Companion");
            before = (int)generation.GetValue(backend);
            backend.SendChat("Unsent lifecycle check one");
            int first = (int)generation.GetValue(backend);
            busy.SetValue(backend, false);
            backend.SendChat("Unsent lifecycle check two");
            int second = (int)generation.GetValue(backend);
            backend.InterruptChat();
            if (first != before + 1 || second != first + 1)
                throw new Exception("A newer message did not invalidate speech callbacks from its predecessor.");
            string previous = viewport.LoadedModelPath;
            string invalid = await backend.ApplyModelSelection(new JObject { ["avatar_model"] = "../settings.json" });
            if (!invalid.StartsWith("The outfit did not load") || viewport.LoadedModelPath != previous)
                throw new Exception("Invalid model reply altered the avatar or claimed success.");
            string loaded = await backend.ApplyModelSelection(new JObject { ["avatar_model"] = "pmx/Ringo/Ringo.pmx" });
            if (loaded != "Changed to Ringo." || viewport.LoadedModelPath != "pmx/Ringo/Ringo.pmx")
                throw new Exception("Chat model selection did not load the real installed character: " + loaded);
            using (var cancelled = new System.Threading.CancellationTokenSource())
            {
                cancelled.Cancel(); var originalAvatar = viewport.AvatarRoot;
                await viewport.LoadModel(backend.SourceFolder, "pmx/Ringo/Ringo.pmx", cancelled.Token);
                if (viewport.AvatarRoot != originalAvatar || viewport.Loading) throw new Exception("A cancelled import replaced the avatar or retained its loading lock.");
            }
            var placement = viewport.PluginPosition();
            try
            {
                var moved = viewport.PluginMove(new JObject { ["positionX"] = .3f, ["positionY"] = -.2f, ["size"] = 1.2f, ["rotation"] = 20 });
                if (Math.Abs(moved.Value<float>("positionX") - .3f) > .001f || Math.Abs(moved.Value<float>("positionY") + .2f) > .001f || moved.Value<float>("rotation") != 20)
                    throw new Exception("Plugin movement did not move the native viewport.");
                bool invalidMove = false;
                try { viewport.PluginMove(new JObject { ["size"] = 2, ["positionY"] = double.NaN }); }
                catch (ArgumentException) { invalidMove = true; }
                if (!invalidMove || !JToken.DeepEquals(moved, viewport.PluginPosition())) throw new Exception("Invalid plugin movement changed the viewport.");
            }
            finally { viewport.PluginMove(placement); }
            string frame = viewport.CaptureVisionFrame();
            var jpeg = Convert.FromBase64String(frame);
            if (jpeg.Length > 2000000 || jpeg.Length < 3 || jpeg[0] != 255 || jpeg[1] != 216 || jpeg[2] != 255)
                throw new Exception("Native rendered vision context was not a bounded JPEG.");
            studio.SwitchMode("VTuber");
            string denied = await backend.ApplyModelSelection(new JObject { ["avatar_model"] = "../settings.json" });
            if (!denied.Contains("Companion mode")) throw new Exception("VTuber mode accepted an AI model-change reply.");
            studio.SwitchMode("Companion");
            var handler = typeof(SakuraStudio).GetMethod("ApplyAvatarReply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            handler.Invoke(studio, new object[] { new JObject { ["avatar_picker"] = true, ["avatar_query"] = "Ringo" } });
            if (studio.CurrentPanel != "Character" || studio.GetComponent<UIDocument>().rootVisualElement.Q<TextField>(className: "search").value != "Ringo")
                throw new Exception("Chat model-picker request did not open the searchable library.");
            Debug.Log("SAKURA_AVATAR_REPLY_CHECKS_OK: installed model loading, truthful success, traversal rejection, VTuber isolation, library search.");
        }
    }
}
