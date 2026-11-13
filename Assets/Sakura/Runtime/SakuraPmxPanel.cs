using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraPmxPanel
    {
        readonly SakuraAvatarViewport viewport;
        readonly SakuraBackend backend;
        readonly SakuraPmxModel model;
        readonly VisualElement body;
        readonly Label status;
        readonly VisualElement previews;
        readonly List<Texture2D> previewTextures = new List<Texture2D>();
        string tab = "Texture remapping";
        int part, bone;
        bool alive = true;
        byte[] selectedTexture;
        public SakuraPmxPanel(SakuraAvatarViewport viewport, SakuraBackend backend, VisualElement host, string initialTab = "Texture remapping", bool showTabs = true)
        {
            var owner = new VisualElement(); host.Add(owner); host = owner;
            this.viewport = viewport; this.backend = backend; model = viewport.Pmx;
            tab = initialTab;
            if (showTabs)
            {
            var tabs = new ScrollView(ScrollViewMode.Horizontal); tabs.contentContainer.style.flexDirection = FlexDirection.Row; host.Add(tabs);
            foreach (string title in new[] { "Texture remapping", "Texture coloring", "Bones", "Appearance" })
            { string choice = title; Button(title, () => { tab = choice; Render(); }, tabs.contentContainer); }
            }
            body = new VisualElement(); host.Add(body); status = new Label(); status.AddToClassList("description"); host.Add(status);
            previews = new VisualElement(); previews.style.flexDirection = FlexDirection.Row; host.Add(previews);
            host.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; viewport.CancelPartPick(); foreach (var texture in previewTextures) if (texture != null) UnityEngine.Object.Destroy(texture); previewTextures.Clear(); });
            Render();
        }
        void Preview(string file)
        {
            if (!alive) return;
            if (previewTextures.Count == 2) { foreach (var texture in previewTextures) UnityEngine.Object.Destroy(texture); previewTextures.Clear(); previews.Clear(); }
            var image = SakuraPmxModel.Decode(File.ReadAllBytes(file)); previewTextures.Add(image);
            var preview = new Image { image = image, scaleMode = ScaleMode.ScaleToFit }; preview.style.width = Length.Percent(48); preview.style.height = 160; previews.Add(preview);
        }
        void Run(Action action)
        {
            if (!alive) return;
            if (model == null || model != viewport.Pmx) { status.text = "Reopen Model Settings for the selected model."; return; }
            if (model.AppearanceMatching) { status.text = "Wait for the appearance comparison to finish."; return; }
            try { action(); } catch (Exception error) { status.text = error.Message; }
        }
        static void Button(string title, Action action, VisualElement parent) => parent.Add(new Button(action) { text = title });
        void Text(string value) { var label = new Label(value); label.AddToClassList("description"); body.Add(label); }
        void Slider(string title, float value, float min, float max, Action<float> changed)
        {
            var slider = new Slider(title, min, max) { value = value, showInputField = true };
            slider.RegisterValueChangedCallback(evt => Run(() => { changed(evt.newValue); model.Apply(); model.Save(); status.text = "Saved for this model."; })); body.Add(slider);
        }
        void Render()
        {
            body.Clear(); if (model == null) { Text("Select a PMX model first."); return; }
            if (tab == "Bones") { Bones(); return; }
            if (tab == "Appearance") { Appearance(); return; }
            var choices = model.Definition.materials.Select((item, index) => index + " · " + item.name).ToList();
            var material = new DropdownField("Texture / material", choices, Math.Min(part, choices.Count - 1));
            material.RegisterValueChangedCallback(evt => { part = material.index; selectedTexture = null; Render(); }); body.Add(material);
            var edit = model.GetPart(part);
            if (tab == "Texture remapping")
            {
                var snapshot = new Button(Diagnose) { text = "Take snapshot and check textures" };
                body.Insert(0,snapshot);
                Text("Replace the diffuse image for this material. Use a texture designed for this model’s UV layout.");
                var fileRow = new VisualElement(); fileRow.AddToClassList("row"); body.Add(fileRow);
                var filename = new Label("No file chosen");
                var apply = new Button(() => Run(() =>
                {
                    if (selectedTexture == null) return;
                    model.ReplaceTexture(part,selectedTexture); status.text = "Replacement saved for this model.";
                })) { text = "Apply replacement" };
                apply.SetEnabled(selectedTexture != null);
                Button("Choose file", () => Run(() =>
                {
                    int selected = part;
                    backend.RunPmxTool("texture", viewport.LoadedModelPath, (json, error) => Run(() =>
                    {
                        if (error != null) throw new InvalidOperationException(error);
                        var result = JObject.Parse(json); if (result.Value<bool>("cancelled")) { status.text = "Cancelled."; return; }
                        if (selected != part) { status.text = "Material changed. Choose the texture again for this material."; return; }
                        selectedTexture = Convert.FromBase64String(result.Value<string>("image"));
                        filename.text = result.Value<string>("name") ?? "Replacement selected";
                        apply.SetEnabled(true); status.text = "Texture selected. Click Apply replacement to save it.";
                    }));
                }), fileRow);
                fileRow.Add(filename); body.Add(apply);
                Button("Restore original texture", () => Run(() => { model.RestoreTexture(part); status.text = "Original texture restored."; }), body);
            }
            else
            {
                var operation = new DropdownField("What would you like to change?",new List<string> { "Change color","Change transparency" },0); body.Add(operation);
                Button("Pick part on model",() => Run(() =>
                {
                    status.text = "Click the part you want to change on the model.";
                    viewport.BeginPartPick(false,index => { if (!alive) return; part = index; selectedTexture = null; Render(); status.text = "Selected " + model.Definition.materials[index].name; });
                }),body);
                var area = new VisualElement(); body.Add(area);
                void RenderOperation()
                {
                    area.Clear();
                    if (operation.index == 0)
                    {
                        Color picked = edit.color;
                        new SakuraColourPicker(area,edit.color,value => picked = value);
                        var strength = new Slider("Color strength",0,100) { value = edit.strength * 100, showInputField = true }; area.Add(strength);
                        Button("Apply color",() => Run(() => { edit.color = picked; edit.strength = strength.value / 100; edit.colored = true; model.Apply(); model.Save(); status.text = "Color saved for this model."; }),area);
                        Button("Restore original color",() => Run(() => { edit.colored = false; model.Apply(); model.Save(); Render(); }),area);
                    }
                    else
                    {
                        var transparency = new Slider("Transparency %",-100,100) { value = edit.transparency * 100, showInputField = true }; area.Add(transparency);
                        transparency.RegisterValueChangedCallback(change => Run(() => { edit.transparency = change.newValue / 100; model.Apply(); model.Save(); status.text = "Transparency saved. Existing color retained."; }));
                        area.Add(new Label("Negative values strengthen opacity. Texture cutouts stay intact."));
                    }
                }
                operation.RegisterValueChangedCallback(_ => RenderOperation()); RenderOperation();
                viewport.AvatarRoot.GetComponent<SakuraMaterialExpressions>()?.BuildCapturePanel(body, () => part, () => operation.index == 0 ? "tint" : "opacity");
                Text("PMX appearance"); Appearance();
            }
        }
        void Bones()
        {
            Text("Freeze a bone at its local rest pose. Duplicate bone names retain separate indexed controls.");
            var search = new TextField("Search bones"); body.Add(search); var area = new VisualElement(); body.Add(area);
            void Populate()
            {
                area.Clear();
                var indices = Enumerable.Range(0, model.Instance.BoneTransforms.Length).Where(index => model.Instance.BoneTransforms[index].name.IndexOf(search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (indices.Count == 0) { area.Add(new Label("No matching bones.")); return; }
                var choices = indices.Select(index => index + " · " + model.Instance.BoneTransforms[index].name).ToList();
                int selected = Math.Max(0, indices.IndexOf(bone)); bone = indices[selected];
                var picker = new DropdownField("Bone", choices, selected); area.Add(picker);
                var freeze = new Toggle("Freeze this bone") { value = model.Settings.frozen.Contains(bone) }; area.Add(freeze);
                picker.RegisterValueChangedCallback(evt => { bone = indices[picker.index]; freeze.SetValueWithoutNotify(model.Settings.frozen.Contains(bone)); });
                freeze.RegisterValueChangedCallback(evt => Run(() => { model.Freeze(bone, evt.newValue); status.text = model.Settings.frozen.Count + " frozen bones saved."; }));
            }
            search.RegisterValueChangedCallback(_ => Populate()); Populate();
            Button("Pick bone on model",() => Run(() =>
            {
                status.text = "Click the model to select the strongest bone at that point.";
                viewport.BeginPartPick(true,index => { if (!alive) return; bone = index; search.SetValueWithoutNotify(""); Populate(); status.text = "Selected " + model.Instance.BoneTransforms[index].name; });
            }),body);
            Button("Restore all bones", () => Run(() => { model.Settings.frozen.Clear(); model.Save(); Populate(); status.text = "All bones unfrozen."; }), body);
        }
        void Appearance()
        {
            Text("Saved per model. These controls adjust the native MMD toon shader without replacing textures."); var s = model.Settings;
            Slider("Brightness", s.brightness, 0, 2, value => s.brightness = value);
            Slider("Ambient fill", s.ambient, 0, 2, value => s.ambient = value);
            Slider("Authored material tint", s.diffuse, 0, 1, value => s.diffuse = value);
            Slider("Toon shading", s.toon, 0, 1, value => s.toon = value);
            Slider("Directional shading", s.directional, 0, 1, value => s.directional = value);
            Slider("Sphere / shine maps", s.sphere, 0, 2, value => s.sphere = value);
            Slider("Specular highlights", s.specular, 0, 2, value => s.specular = value);
            Slider("Colour saturation", s.saturation, 0, 2, value => s.saturation = value);
            Button("Reset appearance sliders", () => Run(() =>
            {
                s.brightness = s.ambient = s.diffuse = s.toon = s.sphere = s.specular = s.saturation = 1; s.directional = 0; model.Apply(); model.Save(); Render();
            }), body);
            Button("Take appearance snapshot / open PMX Editor", () => Run(() =>
            {
                Preview(viewport.SaveSnapshot("appearance")); backend.RunPmxTool("editor", viewport.LoadedModelPath, (json, error) => status.text = error ?? "Snapshot saved. The selected model was sent to PMX Editor for comparison.");
            }), body);
            Button("Match appearance from PMX Editor", () => Run(() =>
            {
                status.text = "Capturing the selected model in PMX Editor…";
                Preview(viewport.SaveSnapshot("appearance-before"));
                backend.RunPmxTool("reference", viewport.LoadedModelPath, async (json, error) =>
                {
                    if (model == null || viewport.Pmx != model) return;
                    try
                    {
                        if (error != null) throw new InvalidOperationException(error);
                        status.text = await SakuraPmxAppearance.Match(viewport, json, text => status.text = text);
                        Preview(await viewport.SaveSnapshotNextFrame("appearance-after")); if (alive) Render();
                    }
                    catch (Exception failure) { status.text = failure.Message; }
                }, viewport.EditorDirection());
            }), body);
        }
        void Diagnose() => Run(() =>
        {
            Preview(viewport.SaveSnapshot("textures-before")); status.text = "Checking every referenced texture…"; var slots = new JArray();
            foreach (var item in model.Definition.materials)
            {
                void Add(string slot, string reference) { if (!string.IsNullOrEmpty(reference)) slots.Add(new JObject { ["material"] = item.index, ["slot"] = slot, ["reference"] = reference }); }
                Add("diffuse", item.texture); Add("sphere", item.sphereTexture); if (!item.toonShared) Add("toon", item.toonTexture);
            }
            backend.Api("/api/models/texture-diagnose", new JObject { ["model"] = viewport.LoadedModelPath, ["slots"] = slots }.ToString(), FinishDiagnostics);
        });
        async void FinishDiagnostics(string json, string error)
        {
            if (!alive || model == null || viewport.Pmx != model) return;
            try
            {
                if (error != null) throw new InvalidOperationException(error);
                var results = (JArray)JObject.Parse(json)["slots"];
                int repaired = 0;
                foreach (var item in results.Where(item => item.Value<string>("status") == "repairable"))
                {
                    model.RepairTexture(item.Value<int>("material"), item.Value<string>("slot"), item.Value<string>("resolved")); repaired++;
                }
                Preview(await viewport.SaveSnapshotNextFrame("textures-after"));
                int missing = results.Count(item => item.Value<string>("status") != "ok" && item.Value<string>("status") != "repairable");
                status.text = "Checked " + results.Count + " references. " + missing + " need attention. " + repaired + " confirmed path repairs saved.";
                backend.RunPmxTool("editor", viewport.LoadedModelPath, (reply, failure) => { if (failure != null) status.text += " " + failure; });
            }
            catch (Exception failure) { status.text = failure.Message; }
        }
    }
}
