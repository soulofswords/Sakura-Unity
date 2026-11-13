using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraLocalPreviewChecks
    {
        public static void Validate()
        {
            string folder = Path.Combine(Path.GetTempPath(), "sakura-preview-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "models", "pmx"));
            string model = Path.Combine(folder, "models", "pmx", "fixture.pmx");
            File.WriteAllText(model, "fixture");
            try
            {
                if (SakuraLocalPreviews.ResolvePreviewPath(folder, "pmx/fixture.pmx") != model) throw new Exception("Local preview did not resolve its confined model.");
                foreach (string path in new[] { "../outside.pmx", "pmx/missing.pmx", "pmx/fixture.txt" })
                {
                    bool refused = false;
                    try { SakuraLocalPreviews.ResolvePreviewPath(folder, path); }
                    catch (Exception error) when (error is ArgumentException || error is FileNotFoundException) { refused = true; }
                    if (!refused) throw new Exception("Local preview accepted an unavailable or outside-library model.");
                }
                if (SakuraLocalPreviews.IsVisible(new VisualElement()) || SakuraLocalPreviews.PreviewLayer == 30 || SakuraLocalPreviews.PreviewSize != 160)
                    throw new Exception("Local preview visibility, isolation or render size differs.");
            }
            finally { Directory.Delete(folder, true); }
            Debug.Log("PASS: confined local preview paths, unavailable-file rejection, detached-card visibility and 160px isolated render layer.");
        }

        public static async Task VerifyLoadedModel()
        {
            Validate();
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (!Application.isPlaying || studio == null) throw new InvalidOperationException("Enter Studio Play mode first.");
            var viewport = studio.GetComponent<SakuraAvatarViewport>();
            if (!viewport.HasAvatar || viewport.Loading) throw new InvalidOperationException("Load a local character before checking its hover preview.");
            var mainAvatar = viewport.AvatarRoot;
            string selected = viewport.LoadedModelPath;
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            var fixture = new VisualElement { name = "local-preview-verification" };
            fixture.style.position = Position.Absolute; fixture.style.left = 120; fixture.style.top = 80; fixture.style.width = 180; fixture.style.height = 190;
            root.Add(fixture);
            var card = new Button { text = "Preview verification" }; card.style.width = 160; card.style.height = 180; fixture.Add(card);
            var previews = SakuraLocalPreviews.Attach(studio.GetComponent<SakuraBackend>().SourceFolder, fixture);
            previews.Bind(card, selected);
            try
            {
                await Task.Delay(100);
                using (var enter = PointerEnterEvent.GetPooled()) { enter.target = card; card.SendEvent(enter); }
                var image = card.Q<Image>(className: "local-preview-image");
                float deadline = Time.realtimeSinceStartup + 40;
                while (image.image == null && Time.realtimeSinceStartup < deadline && card.Q<Label>(className: "local-preview-status").text != "Preview unavailable")
                {
                    if (SakuraLocalPreviews.ImportsInFlight > 1) throw new Exception("Local previews imported multiple models concurrently.");
                    await Task.Delay(100);
                }
                if (image.image is not RenderTexture texture || texture.width != 160 || texture.height != 160 || !previews.HasPreview)
                    throw new Exception("Native hover preview did not render the loaded model. " + card.tooltip);
                var camera = previews.GetComponentsInChildren<Camera>().Single(value => value.enabled);
                if (camera.cullingMask != 1 << SakuraLocalPreviews.PreviewLayer) throw new Exception("Local preview camera sees the active avatar layer.");
                if (viewport.AvatarRoot != mainAvatar || viewport.LoadedModelPath != selected) throw new Exception("Local preview replaced the active character.");
                if (!previews.GetComponentsInChildren<Renderer>().All(renderer => renderer.gameObject.layer == SakuraLocalPreviews.PreviewLayer))
                    throw new Exception("A local preview renderer escaped its dedicated layer.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-local-hover-preview.png");
                fixture.RemoveFromHierarchy();
                if (previews.HasPreview || previews.Loading) throw new Exception("Closing the picker retained its preview model or import.");
                await Task.Delay(100);
                Debug.Log("PASS: real native local hover preview, bounded import, isolated camera/model layer, main-avatar preservation and picker-close cleanup.");
            }
            finally { previews.Close(); fixture.RemoveFromHierarchy(); }
        }
    }
}
