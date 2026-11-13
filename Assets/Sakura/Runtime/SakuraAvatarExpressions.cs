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
        }
        [Serializable] sealed class SavedEntry { public string id, label, hotkey; public float strength = 1; }
        [Serializable] sealed class Saved { public List<SavedEntry> entries = new List<SavedEntry>(); }
        readonly List<Entry> entries = new List<Entry>();
        readonly Dictionary<CubismParameter, (float before, float after)> parameterWrites = new Dictionary<CubismParameter, (float, float)>();
        readonly HashSet<Entry> applied = new HashSet<Entry>();
        CubismModel cubism;
        Vrm10Instance vrm;
        string preference;
        public event Action Changed;
        public IReadOnlyList<Entry> Entries => entries;
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
                        if (ValidHotkey(value.hotkey) && !entries.Any(item => item.Hotkey == value.hotkey)) entry.Hotkey = value.hotkey;
                    }
                }
                catch (Exception error) { Debug.LogWarning("Expression preferences could not be read: " + error.Message); }
            }
        }
        Entry Add(string id, string name, Action<float> apply)
        {
            var entry = new Entry { Id = id, Name = name, Label = name, Apply = apply }; entries.Add(entry); return entry;
        }
        void LoadLiveExpressions(string path)
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Model manifest exceeds 8 MB.");
            var files = JObject.Parse(File.ReadAllText(path))["FileReferences"]?["Expressions"] as JArray;
            if (files == null) return;
            foreach (var item in files.Take(500))
            {
                try
                {
                    string relative = item.Value<string>("File");
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
                    Add(id, item.Value<string>("Name") ?? Path.GetFileNameWithoutExtension(relative), weight =>
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
        public void Clear() { foreach (var entry in entries) { entry.Active = false; entry.PreviewUntil = 0; } ApplyNow(); Changed?.Invoke(); }
        public void Bind(string id, string hotkey)
        {
            if (!string.IsNullOrEmpty(hotkey) && !ValidHotkey(hotkey)) throw new ArgumentException("Choose a letter, digit, function key or navigation key, with optional modifiers.");
            var target = Find(id);
            if (!string.IsNullOrEmpty(hotkey) && entries.Any(entry => entry != target && entry.Hotkey == hotkey)) throw new ArgumentException("That shortcut is already assigned to another expression.");
            target.Hotkey = hotkey; Save();
        }
        void Save()
        {
            PlayerPrefs.SetString(preference, JsonUtility.ToJson(new Saved { entries = entries.Select(entry => new SavedEntry { id = entry.Id, label = entry.Label, hotkey = entry.Hotkey, strength = entry.Strength }).ToList() }));
            PlayerPrefs.Save(); Changed?.Invoke();
        }
        public bool TriggerHotkey(string hotkey)
        {
            var entry = entries.FirstOrDefault(value => !string.IsNullOrEmpty(value.Hotkey) && value.Hotkey == hotkey);
            if (entry == null) return false; SetActive(entry.Id, !entry.Active); return true;
        }
        static bool TryKey(string code, out Key key)
        {
            if (code.StartsWith("Key", StringComparison.Ordinal)) code = code.Substring(3);
            return Enum.TryParse(code, out key) && key != Key.None && !IsModifier(key);
        }
        public static bool ValidHotkey(string hotkey)
        {
            if (string.IsNullOrEmpty(hotkey)) return false;
            var pieces = hotkey.Split('+'); if (!TryKey(pieces[pieces.Length - 1], out _)) return false;
            var modifiers = pieces.Take(pieces.Length - 1).ToArray();
            return modifiers.Distinct().Count() == modifiers.Length && modifiers.All(value => value == "Ctrl" || value == "Alt" || value == "Shift" || value == "Meta");
        }
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
            if (Application.isFocused && !SuspendHotkeys && !EditingText())
            { string hotkey = PressedHotkey(); if (hotkey != null) TriggerHotkey(hotkey); }
            if (vrm != null) ApplyNow();
        }
        public void ApplyNow()
        {
            foreach (var entry in entries)
            {
                bool active = entry.Active || entry.PreviewUntil > Time.unscaledTime;
                if (active) { entry.Apply(entry.Strength); applied.Add(entry); }
                else if (applied.Remove(entry) && cubism == null) entry.Apply(0);
            }
        }
        public void OnLateUpdate() { if (isActiveAndEnabled && cubism != null) ApplyNow(); }
        void LateUpdate() { if (cubism == null) { if (vrm == null) ApplyNow(); } else if (!HasUpdateController) OnLateUpdate(); }
        void OnDisable()
        {
            RestoreParameters();
            if (cubism == null) foreach (var entry in applied) entry.Apply(0);
            applied.Clear();
        }
    }
}
