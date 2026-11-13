using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(-6)]
    public sealed class SakuraParameterMappings : MonoBehaviour
    {
        [Serializable] public sealed class Mapping
        {
            public string input, target;
            public bool enabled = true;
            public float inputMin, inputMax = 1, outputMin, outputMax = 1, smoothing = .08f;
        }
        [Serializable] public sealed class Profile { public int version = 1; public List<Mapping> mappings = new List<Mapping>(); }
        private Profile profile = new Profile();
        private SakuraAvatarViewport viewport;
        private SakuraTracking tracking;
        private SakuraPluginBridge plugins;
        private GameObject avatar;
        private CubismModel model;
        private SakuraParameterMappingRig rig;
        private string preference;
        public string Status { get; private set; } = "Load a Live2D model to configure parameter inputs.";
        public event Action Changed;
        public IReadOnlyList<Mapping> Mappings => profile.mappings;
        public void Initialize(SakuraBackend backend, SakuraAvatarViewport avatars) { viewport = avatars; tracking = backend.GetComponent<SakuraTracking>(); plugins = backend.GetComponent<SakuraPluginBridge>(); }
        public static Profile ParseProfile(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 65536 || Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("Parameter mapping profile must be under 64 KB.");
            JObject data;
            try { data = JObject.Parse(json); }
            catch (JsonException error) { throw new ArgumentException("Parameter mapping profile is invalid JSON.", error); }
            if (data["version"]?.Type != JTokenType.Integer || data["version"].ToString() != "1" || !(data["mappings"] is JArray rows) || rows.Count > 64) throw new ArgumentException("Use a version 1 profile with up to 64 mappings.");
            var result = new Profile(); var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in rows)
            {
                if (!(token is JObject row) || row["input"]?.Type != JTokenType.String || row["target"]?.Type != JTokenType.String || row["enabled"]?.Type != JTokenType.Boolean) throw new ArgumentException("Every mapping needs input, target and enabled fields.");
                foreach (string key in new[] { "inputMin", "inputMax", "outputMin", "outputMax", "smoothing" })
                    if (row[key]?.Type != JTokenType.Integer && row[key]?.Type != JTokenType.Float) throw new ArgumentException("Mapping ranges and smoothing must be numeric.");
                Mapping mapping;
                try { mapping = row.ToObject<Mapping>(); }
                catch (JsonException error) { throw new ArgumentException("Mapping values are invalid.", error); }
                Validate(mapping);
                if (!targets.Add(mapping.target)) throw new ArgumentException("Each output parameter can have one mapping.");
                result.mappings.Add(mapping);
            }
            return result;
        }
        public static void Validate(Mapping mapping)
        {
            if (mapping == null || string.IsNullOrWhiteSpace(mapping.input) || string.IsNullOrWhiteSpace(mapping.target) || mapping.input.Length > 160 || mapping.target.Length > 160 || mapping.input.Any(char.IsControl) || mapping.target.Any(char.IsControl)) throw new ArgumentException("Choose input and output names of up to 160 characters.");
            foreach (float value in new[] { mapping.inputMin, mapping.inputMax, mapping.outputMin, mapping.outputMax, mapping.smoothing })
                if (!float.IsFinite(value) || Math.Abs(value) > 1000000) throw new ArgumentException("Mapping numbers must be finite and within ±1000000.");
            if (mapping.inputMin == mapping.inputMax || mapping.smoothing < 0 || mapping.smoothing > 1) throw new ArgumentException("Input bounds must differ. Smoothing must be between 0 and 1 second.");
        }
        public static float MapValue(Mapping mapping, float input, float previous, float delta)
        {
            if (!float.IsFinite(input) || !float.IsFinite(previous) || !float.IsFinite(delta)) throw new ArgumentException("Input values and time must be finite.");
            float position = Mathf.Clamp01((input - mapping.inputMin) / (mapping.inputMax - mapping.inputMin));
            float desired = Mathf.Lerp(mapping.outputMin, mapping.outputMax, position);
            return mapping.smoothing <= 0 ? desired : Mathf.Lerp(previous, desired, 1 - Mathf.Exp(-Mathf.Clamp(delta, 0, .1f) / mapping.smoothing));
        }
        public bool TryInput(string input, out float value)
        {
            value = 0;
            if (string.IsNullOrEmpty(input)) return false;
            bool found = input.StartsWith("plugin:", StringComparison.Ordinal) ? plugins != null && plugins.TryCustomParameter(input.Substring(7), out value) : tracking != null && tracking.GetInputValue(input, out value);
            return found && float.IsFinite(value);
        }
        private List<string> InputNames()
        {
            if (tracking == null) tracking = GetComponent<SakuraTracking>();
            if (plugins == null) plugins = GetComponent<SakuraPluginBridge>();
            return (tracking?.InputNames ?? Array.Empty<string>()).Concat((plugins?.CustomParameterNames ?? Array.Empty<string>()).Select(name => "plugin:" + name)).Distinct().Take(600).ToList();
        }
        private void Update()
        {
            if (viewport == null) viewport = GetComponent<SakuraAvatarViewport>();
            if (viewport == null || avatar == viewport.AvatarRoot) return;
            rig?.Restore(); avatar = viewport.AvatarRoot;
            model = avatar == null ? null : avatar.GetComponent<CubismModel>();
            profile = new Profile(); preference = null;
            if (model != null)
            {
                preference = SakuraAvatarExpressions.PreferenceKey(viewport.LoadedModelPath) + ":parameter-mappings";
                try { if (PlayerPrefs.HasKey(preference)) profile = ParseProfile(PlayerPrefs.GetString(preference)); Status = profile.mappings.Count + " mappings loaded."; }
                catch (ArgumentException error) { Status = error.Message + " The saved profile has been kept."; }
                rig = avatar.GetComponent<SakuraParameterMappingRig>() ?? avatar.AddComponent<SakuraParameterMappingRig>(); rig.Initialize(this, model, viewport.Expressions);
            }
            else { rig = null; Status = "Parameter input mappings are available for Live2D models."; }
            Changed?.Invoke();
        }
        private void Commit(Profile candidate)
        {
            string json = JsonConvert.SerializeObject(candidate, Formatting.None);
            profile = ParseProfile(json); rig?.Rebind();
            try { if (preference != null) { PlayerPrefs.SetString(preference, json); PlayerPrefs.Save(); } Status = profile.mappings.Count + " mappings saved for this model."; }
            catch (Exception error) when (error is UnityException || error is System.IO.IOException) { Status = "Mappings are active for this session. Storage could not save them."; }
            Changed?.Invoke();
        }
        private void Edit(Action<Profile> change)
        {
            try { var candidate = ParseProfile(JsonConvert.SerializeObject(profile)); change(candidate); Commit(candidate); }
            catch (ArgumentException error) { Status = error.Message; Changed?.Invoke(); }
        }
        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("Tracking input mappings"); title.AddToClassList("card-title"); card.Add(title);
            var status = new Label(Status); status.AddToClassList("description"); card.Add(status);
            var rows = new VisualElement(); card.Add(rows);
            var actions = new VisualElement(); actions.AddToClassList("row"); card.Add(actions);
            var add = new Button(() => Edit(candidate =>
            {
                var input = InputNames().FirstOrDefault(); var target = model?.Parameters.FirstOrDefault(parameter => candidate.mappings.All(mapping => mapping.target != parameter.Id));
                if (input == null || target == null) throw new ArgumentException("Connect a tracker or plugin and choose a Live2D model with an unmapped parameter.");
                candidate.mappings.Add(new Mapping { input = input, target = target.Id, outputMin = target.MinimumValue, outputMax = target.MaximumValue });
            })) { text = "Add mapping" }; actions.Add(add);
            actions.Add(new Button(() => Changed?.Invoke()) { text = "Refresh input names" });
            Action render = () =>
            {
                rows.Clear(); status.text = Status; add.SetEnabled(model != null && profile.mappings.Count < 64);
                for (int index = 0; index < profile.mappings.Count; index++)
                {
                    int captured = index; var mapping = profile.mappings[index];
                    var fold = new Foldout { text = mapping.input + " → " + mapping.target, value = false }; rows.Add(fold);
                    bool built = false;
                    fold.RegisterValueChangedCallback(evt =>
                    {
                        if (!evt.newValue || built) return; built = true;
                        var draft = JsonConvert.DeserializeObject<Mapping>(JsonConvert.SerializeObject(mapping));
                        var inputs = InputNames(); if (!inputs.Contains(draft.input)) inputs.Insert(0, draft.input);
                        var input = new DropdownField("Source input", inputs, inputs.IndexOf(draft.input)); fold.Add(input); input.RegisterValueChangedCallback(change => draft.input = change.newValue);
                        var targets = model.Parameters.Select(parameter => parameter.Id).ToList(); if (!targets.Contains(draft.target)) targets.Insert(0, draft.target);
                        var target = new DropdownField("Output parameter", targets, targets.IndexOf(draft.target)); fold.Add(target); target.RegisterValueChangedCallback(change => draft.target = change.newValue);
                        var enabled = new Toggle("Enabled") { value = draft.enabled }; fold.Add(enabled); enabled.RegisterValueChangedCallback(change => draft.enabled = change.newValue);
                        Number("Input minimum", draft.inputMin, value => draft.inputMin = value, fold); Number("Input maximum", draft.inputMax, value => draft.inputMax = value, fold);
                        Number("Output minimum", draft.outputMin, value => draft.outputMin = value, fold); Number("Output maximum", draft.outputMax, value => draft.outputMax = value, fold);
                        var smoothing = new Slider("Smoothing (seconds)", 0, 1) { value = draft.smoothing, showInputField = true }; fold.Add(smoothing); smoothing.RegisterValueChangedCallback(change => draft.smoothing = change.newValue);
                        var current = new Label(); fold.Add(current);
                        var poll = current.schedule.Execute(() => current.text = TryInput(draft.input, out float value) ? "Live input " + value.ToString("0.###") : "Input unavailable · waiting for a fresh tracker or plugin value.").Every(200);
                        current.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
                        fold.Add(new Button(() => Edit(candidate => candidate.mappings[captured] = draft)) { text = "Save mapping" });
                        fold.Add(new Button(() => Edit(candidate => candidate.mappings.RemoveAt(captured))) { text = "Remove mapping" });
                    });
                }
            };
            Changed += render; card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= render); render();
            var note = new Label("Uses current tracker signals or injected plugin inputs. Missing or expired inputs stop driving the parameter. Manual parameter values and expressions take priority. Each output supports one mapping."); note.AddToClassList("description"); card.Add(note);
        }
        private static void Number(string title, float value, Action<float> change, VisualElement parent) { var field = new FloatField(title) { value = value }; field.RegisterValueChangedCallback(evt => change(evt.newValue)); parent.Add(field); }
        private void OnDisable() { rig?.Restore(); }
    }

    [DefaultExecutionOrder(-6)]
    public sealed class SakuraParameterMappingRig : MonoBehaviour, ICubismUpdatable
    {
        private sealed class Binding { public SakuraParameterMappings.Mapping Mapping; public CubismParameter Parameter; public SakuraModelTools.ParameterSettings Manual; public float Current; public bool Ready; }
        private SakuraParameterMappings owner;
        private CubismModel model;
        private SakuraAvatarExpressions expressions;
        private readonly List<Binding> bindings = new List<Binding>();
        private readonly Dictionary<CubismParameter, (float before, float after)> writes = new Dictionary<CubismParameter, (float, float)>();
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 875;
        public bool NeedsUpdateOnEditing => false;
        public void Initialize(SakuraParameterMappings source, CubismModel cubism, SakuraAvatarExpressions controller)
        {
            Restore(); owner = source; model = cubism; expressions = controller; Rebind();
            var updates = GetComponent<CubismUpdateController>(); HasUpdateController = updates != null; updates?.Refresh();
        }
        public void Rebind()
        {
            Restore(); bindings.Clear(); var tools = GetComponent<SakuraModelTools>();
            foreach (var mapping in owner.Mappings)
            {
                var parameter = model.Parameters.FirstOrDefault(value => value.Id == mapping.target);
                if (parameter != null) bindings.Add(new Binding { Mapping = mapping, Parameter = parameter, Manual = tools?.GetParameter(parameter.Id) });
            }
        }
        public void Restore()
        {
            foreach (var row in writes) if (row.Key != null && Mathf.Abs(row.Key.Value - row.Value.after) < .00001f) row.Key.Value = row.Value.before;
            writes.Clear();
        }
        private void Update() { Restore(); }
        private bool ManualOwns(string id)
        {
            if (expressions == null) return false;
            foreach (var entry in expressions.Entries)
                if ((entry.Active || entry.PreviewUntil > Time.unscaledTime) && (!entry.Id.StartsWith("parameter:", StringComparison.Ordinal) || entry.Id == "parameter:" + id)) return true;
            return false;
        }
        public void OnLateUpdate()
        {
            if (!isActiveAndEnabled || owner == null || !owner.isActiveAndEnabled) return;
            foreach (var binding in bindings)
            {
                if (!binding.Mapping.enabled || binding.Parameter == null || binding.Manual?.enabled == true || ManualOwns(binding.Parameter.Id) || !owner.TryInput(binding.Mapping.input, out float input))
                { binding.Ready = false; continue; }
                float before = binding.Parameter.Value;
                if (!binding.Ready) { binding.Current = before; binding.Ready = true; }
                binding.Current = SakuraParameterMappings.MapValue(binding.Mapping, input, binding.Current, Time.unscaledDeltaTime);
                float after = Mathf.Clamp(binding.Current, binding.Parameter.MinimumValue, binding.Parameter.MaximumValue);
                binding.Parameter.Value = after; writes[binding.Parameter] = (before, after);
            }
        }
        private void LateUpdate() { if (!HasUpdateController) OnLateUpdate(); }
        private void OnDisable() { Restore(); }
    }
}
