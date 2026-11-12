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
            }
            finally
            {
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
            if (ui.Query<Button>().ToList().Count(button => button.text == "Bones" || button.text == "Appearance" || button.text == "Texture remapping") != 3) throw new Exception("PMX tabs are unreachable.");
            await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pmx-native-tools.png");
            File.WriteAllText(@"C:\AI\Unity-Verification\pmx-tools-check.json", "{\"transparencyIndependentOfColour\":true,\"colourTexturePixels\":true,\"appearanceShader\":true,\"boneFreeze\":true,\"persistenceReload\":true,\"snapshot\":true,\"breathingAdvances\":true,\"nativeTabs\":true}");
        }
    }
}
