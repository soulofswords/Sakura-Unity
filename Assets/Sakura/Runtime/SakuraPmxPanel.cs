using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        readonly VisualElement owner;
        readonly Label status;
        readonly VisualElement previews;
        readonly List<Texture2D> previewTextures = new List<Texture2D>();
        string tab = "Texture remapping";
        int part, bone;
        bool alive = true;
        bool diagnosing;
        byte[] selectedTexture;
        public SakuraPmxPanel(SakuraAvatarViewport viewport, SakuraBackend backend, VisualElement host, string initialTab = "Texture remapping", bool showTabs = true)
        {
            owner = new VisualElement(); host.Add(owner); host = owner;
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
            var slider = new SakuraSlider(title, min, max) { value = value, showInputField = true };
            slider.RegisterValueChangedCallback(evt => Run(() => { changed(evt.newValue); model.Apply(); model.Save(); status.text = "Saved for this model."; })); body.Add(slider);
        }
        void Render()
        {
            body.Clear();
            if (status.parent != owner) owner.Add(status);
            if (previews.parent != owner) owner.Add(previews);
            if (model == null) { Text("Select a PMX model first."); return; }
            if (tab == "Bones") { Bones(); return; }
            if (tab == "Appearance") { Appearance(); return; }
            Button("Import appearance package from legacy Sakura", () => Run(() =>
            {
                status.text = "Choose an appearance package exported by legacy Sakura.";
                backend.RunPmxTool("appearance-import", viewport.LoadedModelPath, (json, error) => Run(() => {
                    if (error != null) throw new InvalidDataException(error);
                    var result = JObject.Parse(json);
                    if (result.Value<bool>("cancelled")) { status.text = "Import cancelled."; return; }
                    status.text = SakuraAppearanceImport.Apply(result.Value<string>("path"), viewport, backend);
                    Render();
                }));
            }), body);
            var choices = model.Definition.materials.Select((item, index) => index + " · " + item.name).ToList();
            var material = new DropdownField("Texture / material", choices, Math.Min(part, choices.Count - 1));
            material.RegisterValueChangedCallback(evt => { part = material.index; selectedTexture = null; Render(); }); body.Add(material);
            var edit = model.GetPart(part);
            if (tab == "Texture remapping")
            {
                var snapshot = new Button(Diagnose) { text = "Take snapshot and check textures" };
                body.Insert(0,snapshot);
                if (string.IsNullOrEmpty(status.text)) status.text = "Check texture decoding and repair confirmed PMX paths. This preserves original artwork and your saved colour overrides.";
                body.Insert(1, status); body.Insert(2, previews);
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
                PackageTextures();
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
                        var strength = new SakuraSlider("Color strength",0,100) { value = edit.strength * 100, showInputField = true }; area.Add(strength);
                        Button("Apply color",() => Run(() => { edit.color = picked; edit.strength = strength.value / 100; edit.colored = true; model.Apply(); model.Save(); status.text = "Color saved for this model."; }),area);
                        Button("Restore original color",() => Run(() => { edit.colored = false; model.Apply(); model.Save(); Render(); }),area);
                    }
                    else
                    {
                        var transparency = new SakuraSlider("Transparency %",-100,100) { value = edit.transparency * 100, showInputField = true }; area.Add(transparency);
                        transparency.RegisterValueChangedCallback(change => Run(() => { edit.transparency = change.newValue / 100; model.Apply(); model.Save(); status.text = "Transparency saved. Existing color retained."; }));
                        area.Add(new Label("Negative values strengthen opacity. Texture cutouts stay intact."));
                    }
                }
                operation.RegisterValueChangedCallback(_ => RenderOperation()); RenderOperation();
                viewport.AvatarRoot.GetComponent<SakuraMaterialExpressions>()?.BuildCapturePanel(body, () => part, () => operation.index == 0 ? "tint" : "opacity");
                Text("PMX appearance"); Appearance();
            }
        }
        void PackageTextures()
        {
            var card = new VisualElement(); card.AddToClassList("card"); body.Add(card);
            var heading = new Label("PMX material texture:"); heading.AddToClassList("card-title"); card.Add(heading);
            var slots = model.Definition.materials.SelectMany(item => new[] { "diffuse", "sphere", "toon" }
                .Select(slot => (index: item.index, name: item.name, slot))).ToList();
            var slotChoice = new DropdownField(slots.Select(item => (item.index + 1) + ". " + item.name + " — " + item.slot).ToList(), 0);
            slotChoice.name = "pmx-material-slot"; card.Add(slotChoice);
            var original = new Label(); original.AddToClassList("description"); card.Add(original);
            var imageChoice = new DropdownField("Replacement image", new List<string> { "Reading texture files…" }, 0);
            imageChoice.name = "pmx-texture-choice"; card.Add(imageChoice);
            bool ready = false, saving = false;
            void Selection()
            {
                var item = slots[slotChoice.index];
                string authored = model.OriginalTextureReference(item.index, item.slot) ?? "";
                var definition = model.Definition.materials[item.index];
                string current = item.slot == "diffuse" ? definition.texture : item.slot == "sphere" ? definition.sphereTexture : definition.toonShared ? "Shared toon " + (definition.sharedToonIndex + 1) : definition.toonTexture;
                original.text = "Original: " + (string.IsNullOrEmpty(authored) ? "(none)" : authored) + ". Current override: " + (current == authored || string.IsNullOrEmpty(current) ? "none" : current) + ".";
                if (ready) imageChoice.SetValueWithoutNotify(imageChoice.choices.Contains(current) ? current : "");
            }
            void Refresh()
            {
                backend.Api("/api/models/texture-options?model=" + Uri.EscapeDataString(viewport.LoadedModelPath), null, (json, error) => Run(() =>
                {
                    if (error != null) throw new InvalidOperationException(error);
                    var files = (JArray)JObject.Parse(json)["textures"];
                    imageChoice.choices = files.Values<string>().ToList(); ready = imageChoice.choices.Count > 0;
                    imageChoice.SetEnabled(ready); Selection();
                }));
            }
            void Save(bool reset)
            {
                if (saving || viewport.Loading) { status.text = "Wait for the texture assignment to finish loading."; return; }
                if (!reset && (!ready || string.IsNullOrEmpty(imageChoice.value))) { status.text = "Choose a material and replacement image first."; return; }
                saving = true;
                var item = slots[slotChoice.index]; string path = viewport.LoadedModelPath;
                backend.Api("/api/models/texture-material", new JObject { ["model"] = path, ["material"] = item.index, ["slot"] = item.slot,
                    ["texture"] = reset ? JValue.CreateNull() : new JValue(imageChoice.value) }.ToString(), async (json, error) =>
                {
                    try
                    {
                        if (!alive || model == null || viewport.Pmx != model) return;
                        if (error != null) { status.text = error; return; }
                        var framing = viewport.PluginPosition();
                        await viewport.LoadModel(backend.SourceFolder, path);
                        if (viewport.LoadedModelPath == path && viewport.LastImportError == null) viewport.PluginMove(framing);
                    }
                    catch (Exception failure) { if (alive) status.text = failure.Message; }
                    finally { saving = false; }
                });
            }
            slotChoice.RegisterValueChangedCallback(_ => Selection());
            var actions = new VisualElement(); actions.AddToClassList("row"); card.Add(actions);
            Button("Apply texture", () => Run(() => Save(false)), actions);
            Button("Restore original", () => Run(() => Save(true)), actions);
            Button("Refresh files", () => Run(Refresh), actions);
            var hint = new Label("Choose a loaded PMX model. To use a new image, copy it into the model folder and refresh files. Original PMX files are unchanged."); hint.AddToClassList("description"); card.Add(hint);
            Selection(); Refresh();
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
            Button("Restore all bones", () => Run(() => { model.Settings.frozen.Clear(); model.GetComponent<SakuraRigMonitor>()?.Forget(); model.Save(); Populate(); status.text = "All bones unfrozen."; }), body);
            SakuraRigMonitor.BuildPanel(model.gameObject, body);
            var diagnostic = new Label { style = { whiteSpace = WhiteSpace.Normal } }; body.Add(diagnostic);
            Button("Diagnose bones", () => Run(() =>
            {
                var transforms = model.Instance.BoneTransforms;
                var names = transforms.Select(bone => bone.name).ToArray();
                string[] standard = { "センター", "上半身", "上半身2", "首", "頭", "左腕", "右腕", "左足", "右足", "左ひじ", "右ひじ", "左ひざ", "右ひざ" };
                var missing = standard.Where(name => !names.Any(value => value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                var duplicates = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key + " ×" + group.Count()).ToArray();
                bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
                var broken = transforms.Select((bone, index) => (bone, index)).Where(pair =>
                {
                    var position = pair.bone.localPosition; var rotation = pair.bone.localRotation;
                    return !Finite(position.x) || !Finite(position.y) || !Finite(position.z) || !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w);
                }).Select(pair => pair.index + " · " + pair.bone.name).ToArray();
                diagnostic.text = (missing.Length == 0 ? "All standard humanoid bones found. " : "Missing standard bones: " + string.Join(", ", missing) + ". ")
                    + (duplicates.Length == 0 ? "No duplicate names. " : "Duplicate names: " + string.Join(", ", duplicates) + ". ")
                    + (broken.Length == 0 ? "All bone transforms are finite." : "Non-finite transforms: " + string.Join(", ", broken) + ".");
                status.text = "Bone diagnosis complete.";
            }), body);
            Button("Load saved bone repairs", () => Run(() =>
            {
                string modelPath = viewport.LoadedModelPath ?? "";
                if (string.IsNullOrEmpty(modelPath)) throw new InvalidDataException("Select a PMX model first.");
                backend.Api("/api/models/bone-repairs?model=" + Uri.EscapeDataString(modelPath), null, (json, error) => Run(() =>
                {
                    if (error != null) throw new InvalidDataException(error);
                    var data = JObject.Parse(json);
                    if (data.Value<bool?>("invalidated") == true) { status.text = "Saved repairs belong to a different version of this model."; return; }
                    var repairs = data["repairs"] as JObject;
                    var diagnostic = repairs?["diagnostic"] as JObject;
                    var frozen = diagnostic?["frozen"] as JArray;
                    if (frozen == null) { status.text = "No saved bone repairs for this model."; return; }
                    model.Settings.frozen = frozen.Values<int>().Where(index => index >= 0 && index < model.Instance.BoneTransforms.Length).Distinct().ToList();
                    model.Apply(); model.Save(); Populate();
                    status.text = "Loaded " + model.Settings.frozen.Count + " saved bone repairs.";
                }));
            }), body);
            Button("Save bone repairs to library", () => Run(() =>
            {
                string modelPath = viewport.LoadedModelPath ?? "";
                if (string.IsNullOrEmpty(modelPath)) throw new InvalidDataException("Select a PMX model first.");
                string signature = string.Join(";", model.Instance.BoneTransforms.Select(entry => entry.name + ":" + entry.localPosition.x.ToString("R") + "," + entry.localPosition.y.ToString("R") + "," + entry.localPosition.z.ToString("R")));
                var repairs = new JObject
                {
                    ["manual"] = new JArray(),
                    ["diagnostic"] = new JObject
                    {
                        ["version"] = 2,
                        ["signature"] = signature,
                        ["frozen"] = new JArray(model.Settings.frozen.Select(index => (object)index))
                    }
                };
                backend.Api("/api/models/bone-repairs", new JObject { ["model"] = modelPath, ["repairs"] = repairs }.ToString(), (json, error) => Run(() =>
                {
                    if (error != null) throw new InvalidDataException(error);
                    status.text = "Bone repairs saved to the model library.";
                }));
            }), body);
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
            if (s.previousMatch?.Length == 8)
                Button("Undo automatic appearance match", () => Run(() =>
                {
                    model.UndoAppearanceMatch();
                    status.text = "Restored appearance from before automatic matching.";
                    Render();
                }), body);
        }
        async void Diagnose()
        {
            if (diagnosing || !alive || model == null || model != viewport.Pmx || model.AppearanceMatching) return;
            diagnosing = true;
            bool Current() => alive && model != null && viewport.Pmx == model;
            void RequireCurrent() { if (!Current()) throw new InvalidOperationException("Model changed. Run the texture check on the selected model."); }
            Task<string> Api(string route, JObject payload)
            {
                var done = new TaskCompletionSource<string>();
                backend.Api(route, payload.ToString(), (json, error) => { if (error == null) done.TrySetResult(json); else done.TrySetException(new InvalidOperationException(error)); });
                return done.Task;
            }
            try
            {
                string path = viewport.LoadedModelPath;
                string before = await viewport.SaveTextureDiagnosticSnapshot("textures-before"); RequireCurrent(); Preview(before);
                status.text = "Checking every referenced texture…";
                var slots = new JArray();
                foreach (var item in model.Definition.materials)
                {
                    void Add(string slot, string reference) { if (!string.IsNullOrEmpty(reference)) slots.Add(new JObject { ["material"] = item.index, ["slot"] = slot, ["reference"] = reference }); }
                    Add("diffuse", item.texture); Add("sphere", item.sphereTexture); if (!item.toonShared) Add("toon", item.toonTexture);
                }
                var report = JObject.Parse(await Api("/api/models/texture-diagnose", new JObject { ["model"] = path, ["slots"] = slots })); RequireCurrent();
                var results = report["slots"] as JArray ?? throw new InvalidDataException("Texture diagnosis returned no references.");
                int repaired = 0;
                var issues = new JArray();
                foreach (var item in results)
                {
                    RequireCurrent();
                    string state = item.Value<string>("status"), slot = item.Value<string>("slot");
                    int material = item.Value<int>("material");
                    if (state != "ok" && state != "repairable") { issues.Add("Material " + (material + 1) + " / " + slot + ": " + item.Value<string>("message")); continue; }
                    string resolved = item.Value<string>("resolved") ?? item.Value<string>("reference");
                    if (state == "repairable")
                    {
                        await Api("/api/models/texture-material", new JObject { ["model"] = path, ["material"] = material, ["slot"] = slot, ["texture"] = resolved }); RequireCurrent(); repaired++;
                    }
                    model.RefreshTexture(material, slot, resolved);
                }
                status.text = "Texture paths checked. Comparing rendered appearance with PMX Editor…";
                var reference = new TaskCompletionSource<string>();
                backend.RunPmxTool("reference", path, (json, error) => { if (error == null) reference.TrySetResult(json); else reference.TrySetException(new InvalidOperationException(error)); }, viewport.EditorDirection());
                try
                {
                    string json = await reference.Task; RequireCurrent();
                    report["appearance"] = await SakuraPmxAppearance.Match(viewport, json, text => { if (Current()) status.text = text; }); RequireCurrent();
                }
                catch (Exception error) { RequireCurrent(); issues.Add("PMX Editor appearance comparison: " + error.Message); }
                string after = await viewport.SaveTextureDiagnosticSnapshot("textures-after"); RequireCurrent(); Preview(after);
                var loaded = SakuraTextureDiagnostics.Inspect(viewport.AvatarRoot);
                foreach (var issue in (JArray)loaded["issues"]) issues.Add(issue);
                report["checked"] = results.Count; report["repaired"] = repaired; report["issues"] = issues; report["loadedImages"] = loaded["checked"];
                SakuraTextureDiagnostics.Save(before, after, report);
                status.text = "Checked all " + results.Count + " texture references. " + repaired + " confirmed path repairs saved. "
                    + (issues.Count == 0 ? "No missing or unreadable textures detected." : "Needs attention: " + string.Join(" ", issues.Values<string>()))
                    + " Snapshots and report saved locally for this model.";
            }
            catch (Exception failure) { if (Current()) status.text = failure.Message; }
            finally { diagnosing = false; }
        }
    }
}
