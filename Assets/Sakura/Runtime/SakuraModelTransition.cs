using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>Crossfades the last rendered frame while a new avatar loads.</summary>
    public sealed class SakuraModelTransition : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var studio = FindFirstObjectByType<SakuraStudio>();
            if (studio != null && studio.GetComponent<SakuraModelTransition>() == null) studio.gameObject.AddComponent<SakuraModelTransition>();
        }

        private SakuraAvatarViewport viewport;
        private UIDocument document;
        private GameObject lastRoot;
        private Texture2D snapshot, overlayTexture;
        private VisualElement overlay;
        private float nextCapture;

        private void Start()
        {
            viewport = GetComponent<SakuraAvatarViewport>();
            document = GetComponent<UIDocument>();
        }

        private void Update()
        {
            if (viewport == null) viewport = GetComponent<SakuraAvatarViewport>() ?? FindFirstObjectByType<SakuraAvatarViewport>();
            if (document == null) document = GetComponent<UIDocument>() ?? FindFirstObjectByType<UIDocument>();
            if (viewport == null || document == null) return;
            var root = document.rootVisualElement;
            if (root == null) return;
            var current = viewport.AvatarRoot;
            if (current != lastRoot)
            {
                if (lastRoot != null && snapshot != null) Show(snapshot, root);
                lastRoot = current;
            }
            if (Time.unscaledTime < nextCapture || viewport.OutputTexture == null) return;
            nextCapture = Time.unscaledTime + .2f;
            Capture(viewport.OutputTexture);
        }

        private void Capture(RenderTexture source)
        {
            if (snapshot == null || snapshot.width != source.width || snapshot.height != source.height)
            {
                if (snapshot != null) Destroy(snapshot);
                snapshot = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false) { name = "Sakura transition snapshot" };
            }
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                snapshot.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                snapshot.Apply(false, false);
            }
            finally { RenderTexture.active = previous; }
        }

        private void Show(Texture2D source, VisualElement root)
        {
            if (overlay != null) { overlay.RemoveFromHierarchy(); overlay = null; }
            if (overlayTexture != null) { Destroy(overlayTexture); overlayTexture = null; }
            overlayTexture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false) { name = "Sakura transition cover" };
            overlayTexture.SetPixels(source.GetPixels());
            overlayTexture.Apply(false, false);
            overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
            overlay.style.opacity = 1;
            var image = new Image { image = overlayTexture, scaleMode = ScaleMode.ScaleAndCrop };
            image.style.flexGrow = 1;
            overlay.Add(image);
            root.Add(overlay);
            float started = Time.unscaledTime;
            overlay.schedule.Execute(() =>
            {
                if (overlay == null) return;
                float progress = Mathf.Clamp01((Time.unscaledTime - started) / .45f);
                overlay.style.opacity = 1 - progress;
                if (progress >= 1)
                {
                    overlay.RemoveFromHierarchy(); overlay = null;
                    if (overlayTexture != null) { Destroy(overlayTexture); overlayTexture = null; }
                }
            }).Every(16);
        }

        private void OnDestroy()
        {
            if (snapshot != null) Destroy(snapshot);
            if (overlayTexture != null) Destroy(overlayTexture);
        }
    }
}
