using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mmd.Parser;
using Mmd.UnityIntegration;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraTextureChecks
    {
        public static void RunBatch()
        {
            string folder = Path.Combine(Path.GetTempPath(), "sakura-texture-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            GameObject root = null; string profile = null;
            try
            {
                var definition = new MmdModelDefinition();
                for (int i = 0; i < 3; i++) definition.vertices.Add(new MmdVertexDefinition { index = i, position = new[] { (float)(i == 1 ? 1 : 0), (float)(i == 2 ? 1 : 0), 0 }, normal = new[] { 0f, 0f, 1f }, uv = new[] { 0f, 0f } });
                definition.indices.AddRange(new[] { 0, 1, 2 });
                definition.materials.Add(new MmdMaterialDefinition { index = 0, name = "fixture", texture = "art.png", vertexCount = 3 });
                string source = Path.Combine(folder, "fixture.pmx");
                var instance = MmdUnityModelFactory.CreateStaticModel(definition, source);
                root = instance.Root;
                var model = root.AddComponent<SakuraPmxModel>(); model.Initialize(instance, definition, source);
                profile = Path.GetDirectoryName(model.StorageFile);
                var refresh = typeof(SakuraPmxModel).GetMethod("RefreshTexture", BindingFlags.Instance | BindingFlags.Public);
                if (refresh == null) throw new Exception("An already-valid texture reference cannot be re-read after artwork appears on disk.");
                var original = typeof(SakuraPmxModel).GetMethod("OriginalTextureReference", BindingFlags.Instance | BindingFlags.Public);
                if (original == null) throw new Exception("Package remapping cannot distinguish the authored texture from its current assignment.");
                void Write(Color color)
                {
                    var texture = new Texture2D(2, 2); texture.SetPixels(Enumerable.Repeat(color, 4).ToArray()); texture.Apply();
                    File.WriteAllBytes(Path.Combine(folder, "art.png"), texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
                }
                Write(Color.red); refresh.Invoke(model, new object[] { 0, "diffuse", "art.png" });
                var actual = (Texture2D)instance.Materials[0].GetTexture("_BaseMap");
                if (actual == null || actual.GetPixel(0, 0).r < .9f) throw new Exception("Valid texture did not replace the unloaded fallback.");
                Write(Color.blue); refresh.Invoke(model, new object[] { 0, "diffuse", "art.png" });
                actual = (Texture2D)instance.Materials[0].GetTexture("_BaseMap");
                if (actual.GetPixel(0, 0).b < .9f) throw new Exception("Texture check reused stale bytes.");
                File.Copy(Path.Combine(folder, "art.png"), Path.Combine(folder, "other.png"));
                refresh.Invoke(model, new object[] { 0, "diffuse", "other.png" });
                if ((string)original.Invoke(model, new object[] { 0, "diffuse" }) != "art.png") throw new Exception("Package remapping overwrote the authored reference needed by Restore original.");
                model.ReplaceTexture(0, File.ReadAllBytes(Path.Combine(folder, "art.png")));
                var part = model.GetPart(0); part.transparency = .25f; part.colored = true; part.color = Color.green; model.Apply();
                string state = JsonUtility.ToJson(model.Settings);
                Write(Color.red); refresh.Invoke(model, new object[] { 0, "diffuse", "art.png" });
                if (JsonUtility.ToJson(model.Settings) != state) throw new Exception("Texture check changed saved coloring or replacement choices.");
                if (((Texture2D)instance.Materials[0].GetTexture("_BaseMap")).GetPixel(0, 0).g < .9f) throw new Exception("Texture check discarded the colored replacement.");
                Debug.Log("SakuraTextureChecks PASS: valid-file reread, stale-image refresh, replacement and color preservation.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogError(error); EditorApplication.Exit(1); }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                if (profile != null && Directory.Exists(profile)) Directory.Delete(profile, true);
            }
        }
    }
}
