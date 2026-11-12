using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Live2D.Cubism.Rendering.URP;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sakura.Unity.Editor
{
    public static class SakuraModelSetup
    {
        [MenuItem("Sakura/Configure Model Shaders")]
        public static void Configure()
        {
            var reference = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Live2D/Cubism/Rendering/URP/CubismURPRenderer.asset");
            if (reference == null) throw new InvalidOperationException("Import the Cubism Unity URP SDK first.");
            foreach (string guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets/Settings" }))
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var feature in data.rendererFeatures.OfType<CubismRenderPassFeature>().Where(feature => feature.name == "Sakura Cubism Render Pass").ToArray())
                {
                    data.rendererFeatures.Remove(feature);
                    UnityEngine.Object.DestroyImmediate(feature, true);
                }
                data.SetDirty(); EditorUtility.SetDirty(data);
            }
            foreach (string guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset", new[] { "Assets/Settings" }))
            {
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
                var serialized = new SerializedObject(pipeline);
                var renderers = serialized.FindProperty("m_RendererDataList");
                if (renderers.arraySize == 1) renderers.InsertArrayElementAtIndex(1);
                if (renderers.arraySize != 2) throw new InvalidOperationException("Review the pipeline's custom renderer list before adding Cubism.");
                renderers.GetArrayElementAtIndex(1).objectReferenceValue = reference;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            string materialPath = "Assets/Resources/Sakura/PmxToon.mat";
            if (!File.Exists(materialPath))
            {
                var shader = Shader.Find("MMD URP Toon");
                if (shader == null) throw new InvalidOperationException("The MMD URP shader has not compiled.");
                AssetDatabase.CreateAsset(new Material(shader), materialPath);
            }
            AssetDatabase.SaveAssets();
        }

        public static void VerifyFileBoundaries()
        {
            string root = Path.GetFullPath(Path.Combine(Application.temporaryCachePath, "SakuraModelBoundaryCheck"));
            Directory.CreateDirectory(root);
            string valid = SakuraModelImport.ResolveFile(root, "textures/face.png");
            if (!valid.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Valid texture path failed.");
            foreach (string escape in new[] { "../outside.png", @"C:\Windows\win.ini", @"\\server\share\avatar.png" })
            {
                bool rejected = false;
                try { SakuraModelImport.ResolveFile(root, escape); }
                catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("Model path escaped the allowed root.");
            }
            Configure();
        }

        public static async Task VerifyImports()
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Run the studio in unpaused Play mode.");
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            var reports = new List<object>();
            var samples = new[] {
                ("PMX", "pmx/Ringo/Ringo.pmx", "pmx-ringo"),
                ("Live2D", "live2d/DeepSeek/大肥鱼.model3.json", "live2d-deepseek"),
                ("PMX", "pmx/Miku universe/luka/Killer Luka/LUKA.pmx", "pmx-killer-luka"),
                ("Live2D", "live2d/Amika/AmikaV4.model3.json", "live2d-amika"),
                ("VRM", "vrm/ArisuMaid.vrm", "vrm-regression")
            };
            foreach (var (format, path, output) in samples)
            {
                await viewport.LoadModel(@"F:\AI\assistant\Sakura", path);
                await Task.Delay(250);
                if (!viewport.HasAvatar || viewport.LoadedFormat != format || viewport.LastImportError != null)
                    throw new Exception("Import failed for " + path + ": " + viewport.LastImportError);
                var materials = viewport.GetComponentsInChildren<Renderer>().SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null).ToArray();
                if (format == "Live2D") materials = viewport.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>()
                    .Select(renderer => renderer.DrawMaterial ?? renderer.Material).Where(material => material != null).ToArray();
                if (materials.Length == 0 || materials.Any(material => material.shader == null || !material.shader.isSupported || material.shader.name.Contains("InternalError")))
                    throw new Exception("A model material has no supported shader.");
                if (format == "PMX" && materials.Any(material => material.shader.name != "MMD URP Toon"))
                    throw new Exception("PMX fell back to another shader.");
                var camera = viewport.GetComponentInChildren<Camera>();
                var previous = RenderTexture.active;
                var pixels = new Texture2D(camera.targetTexture.width, camera.targetTexture.height, TextureFormat.RGBA32, false);
                int visible;
                try
                {
                    RenderTexture.active = camera.targetTexture;
                    pixels.ReadPixels(new Rect(0, 0, pixels.width, pixels.height), 0, 0); pixels.Apply();
                    visible = pixels.GetPixels32().Count(pixel => pixel.a > 32);
                    if (visible < 500) throw new Exception("The model rendered an empty image.");
                }
                finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(pixels); }
                reports.Add(new { format, path, materials = materials.Length, visiblePixels = visible, shaders = materials.Select(material => material.shader.name).Distinct().ToArray(), details = viewport.ImportDetails });
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\" + output + ".png");
            }
            string lastFormat = viewport.LoadedFormat;
            await viewport.LoadModel(@"F:\AI\assistant\Sakura", "pmx/missing-test-model.pmx");
            if (!viewport.HasAvatar || viewport.LoadedFormat != lastFormat || viewport.LastImportError == null)
                throw new Exception("Failed-load recovery did not preserve the previous model.");
            await SakuraSetup.VerifyDoubleClick();
            File.WriteAllText(@"C:\AI\Unity-Verification\model-import-check.json", Newtonsoft.Json.JsonConvert.SerializeObject(new { models = reports, failedLoadPreservesAvatar = true }, Newtonsoft.Json.Formatting.Indented));
        }
    }
}
