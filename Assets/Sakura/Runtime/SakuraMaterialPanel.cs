using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraMaterialPanel
    {
        readonly SakuraAvatarViewport viewport;
        readonly SakuraBackend backend;
        readonly SakuraModelTools tools;
        readonly VisualElement body;
        readonly Label status;
        readonly string tab;
        bool alive = true;
        int selected;
        byte[] replacement;

        public SakuraMaterialPanel(SakuraAvatarViewport viewport, SakuraBackend backend, VisualElement parent, string tab)
        {
            this.viewport = viewport; this.backend = backend; this.tab = tab;
            tools = SakuraModelTools.GetOrCreate(viewport.AvatarRoot, viewport.LoadedModelPath);
            var owner = new VisualElement(); parent.Add(owner);
            body = new VisualElement(); owner.Add(body); status = new Label(tools.Warning ?? ""); status.AddToClassList("description"); owner.Add(status);
            owner.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; viewport.CancelPartPick(); });
            Render();
        }
        void Run(Action action)
        {
            if (!alive || tools == null || viewport.AvatarRoot != tools.gameObject) return;
            try { action(); } catch (Exception error) { status.text = error.Message; }
        }
        void Button(string title, Action action, VisualElement host = null) => (host ?? body).Add(new Button(() => Run(action)) { text = title });
        void Text(string value) { var label = new Label(value); label.AddToClassList("description"); body.Add(label); }
        void Render()
        {
            body.Clear();
            if (tab == "Bones") { Bones(); return; }
            if (tab == "Parameters") { Parameters(); return; }
            if (tab == "Effects") { Effects(); return; }
            if (tools.Parts.Count == 0) { Text("No editable model materials were found."); return; }
            selected = Mathf.Clamp(selected, 0, tools.Parts.Count - 1);
            if (tab == "Texture remapping") { Remapping(); return; }
            Coloring();
        }
        void Coloring()
        {
            var picker = new DropdownField(viewport.LoadedFormat == "Live2D" ? "ArtMesh" : "Texture / material", tools.Parts.Select((part, index) => index + " · " + part.Name).ToList(), selected); body.Add(picker);
            picker.RegisterValueChangedCallback(_ => { selected = picker.index; Render(); });
            Text(viewport.LoadedFormat == "Live2D" ? "Choose a Live2D ArtMesh to tint. Names come from the imported rig. Tint preserves its masks and transparency." : "Choose a material for hair, clothing or another part. Coloring preserves texture detail and transparency.");
            var operation = new DropdownField("What would you like to change?", new List<string> { "Change color", "Change transparency" }, 0); body.Add(operation);
            if (viewport.LoadedFormat != "Live2D") Button("Pick part on model", () =>
            {
                status.text = "Click the part you want to change on the model.";
                viewport.BeginPartPick(false, index => Run(() => { if (index < 0 || index >= tools.Parts.Count) return; selected = index; Render(); status.text = "Selected " + tools.Parts[index].Name; }));
            });
            var area = new VisualElement(); body.Add(area);
            void Populate()
            {
                area.Clear(); var settings = tools.Parts[selected].Settings;
                if (operation.index == 0)
                {
                    Color color = settings.color; new SakuraColourPicker(area, color, value => color = value);
                    var strength = new Slider("Color strength", 0, 100) { value = settings.strength > 0 ? settings.strength * 100 : 100, showInputField = true }; area.Add(strength);
                    Button("Apply color", () => { tools.SetColor(selected, color, strength.value / 100); status.text = "Color saved for this model."; }, area);
                    Button("Restore original color", () => { tools.RestoreColor(selected); Populate(); status.text = "Original color restored."; }, area);
                }
                else
                {
                    var transparency = new Slider("Transparency %", -100, 100) { value = settings.transparency * 100, showInputField = true }; area.Add(transparency);
                    Button("Apply transparency", () => { tools.SetTransparency(selected, transparency.value / 100); status.text = "Transparency saved for this model."; }, area);
                    Button("Restore original transparency", () => { tools.SetTransparency(selected, 0); Populate(); status.text = "Original transparency restored."; }, area);
                    area.Add(new Label("Negative values strengthen opacity. Texture cutouts stay intact."));
                }
            }
            operation.RegisterValueChangedCallback(_ => Populate()); Populate();
        }
        void Remapping()
        {
            bool live = viewport.LoadedFormat == "Live2D";
            var groups = live
                ? tools.Parts.Select((part, index) => new { part, index }).GroupBy(entry => entry.part.OriginalTexture).Select(group => group.Select(entry => entry.index).ToArray()).ToList()
                : tools.Parts.Select((part, index) => new[] { index }).ToList();
            int groupIndex = Mathf.Max(0, groups.FindIndex(group => group.Contains(selected)));
            var choices = groups.Select((group, index) => live ? "Texture atlas " + (index + 1) : tools.Parts[group[0]].Name).ToList();
            var picker = new DropdownField(live ? "Texture atlas" : "Texture / material", choices, groupIndex); body.Add(picker);
            picker.RegisterValueChangedCallback(_ => { selected = groups[picker.index][0]; replacement = null; Render(); });
            Text(live ? "Replace a texture atlas with an image using the same UV layout. One atlas can contain several body parts." : "Replace the diffuse image for a material. Use a texture designed for this model’s UV layout.");
            var filename = new Label("No file chosen"); body.Add(filename);
            var apply = new Button(() => Run(() =>
            {
                if (replacement == null) return;
                tools.ReplaceTextureGroup(groups[groupIndex], replacement); replacement = null; status.text = "Replacement saved for this model.";
            })) { text = "Apply replacement" }; apply.SetEnabled(false);
            Button("Choose file", () =>
            {
                int expected = selected;
                backend.RunPmxTool("texture", viewport.LoadedModelPath, (json, error) => Run(() =>
                {
                    if (error != null) throw new InvalidOperationException(error);
                    var result = JObject.Parse(json); if (result.Value<bool>("cancelled")) { status.text = "Cancelled."; return; }
                    if (selected != expected) throw new InvalidOperationException("Selected part changed. Choose the image again.");
                    string encoded = result.Value<string>("image");
                    if (string.IsNullOrEmpty(encoded) || encoded.Length > 24 * 1024 * 1024) throw new InvalidOperationException("Replacement images must be under 16 MB.");
                    var bytes = Convert.FromBase64String(encoded); var decoded = SakuraModelTools.DecodeImage(bytes); UnityEngine.Object.Destroy(decoded);
                    replacement = bytes; filename.text = result.Value<string>("name") ?? "Replacement selected"; apply.SetEnabled(true);
                    status.text = "Texture selected. Click Apply replacement to save it.";
                }));
            });
            body.Add(apply);
            Button("Restore original texture", () => { tools.RestoreTextureGroup(groups[groupIndex]); status.text = "Original texture restored."; });
        }
        void Bones()
        {
            if (viewport.LoadedFormat == "Live2D") { Text("Bone controls are available for PMX and VRM models. Live2D uses authored parameters."); Button("Parameters", () => { body.Clear(); Parameters(); }); return; }
            if (tools.Bones.Count == 0) { Text("No model bones were found."); return; }
            Text("Frozen bones keep their local rest pose. Other bones still move.");
            var search = new TextField("Search bones"); body.Add(search); var area = new VisualElement(); body.Add(area);
            void Populate()
            {
                area.Clear();
                var indices = Enumerable.Range(0, tools.Bones.Count).Where(index => tools.Bones[index].Name.IndexOf(search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (indices.Count == 0) { area.Add(new Label("No matching bones.")); return; }
                int choice = Mathf.Max(0, indices.IndexOf(selected)); selected = indices[choice];
                var picker = new DropdownField("Bone", indices.Select(index => index + " · " + tools.Bones[index].Name).ToList(), choice); area.Add(picker);
                var freeze = new Toggle("Freeze this bone") { value = tools.IsFrozen(selected) }; area.Add(freeze);
                picker.RegisterValueChangedCallback(_ => { selected = indices[picker.index]; freeze.SetValueWithoutNotify(tools.IsFrozen(selected)); });
                freeze.RegisterValueChangedCallback(change => Run(() => { tools.Freeze(selected, change.newValue); status.text = Enumerable.Range(0, tools.Bones.Count).Count(tools.IsFrozen) + " frozen bones saved."; }));
            }
            search.RegisterValueChangedCallback(_ => Populate()); Populate();
            Button("Pick bone on model", () => { status.text = "Click the model to select the strongest bone at that point."; viewport.BeginPartPick(true, index => Run(() => { if (index < 0 || index >= tools.Bones.Count) return; selected = index; search.SetValueWithoutNotify(""); Populate(); })); });
            Button("Restore all bones", () => { tools.RestoreBones(); Populate(); status.text = "All bones unfrozen."; });
        }
        void Parameters()
        {
            if (tools.Parameters.Length == 0) { Text("Parameter controls are available for Live2D models."); return; }
            var search = new TextField("Search parameters"); body.Add(search);
            var rows = new VisualElement(); body.Add(rows);
            void Populate()
            {
                rows.Clear();
                foreach (var parameter in tools.Parameters.Where(parameter => parameter.Id.IndexOf(search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var captured = parameter;
                    var fold = new Foldout { text = parameter.Id, value = false }; fold.AddToClassList("expression-card"); rows.Add(fold);
                    bool built = false;
                    fold.RegisterValueChangedCallback(evt =>
                    {
                        if (!evt.newValue || built) return; built = true;
                        var settings = tools.GetParameter(captured.Id);
                        var range = new Label("Range " + captured.MinimumValue.ToString("0.##") + " to " + captured.MaximumValue.ToString("0.##") + " · Rest " + captured.DefaultValue.ToString("0.##")); fold.Add(range);
                        var current = new Label(); fold.Add(current);
                        var poll = current.schedule.Execute(() => { if (captured != null) current.text = "Current value " + captured.Value.ToString("0.##"); }).Every(100);
                        current.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
                        var input = new Toggle("Manual input") { value = settings.enabled }; fold.Add(input);
                        var value = new Slider("Output", captured.MinimumValue, captured.MaximumValue) { value = settings.value, showInputField = true }; fold.Add(value);
                        var smoothing = new Slider("Smoothing", 0, 100) { value = settings.smoothing, showInputField = true }; fold.Add(smoothing);
                        var blink = new Toggle("Auto-Blinking") { value = settings.autoBlink }; fold.Add(blink);
                        var breath = new Toggle("Auto-Breathing") { value = settings.autoBreath }; fold.Add(breath);
                        void Save() { tools.SaveParameter(captured.Id); status.text = "Parameter settings saved for this model."; }
                        input.RegisterValueChangedCallback(change => Run(() => { settings.enabled = change.newValue; Save(); }));
                        value.RegisterValueChangedCallback(change => Run(() => { settings.value = change.newValue; settings.enabled = true; input.SetValueWithoutNotify(true); Save(); }));
                        smoothing.RegisterValueChangedCallback(change => Run(() => { settings.smoothing = change.newValue; Save(); }));
                        blink.RegisterValueChangedCallback(change => Run(() => { settings.autoBlink = change.newValue; Save(); }));
                        breath.RegisterValueChangedCallback(change => Run(() => { settings.autoBreath = change.newValue; Save(); }));
                        Button("Restore default", () =>
                        {
                            settings.enabled = settings.autoBlink = settings.autoBreath = false; settings.value = captured.DefaultValue; settings.smoothing = 0; settings.started = false;
                            captured.Value = captured.DefaultValue; input.SetValueWithoutNotify(false); value.SetValueWithoutNotify(settings.value); smoothing.SetValueWithoutNotify(0); blink.SetValueWithoutNotify(false); breath.SetValueWithoutNotify(false); Save();
                        }, fold);
                    });
                }
            }
            search.RegisterValueChangedCallback(_ => Populate()); Populate();
        }
        void Effects()
        {
            var settings = tools.Effects;
            var glow = new Slider("Bloom / glow", 0, 100) { value = settings.glow * 100, showInputField = true }; body.Add(glow);
            var saturation = new Slider("Saturation", 0, 200) { value = settings.saturation * 100, showInputField = true }; body.Add(saturation);
            var contrast = new Slider("Contrast", 0, 200) { value = settings.contrast * 100, showInputField = true }; body.Add(contrast);
            body.Add(new Label("Scene colour overlay"));
            Color overlay = settings.overlayColor;
            var picker = new VisualElement(); body.Add(picker);
            var strength = new Slider("Overlay strength", 0, 100) { value = settings.overlayStrength * 100, showInputField = true }; body.Add(strength);
            void Save()
            {
                tools.SetEffects(new SakuraModelTools.EffectSettings { glow = glow.value / 100, saturation = saturation.value / 100, contrast = contrast.value / 100, overlayColor = overlay, overlayStrength = strength.value / 100 });
                status.text = "Effects applied to this model and saved.";
            }
            new SakuraColourPicker(picker, overlay, value => Run(() => { overlay = value; Save(); }));
            glow.RegisterValueChangedCallback(_ => Run(Save)); saturation.RegisterValueChangedCallback(_ => Run(Save));
            contrast.RegisterValueChangedCallback(_ => Run(Save)); strength.RegisterValueChangedCallback(_ => Run(Save));
            Button("Reset effects", () => { tools.SetEffects(new SakuraModelTools.EffectSettings()); body.Clear(); Effects(); status.text = "Original effects restored."; });
        }
    }
}
