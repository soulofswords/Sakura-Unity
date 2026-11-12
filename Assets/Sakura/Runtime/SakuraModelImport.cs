using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework.Json;
using Live2D.Cubism.Rendering.Masking;
using Mmd.Parser;
using Mmd.Rendering;
using Mmd.UnityIntegration;
using UnityEngine;
using Object = UnityEngine.Object;
using Newtonsoft.Json.Linq;

namespace Sakura.Unity
{
    public static class SakuraModelImport
    {
        public static string ResolveFile(string root, string relative)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The model reference leaves its allowed folder.");
            for (string current = path; current != null && current.Length >= root.Length - 1; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Model files cannot cross symbolic links or junctions.");
            return path;
        }

        public static async Task<GameObject> LoadPmx(string path, CancellationToken token, Action<string> status)
        {
            if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("PMX file exceeds the 512 MB import limit.");
            var model = await Task.Run(() => new NativeMmdParser().LoadModel(File.ReadAllBytes(path)), token);
            token.ThrowIfCancellationRequested();
            ApplyPackageTextureOverrides(path, model);
            var instance = model.bones.Count == 0
                ? MmdUnityModelFactory.CreateStaticModel(model, path, .08f, MmdMaterialPreset.MmdToonLit)
                : MmdUnityModelFactory.CreateSkinnedModel(model, path, .08f, MmdMaterialPreset.MmdToonLit);
            var resources = instance.Root.AddComponent<SakuraImportedAssets>();
            resources.Owned.Add(instance.Mesh);
            resources.Owned.AddRange(instance.Materials);
            resources.Owned.AddRange(instance.OwnedTextures);
            instance.Root.AddComponent<SakuraPmxModel>().Initialize(instance, model, path);
            if (instance.ShaderDiagnostics.shaderFallbackUsed)
            {
                Object.Destroy(instance.Root);
                throw new InvalidOperationException("The MMD URP toon shader is unavailable. " + instance.ShaderDiagnostics.fallbackReason);
            }
            status("PMX loaded · " + instance.Materials.Length + " materials · " + instance.LoadedDiffuseTextureCount +
                " colour textures · " + instance.LoadedToonTextureCount + " toon maps · " + instance.LoadedSphereTextureCount +
                " sphere maps · " + instance.SkippedTextureReferenceCount + " unresolved references");
            return instance.Root;
        }

        public static void ApplyPackageTextureOverrides(string path, MmdModelDefinition model)
        {
            string folder = Path.GetDirectoryName(path);
            string metadata = ResolveFile(folder, "textures.json");
            if (!File.Exists(metadata)) return;
            if (new FileInfo(metadata).Length > 2L * 1024 * 1024) throw new InvalidDataException("Texture mapping metadata exceeds 2 MB.");
            var json = JObject.Parse(File.ReadAllText(metadata));
            var explicitParts = (json["materials"] as JObject)?.GetValue(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) as JObject;
            var aliases = json["textures"] as JObject;
            string Resolve(int index, string slot, string original)
            {
                string chosen = (explicitParts?[index.ToString()] as JObject)?.Value<string>(slot);
                if (chosen == null && aliases != null)
                    chosen = aliases.Properties().FirstOrDefault(property => property.Name.Replace('\\','/').Equals(original.Replace('\\','/'), StringComparison.OrdinalIgnoreCase))?.Value.Value<string>();
                if (chosen == null) return original;
                string resolved = ResolveFile(folder, chosen);
                return resolved.Substring(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\','/');
            }
            foreach (var material in model.materials)
            {
                material.texture = Resolve(material.index, "diffuse", material.texture);
                material.sphereTexture = Resolve(material.index, "sphere", material.sphereTexture);
                string toon = Resolve(material.index, "toon", material.toonTexture);
                if (toon != material.toonTexture) { material.toonShared = false; material.sharedToonIndex = -1; }
                material.toonTexture = toon;
            }
        }

        public static GameObject LoadLive2D(string path)
        {
            if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new InvalidDataException("The Live2D model manifest exceeds 8 MB.");
            string folder = Path.GetDirectoryName(path);
            var owned = new List<Object>();
            var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            CubismModel model = null;
            try
            {
                object ReadAsset(Type type, string assetPath)
                {
                    string file = ResolveFile(folder, assetPath);
                    if (!File.Exists(file)) return null;
                    if (new FileInfo(file).Length > 128L * 1024 * 1024) throw new InvalidDataException("A Live2D asset exceeds the 128 MB runtime limit.");
                    if (type == typeof(string)) return File.ReadAllText(file);
                    if (type == typeof(byte[])) return File.ReadAllBytes(file);
                    if (type != typeof(Texture2D)) throw new InvalidDataException("Unsupported Live2D asset type.");
                    if (textures.TryGetValue(file, out var existing)) return existing;
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = Path.GetFileName(file), wrapMode = TextureWrapMode.Clamp };
                    owned.Add(texture);
                    if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(file))) throw new InvalidDataException("Could not decode Live2D texture " + texture.name);
                    textures.Add(file, texture);
                    return texture;
                }

                var json = CubismModel3Json.LoadAtPath(path, ReadAsset);
                if (json == null || json.Moc3 == null) throw new InvalidDataException("The model3.json or referenced MOC3 is missing.");
                foreach (var texture in json.Textures)
                    if (texture == null) throw new InvalidDataException("A referenced Live2D texture is missing.");
                model = json.ToModel(shouldImportAsOriginalWorkflow: true);
                if (model == null) throw new InvalidDataException("Cubism could not create this model.");
                int maskGroups = model.Drawables.Select(drawable => drawable.Masks).Where(masks => masks.Length > 0)
                    .Select(masks => string.Join("|", masks.Select(mask => mask.Id).OrderBy(id => id))).Distinct().Count();
                var maskController = model.GetComponent<CubismMaskTextureController>();
                if (maskController != null && maskGroups > 36) maskController.RenderTextureCount = (maskGroups + 31) / 32;
                owned.Add(model.Moc);
                var resources = model.gameObject.AddComponent<SakuraImportedAssets>();
                resources.Owned.AddRange(owned);
                return model.gameObject;
            }
            catch
            {
                if (model != null) Object.Destroy(model.gameObject);
                foreach (var asset in owned) if (asset != null) Object.Destroy(asset);
                throw;
            }
        }
    }
}
