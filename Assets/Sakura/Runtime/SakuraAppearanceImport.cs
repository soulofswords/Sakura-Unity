using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace Sakura.Unity
{
    public static class SakuraAppearanceImport
    {
        public static string Apply(string path, SakuraAvatarViewport viewport, SakuraBackend backend)
        {
            var tools = viewport.Pmx == null ? SakuraModelTools.GetOrCreate(viewport.AvatarRoot, viewport.LoadedModelPath) : null;
                if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(path))
                    throw new InvalidDataException("Choose an appearance package stored on this PC.");
                var info = new FileInfo(path);
                if (info.Length < 1 || info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Appearance package must be under 64 MB.");
                JObject package = JObject.Parse(File.ReadAllText(path));
                if ((string)package["format"] != "sakura-appearance-export" || (int?)package["version"] != 1)
                    throw new InvalidDataException("Unsupported Sakura appearance package format.");
                string expectedType = viewport.LoadedFormat == "Live2D" ? "Live2D" : viewport.LoadedFormat;
                if (!string.Equals((string)package["modelType"], expectedType, StringComparison.Ordinal))
                    throw new InvalidDataException("This package is for a different model format.");
                string expectedHash = (string)package["modelSha256"];
                if (!Regex.IsMatch(expectedHash ?? "", "\\A[0-9a-fA-F]{64}\\z")) throw new InvalidDataException("Package has no valid model fingerprint.");
                string modelPath = SakuraAvatarViewport.ResolveCharacterPath(backend.SourceFolder, viewport.LoadedModelPath);
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(modelPath))
                    if (!string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("This appearance package was exported from different model-file bytes. Nothing was changed.");
                var rows = package["parts"] as JArray ?? throw new InvalidDataException("Appearance package has no parts list.");
                if (rows.Count > 512) throw new InvalidDataException("Appearance package has too many entries.");
                var changes = new List<SakuraModelTools.AppearanceChange>();
                int skipped = 0;
                var pmx = viewport.Pmx;
                foreach (var token in rows)
                {
                    if (!(token is JObject row)) { skipped++; continue; }
                    string kind = (string)row["kind"], name = ((string)row["name"] ?? "").Trim();
                    IEnumerable<int> candidates = Enumerable.Empty<int>();
                    if (kind == "material" && pmx != null)
                    {
                        int originalIndex = row.Value<int?>("sourceIndex") ?? -1;
                        // Exact file hash and PMX table order disambiguate duplicate material names.
                        if (originalIndex >= 0 && originalIndex < pmx.Definition.materials.Count &&
                            (string)row["id"] == "mat:" + originalIndex && pmx.Definition.materials[originalIndex].name == name)
                            candidates = new[] { originalIndex };
                        else candidates = Enumerable.Range(0, pmx.Definition.materials.Count).Where(index => string.Equals(pmx.Definition.materials[index].name, name, StringComparison.Ordinal));
                    }
                    else if (kind == "art" && viewport.LoadedFormat == "Live2D")
                        candidates = Enumerable.Range(0, tools.Parts.Count).Where(index => tools.Parts[index].ArtMesh != null && string.Equals(tools.Parts[index].Name, name, StringComparison.Ordinal));
                    else if (kind == "material" && viewport.LoadedFormat != "Live2D")
                        candidates = Enumerable.Range(0, tools.Parts.Count).Where(index => tools.Parts[index].Renderer != null &&
                            (string.Equals(tools.Parts[index].Name, name, StringComparison.Ordinal) || tools.Parts[index].Name.EndsWith(" · " + name, StringComparison.Ordinal)));
                    else if (kind == "atlas" && viewport.LoadedFormat == "Live2D")
                    {
                        int atlas = row.Value<int?>("sourceIndex") ?? -1;
                        if (atlas >= 0 && atlas < 512 && (string)row["id"] == "atlas:" + atlas && name == "Texture atlas " + (atlas + 1))
                            candidates = Enumerable.Range(0, tools.Parts.Count).Where(index =>
                                tools.Parts[index].ArtMesh != null &&
                                tools.Parts[index].ArtMesh.GetComponent<Live2D.Cubism.Core.CubismDrawable>()?.TextureIndex == atlas);
                    }
                    int[] matches = candidates.Distinct().ToArray();
                    if (matches.Length != 1 && !(kind == "atlas" && matches.Length > 1)) { skipped++; continue; }
                    byte[] textureBytes = null;
                    string encoded = (string)row["textureBase64"];
                    if (!string.IsNullOrEmpty(encoded))
                    {
                        if (encoded.Length > 22_369_624) throw new InvalidDataException("An exported replacement texture exceeds 16 MB.");
                        try { textureBytes = Convert.FromBase64String(encoded); }
                        catch (FormatException) { throw new InvalidDataException("Appearance package contains invalid texture data."); }
                        if (textureBytes.Length < 1 || textureBytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("An exported replacement texture exceeds 16 MB.");
                    }
                    Color color = Color.white; bool hasColor = !string.IsNullOrEmpty((string)row["color"]);
                    if (hasColor && (!ColorUtility.TryParseHtmlString((string)row["color"], out color) || color.a < .999f)) throw new InvalidDataException("Appearance package contains an invalid color.");
                    float strength = row.Value<float?>("strength") ?? 0, opacity = row.Value<float?>("opacity") ?? 1;
                    if (float.IsNaN(strength) || float.IsInfinity(strength) || strength < 0 || strength > 1 || float.IsNaN(opacity) || float.IsInfinity(opacity) || opacity < 0 || opacity > 2)
                        throw new InvalidDataException("Appearance package contains an invalid appearance value.");
                    bool hasOpacity = row["opacity"] != null;
                    if (!hasColor && !hasOpacity && textureBytes == null) { skipped++; continue; }
                    foreach (int index in matches)
                    {
                        var change = changes.FirstOrDefault(item => item.index == index);
                        if (change == null) { change = new SakuraModelTools.AppearanceChange { index = index }; changes.Add(change); }
                        if ((hasColor && change.hasColor) || (hasOpacity && change.hasOpacity) || (textureBytes != null && change.textureBytes != null))
                            throw new InvalidDataException("Appearance package defines the same part channel more than once.");
                        if (hasColor) { change.hasColor = true; change.color = color; change.strength = strength; }
                        if (hasOpacity) { change.hasOpacity = true; change.opacity = opacity; }
                        if (textureBytes != null) change.textureBytes = textureBytes;
                    }
                }
                int applied = pmx != null ? pmx.ImportAppearance(changes) : tools.ImportAppearance(changes);
                return "Imported " + applied + " appearance entries for this model; skipped " + skipped + " unmatched or ambiguous entries.";
        }
    }
}
