using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            var detect = new Button { text = "Detect previous library" }; parent.Add(detect);
            var notice = new Label("Copies models, voices, profiles, settings and local AI weights. Originals are preserved. Browser cookies and signed-in sessions are left behind unless you opt in below. Existing differing files stop migration. Close the previous app first; allow space for a full copy.");
            notice.style.whiteSpace = WhiteSpace.Normal; parent.Add(notice);
            var browser = new Toggle("Also copy browser cookies and signed-in sessions") { value = false }; parent.Add(browser);
            var copy = new Button { text = "Copy and verify my library" }; parent.Add(copy);
            detect.clicked += () =>
            {
                try
                {
                    string target = Path.GetFullPath(backend.SourceFolder);
                    string found = FindPreviousLibrary(target);
                    if (found == null)
                    {
                        notice.text = "No previous Sakura library was found in standard user folders. Choose the old Sakura folder manually; it must contain a models folder.";
                        return;
                    }
                    source.value = found;
                    notice.text = "Found a previous Sakura library. Review the folder, then choose Copy and verify my library when ready. Originals are preserved.";
                }
                catch (Exception error) { notice.text = "Could not search for a previous library: " + error.Message; }
            };
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

        internal static string FindPreviousLibrary(string destination)
        {
            string target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
            var candidates = new List<string>();
            foreach (string root in roots)
            {
                foreach (string name in new[] { "Sakura", "Sakura Companion", "Sakura VTuber" })
                {
                    string direct = Path.Combine(root, name);
                    if (Directory.Exists(direct)) candidates.Add(direct);
                    string programs = Path.Combine(root, "Programs", name);
                    if (Directory.Exists(programs)) candidates.Add(programs);
                }
            }

            return candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => Directory.Exists(Path.Combine(path, "models")))
                .Where(path => !IsSameOrNested(path, target))
                .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }

        private static bool IsSameOrNested(string first, string second)
        {
            string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }
    }
}
