using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Sakura.Unity
{
    public static class SakuraVtubeHotkeys
    {
        public sealed class Binding { public string File, Name, Hotkey, Action; }
        public sealed class Expression { public string File, Name; }
        public sealed class Profile
        {
            public bool Present;
            public readonly List<Binding> Bindings = new List<Binding>();
            public readonly List<Expression> Expressions = new List<Expression>();
            public string Warning;
        }
        static JObject Read(string file)
        {
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("VTube metadata exceeds 8 MB.");
            return JToken.Parse(File.ReadAllText(file)) as JObject;
        }
        static string SafeFile(string root, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Replace('\\', '/').Split('/').Contains("..")) return null;
            try
            {
                string file = SakuraModelImport.ResolveFile(root, reference.Replace('\\', '/'));
                for (string current = file; current != null && !current.Equals(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                return File.Exists(file) ? file : null;
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException) { return null; }
        }
        internal static List<string> Files(string root)
        {
            var result = new List<string>(); var folders = new Stack<(string path, int depth)>(); folders.Push((root, 0)); int count = 0;
            while (folders.Count > 0)
            {
                var folder = folders.Pop(); if (++count > 500) throw new InvalidDataException("VTube package has too many folders.");
                foreach (string file in Directory.EnumerateFiles(folder.path))
                {
                    if (result.Count >= 10000) throw new InvalidDataException("VTube package has too many files.");
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) result.Add(file);
                }
                if (folder.depth >= 16) continue;
                foreach (string child in Directory.EnumerateDirectories(folder.path))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) folders.Push((child, folder.depth + 1));
            }
            result.Sort(StringComparer.Ordinal); return result;
        }
        public static Profile Load(string modelPath)
        {
            var result = new Profile(); if (!File.Exists(modelPath)) return result;
            string root = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            var sidecars = Directory.EnumerateFiles(root, "*.vtube.json").OrderBy(file => file, StringComparer.Ordinal).Take(65).ToArray();
            result.Present = sidecars.Length > 0;
            if (sidecars.Length > 64) { result.Warning = "Only the first 64 VTube profiles were read."; sidecars = sidecars.Take(64).ToArray(); }
            List<string> files;
            try { files = Files(root); }
            catch (Exception error) { files = new List<string>(); result.Warning = "Unreferenced expression scan was skipped. " + error.Message; }
            var expressions = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
            var manifest = Read(modelPath);
            foreach (var row in (manifest?["FileReferences"]?["Expressions"] as JArray ?? new JArray()).OfType<JObject>().Take(500))
            {
                string file = SafeFile(root, row.Value<string>("File"));
                if (file != null) expressions[file] = new Expression { File = file.Substring(root.Length + 1).Replace('\\', '/'), Name = row.Value<string>("Name") };
            }
            foreach (string file in files.Where(file => file.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase)).Take(500))
                if (!expressions.ContainsKey(file)) expressions.Add(file, new Expression { File = file.Substring(root.Length + 1).Replace('\\', '/') });
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sidecar in sidecars)
            {
                try
                {
                    if ((File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0) continue;
                    var config = Read(sidecar);
                    foreach (var row in (config?["Hotkeys"] as JArray ?? new JArray()).OfType<JObject>().Take(1000))
                    {
                        string action = row.Value<string>("Action"); string name = row.Value<string>("Name") ?? "";
                        if (action == "RemoveAllExpressions") { result.Bindings.Add(new Binding { Action = action, Name = name, Hotkey = TriggerCode(row["Triggers"] as JObject) }); continue; }
                        if (action != "ToggleExpression" && action != "SetExpression" && action != "Set/UnsetExpression" && action != "TriggerAnimation" && action != "StartAnimation") continue;
                        string reference = row.Value<string>("File"); if (reference == null) continue;
                        labels[Path.GetFileName(reference.Replace('\\', '/'))] = name.Trim();
                        string file = SafeFile(root, reference);
                        string folder = row.Value<string>("Folder");
                        if (file == null && !string.IsNullOrEmpty(folder)) file = SafeFile(root, folder.Replace('\\', '/') + "/" + reference.Replace('\\', '/'));
                        if (file == null && !Path.IsPathRooted(reference) && !reference.Replace('\\', '/').Split('/').Contains(".."))
                        {
                            var matches = files.Where(candidate => (candidate.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) || candidate.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)) && Path.GetFileName(candidate).Equals(Path.GetFileName(reference.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                            if (matches.Length == 1) file = matches[0];
                        }
                        if (file != null) result.Bindings.Add(new Binding { File = file.Substring(root.Length + 1).Replace('\\', '/'), Name = name, Hotkey = TriggerCode(row["Triggers"] as JObject), Action = action });
                    }
                }
                catch (Exception error) { result.Warning = "Some VTube shortcuts could not be read. " + error.Message; }
            }
            var used = new HashSet<string>();
            foreach (var expression in expressions.Values.Take(500))
            {
                string filename = Path.GetFileName(expression.File);
                string stem = filename.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) ? filename.Substring(0, filename.Length - 10) : Path.GetFileNameWithoutExtension(filename);
                string label = string.IsNullOrWhiteSpace(expression.Name) ? labels.TryGetValue(filename, out var imported) && !string.IsNullOrWhiteSpace(imported) ? imported : stem : expression.Name.Trim();
                string unique = used.Contains(label) ? label + " (" + stem + ")" : label; int suffix = 2;
                while (!used.Add(unique)) unique = label + " (" + stem + " " + suffix++ + ")";
                expression.Name = unique; result.Expressions.Add(expression);
            }
            return result;
        }
        public static string TriggerCode(JObject triggers)
        {
            if (triggers == null) return null;
            var modifiers = new HashSet<string>(); string code = null;
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["control"] = "Ctrl", ["ctrl"] = "Ctrl", ["alt"] = "Alt", ["shift"] = "Shift", ["command"] = "Meta", ["win"] = "Meta" };
            foreach (string slot in new[] { "Trigger1", "Trigger2", "Trigger3" })
            {
                string key = triggers.Value<string>(slot); if (string.IsNullOrEmpty(key) || key.Equals("None", StringComparison.OrdinalIgnoreCase) || key.Equals("Unset", StringComparison.OrdinalIgnoreCase)) continue;
                if (aliases.TryGetValue(key, out string modifier)) { modifiers.Add(modifier); continue; }
                string next = key;
                if (key.Length == 2 && char.ToUpperInvariant(key[0]) == 'N' && char.IsDigit(key[1])) next = "Digit" + key[1];
                else if (key.Length == 1 && char.IsLetter(key[0])) next = "Key" + key.ToUpperInvariant();
                if (code != null) return null; code = next;
            }
            if (code == null) return null;
            return SakuraAvatarExpressions.NormalizeHotkey(string.Join("+", new[] { "Ctrl", "Alt", "Shift", "Meta" }.Where(modifiers.Contains).Concat(new[] { code })));
        }
    }
}
