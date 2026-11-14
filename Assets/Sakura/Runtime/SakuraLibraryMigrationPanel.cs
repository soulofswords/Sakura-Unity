using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public static class SakuraLibraryMigrationPanel
    {
        public static void Build(VisualElement parent, SakuraBackend backend)
        {
            var source = new TextField("Previous Sakura folder"); parent.Add(source);
            var notice = new Label("Copies models, voices, profiles, settings and local AI weights. Originals are preserved. Browser cookies and signed-in sessions are left behind unless you opt in below. Existing differing files stop migration. Close the previous app first; allow space for a full copy.");
            notice.style.whiteSpace = WhiteSpace.Normal; parent.Add(notice);
            var browser = new Toggle("Also copy browser cookies and signed-in sessions") { value = false }; parent.Add(browser);
            var copy = new Button { text = "Copy and verify my library" }; parent.Add(copy);
            copy.clicked += async () =>
            {
                copy.SetEnabled(false);
                try
                {
                    string previous = Path.GetFullPath(source.value.Trim().Trim('"'));
                    string target = Path.GetFullPath(backend.SourceFolder);
                    if (previous.Contains("\"") || target.Contains("\"")) throw new ArgumentException("Invalid folder path.");
                    string tool = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "migrate_library.py"));
                    if (!File.Exists(tool)) throw new FileNotFoundException("The library migration tool is missing. Repair this installation.");
                    var info = new ProcessStartInfo(SakuraBackend.PythonExecutable(target),
                        "-u \"" + tool + "\" --source \"" + previous.TrimEnd('\\') + "\" --destination \"" + target.TrimEnd('\\') + "\"" + (browser.value ? " --include-browser-profile" : ""))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    notice.text = "Copying and verifying the library… Keep this app open.";
                    using (var process = new Process { StartInfo = info })
                    {
                        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                        await Task.Run(() => process.WaitForExit());
                        string result = await output, error = await errors;
                        if (process.ExitCode != 0) throw new IOException(error.Trim());
                        notice.text = "Library copied and verified. Restart Sakura to load migrated settings. " + result.Trim();
                        backend.RefreshCatalog();
                    }
                }
                catch (Exception error) { notice.text = "Migration stopped: " + error.Message; }
                finally { copy.SetEnabled(true); }
            };
        }
    }
}
