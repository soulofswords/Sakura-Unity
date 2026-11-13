using System;
using System.Collections.Generic;
using System.Linq;
using Live2D.Cubism.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(33000)]
    public sealed class SakuraMotionRecorder : MonoBehaviour
    {
        public bool Recording { get; private set; }
        public string Status { get; private set; } = "Record Live2D parameters for up to 30 seconds, then save a motion3.json beside the model.";
        public float Duration { get; private set; }
        public event Action Changed;
        SakuraBackend backend;
        SakuraAvatarViewport viewport;
        CubismModel model;
        string modelPath;
        string[] ids;
        float started, next;
        readonly List<float> times = new List<float>();
        readonly List<float[]> frames = new List<float[]>();
        JObject recorded;
        bool saving;
        public void Initialize(SakuraBackend service, SakuraAvatarViewport avatars) { backend = service; viewport = avatars; }
        public void Begin()
        {
            if (saving || Recording) return;
            model = viewport?.LoadedFormat == "Live2D" ? viewport.AvatarRoot?.GetComponent<CubismModel>() : null;
            if (model == null || model.Parameters.Length == 0 || model.Parameters.Length > 512) { Notice("Choose a Live2D model with 1–512 parameters to record."); return; }
            modelPath = viewport.LoadedModelPath; ids = model.Parameters.Select(parameter => parameter.Id).ToArray();
            frames.Clear(); times.Clear(); recorded = null; Duration = 0; started = next = Time.unscaledTime; Recording = true;
            Notice("Recording… move the character, then press Stop.");
        }
        void LateUpdate()
        {
            if (!Recording) return;
            if (model == null || viewport.AvatarRoot != model.gameObject) { Recording = false; recorded = null; Notice("Recording cancelled because the selected model changed."); return; }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f / 30;
            Duration = Mathf.Min(30, Time.unscaledTime - started);
            times.Add(times.Count == 0 ? 0 : Duration);
            frames.Add(model.Parameters.Select(parameter => parameter.Value).ToArray());
            if (Duration >= 30 || frames.Count >= 901) Stop();
        }
        public static JObject BuildMotion(string[] ids, IReadOnlyList<float> times, IReadOnlyList<float[]> frames)
        {
            if (ids == null || ids.Length < 1 || ids.Length > 512 || times == null || frames == null || times.Count != frames.Count || times.Count < 2 || times.Count > 901)
                throw new ArgumentException("Use 2–901 frames with at most 512 parameters.");
            for (int frame = 0; frame < frames.Count; frame++)
                if (!float.IsFinite(times[frame]) || times[frame] < 0 || times[frame] > 30 || frame > 0 && times[frame] <= times[frame - 1]
                    || frames[frame] == null || frames[frame].Length != ids.Length || frames[frame].Any(value => !float.IsFinite(value)))
                    throw new ArgumentException("Motion frames must contain finite values and increasing times up to 30 seconds.");
            if (ids.Any(id => string.IsNullOrEmpty(id) || id.Length > 256) || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Use unique parameter IDs.");
            var curves = new JArray();
            for (int parameter = 0; parameter < ids.Length; parameter++)
            {
                float minimum = frames.Min(frame => frame[parameter]), maximum = frames.Max(frame => frame[parameter]);
                if (maximum - minimum < .0001f) continue;
                var segments = new JArray(Math.Round(times[0], 4), Math.Round(frames[0][parameter], 4));
                for (int frame = 1; frame < frames.Count; frame++) { segments.Add(0); segments.Add(Math.Round(times[frame], 4)); segments.Add(Math.Round(frames[frame][parameter], 4)); }
                curves.Add(new JObject { ["Target"] = "Parameter", ["Id"] = ids[parameter], ["Segments"] = segments });
            }
            return new JObject { ["Version"] = 3, ["Meta"] = new JObject { ["Duration"] = Math.Round(times.Last(), 3), ["Fps"] = 30, ["Loop"] = false,
                ["AreBeziersRestricted"] = true, ["CurveCount"] = curves.Count, ["TotalSegmentCount"] = curves.Count * (frames.Count - 1), ["TotalPointCount"] = curves.Count * frames.Count, ["UserDataCount"] = 0, ["TotalUserDataSize"] = 0 }, ["Curves"] = curves };
        }
        public void Stop()
        {
            if (!Recording) return;
            Recording = false;
            try { recorded = BuildMotion(ids, times, frames); Notice(((JArray)recorded["Curves"]).Count == 0 ? "No changing parameters were recorded." : "Recording stopped. Name it and save the motion."); }
            catch (ArgumentException error) { recorded = null; Notice(error.Message); }
        }
        public void Save(string name)
        {
            if (saving || recorded == null || ((JArray)recorded["Curves"]).Count == 0) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"\A[A-Za-z0-9 _\-.()]{1,80}\z")) { Notice("Use a motion name with letters, numbers, spaces, - _ . ( )."); return; }
            if (modelPath != viewport.LoadedModelPath) { Notice("Return to the recorded model before saving its motion."); return; }
            var payload = new JObject { ["model"] = modelPath, ["name"] = name, ["motion"] = recorded }.ToString(Formatting.None);
            if (payload.Length > 8 * 1024 * 1024) { Notice("The recording exceeds 8 MB. Record a shorter motion."); return; }
            saving = true; Notice("Saving motion…");
            backend.Api("/api/models/live2d-motion", payload, (json, error) =>
            {
                if (this == null) return;
                saving = false;
                if (error != null) { Notice(error); return; }
                try { Notice("Saved " + JObject.Parse(json).Value<string>("path") + ". Reload the model to discover the motion."); }
                catch (JsonException) { Notice("The motion save returned an unreadable response."); }
            });
        }
        void Notice(string text) { Status = text; Changed?.Invoke(); }
        void OnDisable() { Recording = false; }
        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card); card.Add(new Label("Record Live2D motion"));
            var name = new TextField("Motion name") { value = "Recorded motion" }; card.Add(name);
            var start = new Button(Begin) { text = "Record" }; card.Add(start);
            var stop = new Button(Stop) { text = "Stop" }; card.Add(stop);
            var save = new Button(() => Save(name.value)) { text = "Save motion3.json" }; card.Add(save);
            var status = new Label(Status); status.AddToClassList("description"); card.Add(status);
            var poll = card.schedule.Execute(() => { if (this == null) return; status.text = Status + " · " + Duration.ToString("0.0") + " s"; start.SetEnabled(!Recording && !saving); stop.SetEnabled(Recording); save.SetEnabled(!Recording && !saving && recorded != null && ((JArray)recorded["Curves"]).Count > 0); }).Every(100);
            card.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
        }
    }
}
