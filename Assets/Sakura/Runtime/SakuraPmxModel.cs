using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mmd.Parser;
using Mmd.UnityIntegration;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sakura.Unity
{
    // Overrides belong to the user profile, never to the imported PMX or its artwork.
    public sealed class SakuraPmxModel : MonoBehaviour
    {
        [Serializable] public class Part
        {
            public int index;
            public bool colored;
            public Color color = Color.white;
            public float strength = 1, transparency;
            public string replacement = "";
            public bool repairedSphere, repairedToon;
        }
        [Serializable] public class Edits
        {
            public List<Part> parts = new List<Part>();
            public List<int> frozen = new List<int>();
            public float brightness = 1, ambient = 1, diffuse = 1, toon = 1, directional, sphere = 1, specular = 1, saturation = 1;
        }
        public MmdUnityModelInstance Instance { get; private set; }
        public MmdModelDefinition Definition { get; private set; }
        public Edits Settings { get; private set; } = new Edits();
        public string StorageFile { get; private set; }
        public bool AppearanceMatching { get; set; }
        private Material[] originals;
        private Texture2D[] replacements, painted;
        private Texture2D[] sphereReplacements, toonReplacements;
        public string SourcePath { get; private set; }
        private Color[] paintColors;
        private float[] paintStrengths;
        private Texture2D[] paintSources;
        private Vector3[] restScale;

        public void Initialize(MmdUnityModelInstance instance, MmdModelDefinition definition, string path)
        {
            Instance = instance; Definition = definition;
            SourcePath = path;
            originals = instance.Materials.Select(material => new Material(material)).ToArray();
            replacements = new Texture2D[originals.Length]; painted = new Texture2D[originals.Length];
            sphereReplacements = new Texture2D[originals.Length]; toonReplacements = new Texture2D[originals.Length];
            paintColors = new Color[originals.Length]; paintStrengths = new float[originals.Length]; paintSources = new Texture2D[originals.Length];
            restScale = instance.BoneTransforms.Select(bone => bone.localScale).ToArray();
            string hash;
            using (var digest = SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()))).Replace("-", "");
            string folder = Path.Combine(Application.persistentDataPath, "pmx-overrides", hash);
            Directory.CreateDirectory(folder); StorageFile = Path.Combine(folder, "settings.json");
            if (File.Exists(StorageFile))
            {
                try
                {
                    if (new FileInfo(StorageFile).Length > 1024 * 1024) throw new InvalidDataException("Oversized PMX settings.");
                    Settings = JsonUtility.FromJson<Edits>(File.ReadAllText(StorageFile)) ?? new Edits();
                    Settings.parts ??= new List<Part>(); Settings.frozen ??= new List<int>();
                    Settings.parts = Settings.parts.Where(part => part != null && part.index >= 0 && part.index < originals.Length).GroupBy(part => part.index).Select(group => group.First()).ToList();
                    Settings.frozen = Settings.frozen.Where(index => index >= 0 && index < instance.BoneTransforms.Length).Distinct().ToList();
                    foreach (var part in Settings.parts)
                    {
                        Texture2D Restore(string suffix)
                        {
                            string saved = Path.Combine(folder, "part-" + part.index + suffix + ".png");
                            return File.Exists(saved) && new FileInfo(saved).Length <= 64L * 1024 * 1024 ? Decode(File.ReadAllBytes(saved)) : null;
                        }
                        if (part.repairedSphere) sphereReplacements[part.index] = Restore("-sphere");
                        if (part.repairedToon) toonReplacements[part.index] = Restore("-toon");
                        // Stored texture names are identifiers, not arbitrary file paths.
                        if (part.replacement == "part-" + part.index + ".png")
                        {
                            string image = Path.Combine(folder, part.replacement);
                            if (File.Exists(image) && new FileInfo(image).Length <= 64L * 1024 * 1024) replacements[part.index] = Decode(File.ReadAllBytes(image));
                        }
                    }
                }
                catch (Exception error) { Debug.LogWarning("Could not restore PMX overrides: " + error.Message); Settings = new Edits(); }
            }
            Apply();
        }

        public Part GetPart(int index)
        {
            if (index < 0 || index >= originals.Length) throw new ArgumentOutOfRangeException(nameof(index));
            var part = Settings.parts.Find(item => item.index == index);
            if (part == null) { part = new Part { index = index }; Settings.parts.Add(part); }
            return part;
        }

        public void Save()
        {
            string temporary = StorageFile + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(Settings, true));
            if (File.Exists(StorageFile)) File.Replace(temporary, StorageFile, null); else File.Move(temporary, StorageFile);
        }

        public static Texture2D Decode(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(texture, bytes)) { Destroy(texture); throw new InvalidDataException("Could not decode the replacement texture."); }
            return texture;
        }

        public void ReplaceTexture(int index, byte[] png)
        {
            var texture = Decode(png);
            string name = "part-" + index + ".png";
            string file = Path.Combine(Path.GetDirectoryName(StorageFile), name), temporary = file + ".tmp";
            try { File.WriteAllBytes(temporary, png); if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file); }
            catch { Destroy(texture); throw; }
            if (replacements[index] != null) Destroy(replacements[index]);
            replacements[index] = texture; GetPart(index).replacement = name; Apply(); Save();
        }

        public void RestoreTexture(int index)
        {
            GetPart(index).replacement = "";
            if (replacements[index] != null) Destroy(replacements[index]);
            replacements[index] = null; Apply(); Save();
        }

        public void RepairTexture(int index, string slot, string relative)
        {
            string path = SakuraModelImport.ResolveFile(Path.GetDirectoryName(SourcePath), relative);
            if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("Texture exceeds 64 MB.");
            var texture = MmdRuntimeTextureResolver.DecodeTextureBytes(File.ReadAllBytes(path), Path.GetExtension(path), Path.GetFileName(path));
            if (texture == null) throw new InvalidDataException("Could not decode confirmed texture.");
            byte[] png;
            try { png = texture.EncodeToPNG(); } finally { Destroy(texture); }
            if (slot == "diffuse") { ReplaceTexture(index, png); return; }
            if (slot != "sphere" && slot != "toon") throw new ArgumentException("Invalid texture slot.");
            var values = slot == "sphere" ? sphereReplacements : toonReplacements;
            string file = Path.Combine(Path.GetDirectoryName(StorageFile), "part-" + index + "-" + slot + ".png"), temporary = file + ".tmp";
            File.WriteAllBytes(temporary, png); if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            var replacement = Decode(png); if (values[index] != null) Destroy(values[index]); values[index] = replacement;
            if (slot == "sphere") GetPart(index).repairedSphere = true; else GetPart(index).repairedToon = true;
            Apply(); Save();
        }

        public void Freeze(int index, bool enabled)
        {
            if (index < 0 || index >= Instance.BoneTransforms.Length) throw new ArgumentOutOfRangeException(nameof(index));
            Settings.frozen.Remove(index); if (enabled) Settings.frozen.Add(index);
            LateUpdate(); Save();
        }

        private void LateUpdate()
        {
            if (Instance == null) return;
            foreach (int index in Settings.frozen)
            {
                var bone = Instance.BoneTransforms[index];
                bone.localPosition = Instance.BindLocalPositions[index]; bone.localRotation = Instance.BindLocalRotations[index]; bone.localScale = restScale[index];
            }
        }

        public void Apply()
        {
            for (int index = 0; index < originals.Length; index++)
            {
                var material = Instance.Materials[index]; var original = originals[index];
                material.CopyPropertiesFromMaterial(original);
                var part = GetPart(index);
                var texture = replacements[index] ?? original.GetTexture("_BaseMap") as Texture2D;
                Color tint = part.color; tint.a = 1;
                if ((!part.colored || paintColors[index] != tint || paintStrengths[index] != part.strength || paintSources[index] != texture) && painted[index] != null)
                { Destroy(painted[index]); painted[index] = null; }
                if (part.colored && texture != null && painted[index] == null)
                {
                    var pixels = texture.GetPixels();
                    float average = Mathf.Max(.12f, pixels.Length == 0 ? 1 : pixels.Average(pixel => pixel.grayscale));
                    for (int p = 0; p < pixels.Length; p++)
                    {
                        var result = Color.Lerp(pixels[p], tint * (pixels[p].grayscale / average), Mathf.Clamp01(part.strength)); result.a = pixels[p].a; pixels[p] = result;
                    }
                    var recolored = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, true, false) { wrapMode = texture.wrapMode };
                    recolored.SetPixels(pixels); recolored.Apply(); painted[index] = recolored; texture = recolored;
                    paintColors[index] = tint; paintStrengths[index] = part.strength; paintSources[index] = replacements[index] ?? original.GetTexture("_BaseMap") as Texture2D;
                }
                else if (part.colored && painted[index] != null) texture = painted[index];
                if (texture != null) { material.SetTexture("_BaseMap", texture); material.SetTexture("_MainTex", texture); material.SetFloat("_BaseMapBound", 1); }
                if (sphereReplacements[index] != null)
                {
                    material.SetTexture("_SphereMap", sphereReplacements[index]);
                    string mode = Definition.materials[index].sphereTextureMode;
                    material.SetFloat("_SphereMode", mode == "multiply-sphere" ? 1 : mode == "additive-sphere" ? 2 : 0);
                }
                if (toonReplacements[index] != null) { material.SetTexture("_ToonMap", toonReplacements[index]); material.SetFloat("_ToonMapBound", 1); }
                Color authored = original.GetColor("_BaseColor");
                Color baseColor = Color.Lerp(Color.white, authored, Mathf.Clamp01(Settings.diffuse));
                if (part.colored) baseColor = Color.Lerp(baseColor, texture == null ? tint : Color.white, Mathf.Clamp01(part.strength));
                float transparency = Mathf.Clamp(part.transparency, -1, 1);
                baseColor.a = transparency < 0 ? Mathf.Lerp(authored.a, 1, -transparency) : authored.a * (1 - transparency);
                material.SetColor("_BaseColor", baseColor);
                material.SetFloat("_Alpha", baseColor.a);
                material.SetColor("_AmbientColor", original.GetColor("_AmbientColor") * Mathf.Clamp(Settings.ambient, 0, 2));
                material.SetFloat("_ToonStrength", original.GetFloat("_ToonStrength") * Mathf.Clamp01(Settings.toon));
                material.SetColor("_StylizedSpecularColor", original.GetColor("_StylizedSpecularColor") * Mathf.Clamp(Settings.specular, 0, 2));
                material.SetFloat("_SakuraBrightness", Mathf.Clamp(Settings.brightness, 0, 2));
                material.SetFloat("_SakuraSaturation", Mathf.Clamp(Settings.saturation, 0, 2));
                material.SetFloat("_SakuraSphere", Mathf.Clamp(Settings.sphere, 0, 2));
                material.SetFloat("_SakuraDirectional", Mathf.Clamp01(Settings.directional));
                if (transparency > 0)
                {
                    material.SetFloat("_TextureAlphaOutputWeight", 1);
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0); material.renderQueue = 3000;
                }
            }
        }

        private void OnDestroy()
        {
            if (originals == null) return;
            foreach (var material in originals) if (material != null) Destroy(material);
            foreach (var texture in replacements.Concat(painted).Concat(sphereReplacements).Concat(toonReplacements)) if (texture != null) Destroy(texture);
        }
    }
}
