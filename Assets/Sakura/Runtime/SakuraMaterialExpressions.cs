using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraMaterialExpressions : MonoBehaviour
    {
        [Serializable] public sealed class Value { public Color color = Color.white; public float strength, transparency; public bool colored; }
        [Serializable] public sealed class Preset
        {
            public string id, name, part, channel, hotkey;
            public Value value, before;
            public bool active;
        }
        [Serializable] private sealed class Profile { public List<Preset> presets = new List<Preset>(); }
        private sealed class Part { public string key; public int index; }
        public const int MaximumPresets = 32, MaximumParts = 512;
        public IReadOnlyList<Preset> Presets => profile.presets;
        public event Action Changed;
        public string Status { get; private set; } = "Save the selected part’s color or transparency as an expression.";
        private Profile profile = new Profile();
        private readonly List<Part> parts = new List<Part>();
        private SakuraPmxModel pmx;
        private SakuraModelTools tools;
        private string storage;
        private bool recording;

        public static SakuraMaterialExpressions GetOrCreate(GameObject model, string modelPath)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var component = model.GetComponent<SakuraMaterialExpressions>();
            if (component == null) { component = model.AddComponent<SakuraMaterialExpressions>(); component.Initialize(modelPath); }
            return component;
        }
        public static string ProfilePath(string modelPath)
        {
            using (var hash = SHA256.Create())
                return Path.Combine(Application.persistentDataPath, "material-expressions", BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(modelPath).ToLowerInvariant()))).Replace("-", ""), "presets.json");
        }
        public static bool ValidValue(Value value)
        {
            bool Unit(float number) => !float.IsNaN(number) && !float.IsInfinity(number) && number >= 0 && number <= 1;
            return value != null && Unit(value.color.r) && Unit(value.color.g) && Unit(value.color.b) && Unit(value.color.a) && Unit(value.strength) && !float.IsNaN(value.transparency) && !float.IsInfinity(value.transparency) && value.transparency >= -1 && value.transparency <= 1;
        }
        public static bool ValidPreset(Preset preset) => preset != null && Guid.TryParseExact(preset.id, "N", out _) && !string.IsNullOrWhiteSpace(preset.name) && preset.name.Length <= 80
            && !string.IsNullOrEmpty(preset.part) && preset.part.Length <= 2048 && (preset.channel == "tint" || preset.channel == "opacity") && ValidValue(preset.value) && (!preset.active || ValidValue(preset.before));
        private void Initialize(string modelPath)
        {
            storage = ProfilePath(modelPath); pmx = GetComponent<SakuraPmxModel>(); tools = GetComponent<SakuraModelTools>();
            if (pmx != null)
                foreach (var material in pmx.Definition.materials.Take(MaximumParts)) parts.Add(new Part { index = material.index, key = "pmx:" + material.index + ":" + material.name });
            else if (tools != null)
                for (int index = 0; index < Math.Min(MaximumParts, tools.Parts.Count); index++)
                { var part = tools.Parts[index]; parts.Add(new Part { index = index, key = part.Id + ":" + part.Name }); }
            try
            {
                if (File.Exists(storage) && new FileInfo(storage).Length <= 512 * 1024) profile = JsonUtility.FromJson<Profile>(File.ReadAllText(storage)) ?? new Profile();
                profile.presets = (profile.presets ?? new List<Preset>()).Where(ValidPreset).GroupBy(preset => preset.id).Select(group => group.First()).Take(MaximumPresets).ToList();
                var active = new HashSet<string>();
                foreach (var preset in profile.presets.Where(preset => preset.active))
                {
                    if (parts.Count(part => part.key == preset.part) != 1 || !active.Add(preset.part + ":" + preset.channel)) { preset.active = false; continue; }
                    Write(Find(preset.part), preset.channel, preset.value);
                }
            }
            catch (Exception error) { Status = "Material expressions could not be restored. " + error.Message; }
        }
        private Part Find(string key) => parts.SingleOrDefault(part => part.key == key) ?? throw new ArgumentException("That model part is unavailable. Load the matching model.");
        private Value Read(Part part)
        {
            if (pmx != null) { var value = pmx.GetPart(part.index); return new Value { color = value.color, strength = value.strength, transparency = value.transparency, colored = value.colored }; }
            var settings = tools.Parts[part.index].Settings;
            return new Value { color = settings.color, strength = settings.strength, transparency = settings.transparency, colored = settings.strength > 0 };
        }
        private void Write(Part part, string channel, Value value)
        {
            if (!ValidValue(value)) throw new ArgumentException("Material expression values must be finite and within range.");
            if (pmx != null)
            {
                var settings = pmx.GetPart(part.index);
                if (channel == "opacity") settings.transparency = value.transparency;
                else { settings.color = value.color; settings.strength = value.strength; settings.colored = value.colored; }
                pmx.Apply(); pmx.Save();
            }
            else if (channel == "opacity") tools.SetTransparency(part.index, value.transparency);
            else tools.SetColor(part.index, value.color, value.strength);
        }
        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storage));
            string temp = storage + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonUtility.ToJson(profile)); if (File.Exists(storage)) File.Replace(temp, storage, null); else File.Move(temp, storage); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public string Capture(string name, int partIndex, string channel)
        {
            if (profile.presets.Count >= MaximumPresets) throw new InvalidOperationException("Delete an old preset first. Up to 32 presets are supported.");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("Enter a preset name of up to 80 characters.");
            if (channel != "tint" && channel != "opacity") throw new ArgumentException("Choose tint or opacity for the preset.");
            var part = parts.SingleOrDefault(value => value.index == partIndex) ?? throw new ArgumentException("Select a model part first.");
            var preset = new Preset { id = Guid.NewGuid().ToString("N"), name = name.Trim(), part = part.key, channel = channel, value = Read(part) };
            if (!ValidPreset(preset)) throw new ArgumentException("The selected material contains invalid appearance values.");
            profile.presets.Add(preset);
            try { Save(); } catch { profile.presets.Remove(preset); throw; }
            Status = "Preset saved. Enable it in Expressions & hotkeys."; Changed?.Invoke(); return preset.id;
        }
        public void SetActive(string id, bool enabled)
        {
            var preset = profile.presets.SingleOrDefault(value => value.id == id) ?? throw new ArgumentException("The material preset is unavailable.");
            if (preset.active == enabled) return;
            var part = Find(preset.part);
            var competing = profile.presets.Where(value => value != preset && value.active && value.part == preset.part && value.channel == preset.channel).ToArray();
            var original = Read(part);
            var backup = JsonUtility.ToJson(profile);
            try
            {
                foreach (var other in competing) { Write(part, other.channel, other.before); other.active = false; }
                if (enabled) preset.before = Read(part);
                Write(part, preset.channel, enabled ? preset.value : preset.before); preset.active = enabled; Save();
            }
            catch { profile = JsonUtility.FromJson<Profile>(backup); Write(part, preset.channel, original); throw; }
            Status = enabled ? "Material expression enabled." : "Previous material values restored."; Changed?.Invoke();
        }
        public void Remove(string id)
        {
            var preset = profile.presets.SingleOrDefault(value => value.id == id); if (preset == null) return;
            if (preset.active) SetActive(id, false);
            int index = profile.presets.IndexOf(preset); profile.presets.Remove(preset);
            try { Save(); } catch { profile.presets.Insert(index, preset); throw; }
            Changed?.Invoke();
        }
        public void Bind(string id, string hotkey)
        {
            var preset = profile.presets.SingleOrDefault(value => value.id == id) ?? throw new ArgumentException("The material preset is unavailable.");
            string normalized = string.IsNullOrEmpty(hotkey) ? null : SakuraAvatarExpressions.NormalizeHotkey(hotkey);
            if (!string.IsNullOrEmpty(hotkey) && normalized == null) throw new ArgumentException("Choose a supported shortcut.");
            var expressions = GetComponent<SakuraAvatarExpressions>();
            if (normalized != null && (profile.presets.Any(value => value != preset && value.hotkey == normalized) || expressions != null && (expressions.Entries.Any(value => value.Hotkey == normalized) || expressions.Actions.Any(value => value.Hotkey == normalized))))
                throw new ArgumentException("That shortcut is already assigned.");
            string previous = preset.hotkey; preset.hotkey = normalized; try { Save(); } catch { preset.hotkey = previous; throw; } Changed?.Invoke();
        }
        private void Update()
        {
            if (recording || !Application.isFocused || GetComponent<SakuraAvatarExpressions>()?.SuspendHotkeys == true) return;
            foreach (var document in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                for (var element = document.rootVisualElement.focusController?.focusedElement as VisualElement; element != null; element = element.parent)
                    if (element is TextField || element.GetType().Name.Contains("TextInput")) return;
            string key = SakuraAvatarExpressions.PressedHotkey(); if (key == null) return;
            var avatarExpressions = GetComponent<SakuraAvatarExpressions>();
            if (avatarExpressions != null && (avatarExpressions.Entries.Any(value => value.Hotkey == key) || avatarExpressions.Actions.Any(value => value.Hotkey == key))) return;
            var preset = profile.presets.FirstOrDefault(value => value.hotkey == key); if (preset == null) return;
            try { SetActive(preset.id, !preset.active); } catch (Exception error) { Status = error.Message; Changed?.Invoke(); }
        }
        public void BuildCapturePanel(VisualElement parent, Func<int> selectedPart, Func<string> selectedChannel = null)
        {
            var owner = new VisualElement(); parent.Add(owner);
            var name = new TextField("Preset name") { maxLength = 80 }; owner.Add(name);
            var channel = new DropdownField("Material channel", new List<string> { "tint", "opacity" }, 0); owner.Add(channel);
            var status = new Label(Status); owner.Add(status);
            owner.Add(new Button(() => { try { Capture(name.value, selectedPart(), selectedChannel?.Invoke() ?? channel.value); name.value = ""; status.text = Status; } catch (Exception error) { status.text = error.Message; } }) { text = "Save selected part as expression" });
        }
        public void BuildPanel(VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner);
            owner.Add(new Label("Part color & transparency expressions"));
            var rows = new VisualElement(); owner.Add(rows); var status = new Label(Status); owner.Add(status);
            IVisualElementScheduledItem recorder = null; bool alive = true, previousSuspend = false;
            var expressions = GetComponent<SakuraAvatarExpressions>();
            void StopRecording()
            {
                if (recording && expressions != null) expressions.SuspendHotkeys = previousSuspend;
                recording = false; recorder?.Pause(); recorder = null;
            }
            void Render()
            {
                StopRecording(); rows.Clear(); status.text = Status;
                if (profile.presets.Count == 0) rows.Add(new Label("No material expressions saved for this model."));
                foreach (var preset in profile.presets)
                {
                    var card = new Foldout { text = preset.name + " · " + preset.channel, value = false }; rows.Add(card);
                    var toggle = new Button(() => { try { SetActive(preset.id, !preset.active); } catch (Exception error) { status.text = error.Message; } }) { text = preset.active ? "On · turn off" : "Off · turn on" }; card.Add(toggle);
                    card.Add(new Label(preset.hotkey ?? "No shortcut"));
                    card.Add(new Button(() =>
                    {
                        StopRecording(); if (expressions != null) { previousSuspend = expressions.SuspendHotkeys; expressions.SuspendHotkeys = true; }
                        recording = true; status.text = "Press a shortcut. Escape cancels.";
                        recorder = owner.schedule.Execute(() =>
                        {
                            if (!alive || owner.resolvedStyle.display == DisplayStyle.None) { StopRecording(); return; }
                            string key = SakuraAvatarExpressions.PressedHotkey(); if (key == null) return;
                            StopRecording(); if (key == "Escape") { status.text = "Shortcut unchanged."; return; }
                            try { Bind(preset.id, key); } catch (Exception error) { status.text = error.Message; }
                        }).Every(16);
                    }) { text = "Set hotkey" });
                    card.Add(new Button(() => { try { Bind(preset.id, null); } catch (Exception error) { status.text = error.Message; } }) { text = "Clear hotkey" });
                    card.Add(new Button(() => { try { Remove(preset.id); } catch (Exception error) { status.text = error.Message; } }) { text = "Delete preset" });
                }
            }
            Changed += Render;
            owner.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; StopRecording(); Changed -= Render; });
            var studio = GetComponentInParent<SakuraStudio>();
            if (studio != null) { studio.PanelsHidden += StopRecording; owner.RegisterCallback<DetachFromPanelEvent>(_ => studio.PanelsHidden -= StopRecording); }
            Render();
        }
    }
}
