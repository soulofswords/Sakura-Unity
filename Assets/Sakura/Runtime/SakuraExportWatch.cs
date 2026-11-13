using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraExportWatch : MonoBehaviour
    {
        public bool Watching { get; private set; }
        public string Status { get; private set; } = "Export reload off";
        SakuraBackend backend;
        SakuraAvatarViewport viewport;
        string model, accepted, candidate;
        float next, changedAt;
        bool loading;
        int revision;

        public void Initialize(SakuraBackend service, SakuraAvatarViewport avatars) { backend = service; viewport = avatars; }
        public void SetWatching(bool enabled)
        {
            revision++; Watching = enabled; model = accepted = candidate = null;
            Status = enabled ? "Watching the selected Live2D export. Save/export into its installed folder." : "Export reload off";
        }
        public static string Fingerprint(string manifest)
        {
            if (new FileInfo(manifest).Length > 8 * 1024 * 1024) throw new InvalidDataException("Live2D manifest exceeds 8 MB.");
            var json = JObject.Parse(File.ReadAllText(manifest));
            string root = Path.GetDirectoryName(manifest);
            var fileReferences = json["FileReferences"] as JObject ?? throw new InvalidDataException("The export has no file references.");
            var fileKeys = new[] { "Moc", "Physics", "Pose", "UserData", "DisplayInfo", "File", "Sound" };
            var references = fileReferences.Descendants().OfType<JProperty>()
                .Where(property => fileKeys.Contains(property.Name) && property.Value.Type == JTokenType.String).Select(property => property.Value.Value<string>())
                .Concat((fileReferences["Textures"] as JArray ?? new JArray()).Values<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(1001).ToArray();
            if (references.Length > 1000) throw new InvalidDataException("The Live2D export has too many references.");
            var discovered = SakuraVtubeHotkeys.Files(root).Where(file => file.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".vtube.json", StringComparison.OrdinalIgnoreCase));
            var files = new[] { manifest }.Concat(references.Select(value => SakuraModelImport.ResolveFile(root, value))).Concat(discovered)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(file => file, StringComparer.Ordinal).Take(2501).ToArray();
            if (files.Length > 2500) throw new InvalidDataException("The Live2D export has too many discoverable controls.");
            var result = new StringBuilder();
            foreach (string file in files)
            {
                var info = new FileInfo(file);
                if (!info.Exists) throw new FileNotFoundException("An exported Live2D asset is still missing.");
                result.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return result.ToString();
        }
        async void Update()
        {
            if (!Watching || loading || viewport == null || viewport.Loading || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1;
            if (viewport.LoadedFormat != "Live2D") { Status = "Load a Live2D export to watch."; model = accepted = candidate = null; return; }
            try
            {
                string selected = viewport.LoadedModelPath;
                string fingerprint = Fingerprint(SakuraAvatarViewport.ResolveCharacterPath(backend.SourceFolder, selected));
                if (selected != model) { model = selected; accepted = fingerprint; candidate = null; Status = "Watching " + Path.GetFileName(selected); return; }
                if (fingerprint == accepted) { candidate = null; return; }
                if (candidate != fingerprint) { candidate = fingerprint; changedAt = Time.unscaledTime; Status = "Export changed · waiting for its files to settle…"; return; }
                if (Time.unscaledTime - changedAt < 2) return;
                int token = revision; var placement = viewport.PluginPosition(); loading = true;
                try
                {
                    await viewport.LoadModel(backend.SourceFolder, selected);
                    if (this == null || token != revision || !Watching) return;
                    if (viewport.LoadedModelPath != selected || !string.IsNullOrEmpty(viewport.LastImportError))
                    { accepted = fingerprint; Status = "Export could not reload. " + viewport.LastImportError + " Export again to retry."; return; }
                    viewport.PluginMove(placement); accepted = fingerprint; candidate = null;
                    Status = "Reloaded the updated Live2D export and restored its placement.";
                }
                finally { loading = false; }
            }
            catch (Exception error) { Status = "Export reload waiting. " + error.Message; }
        }
        void OnDisable() => SetWatching(false);
        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("Live2D export reload"); title.AddToClassList("card-title"); card.Add(title);
            var toggle = new Toggle("Reload when the selected Live2D export changes") { value = Watching }; card.Add(toggle);
            toggle.RegisterValueChangedCallback(change => SetWatching(change.newValue));
            var status = new Label(Status); status.AddToClassList("description"); card.Add(status);
            var note = new Label("Export from Cubism into the model’s installed folder. Sakura waits for the referenced files to settle, then reloads that package. The editable .cmo3 stays in Cubism."); note.AddToClassList("description"); card.Add(note);
            var poll = card.schedule.Execute(() => { if (this != null) { status.text = Status; toggle.SetValueWithoutNotify(Watching); } }).Every(500);
            card.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
        }
    }
}
