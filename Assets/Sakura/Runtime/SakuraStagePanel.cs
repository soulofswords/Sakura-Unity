using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraStagePanel
    {
        public SakuraStage Stage { get; }
        public SakuraAvatarActions Actions { get; }
        readonly SakuraBackend backend;
        readonly SakuraAvatarViewport viewport;
        readonly Label status;
        readonly DropdownField library, selection, kind;
        readonly TextField search, folder;
        readonly VisualElement browser, crumbs;
        readonly Vector3Field position, avatarPosition;
        readonly FloatField scale, yaw, avatarYaw;
        readonly Toggle motion;
        readonly List<JObject> assets = new List<JObject>();
        readonly List<string> paths = new List<string>();
        readonly List<string> selectionIds = new List<string>();
        string browsing = "";
        bool alive = true, updating;
        public SakuraStagePanel(SakuraBackend backend, SakuraAvatarViewport viewport, VisualElement parent, bool props = false)
        {
            this.backend = backend; this.viewport = viewport;
            Stage = viewport.GetComponent<SakuraStage>() ?? viewport.gameObject.AddComponent<SakuraStage>();
            Actions = viewport.GetComponent<SakuraAvatarActions>() ?? viewport.gameObject.AddComponent<SakuraAvatarActions>();
            Actions.Viewport = viewport; Actions.Stage = Stage;
            Stage.Initialize(backend.SourceFolder);
            (viewport.GetComponent<SakuraBackdrop>() ?? viewport.gameObject.AddComponent<SakuraBackdrop>()).BuildPanel(backend, parent);
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("3D backgrounds & props"); title.AddToClassList("card-title"); card.Add(title);
            status = new Label("Opt in to load scenery alongside your character. Up to four static PMX objects."); status.style.whiteSpace = WhiteSpace.Normal;
            Stage.StatusChanged += Notice; Actions.StatusChanged += Notice;
            var enabled = new Toggle("Enable PMX backgrounds and props") { value = Stage.Settings.enabled }; card.Add(enabled);
            enabled.RegisterValueChangedCallback(e => Run(async () => { Actions.DisableRoom(); motion.SetValueWithoutNotify(false); await Stage.SetEnabled(e.newValue); }));
            kind = new DropdownField("Asset library", new List<string> { "Stages", "Tools & Props" }, props ? 1 : 0); card.Add(kind);
            kind.RegisterValueChangedCallback(_ => { browsing = ""; RenderLibrary(); });
            search = new TextField("Search this folder"); card.Add(search); search.RegisterValueChangedCallback(_ => RenderLibrary());
            crumbs = new VisualElement(); card.Add(crumbs); browser = new VisualElement(); card.Add(browser);
            library = new DropdownField("Selected local asset", new List<string> { "Choose a PMX asset" }, 0); card.Add(library);
            Button("Refresh assets", Refresh, card);
            Button("Add selected asset to stage", () => Run(async () => { if (library.index >= 0 && library.index < paths.Count) await Stage.Add(paths[library.index]); RefreshSelection(); }), card);
            folder = new TextField("Complete asset folder"); folder.tooltip = "Paste the complete folder path. Subfolders and textures are preserved by Sakura's existing package importer."; card.Add(folder);
            Button("Import complete folder", () => Run(ImportFolder), card);
            selection = new DropdownField("Stage objects", new List<string> { "Choose a stage object" }, 0); card.Add(selection);
            selection.RegisterValueChangedCallback(_ => { if (!updating && selection.index >= 0 && selection.index < selectionIds.Count) { Actions.DisableRoom(); motion.SetValueWithoutNotify(false); Stage.Select(selectionIds[selection.index]); ShowTransform(); } });
            position = new Vector3Field("Position"); scale = new FloatField("Scale"); yaw = new FloatField("Rotation Y"); card.Add(position); card.Add(scale); card.Add(yaw);
            position.RegisterValueChangedCallback(_ => Transform()); scale.RegisterValueChangedCallback(_ => Transform()); yaw.RegisterValueChangedCallback(_ => Transform());
            Button("Remove selected object", () => { Actions.DisableRoom(); motion.SetValueWithoutNotify(false); Stage.RemoveSelected(); RefreshSelection(); }, card);
            card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            card.style.display = backend.Mode == "Companion" ? DisplayStyle.Flex : DisplayStyle.None;
            title = new Label("Companion room & body"); title.AddToClassList("card-title"); card.Add(title);
            var source = new DropdownField("Room source", new List<string> { "Simple room", "Imported PMX room" }, Actions.ImportedRoom ? 1 : 0); card.Add(source);
            motion = new Toggle("Let Sakura animate her body and interact with a room") { value = Actions.RoomEnabled }; card.Add(motion);
            motion.RegisterValueChangedCallback(e => { try { if (e.newValue) Actions.EnableRoom(source.index == 1); else Actions.DisableRoom(); } catch (Exception error) { motion.SetValueWithoutNotify(false); Notice(error.Message); } });
            source.RegisterValueChangedCallback(_ => { Actions.DisableRoom(); motion.SetValueWithoutNotify(false); });
            avatarPosition = new Vector3Field("Character position"); avatarYaw = new FloatField("Character facing Y"); card.Add(avatarPosition); card.Add(avatarYaw);
            Button("Read character placement", () => { if (viewport.AvatarRoot == null) return; avatarPosition.value = viewport.AvatarRoot.transform.position; avatarYaw.value = viewport.AvatarRoot.transform.eulerAngles.y; }, card);
            Button("Place character for marking", () => { if (viewport.AvatarRoot == null || !SakuraStage.Finite(avatarPosition.value) || float.IsNaN(avatarYaw.value) || float.IsInfinity(avatarYaw.value)) return; Actions.Stop(); viewport.AvatarRoot.transform.position = avatarPosition.value; viewport.AvatarRoot.transform.rotation = Quaternion.Euler(0, avatarYaw.value, 0); }, card);
            foreach (string action in new[] { "desk", "mirror", "bed", "sit", "walk" }) { string a = action; Button(a == "walk" ? "Add walk point" : "Mark " + (a == "sit" ? "chair" : a), () => { try { Actions.Mark(a); Notice("Saved " + a + ". Mark routes in walking order; keep each segment clear."); } catch (Exception error) { Notice(error.Message); } }, card); }
            Button("Clear room points", Actions.ClearPoints, card);
            foreach (string action in new[] { "home", "desk", "mirror", "bed", "sit", "walk", "roam", "explore", "nod", "shake_head", "stretch" }) { string a = action; Button("Room: " + a, () => { if (!Actions.Act(a)) Notice("Enable room motion and mark the required points first."); }, card); }
            var dance = new DropdownField("Body motion", new List<string> { "groove", "step", "wave", "twist", "disco", "march", "hands_both", "hand_left", "hand_right", "greeting_wave", "chest_tap", "laugh", "laugh_bend" }, 0); card.Add(dance);
            Button("Start body motion", () => { if (!Actions.Gesture(dance.value)) Notice("Load a rigged character and stop tracking first."); }, card);
            Button("Stop & return home", Actions.Stop, card);
            var info = new Label("Furniture points follow the selected stage transform. Bed points mark the mattress centre. Chair points mark the standing position over its seat. Tracking takes priority. Routes require clear segments; collision detection and automatic room recognition are not available."); info.style.whiteSpace = WhiteSpace.Normal; card.Add(info); card.Add(status);
            card.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; Stage.StatusChanged -= Notice; Actions.StatusChanged -= Notice; });
            RefreshSelection(); Refresh();
        }
        public bool Dispatch(JObject reply) => alive && backend.Mode == "Companion" && Actions.Dispatch(reply);
        void Notice(string text) { if (alive) status.text = text; }
        static void Button(string text, Action action, VisualElement parent) { parent.Add(new Button(action) { text = text }); }
        async void Run(Func<Task> operation) { try { await operation(); } catch (Exception error) { Notice(error.Message); } }
        void Transform() { if (updating) return; try { Actions.Stop(); Stage.SetTransform(position.value, scale.value, yaw.value); } catch (Exception error) { Notice(error.Message); } }
        void ShowTransform()
        {
            updating = true; var item = Stage.Selected; position.SetValueWithoutNotify(item?.position ?? Vector3.zero); scale.SetValueWithoutNotify(item?.scale ?? 1); yaw.SetValueWithoutNotify(item?.yaw ?? 0); updating = false;
        }
        void RefreshSelection()
        {
            updating = true; selectionIds.Clear(); selectionIds.AddRange(Stage.Settings.items.Select(i => i.id)); selection.choices = Stage.Settings.items.Select(i => i.name).DefaultIfEmpty("No stage objects").ToList(); selection.index = Mathf.Max(0, selectionIds.IndexOf(Stage.Settings.selected)); updating = false; ShowTransform();
        }
        public void Refresh()
        {
            assets.Clear();
            backend.Api("/api/scenery/assets?kind=stage", null, (json, error) => { if (!alive) return; if (error != null) { Notice(error); return; } ReadAssets(json); backend.Api("/api/scenery/assets?kind=prop", null, (prop, failure) => { if (!alive) return; if (failure != null) { Notice(failure); return; } ReadAssets(prop); RenderLibrary(); }); });
        }
        void ReadAssets(string json) { try { assets.AddRange((JObject.Parse(json)["models"] as JArray ?? new JArray()).OfType<JObject>()); } catch (JsonException e) { Notice(e.Message); } }
        void RenderLibrary()
        {
            browser.Clear(); crumbs.Clear(); paths.Clear(); string wanted = kind.index == 0 ? "stage" : "prop", prefix = browsing.Length == 0 ? "" : browsing + "/", query = search.value.Trim();
            Button("All folders", () => { browsing = ""; RenderLibrary(); }, crumbs);
            if (browsing.Length > 0) Button(browsing + " / ←", () => { int slash = browsing.LastIndexOf('/'); browsing = slash < 0 ? "" : browsing.Substring(0, slash); RenderLibrary(); }, crumbs);
            var folders = new SortedSet<string>(); var labels = new List<string>();
            foreach (var row in assets.Where(a => (string)a["asset_kind"] == wanted))
            {
                string relative = string.Join("/", (row["folders"] as JArray ?? new JArray()).Values<string>()), name = (string)row["name"] ?? "PMX", path = (string)row["path"];
                if (query.Length > 0) { if ((relative + "/" + name).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue; }
                else if (relative != browsing) { if (!relative.StartsWith(prefix, StringComparison.Ordinal)) continue; string rest = relative.Substring(prefix.Length); if (rest.Length > 0) folders.Add(rest.Split('/')[0]); continue; }
                paths.Add(path); labels.Add(relative.Length > 0 ? relative + "/" + name : name);
            }
            foreach (string child in folders) { string next = prefix + child; Button("▣ " + child, () => { browsing = next; RenderLibrary(); }, browser); }
            library.choices = labels.Count == 0 ? new List<string> { "No PMX assets in this folder" } : labels; library.index = 0;
        }
        async Task ImportFolder()
        {
            string input = Path.GetFullPath(folder.value.Trim().Trim('"'));
            if (!Directory.Exists(input)) throw new DirectoryNotFoundException("Choose the complete PMX asset folder.");
            string python = Path.Combine(backend.SourceFolder, "venv", "Scripts", "python.exe");
            // Reuse the source application's validated package importer, including its dedicated scenery bucket.
            const string script = "import json,sys;from pathlib import Path;from model_import import import_package,MAX_BYTES;p=json.loads(sys.stdin.read());base=Path(p['folder']);files=[];total=0\nfor f in base.rglob('*'):\n if f.is_symlink():raise ValueError('Symbolic links are not supported')\n if f.is_file():\n  total+=f.stat().st_size\n  if total>MAX_BYTES or len(files)>=5000:raise ValueError('Package exceeds 512 MB or 5000 files')\n  files.append((str(Path(base.name)/f.relative_to(base)).replace(chr(92),'/'),f.read_bytes()))\nprint(json.dumps(import_package(Path(p['root'])/'models',files,'pmx','','',p['kind'])))";
            var info = new ProcessStartInfo(python) { Arguments = "-c \"" + script + "\"", WorkingDirectory = backend.SourceFolder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            string body = new JObject { ["folder"] = input, ["root"] = backend.SourceFolder, ["kind"] = kind.index == 0 ? "stage" : "prop" }.ToString(Formatting.None);
            Notice("Importing complete scenery folder…");
            using (var process = new Process { StartInfo = info })
            {
                process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(body); process.StandardInput.Close();
                await Task.Run(() => process.WaitForExit()); string result = await output, error = await errors;
                if (process.ExitCode != 0) throw new InvalidOperationException("Scenery import failed. " + error.Trim());
                JObject.Parse(result); Notice("Imported complete package. Refreshing the scenery library…"); if (alive) Refresh();
            }
        }
    }
}
