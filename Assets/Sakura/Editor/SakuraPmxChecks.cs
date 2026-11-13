using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraPmxChecks
    {
        public static async Task Verify()
        {
            var opaque = Enumerable.Repeat(new Color32(187,187,187,255), 64).ToArray();
            var translucent = Enumerable.Repeat(new Color32(187,187,187,127), 64).ToArray();
            if (SakuraPmxAppearance.Loss(SakuraPmxAppearance.Statistics(opaque), SakuraPmxAppearance.Statistics(translucent)) != 0)
                throw new Exception("Appearance matching darkened translucent pixels twice.");
            EditorApplication.isPaused = false;
            Application.runInBackground = true;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            if (viewport == null) throw new Exception("Start the Sakura scene first.");
            await viewport.LoadModel(@"F:\AI\assistant\Sakura", "pmx/Ringo/Ringo.pmx");
            if (viewport.Pmx == null || viewport.LastImportError != null) throw new Exception(viewport.LastImportError ?? "PMX tools missing.");
            var pmx = viewport.Pmx;
            string file = pmx.StorageFile;
            string saved = File.Exists(file) ? File.ReadAllText(file) : null;
            string options = JsonUtility.ToJson(pmx.Settings);
            string replacementFile = Path.Combine(Path.GetDirectoryName(file), "part-0.png");
            byte[] previousReplacement = File.Exists(replacementFile) ? File.ReadAllBytes(replacementFile) : null;
            string folder = Path.GetDirectoryName(file);
            var originalImports = Directory.GetFiles(folder, "import-*.png").ToHashSet();
            string packageFile = Path.Combine(folder, "appearance-check.json");
            var idle = viewport.Idle;
            idle.MotionOwned = true;
            try
            {
                var material = pmx.Instance.Materials[0];
                var original = material.GetColor("_BaseColor");
                var part = pmx.GetPart(0);
                part.colored = false; part.transparency = .5f; pmx.Apply();
                var transparent = material.GetColor("_BaseColor");
                if (Mathf.Abs(transparent.r - original.r) > .0001f || Mathf.Abs(transparent.g - original.g) > .0001f || Mathf.Abs(transparent.a - original.a * .5f) > .0001f) throw new Exception("Transparency changed colour or failed to change alpha.");
                part.transparency = 0; part.color = Color.red; part.colored = true; pmx.Apply();
                var pixels = ((Texture2D)material.GetTexture("_BaseMap")).GetPixels();
                if (!pixels.Any(pixel => pixel.r > .1f && pixel.g < .01f && pixel.a > .1f)) throw new Exception("Material colour did not affect texture pixels.");
                part.colored = false; pmx.Settings.brightness = .5f; pmx.Apply(); pmx.Save();
                if (material.GetFloat("_SakuraBrightness") != .5f) throw new Exception("Appearance did not reach shader.");
                await viewport.SaveSnapshotNextFrame("brightness-half");
                var half = SakuraPmxAppearance.Statistics(viewport.ReadAvatarPixels(128));
                pmx.Settings.brightness = 1; pmx.Apply(); await viewport.SaveSnapshotNextFrame("brightness-full");
                if (SakuraPmxAppearance.Loss(half, SakuraPmxAppearance.Statistics(viewport.ReadAvatarPixels(128))) < .0001f) throw new Exception("Brightness did not change rendered pixels.");
                pmx.Settings.brightness = .5f; pmx.Apply(); pmx.Save();
                var image = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                try
                {
                    image.SetPixels(Enumerable.Repeat(new Color(1, 0, 0, .5f), 256).ToArray()); image.Apply();
                    pmx.ReplaceTexture(0, image.EncodeToPNG());
                    var pixel = ((Texture2D)material.GetTexture("_BaseMap")).GetPixel(0, 0);
                    if (pixel.r < .99f || pixel.g > .01f || Mathf.Abs(pixel.a - .5f) > .01f) throw new Exception("Replacement texture lost colour or alpha.");
                    pmx.RestoreTexture(0);
                    string hash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(pmx.SourcePath))).Replace("-", "").ToLowerInvariant();
                    var package = new Newtonsoft.Json.Linq.JObject {
                        ["format"] = "sakura-appearance-export", ["version"] = 1, ["modelType"] = "PMX", ["modelSha256"] = hash,
                        ["parts"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject {
                            ["id"] = "mat:0", ["sourceIndex"] = 0, ["name"] = pmx.Definition.materials[0].name, ["kind"] = "material",
                            ["color"] = "#ffffff", ["strength"] = 0, ["opacity"] = .5, ["textureBase64"] = Convert.ToBase64String(image.EncodeToPNG()) }) };
                    File.WriteAllText(packageFile, package.ToString());
                    string imported = SakuraAppearanceImport.Apply(packageFile, viewport, viewport.GetComponent<SakuraBackend>());
                    if (!imported.StartsWith("Imported 1 ") || pmx.GetPart(0).transparency != .5f || !pmx.GetPart(0).replacement.StartsWith("import-"))
                        throw new Exception("Legacy PMX appearance package did not reach the PMX profile.");
                    package["modelSha256"] = new string('0', 64); File.WriteAllText(packageFile, package.ToString());
                    bool mismatchRejected = false;
                    try { SakuraAppearanceImport.Apply(packageFile, viewport, viewport.GetComponent<SakuraBackend>()); }
                    catch (InvalidDataException) { mismatchRejected = true; }
                    if (!mismatchRejected) throw new Exception("Appearance import accepted a mismatched model fingerprint.");
                }
                finally { UnityEngine.Object.Destroy(image); }
                pmx.Freeze(0, true);
                var bone = pmx.Instance.BoneTransforms[0]; bone.localPosition += Vector3.one;
                await Task.Delay(120);
                if ((bone.localPosition - pmx.Instance.BindLocalPositions[0]).sqrMagnitude > .000001f) throw new Exception("Frozen bone moved.");
                string snapshot = viewport.SaveSnapshot("pmx-tools-check");
                if (new FileInfo(snapshot).Length < 500) throw new Exception("Snapshot was empty.");
                await viewport.LoadModel(@"F:\AI\assistant\Sakura", "pmx/Ringo/Ringo.pmx");
                pmx = viewport.Pmx;
                if (pmx.Settings.brightness != .5f || !pmx.Settings.frozen.Contains(0)) throw new Exception("PMX settings did not survive reload.");
                if (pmx.GetPart(0).transparency != .5f || ((Texture2D)pmx.Instance.Materials[0].GetTexture("_BaseMap")).width != 16)
                    throw new Exception("Imported PMX texture/transparency did not survive reload.");
            }
            finally
            {
                if (File.Exists(packageFile)) File.Delete(packageFile);
                foreach (string imported in Directory.GetFiles(folder, "import-*.png").Where(path => !originalImports.Contains(path))) File.Delete(imported);
                if (saved == null) { if (File.Exists(file)) File.Delete(file); } else File.WriteAllText(file, saved);
                if (previousReplacement == null) { if (File.Exists(replacementFile)) File.Delete(replacementFile); } else File.WriteAllBytes(replacementFile, previousReplacement);
                await viewport.LoadModel(@"F:\AI\assistant\Sakura", "pmx/Ringo/Ringo.pmx");
            }
            idle = viewport.Idle;
            float before = idle.BreathValue;
            await Task.Delay(900);
            if (Mathf.Abs(idle.BreathValue - before) < .0001f) throw new Exception("Idle breathing is not advancing.");
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>(); studio.ShowPanel("Model Settings");
            var ui = studio.GetComponent<UIDocument>().rootVisualElement;
            var tabs = ui.Query<Button>(className: "model-tab").ToList();
            if (!tabs.Select(button => button.text).SequenceEqual(new[] { "Texture remapping", "Texture coloring", "Expressions & hotkeys", "Bones", "Mouth" }))
                throw new Exception("PMX tabs differ from the reference app.");
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = tabs[1]; tabs[1].SendEvent(submit); }
            if (ui.Query<Slider>().ToList().Count(slider => new[] { "Brightness", "Ambient fill", "Authored material tint", "Toon shading", "Directional shading", "Sphere / shine maps", "Specular highlights", "Colour saturation" }.Contains(slider.label)) != 8)
                throw new Exception("PMX appearance controls are unreachable from Texture coloring.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pmx-native-tools.png");
            File.WriteAllText(@"C:\AI\Unity-Verification\pmx-tools-check.json", "{\"transparencyIndependentOfColour\":true,\"colourTexturePixels\":true,\"appearanceShader\":true,\"boneFreeze\":true,\"persistenceReload\":true,\"snapshot\":true,\"breathingAdvances\":true,\"nativeTabs\":true}");
        }
    }
}
