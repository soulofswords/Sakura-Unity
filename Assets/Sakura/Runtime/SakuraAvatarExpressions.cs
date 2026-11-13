using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UniVRM10;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(-10)]
    public sealed class SakuraAvatarExpressions : MonoBehaviour, ICubismUpdatable
    {
        public sealed class Entry
        {
            public string Id, Name, Label, Hotkey;
            public float Strength = 1;
            public bool Active;
            internal Action<float> Apply;
            internal float PreviewUntil;
            internal string Source;
        }
        public sealed class ActionEntry { public string Id, Name, Hotkey; internal string Source; internal Action Run; }
        [Serializable] sealed class SavedEntry { public string id, label, hotkey, source; public float strength = 1; }
        [Serializable] sealed class Saved { public List<SavedEntry> entries = new List<SavedEntry>(); public List<SavedEntry> actions = new List<SavedEntry>(); }
        readonly List<Entry> entries = new List<Entry>();
        readonly List<ActionEntry> actions = new List<ActionEntry>();
        SakuraVtubeHotkeys.Profile vtube;
        readonly Dictionary<CubismParameter, (float before, float after)> parameterWrites = new Dictionary<CubismParameter, (float, float)>();
        readonly HashSet<Entry> applied = new HashSet<Entry>();
        Entry reaction;
        float reactionWeight, reactionUntil;
        CubismModel cubism;
        Vrm10Instance vrm;
        string preference;
        public event Action Changed;
        public IReadOnlyList<Entry> Entries => entries;
        public IReadOnlyList<ActionEntry> Actions => actions;
        public bool ImportedVtubeHotkeys => vtube != null && vtube.Present;
        public string ImportWarning { get; private set; }
        public bool SuspendHotkeys { get; set; }
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 900;
        public bool NeedsUpdateOnEditing => false;
        public static string PreferenceKey(string modelKey)
        {
            using (var digest = SHA256.Create()) return "sakura-expressions:" + BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(modelKey.Replace('\\', '/').ToLowerInvariant()))).Replace("-", "");
        }
        public void Initialize(string modelKey, SakuraIdleMotion idle)
        {
            if (preference != null) throw new InvalidOperationException("Expressions already initialized.");
            preference = PreferenceKey(modelKey);
            cubism = GetComponent<CubismModel>(); vrm = GetComponent<Vrm10Instance>();
            if (vrm != null)
            {
                foreach (var key in vrm.Runtime.Expression.ExpressionKeys)
                { var captured = key; Add("vrm:" + key.Name, key.Name, value => vrm.Runtime.Expression.SetWeight(captured, value)); }
            }
            else if (cubism != null)
            {
                foreach (var parameter in cubism.Parameters)
                {
                    var captured = parameter;
                    var entry = Add("parameter:" + parameter.Id, "Parameter · " + parameter.Id, value => WriteParameter(captured, Mathf.Lerp(captured.MinimumValue, captured.MaximumValue, value)));
                    entry.Strength = Mathf.InverseLerp(parameter.MinimumValue, parameter.MaximumValue, parameter.DefaultValue);
                }
                try { LoadLiveExpressions(modelKey); }
                catch (Exception error) { ImportWarning = "Some Live2D expressions could not be loaded: " + error.Message; Debug.LogWarning(ImportWarning); }
                var controller = GetComponent<CubismUpdateController>(); HasUpdateController = controller != null; if (controller != null) controller.Refresh();
            }
            else
            {
                int meshIndex = 0;
                foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null) { meshIndex++; continue; }
                    for (int index = 0; index < renderer.sharedMesh.blendShapeCount; index++)
                    {
                        int captured = index; var target = renderer;
                        string name = renderer.sharedMesh.GetBlendShapeName(index);
                        Add("mesh:" + meshIndex + ":" + index, name, value => { if (target != null) target.SetBlendShapeWeight(captured, value * 100); });
                    }
                    meshIndex++;
                }
            }
            AddActions();
            if (PlayerPrefs.HasKey(preference))
            {
                try
                {
                    var saved = JsonUtility.FromJson<Saved>(PlayerPrefs.GetString(preference));
                    foreach (var value in saved.entries ?? new List<SavedEntry>())
                    {
                        var entry = entries.FirstOrDefault(item => item.Id == value.id); if (entry == null) continue;
                        entry.Label = string.IsNullOrWhiteSpace(value.label) ? entry.Name : value.label;
                        if (!float.IsNaN(value.strength) && !float.IsInfinity(value.strength)) entry.Strength = Mathf.Clamp01(value.strength);
                        string key = NormalizeHotkey(value.hotkey);
                        if (key != null && !KeyUsed(key)) entry.Hotkey = key;
                        entry.Source = value.source;
                    }
                    foreach (var value in saved.actions ?? new List<SavedEntry>())
                    {
                        var action = actions.FirstOrDefault(item => item.Id == value.id); if (action == null) continue;
                        string key = NormalizeHotkey(value.hotkey); if (key != null && !KeyUsed(key)) action.Hotkey = key; action.Source = value.source;
                    }
                }
                catch (Exception error) { Debug.LogWarning("Expression preferences could not be read: " + error.Message); }
            }
            AssignDefaults(); Save();
        }
        Entry Add(string id, string name, Action<float> apply)
        {
            var entry = new Entry { Id = id, Name = name, Label = name, Apply = apply }; entries.Add(entry); return entry;
        }
        bool KeyUsed(string key, string except = null) => entries.Any(entry => entry.Id != except && entry.Hotkey == key) || actions.Any(entry => entry.Id != except && entry.Hotkey == key);
        static bool LegacyManual(Entry entry) => entry.Source == null && !string.IsNullOrEmpty(entry.Hotkey) && !System.Text.RegularExpressions.Regex.IsMatch(entry.Hotkey, @"^Ctrl\+(?:(?:Alt|Shift)\+)*Digit[0-9]$");
        void AssignDefaults()
        {
            if (ImportedVtubeHotkeys)
            {
                foreach (var entry in entries.Where(entry => entry.Id.StartsWith("expression:", StringComparison.Ordinal) || entry.Id.StartsWith("motion:", StringComparison.Ordinal)))
                {
                    if (entry.Source == "manual" || LegacyManual(entry)) continue;
                    entry.Hotkey = null; entry.Source = "vtube";
                }
                foreach (var entry in entries.Where(entry => (entry.Id.StartsWith("expression:", StringComparison.Ordinal) || entry.Id.StartsWith("motion:", StringComparison.Ordinal)) && entry.Source == "vtube"))
                {
                    string file = entry.Id.Substring(entry.Id.IndexOf(':') + 1).Replace('\\', '/');
                    var imported = vtube.Bindings.FirstOrDefault(binding => binding.File != null && binding.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (imported?.Hotkey != null && !KeyUsed(imported.Hotkey, entry.Id)) entry.Hotkey = imported.Hotkey;
                }
                var reset = actions.First(entry => entry.Id == "clear-expressions");
                if (reset.Source != "manual")
                {
                    reset.Hotkey = null; reset.Source = "vtube";
                    string key = vtube.Bindings.FirstOrDefault(binding => binding.Action == "RemoveAllExpressions")?.Hotkey;
                    if (key != null && !KeyUsed(key, reset.Id)) reset.Hotkey = key;
                }
                return;
            }
            int index = 0;
            foreach (var entry in entries.Where(entry => !entry.Id.StartsWith("parameter:", StringComparison.Ordinal)))
            {
                if (entry.Source == "manual" || !string.IsNullOrEmpty(entry.Hotkey)) continue;
                while (index < 40)
                {
                    string key = new[] { "Ctrl+", "Ctrl+Alt+", "Ctrl+Shift+", "Ctrl+Alt+Shift+" }[index / 10] + "Digit" + "1234567890"[index % 10]; index++;
                    if (KeyUsed(key)) continue;
                    entry.Hotkey = key; entry.Source = "automatic"; break;
                }
            }
        }
        SakuraAvatarViewport Viewport() => GetComponentInParent<SakuraAvatarViewport>() ?? throw new InvalidOperationException("Load this model in the native viewport first.");
        void AddActions()
        {
            void AddAction(string id, string name, Action run) => actions.Add(new ActionEntry { Id = id, Name = name, Run = run });
            void ToggleMotion(string option)
            {
                var motion = GetComponent<SakuraIdleMotion>() ?? throw new InvalidOperationException("This model has no idle controls.");
                if (option == "breath") motion.Breathing = !motion.Breathing;
                else if (option == "blink") motion.Blinking = !motion.Blinking;
                else motion.IdleMotions = !motion.IdleMotions;
                motion.Save();
            }
            AddAction("clear-expressions", "Clear expressions", Clear);
            AddAction("screenshot", "Take screenshot", () => { var output = Viewport().GetComponent<SakuraOutput>() ?? throw new InvalidOperationException("Screenshot output is unavailable."); output.Screenshot(); });
            AddAction("toggle-breathing", "Toggle auto breathing", () => ToggleMotion("breath"));
            AddAction("toggle-blink", "Toggle auto blinking", () => ToggleMotion("blink"));
            AddAction("toggle-idle", "Toggle idle motions", () => ToggleMotion("idle"));
            AddAction("toggle-model-lock", "Lock / unlock model position", () => { var viewport = Viewport(); viewport.PositionLocked = !viewport.PositionLocked; });
            AddAction("toggle-items", "Show / hide stage items", () => { var stage = Viewport().GetComponent<SakuraStageItems>() ?? throw new InvalidOperationException("Stage items are unavailable."); stage.SetHidden(!stage.Hidden); });
            void Nudge(float dx, float dy)
            {
                var viewport = Viewport(); var position = viewport.PluginPosition();
                viewport.PluginMove(new JObject { ["positionX"] = position.Value<float>("positionX") + dx, ["positionY"] = position.Value<float>("positionY") + dy, ["size"] = position.Value<float>("size"), ["rotation"] = position.Value<float>("rotation") });
            }
            AddAction("model-left", "Move model left", () => Nudge(-.02f, 0));
            AddAction("model-right", "Move model right", () => Nudge(.02f, 0));
            AddAction("model-up", "Move model up", () => Nudge(0, -.02f));
            AddAction("model-down", "Move model down", () => Nudge(0, .02f));
            AddAction("model-bigger", "Make model bigger", () => { var viewport = Viewport(); viewport.SetZoomPercent(viewport.ZoomPercent * 1.05f); });
            AddAction("model-smaller", "Make model smaller", () => { var viewport = Viewport(); viewport.SetZoomPercent(viewport.ZoomPercent * .95f); });
            void Rotate(float angle)
            {
                Viewport().Rotate(angle);
            }
            AddAction("model-rotate-left", "Rotate model left", () => Rotate(-5));
            AddAction("model-rotate-right", "Rotate model right", () => Rotate(5));
            AddAction("cycle-background", "Cycle stage background", () =>
            {
                var backdrop = Viewport().GetComponent<SakuraBackdrop>() ?? throw new InvalidOperationException("Stage background controls are unavailable.");
                Color[] palette = { Color.green, new Color32(22, 143, 213, 255), new Color32(243, 237, 249, 255), new Color32(18, 35, 51, 255) };
                int index = Array.FindIndex(palette, color => ((Vector4)(color - backdrop.Colour)).sqrMagnitude < .0001f);
                backdrop.SetColour(palette[(index + 1) % palette.Length]); backdrop.SetSolid(true);
            });
        }
        public void BindAction(string id, string hotkey)
        {
            var target = actions.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("Action no longer exists.");
            if (!string.IsNullOrEmpty(hotkey)) hotkey = NormalizeHotkey(hotkey) ?? throw new ArgumentException("Choose a supported shortcut.");
            if (!string.IsNullOrEmpty(hotkey) && KeyUsed(hotkey, target.Id)) throw new ArgumentException("That shortcut is already assigned.");
            target.Hotkey = hotkey; target.Source = "manual"; Save();
        }
        public void RunAction(string id) => (actions.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("Action no longer exists.")).Run();
        void LoadLiveExpressions(string path)
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Model manifest exceeds 8 MB.");
            vtube = SakuraVtubeHotkeys.Load(path); if (vtube.Warning != null) ImportWarning = vtube.Warning;
            var motions = GetComponent<SakuraLive2DMotions>() ?? gameObject.AddComponent<SakuraLive2DMotions>();
            motions.Initialize(path, vtube);
            var files = vtube.Expressions;
            foreach (var item in files.Take(500))
            {
                try
                {
                    string relative = item.File;
                    string file = SakuraModelImport.ResolveFile(Path.GetDirectoryName(path), relative);
                    if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("Missing or oversized expression: " + relative);
                    var json = JObject.Parse(File.ReadAllText(file));
                    var parameters = new List<(CubismParameter parameter, string blend, float value)>();
                    foreach (var data in (json["Parameters"] as JArray ?? new JArray()).Take(5000))
                    {
                        var parameter = cubism.Parameters.FirstOrDefault(value => value.Id == data.Value<string>("Id"));
                        float value = data.Value<float>("Value"); string blend = data.Value<string>("Blend") ?? "Add";
                        if (parameter == null || float.IsNaN(value) || float.IsInfinity(value)) continue;
                        if (blend != "Add" && blend != "Multiply" && blend != "Overwrite") throw new InvalidDataException("Unknown expression blend: " + blend);
                        parameters.Add((parameter, blend, value));
                    }
                    if (parameters.Count == 0) continue;
                    string id = "expression:" + relative;
                    if (entries.Any(entry => entry.Id == id)) continue;
                    Add(id, item.Name ?? Path.GetFileNameWithoutExtension(relative), weight =>
                    {
                        foreach (var data in parameters)
                        {
                            float before = data.parameter.Value;
                            float after = data.blend == "Multiply" ? before * Mathf.Lerp(1, data.value, weight) : data.blend == "Overwrite" ? Mathf.Lerp(before, data.value, weight) : before + data.value * weight;
                            WriteParameter(data.parameter, after);
                        }
                    });
                }
                catch (Exception error) { ImportWarning = error.Message; Debug.LogWarning("Live2D expression skipped: " + error.Message); }
            }
            foreach (var motion in motions.Motions)
            {
                string relative = motion.File;
                Add("motion:" + relative, "Motion · " + motion.Name, weight => motions.SetWeight(relative, weight));
            }
            motions.ActiveChanged += (relative, active) =>
            {
                var entry = entries.FirstOrDefault(value => value.Id == "motion:" + relative);
                if (entry != null) { entry.Active = active; Changed?.Invoke(); }
            };
        }
        void WriteParameter(CubismParameter parameter, float value)
        {
            float before = parameterWrites.TryGetValue(parameter, out var previous) ? previous.before : parameter.Value;
            parameter.Value = Mathf.Clamp(value, parameter.MinimumValue, parameter.MaximumValue);
            parameterWrites[parameter] = (before, parameter.Value);
        }
        void RestoreParameters()
        {
            foreach (var entry in parameterWrites)
                if (entry.Key != null && Mathf.Abs(entry.Key.Value - entry.Value.after) < .00001f) entry.Key.Value = entry.Value.before;
            parameterWrites.Clear();
        }
        Entry Find(string id) => entries.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("Expression no longer exists.");
        public void Rename(string id, string label) { var entry = Find(id); entry.Label = string.IsNullOrWhiteSpace(label) ? entry.Name : label.Trim().Substring(0, Math.Min(label.Trim().Length, 160)); Save(); }
        public void SetStrength(string id, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Strength must be finite.");
            Find(id).Strength = Mathf.Clamp01(value); Save();
        }
        public void SetActive(string id, bool active) { var entry = Find(id); entry.Active = active; entry.PreviewUntil = 0; Changed?.Invoke(); }
        public void Preview(string id, float seconds = 1.5f) { Find(id).PreviewUntil = Time.unscaledTime + Mathf.Clamp(seconds, .01f, 10); Changed?.Invoke(); }
        public void Clear() { StopReplyMood(); foreach (var entry in entries) { entry.Active = false; entry.PreviewUntil = 0; } ApplyNow(); Changed?.Invoke(); }
        public void StopReplyMood()
        {
            reactionUntil = reactionWeight = 0;
            if (reaction != null && !reaction.Active && applied.Remove(reaction) && cubism == null) reaction.Apply(0);
            reaction = null;
        }
        public bool SetReplyMood(string mood)
        {
            StopReplyMood();
            string pattern = mood switch
            {
                "happy" => @"happy|joy|smile|cheer|笑|嬉|喜|开心|にこ",
                "amused" => @"amuse|laugh|grin|にや|ニヤ|笑い|大笑|happy|joy|smile",
                "surprised" => @"surpris|shock|wow|驚|びっくり|惊|吃惊",
                "concerned" => @"sad|worr|concern|困|悲|哀|心配|难过",
                "focused" or "curious" => @"focus|serious|think|真剣|考|思考|认真",
                "sparkle" => @"sparkl|twinkl|star.?eyes|キラ|きら|星目|星星眼|happy|smile",
                _ => null
            };
            if (pattern == null || entries.Any(entry => entry.Active || entry.PreviewUntil > Time.unscaledTime)) return false;
            const string unsafeNames = @"outfit|cloth|dress|nude|undress|remove|toggle|accessor|stocking|weapon|hide|衣|服|脱|消|口|mouth|blink|ウィンク|まばたき|eyeclose";
            var chosen = entries.FirstOrDefault(entry => !entry.Id.StartsWith("motion:", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(entry.Name + " " + entry.Label, unsafeNames, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(entry.Name + " " + entry.Label, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (chosen == null) return false;
            if (reaction != chosen)
            {
                if (reaction != null && applied.Remove(reaction) && cubism == null) reaction.Apply(0);
                reaction = chosen; reactionWeight = 0;
            }
            reactionUntil = Time.unscaledTime + 6; return true;
        }
        public bool ApplyByLabel(JObject command)
        {
            if (command == null) return false;
            if (command.Value<bool?>("clear") == true) { Clear(); return true; }
            string label = command.Value<string>("label");
            if (string.IsNullOrWhiteSpace(label)) return false;
            var matches = entries.Where(entry => string.Equals(entry.Label, label, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Name, label, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return false;
            SetActive(matches[0].Id, true); ApplyNow(); return true;
        }
        public void Bind(string id, string hotkey)
        {
            if (!string.IsNullOrEmpty(hotkey)) hotkey = NormalizeHotkey(hotkey) ?? throw new ArgumentException("Choose a letter, digit, function key or navigation key, with optional modifiers.");
            var target = Find(id);
            if (!string.IsNullOrEmpty(hotkey) && (entries.Any(entry => entry != target && entry.Hotkey == hotkey) || actions.Any(entry => entry.Hotkey == hotkey))) throw new ArgumentException("That shortcut is already assigned.");
            target.Hotkey = hotkey; target.Source = "manual"; Save();
        }
        void Save()
        {
            PlayerPrefs.SetString(preference, JsonUtility.ToJson(new Saved { entries = entries.Select(entry => new SavedEntry { id = entry.Id, label = entry.Label, hotkey = entry.Hotkey, strength = entry.Strength, source = entry.Source }).ToList(), actions = actions.Select(entry => new SavedEntry { id = entry.Id, hotkey = entry.Hotkey, source = entry.Source }).ToList() }));
            PlayerPrefs.Save(); Changed?.Invoke();
        }
        public bool TriggerHotkey(string hotkey)
        {
            hotkey = NormalizeHotkey(hotkey); if (hotkey == null) return false;
            var action = actions.FirstOrDefault(value => value.Hotkey == hotkey);
            if (action != null) { RunAction(action.Id); return true; }
            var entry = entries.FirstOrDefault(value => !string.IsNullOrEmpty(value.Hotkey) && value.Hotkey == hotkey);
            if (entry == null) return false; SetActive(entry.Id, !entry.Active); return true;
        }
        static bool TryKey(string code, out Key key)
        {
            if (code.StartsWith("Key", StringComparison.Ordinal)) code = code.Substring(3);
            return Enum.TryParse(code, true, out key) && key != Key.None && !IsModifier(key) && Enum.IsDefined(typeof(Key), key);
        }
        public static string NormalizeHotkey(string hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey) || hotkey.Length > 100) return null;
            var pieces = hotkey.Split('+').Select(value => value.Trim()).ToArray(); string code = pieces[pieces.Length - 1];
            var aliases = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ArrowLeft"] = "LeftArrow", ["ArrowRight"] = "RightArrow", ["ArrowUp"] = "UpArrow", ["ArrowDown"] = "DownArrow", ["Left"] = "LeftArrow", ["Right"] = "RightArrow", ["Up"] = "UpArrow", ["Down"] = "DownArrow", ["Return"] = "Enter" };
            if (aliases.TryGetValue(code, out string alias)) code = alias;
            if (code.Length == 1 && char.IsDigit(code[0])) code = "Digit" + code;
            else if (code.All(char.IsDigit)) return null;
            if (code.StartsWith("Key", StringComparison.OrdinalIgnoreCase)) code = code.Substring(3);
            if (!TryKey(code, out Key key)) return null;
            var modifiers = pieces.Take(pieces.Length - 1).Select(value => new[] { "Ctrl", "Alt", "Shift", "Meta" }.FirstOrDefault(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (modifiers.Any(value => value == null) || modifiers.Distinct().Count() != modifiers.Length) return null;
            string name = key.ToString(); if (name.Length == 1 && char.IsLetter(name[0])) name = "Key" + name;
            string normalized = string.Join("+", new[] { "Ctrl", "Alt", "Shift", "Meta" }.Where(modifiers.Contains).Concat(new[] { name }));
            return normalized == "Tab" || normalized == "Ctrl+Shift+KeyT" || normalized == "Ctrl+Shift+KeyH" ? null : normalized;
        }
        public static bool ValidHotkey(string hotkey) => NormalizeHotkey(hotkey) != null;
        static bool IsModifier(Key key) => key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftMeta || key == Key.RightMeta;
        public static string PressedHotkey()
        {
            var keyboard = Keyboard.current; if (keyboard == null) return null;
            foreach (var control in keyboard.allKeys)
            {
                if (!control.wasPressedThisFrame || IsModifier(control.keyCode)) continue;
                string key = control.keyCode.ToString(); if (key.Length == 1 && char.IsLetter(key[0])) key = "Key" + key;
                return (keyboard.ctrlKey.isPressed ? "Ctrl+" : "") + (keyboard.altKey.isPressed ? "Alt+" : "") + (keyboard.shiftKey.isPressed ? "Shift+" : "") + (keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed ? "Meta+" : "") + key;
            }
            return null;
        }
        static bool EditingText()
        {
            foreach (var document in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                var focus = document.rootVisualElement?.focusController?.focusedElement as VisualElement;
                for (var element = focus; element != null; element = element.parent) if (element is TextField || element.GetType().Name.Contains("TextInput")) return true;
            }
            return false;
        }
        void Update()
        {
            RestoreParameters();
            bool automatic = reaction != null && reactionUntil > Time.unscaledTime && !entries.Any(entry => entry.Active || entry.PreviewUntil > Time.unscaledTime);
            reactionWeight = Mathf.Lerp(reactionWeight, automatic ? .65f : 0, 1 - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, .1f) / .28f));
            if (Application.isFocused && !SuspendHotkeys && !EditingText())
            {
                string hotkey = PressedHotkey();
                if (hotkey != null) try { TriggerHotkey(hotkey); } catch (Exception error) { Debug.LogWarning("Shortcut could not run. " + error.Message); }
            }
            if (vrm != null) ApplyNow();
        }
        public void ApplyNow()
        {
            foreach (var entry in entries)
            {
                bool active = entry.Active || entry.PreviewUntil > Time.unscaledTime;
                if (active) { entry.Apply(entry.Strength); applied.Add(entry); }
                else if (entry == reaction && reactionWeight > .005f && !entries.Any(manual => manual.Active || manual.PreviewUntil > Time.unscaledTime))
                { entry.Apply(entry.Strength * reactionWeight); applied.Add(entry); }
                else if (applied.Remove(entry) && (cubism == null || entry.Id.StartsWith("motion:", StringComparison.Ordinal))) entry.Apply(0);
            }
        }
        public void OnLateUpdate() { if (isActiveAndEnabled && cubism != null) ApplyNow(); }
        void LateUpdate() { if (cubism == null) { if (vrm == null) ApplyNow(); } else if (!HasUpdateController) OnLateUpdate(); }
        void OnDisable()
        {
            reaction = null; reactionWeight = reactionUntil = 0;
            RestoreParameters();
            foreach (var entry in applied) if (cubism == null || entry.Id.StartsWith("motion:", StringComparison.Ordinal)) entry.Apply(0);
            applied.Clear();
        }
    }
}
