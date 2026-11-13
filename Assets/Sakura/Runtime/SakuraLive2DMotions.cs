using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Framework.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(-9)]
    public sealed class SakuraLive2DMotions : MonoBehaviour, ICubismUpdatable
    {
        public sealed class Motion { public string File, Name; }
        private readonly List<Motion> motions = new List<Motion>();
        private readonly HashSet<string> held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<CubismParameter, (float before, float after)> writes = new Dictionary<CubismParameter, (float, float)>();
        private CubismParameter[] targets = Array.Empty<CubismParameter>();
        private float[] before = Array.Empty<float>();
        private CubismModel model;
        private AnimationClip clip;
        private string root;
        private float started, duration;
        private bool loop;
        public string ActiveFile { get; private set; }
        public bool Playing => clip != null && ActiveFile != null;
        public string Status { get; private set; } = "No authored motion is playing.";
        public IReadOnlyList<Motion> Motions => motions;
        public event Action Changed;
        public event Action<string, bool> ActiveChanged;
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 830;
        public bool NeedsUpdateOnEditing => false;

        public void Initialize(string modelPath, SakuraVtubeHotkeys.Profile imported = null)
        {
            Stop(); motions.Clear(); model = GetComponent<CubismModel>();
            if (model == null) throw new ArgumentException("Authored Live2D motions require a Cubism model.");
            root = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            if (new FileInfo(modelPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("The model manifest exceeds 8 MB.");
            var manifest = JObject.Parse(File.ReadAllText(modelPath));
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string relative, string name)
            {
                if (motions.Count >= 500 || string.IsNullOrEmpty(relative) || !relative.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    string resolved = SakuraModelImport.ResolveFile(root, relative);
                    if (!File.Exists(resolved) || new FileInfo(resolved).Length > 8 * 1024 * 1024 || !known.Add(resolved)) return;
                    motions.Add(new Motion { File = Path.GetRelativePath(root, resolved).Replace('\\', '/'), Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(relative).Replace(".motion3.json", "") : name.Trim() });
                }
                catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException) { Status = "Some authored motions could not be discovered: " + error.Message; }
            }
            if (manifest["FileReferences"]?["Motions"] is JObject groups)
                foreach (var group in groups.Properties())
                    foreach (var entry in (group.Value as JArray ?? new JArray()).OfType<JObject>().Take(500)) Add(entry.Value<string>("File"), group.Name + " · " + Path.GetFileName(entry.Value<string>("File") ?? ""));
            if (imported != null) foreach (var binding in imported.Bindings)
            {
                Add(binding.File, binding.Name);
                var named = motions.FirstOrDefault(motion => motion.File.Equals(binding.File?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (named != null && !string.IsNullOrWhiteSpace(binding.Name)) named.Name = binding.Name.Trim();
            }
            try { foreach (string file in SakuraVtubeHotkeys.Files(root).Where(path => path.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)).Take(500)) Add(Path.GetRelativePath(root, file), null); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Status = "Motion folder scan was incomplete: " + error.Message; }
            var updates = GetComponent<CubismUpdateController>(); HasUpdateController = updates != null; updates?.Refresh();
            Status = motions.Count + " authored motions available."; Changed?.Invoke();
        }
        public static JObject ValidateMotion(string json, IEnumerable<string> parameterIds)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024 || Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new ArgumentException("Authored motions must be under 8 MB.");
            JObject data;
            try { data = JObject.Parse(json); } catch (Newtonsoft.Json.JsonException error) { throw new ArgumentException("Authored motion JSON is invalid.", error); }
            double Number(JToken token)
            {
                if (token == null || token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new ArgumentException("Motion curve values must be numeric.");
                double value = token.Value<double>();
                if (!double.IsFinite(value) || Math.Abs(value) > 1000000) throw new ArgumentException("Motion curve values must be finite and bounded.");
                return value;
            }
            double length = Number(data["Meta"]?["Duration"]);
            double fps = Number(data["Meta"]?["Fps"]);
            if (length <= 0 || length > 3600 || fps <= 0 || fps > 240 || !(data["Curves"] is JArray curves) || curves.Count == 0 || curves.Count > 4096) throw new ArgumentException("Motion duration, frame rate or curve count is unsupported.");
            if (data["Meta"]?["Loop"] != null && data["Meta"]["Loop"].Type != JTokenType.Boolean) throw new ArgumentException("Motion loop must be true or false.");
            var allowed = new HashSet<string>(parameterIds, StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal);
            int total = 0;
            foreach (var token in curves)
            {
                if (!(token is JObject)) throw new ArgumentException("Every motion curve must be an object.");
                string id = token.Value<string>("Id");
                if (token.Value<string>("Target") != "Parameter" || string.IsNullOrEmpty(id) || id.Length > 160 || id.Contains("/") || id.Contains("\\") || id.Contains("..") || !allowed.Contains(id) || !used.Add(id)) throw new ArgumentException("Only unique parameters authored in this model can be animated.");
                if (!(token["Segments"] is JArray segments) || segments.Count < 2 || (total += segments.Count) > 500000) throw new ArgumentException("Motion segments are missing or excessive.");
                double priorTime = Number(segments[0]); Number(segments[1]);
                if (priorTime < 0 || priorTime > length) throw new ArgumentException("Motion key time is outside its duration.");
                for (int index = 2; index < segments.Count;)
                {
                    double type = Number(segments[index]);
                    if (type < 0 || type > 3 || type != Math.Truncate(type)) throw new ArgumentException("Motion contains an unsupported segment type.");
                    int count = type == 1 ? 6 : 2;
                    if (index + count >= segments.Count) throw new ArgumentException("Motion contains a truncated segment.");
                    for (int value = 1; value <= count; value++) Number(segments[index + value]);
                    double end = Number(segments[index + (type == 1 ? 5 : 1)]);
                    if (end < priorTime || end > length + .001) throw new ArgumentException("Motion key times must be ordered within its duration.");
                    if (type == 1 && (Number(segments[index + 1]) < priorTime || Number(segments[index + 1]) > Number(segments[index + 3]) || Number(segments[index + 3]) > end)) throw new ArgumentException("Bezier control times must be ordered.");
                    priorTime = end; index += count + 1;
                }
            }
            // User data does not drive native playback events.
            data.Remove("UserData"); return data;
        }
        public void Play(string relative, bool repeat) { StartMotion(relative, repeat); }
        public void SetWeight(string relative, float weight)
        {
            if (weight > 0)
            {
                if (held.Contains(relative)) return;
                try { StartMotion(relative, null); }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is Newtonsoft.Json.JsonException || error is UnauthorizedAccessException)
                { held.Remove(relative); ActiveChanged?.Invoke(relative, false); Status = error.Message; Changed?.Invoke(); }
            }
            else if (held.Remove(relative) && string.Equals(ActiveFile, relative, StringComparison.OrdinalIgnoreCase)) Stop();
        }
        private void StartMotion(string relative, bool? repeat)
        {
            var motion = motions.FirstOrDefault(row => row.File.Equals(relative, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Choose an authored motion from this model.");
            string file = SakuraModelImport.ResolveFile(root, motion.File);
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("The authored motion exceeds 8 MB.");
            var data = ValidateMotion(File.ReadAllText(file), model.Parameters.Select(parameter => parameter.Id));
            AnimationClip candidate = null;
            try
            {
                candidate = CubismMotion3Json.LoadFrom(data.ToString(Newtonsoft.Json.Formatting.None), false).ToAnimationClip();
                if (candidate == null || !float.IsFinite(candidate.length) || candidate.length <= 0) throw new ArgumentException("The authored motion has no valid animated duration.");
            }
            catch { if (candidate != null) Destroy(candidate); throw; }
            Stop(); clip = candidate; duration = data["Meta"].Value<float>("Duration"); started = Time.unscaledTime;
            loop = repeat ?? data["Meta"].Value<bool?>("Loop") ?? false;
            targets = ((JArray)data["Curves"]).Select(curve => model.Parameters.First(parameter => parameter.Id == curve.Value<string>("Id"))).ToArray(); before = new float[targets.Length];
            ActiveFile = motion.File; held.Add(motion.File); ActiveChanged?.Invoke(motion.File, true);
            Status = "Playing " + motion.Name + (loop ? " · repeating" : " · once"); Changed?.Invoke();
        }
        public void Stop()
        {
            string stopped = ActiveFile; Restore(); ActiveFile = null;
            if (clip != null) Destroy(clip); clip = null; targets = Array.Empty<CubismParameter>(); before = Array.Empty<float>();
            if (stopped != null) { held.Remove(stopped); ActiveChanged?.Invoke(stopped, false); }
            Status = "No authored motion is playing."; Changed?.Invoke();
        }
        private void Restore()
        {
            foreach (var row in writes) if (row.Key != null && Mathf.Abs(row.Key.Value - row.Value.after) < .00001f) row.Key.Value = row.Value.before;
            writes.Clear();
        }
        private void Update() { Restore(); }
        public static float SampleTime(float elapsed, float length, bool repeat, out bool finished)
        {
            if (!float.IsFinite(elapsed) || !float.IsFinite(length) || length <= 0) throw new ArgumentException("Motion clock and duration must be finite.");
            elapsed = Math.Max(0, elapsed); finished = !repeat && elapsed >= length;
            return repeat ? elapsed % length : Math.Min(elapsed, length);
        }
        public void OnLateUpdate()
        {
            if (!isActiveAndEnabled || !Playing) return;
            float elapsed = Time.unscaledTime - started;
            float time = SampleTime(elapsed, duration, loop, out bool finished);
            if (finished) { Stop(); return; }
            for (int index = 0; index < targets.Length; index++) before[index] = targets[index].Value;
            clip.SampleAnimation(gameObject, time);
            for (int index = 0; index < targets.Length; index++)
            {
                var parameter = targets[index]; parameter.Value = Mathf.Clamp(parameter.Value, parameter.MinimumValue, parameter.MaximumValue);
                writes[parameter] = (before[index], parameter.Value);
            }
        }
        private void LateUpdate() { if (!HasUpdateController) OnLateUpdate(); }
        private void OnDisable() { Stop(); }
        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("Authored Live2D motions"); title.AddToClassList("card-title"); card.Add(title);
            var choices = new List<string> { "Choose a motion" }; choices.AddRange(motions.Select(motion => motion.Name + " · " + motion.File));
            var choice = new DropdownField("Motion", choices, 0); card.Add(choice);
            var repeat = new Toggle("Repeat motion"); card.Add(repeat);
            var status = new Label(Status); status.AddToClassList("description"); card.Add(status);
            card.Add(new Button(() => { try { if (choice.index > 0) Play(motions[choice.index - 1].File, repeat.value); else { Status = "Choose one of this model’s authored motions."; Changed?.Invoke(); } } catch (Exception error) when (error is ArgumentException || error is IOException || error is Newtonsoft.Json.JsonException) { Status = error.Message; Changed?.Invoke(); } }) { text = "Play motion" });
            card.Add(new Button(Stop) { text = "Stop motion" });
            Action refresh = () => status.text = Status; Changed += refresh; card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= refresh);
            if (motions.Count == 0) status.text = "This model has no discovered authored motions.";
        }
    }
}
