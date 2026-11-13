using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Sakura.Unity
{
    public static class SakuraMobileCore
    {
        public static string SpeechText(string text)
        {
            text = Regex.Replace(text ?? "", @"\[([^\]]+)\]\(https?://[^\s)]+\)", "$1");
            text = Regex.Replace(text, @"https?://\S+|\[(?:QUIET|HAPPY|SAD|ANGRY|EXCITED)\]", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[\p{Cs}\p{So}\p{Sk}→←↑↓*#`_]", "");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        public static string FriendlyError(long status, string body)
        {
            if (status == 429) return "Your provider quota or rate limit has been reached. Please check your API account.";
            if (status == 401 || status == 403) return "Your API key was rejected. Check the key and its permissions in Settings.";
            if (status >= 500) return "The AI provider is temporarily unavailable. Please retry shortly.";
            return status == 0 ? "Could not reach the AI provider. Check your internet connection." : "The AI request failed (HTTP " + status + "). Check the selected model in Settings.";
        }

        public static CharacterEntry[] FindModels(string root)
        {
            string models = Path.Combine(root,"models","mobile-imports");
            if (!Directory.Exists(models)) return Array.Empty<CharacterEntry>();
            return Directory.EnumerateFiles(models,"*",SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".vrm",StringComparison.OrdinalIgnoreCase) || path.EndsWith(".model3.json",StringComparison.OrdinalIgnoreCase))
                .Select(path => new CharacterEntry { name = Path.GetFileName(path).Replace(".model3.json","").Replace(".vrm",""),
                    path = path.Substring(Path.Combine(root,"models").Length + 1).Replace('\\','/'),
                    format = path.EndsWith(".vrm",StringComparison.OrdinalIgnoreCase) ? "VRM" : "Live2D" }).ToArray();
        }

        public static string ImportModel(string source, string root)
        {
            if (new FileInfo(source).Length > 512L * 1024 * 1024) throw new InvalidDataException("Model package exceeds 512 MB.");
            string extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension != ".vrm" && extension != ".zip") throw new InvalidDataException("Choose a VRM file or a ZIP containing the complete Live2D model folder.");
            string digest;
            using (var stream = File.OpenRead(source))
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant().Substring(0,16);
            string models = Path.Combine(root,"models","mobile-imports"); Directory.CreateDirectory(models);
            string existing = Directory.EnumerateDirectories(models).FirstOrDefault(path => path.EndsWith("-" + digest,StringComparison.Ordinal));
            if (existing != null)
            {
                var saved = FindModels(root).FirstOrDefault(item => item.path.StartsWith("mobile-imports/" + Path.GetFileName(existing) + "/",StringComparison.Ordinal));
                if (saved != null) return saved.path;
                throw new InvalidDataException("An incomplete import already exists. Remove that import before retrying.");
            }
            string name = Regex.Replace(Path.GetFileNameWithoutExtension(source),@"[^\p{L}\p{N} ._-]","_").Trim();
            if (name.Length == 0) name = "Character";
            if (name.Length > 64) name = name.Substring(0,64);
            string destination = Path.Combine(models,name + "-" + digest);
            string staging = Path.Combine(root,"import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            try
            {
                if (extension == ".vrm") File.Copy(source,Path.Combine(staging,name + ".vrm"));
                else
                {
                    using var zip = ZipFile.OpenRead(source);
                    if (zip.Entries.Count > 20000) throw new InvalidDataException("Model package contains too many files.");
                    long total = 0;
                    foreach (var entry in zip.Entries)
                    {
                        string relative = entry.FullName.Replace('\\','/');
                        if (relative.StartsWith("/") || relative.Contains(":") || relative.Split('/').Contains("..") || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                            throw new InvalidDataException("Model package contains an unsafe file path.");
                        if ((total += entry.Length) > 1024L * 1024 * 1024 || entry.Length > 128L * 1024 * 1024)
                            throw new InvalidDataException("Expanded model package exceeds its import limit.");
                        string target = SakuraModelImport.ResolveFile(staging,relative);
                        if (relative.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using var input = entry.Open();
                        using var output = new FileStream(target,FileMode.CreateNew,FileAccess.Write);
                        byte[] buffer = new byte[65536]; long written = 0; int count;
                        while ((count = input.Read(buffer,0,buffer.Length)) > 0)
                        {
                            written += count;
                            if (written > entry.Length) throw new InvalidDataException("Model package length is invalid.");
                            output.Write(buffer,0,count);
                        }
                    }
                }
                if (!Directory.EnumerateFiles(staging,"*",SearchOption.AllDirectories).Any(path => path.EndsWith(".vrm",StringComparison.OrdinalIgnoreCase) || path.EndsWith(".model3.json",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("No Live2D model3.json or VRM file was found in the package.");
                Directory.Move(staging,destination);
                return FindModels(root).First(item => item.path.StartsWith("mobile-imports/" + Path.GetFileName(destination) + "/",StringComparison.Ordinal)).path;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging,true); }
        }

        public static void DeleteModel(string relative, string root)
        {
            string[] segments = relative.Replace('\\','/').Split('/');
            if (segments.Length < 3 || segments[0] != "mobile-imports" || !Regex.IsMatch(segments[1],@"^.{1,64}-[0-9a-f]{16}$"))
                throw new InvalidDataException("Select a model imported on this phone.");
            string models = Path.Combine(root,"models");
            string path = SakuraModelImport.ResolveFile(models,segments[0] + "/" + segments[1]);
            if (Directory.Exists(path)) Directory.Delete(path,true);
        }
    }
}
