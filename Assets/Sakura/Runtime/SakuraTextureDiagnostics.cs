using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public static class SakuraTextureDiagnostics
    {
        static event Action<string> Saved;
        static readonly HashSet<string> Running = new HashSet<string>();

        public static void BuildPanel(SakuraAvatarViewport viewport, SakuraBackend backend, VisualElement host)
        {
            string path = viewport.LoadedModelPath, folder = viewport.SnapshotDirectory;
            var status = new Label("Check texture decoding across the whole model. Original artwork, transparency and saved colours are preserved.");
            status.AddToClassList("description");
            var previews = new VisualElement(); previews.AddToClassList("row");
            var images = new List<Texture2D>();
            bool alive = true;
            void Clear() { previews.Clear(); foreach (var image in images) UnityEngine.Object.Destroy(image); images.Clear(); }
            void Refresh(string savedFolder)
            {
                if (!alive || savedFolder != folder) return;
                try
                {
                    string reportFile = Path.Combine(folder, "texture-report.json");
                    if (!File.Exists(reportFile)) return;
                    var report = JObject.Parse(File.ReadAllText(reportFile));
                    status.text = report.Value<string>("summary") ?? status.text; Clear();
                    foreach (string key in new[] { "before", "after" })
                    {
                        string file = report.Value<string>(key);
                        if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file) continue;
                        string full = Path.Combine(folder, file); if (!File.Exists(full)) continue;
                        var image = SakuraPmxModel.Decode(File.ReadAllBytes(full)); images.Add(image);
                        var preview = new Image { image = image, scaleMode = ScaleMode.ScaleToFit };
                        preview.style.width = Length.Percent(48); preview.style.height = 160; previews.Add(preview);
                    }
                }
                catch (Exception error) { status.text = error.Message; }
            }
            async void Diagnose()
            {
                if (!Running.Add(path)) return;
                bool Current() => viewport != null && viewport.HasAvatar && viewport.LoadedModelPath == path;
                try
                {
                    status.text = "Taking snapshot and checking texture images…";
                    string before = await viewport.SaveTextureDiagnosticSnapshot("textures-before");
                    if (!Current()) return;
                    var report = Inspect(viewport.AvatarRoot);
                    if (((JArray)report["issues"]).Count > 0)
                    {
                        var framing = viewport.PluginPosition();
                        await viewport.LoadModel(backend.SourceFolder, path);
                        if (!Current() || viewport.LastImportError != null) throw new InvalidOperationException(viewport.LastImportError ?? "Model changed during texture reload.");
                        viewport.PluginMove(framing); report = Inspect(viewport.AvatarRoot);
                    }
                    string after = await viewport.SaveTextureDiagnosticSnapshot("textures-after"); if (!Current()) return;
                    var issues = (JArray)report["issues"];
                    report["summary"] = "Checked all " + report.Value<int>("checked") + " loaded texture images. "
                        + (issues.Count == 0 ? "Loaded image dimensions are valid. Missing source artwork is not checked here." : "Needs attention: " + string.Join(" ", issues.Values<string>()))
                        + " Snapshots and report saved locally for this model.";
                    Save(before, after, report);
                }
                catch (Exception error) { if (alive && Current()) status.text = error.Message; }
                finally { Running.Remove(path); }
            }
            host.Add(new Button(Diagnose) { text = "Take snapshot and check textures" }); host.Add(status); host.Add(previews);
            Saved += Refresh;
            host.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; Saved -= Refresh; Clear(); });
            Refresh(folder);
        }

        public static JObject Inspect(GameObject root)
        {
            var seen = new HashSet<Texture>();
            var issues = new JArray();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials.Where(item => item != null))
            foreach (string property in material.GetTexturePropertyNames())
            {
                var texture = material.GetTexture(property);
                if (texture == null || !seen.Add(texture)) continue;
                if (texture.width <= 0 || texture.height <= 0) issues.Add(material.name + " / " + property + ": image is not ready.");
            }
            if (seen.Count == 0) issues.Add("No loaded texture images were found. Check the selected model's artwork.");
            return new JObject { ["checked"] = seen.Count, ["issues"] = issues };
        }

        public static string Save(string before, string after, JObject report)
        {
            report["before"] = Path.GetFileName(before); report["after"] = Path.GetFileName(after);
            report["time"] = DateTime.UtcNow.ToString("o");
            string path = Path.Combine(Path.GetDirectoryName(after), "texture-report.json");
            File.WriteAllText(path, report.ToString());
            Saved?.Invoke(Path.GetDirectoryName(after));
            return path;
        }
    }
}
