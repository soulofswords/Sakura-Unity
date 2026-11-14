using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>Optional external converter bridge presented as a view inside Models.</summary>
    public sealed class SakuraModelConverterPanel
    {
        readonly SakuraBackend backend;
        readonly VisualElement host;
        readonly Action refreshCatalog;
        readonly CharacterEntry[] candidates;
        readonly DropdownField source;
        readonly TextField converterPath, outputName;
        readonly ProgressBar progress;
        readonly Label status;
        Process process;
        bool alive = true;

        public SakuraModelConverterPanel(SakuraBackend backend, CharacterEntry[] catalog, string loadedPath, VisualElement host, Action refreshCatalog)
        {
            this.backend = backend; this.host = host; this.refreshCatalog = refreshCatalog;
            candidates = (catalog ?? Array.Empty<CharacterEntry>())
                .Where(x => x != null && (x.format == "PMX" || x.format == "VRM"))
                .OrderBy(x => x.format, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToArray();

            var card = Card(host);
            AddLabel(card, "VRM ↔ PMX Converter", "card-title");
            AddLabel(card, "Choose a local model. Sakura runs the optional converter in the background and saves a new model under models/converted. The source model is never overwritten.", "description");
            source = new DropdownField("Source model", candidates.Select(x => x.format + " · " + x.name).DefaultIfEmpty("No local PMX or VRM models").ToList(), 0);
            card.Add(source);
            var selectedIndex = Array.FindIndex(candidates, x => string.Equals(x.path, loadedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex >= 0) source.index = selectedIndex;
            var target = AddLabel(card, "Target format is selected automatically from the source.", "description");
            source.RegisterValueChangedCallback(_ => UpdateTarget(target)); UpdateTarget(target);

            outputName = new TextField("Output model name") { value = candidates.Length > 0 ? Path.GetFileNameWithoutExtension(candidates[Mathf.Max(0, source.index)].name) + " converted" : "Converted model" };
            outputName.tooltip = "Letters, numbers, spaces, underscore and hyphen; output is a new file.";
            card.Add(outputName);
            converterPath = new TextField("Model-To-PMX folder") { value = PlayerPrefs.GetString("sakura-converter-path", "") };
            converterPath.textEdition.placeholder = "Folder containing convert/ and formats/";
            card.Add(converterPath);
            AddLabel(card, "Requires separately installed Model-To-PMX at commit 5b00e66ac5585e0c6402ea30ccab5c05fad6db2e and Git. This optional GPL tool runs as an external process. A conversion is best-effort: humanoid bones, morphs, materials, textures, scale and axes may need review. VRM spring bones and PMX rigid bodies/joints do not convert 1:1; PMX toon/sphere and VRM MToon settings may be approximated.", "description");
            progress = new ProgressBar { title = "Conversion", lowValue = 0, highValue = 100, value = 0 }; card.Add(progress);
            status = AddLabel(card, candidates.Length == 0 ? "Import a local PMX or VRM model to begin." : "Ready. No files will be changed until you start conversion.", "description");
            var actions = new VisualElement(); actions.AddToClassList("row"); card.Add(actions);
            var start = new Button(Start) { text = "Convert" }; actions.Add(start);
            var cancel = new Button(Cancel) { text = "Cancel" }; actions.Add(cancel);
            cancel.SetEnabled(false); start.SetEnabled(candidates.Length > 0);
            host.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; Cancel(); });
            cancel.RegisterCallback<ClickEvent>(_ => { if (process == null) { start.SetEnabled(candidates.Length > 0); cancel.SetEnabled(false); } });
            host.userData = new Tuple<Button, Button>(start, cancel);
        }

        static VisualElement Card(VisualElement parent) { var e = new VisualElement(); e.AddToClassList("card"); parent.Add(e); return e; }
        static Label AddLabel(VisualElement parent, string text, string className) { var e = new Label(text); e.AddToClassList(className); e.style.whiteSpace = WhiteSpace.Normal; parent.Add(e); return e; }
        void UpdateTarget(Label label)
        {
            if (candidates.Length == 0 || source.index < 0 || source.index >= candidates.Length) { label.text = "Target format: —"; return; }
            label.text = "Target format: " + (candidates[source.index].format == "PMX" ? "VRM" : "PMX");
        }
        static string Quote(string value)
        {
            if ((value ?? "").Contains("\"")) throw new ArgumentException("Paths cannot contain quotes.");
            return "\"" + (value ?? "").TrimEnd('\\') + "\"";
        }


        async void Start()
        {
            if (process != null || candidates.Length == 0 || source.index < 0 || source.index >= candidates.Length) return;
            var activeButtons = host.userData as Tuple<Button, Button>;
            activeButtons?.Item1.SetEnabled(false); activeButtons?.Item2.SetEnabled(true);
            try
            {
                string converter = Path.GetFullPath(converterPath.value.Trim().Trim('"'));
                string python = SakuraBackend.PythonExecutable(backend.SourceFolder);
                if (!Directory.Exists(converter)) throw new DirectoryNotFoundException("Select the separately installed Model-To-PMX folder.");
                string modelRoot = Path.GetFullPath(Path.Combine(backend.SourceFolder, "models"));
                string model = SakuraAvatarViewport.ResolveCharacterPath(backend.SourceFolder, candidates[source.index].path);
                if (!File.Exists(model) || !model.StartsWith(modelRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected model is outside Sakura's local models folder.");
                string script = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "vrm_pmx_convert.py"));
                if (!File.Exists(script)) throw new FileNotFoundException("The converter bridge is missing from the Sakura source folder.");
                string name = (outputName.value ?? "").Trim();
                if (name.Length == 0 || name.Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
                    throw new InvalidOperationException("Enter a safe output name (up to 80 characters). Existing outputs are never overwritten.");
                PlayerPrefs.SetString("sakura-converter-path", converter); PlayerPrefs.Save();
                string args = "-u " + Quote(script) + " --input " + Quote(model) + " --model-root " + Quote(backend.SourceFolder) + " --output-name " + Quote(name)
                    + " --converter-root " + Quote(converter) + " --python " + Quote(python) + " --job-id " + Quote(Guid.NewGuid().ToString());
                var info = new ProcessStartInfo(python, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = converter, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
                info.EnvironmentVariables["PYTHONUTF8"] = "1";
                process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.OutputDataReceived += OnOutput;
                process.ErrorDataReceived += OnError;
                progress.value = 0; status.text = "Starting converter…";
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                await Task.Run(() => process.WaitForExit());
                int exitCode = process.ExitCode;
                process.Dispose(); process = null;
                if (!alive) return;
                host.schedule.Execute(() =>
                {
                    if (!alive) return;
                    var buttons = host.userData as Tuple<Button, Button>;
                    buttons?.Item1.SetEnabled(candidates.Length > 0); buttons?.Item2.SetEnabled(false);
                    if (exitCode == 0) { progress.value = 100; status.text = "Conversion finished. Review the mapping report and inspect the new model before using it."; refreshCatalog?.Invoke(); }
                    else status.text = "Conversion did not complete. Check the converter installation and revision; source files were preserved.";
                });
            }
            catch (Exception error)
            {
                try { if (process != null && !process.HasExited) process.Kill(); } catch (Exception) { }
                process?.Dispose(); process = null;
                if (alive) { status.text = error.Message; var buttons = host.userData as Tuple<Button, Button>; buttons?.Item1.SetEnabled(candidates.Length > 0); buttons?.Item2.SetEnabled(false); }
            }
        }
        void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data) || !alive) return;
            const string prefix = "SAKURA_PROGRESS ";
            if (!args.Data.StartsWith(prefix, StringComparison.Ordinal)) return;
            try
            {
                var value = JObject.Parse(args.Data.Substring(prefix.Length));
                int percent = Mathf.Clamp((int)value["progress"], 0, 100); string message = (string)value["message"] ?? "Converting…";
                host.schedule.Execute(() => { if (alive) { progress.value = percent; status.text = message; } });
            }
            catch (Exception) { }
        }
        void OnError(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null || !alive || !args.Data.StartsWith("SAKURA_CONVERTER_ERROR ", StringComparison.Ordinal)) return;
            string message = args.Data.Substring("SAKURA_CONVERTER_ERROR ".Length);
            host.schedule.Execute(() => { if (alive) status.text = message; });
        }
        void Cancel()
        {
            if (process == null) return;
            try { if (!process.HasExited) { process.StandardInput.WriteLine("CANCEL"); process.StandardInput.Flush(); } } catch (Exception) { }
            if (alive) status.text = "Conversion cancelled. The source is unchanged; any partial output is cleaned up by the bridge.";
        }
    }
}
