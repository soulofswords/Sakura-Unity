using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UniVRM10;

namespace Sakura.Unity
{
    public sealed class SakuraLocalPreviews : MonoBehaviour
    {
        private sealed class Card
        {
            public VisualElement Element;
            public Image Image;
            public Label Status;
            public string Path;
        }

        public const int PreviewLayer = 29;
        public const int PreviewSize = 160;
        private static readonly SemaphoreSlim ImportGate = new SemaphoreSlim(1, 1);
        private static SakuraLocalPreviews current;
        private string source;
        private Card active;
        private CancellationTokenSource cancellation;
        private GameObject previewModel;
        private Camera camera;
        private RenderTexture texture;
        private bool closed, portrait;
        private int revision;
        public bool Loading { get; private set; }
        public bool HasPreview => previewModel != null;
        public string PreviewPath { get; private set; }
        public static int ImportsInFlight => 1 - ImportGate.CurrentCount;

        public static SakuraLocalPreviews Attach(string sourceFolder, VisualElement pickerRoot)
        {
            if (pickerRoot == null) throw new ArgumentNullException(nameof(pickerRoot));
            string folder = Path.GetFullPath(sourceFolder);
            current?.Close();
            var owner = new GameObject("Sakura local model previews") { hideFlags = HideFlags.HideAndDontSave };
            var previews = owner.AddComponent<SakuraLocalPreviews>();
            previews.source = folder; current = previews;
            pickerRoot.RegisterCallback<DetachFromPanelEvent>(_ => previews.Close());
            return previews;
        }

        public static string ResolvePreviewPath(string sourceFolder, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new ArgumentException("Choose an installed model for preview.");
            string path = SakuraAvatarViewport.ResolveCharacterPath(sourceFolder, relative);
            if (!File.Exists(path)) throw new FileNotFoundException("This model is no longer in the local library.");
            return path;
        }

        public static bool IsVisible(VisualElement element)
        {
            if (element == null || element.panel == null || element.worldBound.width <= 0 || element.worldBound.height <= 0) return false;
            for (var parent = element; parent != null; parent = parent.parent)
            {
                if (parent.resolvedStyle.display == DisplayStyle.None || parent.resolvedStyle.visibility == Visibility.Hidden) return false;
                if (parent is ScrollView scroll && !scroll.contentViewport.worldBound.Overlaps(element.worldBound)) return false;
            }
            return true;
        }

        public void Bind(VisualElement card, string relativeModelPath)
        {
            if (closed) throw new ObjectDisposedException(nameof(SakuraLocalPreviews));
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (card.Q(className: "local-preview-image") != null) return;
            var image = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("local-preview-image");
            image.style.width = Length.Percent(100); image.style.height = 76; image.style.flexShrink = 1;
            image.style.display = DisplayStyle.None;
            var status = new Label("Hover to preview") { pickingMode = PickingMode.Ignore };
            status.AddToClassList("description"); status.AddToClassList("local-preview-status");
            status.style.fontSize = 10; status.style.marginTop = 3; status.style.marginBottom = 0;
            card.style.flexDirection = FlexDirection.Column;
            card.Insert(0, image); card.Add(status);
            var binding = new Card { Element = card, Image = image, Status = status, Path = relativeModelPath };
            card.RegisterCallback<PointerEnterEvent>(_ => Begin(binding));
            card.RegisterCallback<PointerLeaveEvent>(_ => End(binding));
            card.RegisterCallback<FocusInEvent>(_ => Begin(binding));
            card.RegisterCallback<FocusOutEvent>(_ => End(binding));
            card.RegisterCallback<DetachFromPanelEvent>(_ => End(binding));
        }

        private void Begin(Card card)
        {
            if (closed || active == card || !IsVisible(card.Element)) return;
            StopPreview(); active = card;
            cancellation = new CancellationTokenSource();
            int request = ++revision;
            card.Status.text = "Loading preview…"; Loading = true;
            Load(card, request, cancellation.Token);
        }

        private async void Load(Card card, int request, CancellationToken token)
        {
            bool entered = false;
            GameObject loaded = null;
            try
            {
                string path = ResolvePreviewPath(source, card.Path);
                await ImportGate.WaitAsync(token); entered = true;
                token.ThrowIfCancellationRequested();
                if (path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
                {
                    var vrm = await Vrm10.LoadPathAsync(path, canLoadVrm0X: true,
                        materialGenerator: new UrpVrm10MaterialDescriptorGenerator(), ct: token);
                    loaded = vrm == null ? null : vrm.gameObject;
                }
                else if (path.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)) loaded = await SakuraModelImport.LoadPmx(path, token, _ => { });
                else loaded = SakuraModelImport.LoadLive2D(path);
                token.ThrowIfCancellationRequested();
                if (loaded == null) throw new InvalidDataException("The model importer returned no preview.");
                if (this == null || closed || request != revision || !IsVisible(card.Element)) return;
                loaded.transform.SetParent(transform, false);
                transform.position = new Vector3(1000, 1000, 1000);
                loaded.transform.localPosition = Vector3.zero;
                portrait = path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase);
                loaded.transform.localRotation = Quaternion.Euler(0, portrait ? 180 : 0, 0);
                foreach (var child in loaded.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = PreviewLayer;
                foreach (var collider in loaded.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var light in loaded.GetComponentsInChildren<Light>(true)) light.enabled = false;
                foreach (var importedCamera in loaded.GetComponentsInChildren<Camera>(true)) importedCamera.enabled = false;
                foreach (var listener in loaded.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
                foreach (var audio in loaded.GetComponentsInChildren<AudioSource>(true)) { audio.Stop(); audio.enabled = false; }
                foreach (var renderer in loaded.GetComponentsInChildren<Renderer>(true)) { renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false; }
                CreateCamera(portrait);
                previewModel = loaded; loaded = null; PreviewPath = card.Path;
                await Task.Yield();
                if (this == null || closed || request != revision || previewModel == null) return;
                FrameModel(); camera.enabled = true;
                card.Image.image = texture; card.Image.style.display = DisplayStyle.Flex;
                card.Status.text = portrait ? "Live2D portrait" : "Rotating preview";
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (this != null && !closed && request == revision)
                { StopPreview(); card.Status.text = "Preview unavailable"; card.Element.tooltip = error.Message; }
            }
            finally
            {
                if (loaded != null) Destroy(loaded);
                if (entered) ImportGate.Release();
                if (this != null && request == revision) Loading = false;
            }
        }

        private void CreateCamera(bool live2D)
        {
            if (camera == null)
            {
                var owner = new GameObject("Local preview camera") { hideFlags = HideFlags.HideAndDontSave };
                owner.transform.SetParent(transform, false); camera = owner.AddComponent<Camera>();
                camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << PreviewLayer; camera.fieldOfView = 30; camera.nearClipPlane = .01f;
                camera.allowHDR = false; camera.allowMSAA = false;
            }
            var data = camera.GetUniversalAdditionalCameraData(); data.SetRenderer(live2D ? 1 : -1); data.renderPostProcessing = false; data.renderShadows = false;
            if (texture == null)
            {
                texture = new RenderTexture(PreviewSize, PreviewSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1, name = "Sakura local hover preview" };
                texture.Create();
            }
            camera.targetTexture = texture;
        }

        private void FrameModel()
        {
            var renderers = previewModel.GetComponentsInChildren<Renderer>().Where(renderer => renderer.enabled).ToArray();
            if (renderers.Length == 0) throw new InvalidDataException("The model has no visible renderers.");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            Vector3 center = bounds.center;
            float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x);
            if (portrait) { center += Vector3.up * bounds.extents.y * .35f; halfHeight *= .7f; }
            float distance = Mathf.Max(.05f, halfHeight / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.12f);
            camera.transform.position = center + Vector3.forward * distance; camera.transform.LookAt(center);
            camera.farClipPlane = Mathf.Max(100, distance + bounds.size.magnitude * 2);
        }

        private void Update()
        {
            if (closed || active == null) return;
            if (!IsVisible(active.Element)) { StopPreview(); return; }
            if (previewModel != null && camera != null && camera.enabled && !portrait)
                previewModel.transform.Rotate(Vector3.up, Time.unscaledDeltaTime * 28, Space.Self);
        }

        private void End(Card card) { if (active == card) StopPreview(); }

        private void StopPreview()
        {
            revision++; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            Loading = false; PreviewPath = null;
            if (active != null) { active.Image.image = null; active.Image.style.display = DisplayStyle.None; active.Status.text = "Hover to preview"; active = null; }
            if (camera != null) camera.enabled = false;
            if (previewModel != null) { previewModel.SetActive(false); Destroy(previewModel); previewModel = null; }
        }

        public void Close()
        {
            if (closed) return;
            closed = true; StopPreview();
            if (camera != null) { camera.targetTexture = null; Destroy(camera.gameObject); camera = null; }
            if (texture != null) { texture.Release(); Destroy(texture); texture = null; }
            if (current == this) current = null;
            Destroy(gameObject);
        }

        private void OnDisable() { StopPreview(); }
        private void OnDestroy() { Close(); }
    }
}
