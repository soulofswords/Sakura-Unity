using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>
    /// Holds the old avatar while a replacement loads, then reveals the new avatar through a
    /// grid of shrinking blocks. A single full-screen fade made a model swap feel like a page
    /// reload rather than a character change.
    /// </summary>
    public sealed class SakuraModelTransition : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var studio = FindFirstObjectByType<SakuraStudio>();
            if (studio != null && studio.GetComponent<SakuraModelTransition>() == null) studio.gameObject.AddComponent<SakuraModelTransition>();
        }

        private sealed class Block
        {
            public VisualElement Element;
            public Image Image;
            public Rect OldUv, NewUv;
            public float X, Y, Width, Height, Delay;
            public bool ShowingNew;
        }

        private const float RevealSeconds = .9f;
        private const float HoldTimeout = 45f;

        private SakuraAvatarViewport viewport;
        private UIDocument document;
        private VisualElement stage, overlay, cover;
        private Texture2D oldTexture, newTexture;
        private Color32[] oldPixels, newPixels;
        private readonly List<Block> blocks = new List<Block>();
        private GameObject lastRoot;
        private bool wasLoading;
        private bool revealing;
        private float revealStarted, holdStarted;
        private int revision;

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
            if (stage == null) stage = FindFirstObjectByType<SakuraStudio>()?.StageElement;
            if (stage == null) return;

            bool loading = viewport.Loading;
            var current = viewport.AvatarRoot;
            if (loading && !wasLoading) Begin(root);
            if (!loading && wasLoading)
            {
                if (current != null && current != lastRoot) StartCoroutine(RevealAfterRender());
                else Cancel();
            }
            if (!loading && current != lastRoot && !revealing && oldTexture == null) lastRoot = current;
            wasLoading = loading;

            if (revealing)
            {
                float age = (Time.unscaledTime - revealStarted) / RevealSeconds;
                LayoutBlocks(age);
                if (age >= 1f) { Cancel(); return; }
            }
            if (revealing && Time.unscaledTime - revealStarted > RevealSeconds + .35f) Cancel();
            else if (overlay != null && !revealing && Time.unscaledTime - holdStarted > HoldTimeout) Cancel();
        }

        private void Begin(VisualElement root)
        {
            if (viewport.OutputTexture == null || viewport.AvatarRoot == null) return;
            if (oldTexture != null) Cancel();
            if (!Capture(viewport.OutputTexture, out oldTexture, out oldPixels)) return;
            lastRoot = viewport.AvatarRoot;
            holdStarted = Time.unscaledTime;
            overlay = new VisualElement { pickingMode = PickingMode.Ignore, name = "sakura-model-transition" };
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
            overlay.style.overflow = Overflow.Hidden;
            cover = new Image { image = oldTexture, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            cover.style.position = Position.Absolute; cover.style.left = 0; cover.style.top = 0; cover.style.right = 0; cover.style.bottom = 0;
            overlay.Add(cover);
            stage.Add(overlay);
        }

        private System.Collections.IEnumerator RevealAfterRender()
        {
            if (oldTexture == null || viewport.OutputTexture == null) { Cancel(); yield break; }
            int expected = ++revision;
            yield return null;
            yield return null;
            if (this == null || expected != revision || oldTexture == null || viewport.OutputTexture == null) yield break;
            if (!Capture(viewport.OutputTexture, out newTexture, out newPixels, oldTexture.width, oldTexture.height)) { Cancel(); yield break; }
            MakeBlocks();
            if (blocks.Count == 0) { Cancel(); yield break; }
            if (cover != null) { cover.RemoveFromHierarchy(); cover = null; }
            revealing = true;
            revealStarted = Time.unscaledTime;
            lastRoot = viewport.AvatarRoot;
        }

        private bool Capture(RenderTexture source, out Texture2D texture, out Color32[] pixels, int targetWidth = 0, int targetHeight = 0)
        {
            texture = null; pixels = null;
            if (source == null || source.width <= 0 || source.height <= 0) return false;
            // The Python app caps captures at 720px. That keeps the block grid cheap while
            // preserving enough detail for the character silhouette.
            float scale = Mathf.Min(1f, 720f / Mathf.Max(source.width, source.height));
            int width = targetWidth > 0 ? targetWidth : Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = targetHeight > 0 ? targetHeight : Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            RenderTexture temporary = null;
            var previous = RenderTexture.active;
            try
            {
                temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "Sakura transition snapshot" };
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                pixels = texture.GetPixels32();
                return true;
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Sakura] Model transition capture failed: " + error.Message);
                if (texture != null) Destroy(texture); texture = null;
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private void MakeBlocks()
        {
            blocks.Clear();
            if (oldTexture == null || newTexture == null) return;
            int width = oldTexture.width, height = oldTexture.height;
            int size = Mathf.Max(14, Mathf.CeilToInt(width / 30f));
            Rect Uv(int x, int y, int w, int h) => new Rect(x / (float)width, (height - y - h) / (float)height, w / (float)width, h / (float)height);
            for (int y = 0; y < height; y += size)
            {
                int h = Mathf.Min(size, height - y);
                for (int x = 0; x < width; x += size)
                {
                    int w = Mathf.Min(size, width - x);
                    int alpha = 0;
                    for (int sy = y; sy < y + h; sy += 3)
                        for (int sx = x; sx < x + w; sx += 3)
                        {
                            int i = (height - 1 - sy) * width + sx;
                            alpha = Mathf.Max(alpha, Mathf.Max(oldPixels[i].a, newPixels[i].a));
                        }
                    if (alpha < 12) continue;
                    var block = new Block
                    {
                        OldUv = Uv(x, y, w, h),
                        NewUv = Uv(x, y, w, h),
                        X = x / (float)width * 100, Y = y / (float)height * 100,
                        Width = w / (float)width * 100, Height = h / (float)height * 100,
                        Delay = (y / (float)height * .75f + x / (float)width * .25f) * .50f,
                    };
                    block.Element = new VisualElement { pickingMode = PickingMode.Ignore };
                    block.Element.style.position = Position.Absolute;
                    block.Element.style.overflow = Overflow.Hidden;
                    block.Element.style.borderLeftWidth = 0; block.Element.style.borderRightWidth = 0;
                    block.Element.style.borderTopWidth = 0; block.Element.style.borderBottomWidth = 0;
                    block.Element.style.borderLeftColor = new Color(185 / 255f, 180 / 255f, 1f, .7f);
                    block.Element.style.borderRightColor = block.Element.style.borderLeftColor;
                    block.Element.style.borderTopColor = block.Element.style.borderLeftColor;
                    block.Element.style.borderBottomColor = block.Element.style.borderLeftColor;
                    block.Image = new Image { image = oldTexture, sourceRect = block.OldUv, scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
                    block.Image.style.position = Position.Absolute;
                    block.Image.style.left = 0; block.Image.style.top = 0; block.Image.style.right = 0; block.Image.style.bottom = 0;
                    block.Element.Add(block.Image);
                    overlay.Add(block.Element);
                    blocks.Add(block);
                }
            }
            LayoutBlocks(0);
        }

        private void LayoutBlocks(float age)
        {
            if (blocks.Count == 0) return;
            foreach (var block in blocks)
            {
                float t = Mathf.Clamp01((age - block.Delay) / .5f);
                float p = t * t * (3f - 2f * t);
                float width = Mathf.Abs(1f - 2f * p);
                if (width < .008f)
                {
                    if (block.Element.style.display != DisplayStyle.None) block.Element.style.display = DisplayStyle.None;
                    continue;
                }
                if (block.Element.style.display == DisplayStyle.None) block.Element.style.display = DisplayStyle.Flex;
                float height = 1f - Mathf.Sin(p * Mathf.PI) * .12f;
                float w = block.Width * width, h = block.Height * height;
                block.Element.style.left = new Length(block.X + (block.Width - w) * .5f, LengthUnit.Percent);
                block.Element.style.top = new Length(block.Y + (block.Height - h) * .5f, LengthUnit.Percent);
                block.Element.style.width = new Length(w, LengthUnit.Percent);
                block.Element.style.height = new Length(h, LengthUnit.Percent);
                bool next = p >= .5f;
                if (block.ShowingNew != next)
                {
                    block.ShowingNew = next;
                    block.Image.image = next ? newTexture : oldTexture;
                    block.Image.sourceRect = next ? block.NewUv : block.OldUv;
                }
                bool outline = t > .05f && t < .95f;
                float line = outline ? 1 : 0;
                block.Element.style.borderLeftWidth = line;
                block.Element.style.borderRightWidth = line;
                block.Element.style.borderTopWidth = line;
                block.Element.style.borderBottomWidth = line;
            }
        }

        private void Cancel()
        {
            revision++;
            revealing = false;
            if (overlay != null) { overlay.RemoveFromHierarchy(); overlay = null; }
            cover = null;
            blocks.Clear();
            if (oldTexture != null) { Destroy(oldTexture); oldTexture = null; }
            if (newTexture != null) { Destroy(newTexture); newTexture = null; }
            oldPixels = null; newPixels = null;
            lastRoot = viewport != null ? viewport.AvatarRoot : lastRoot;
        }

        private void OnDestroy()
        {
            Cancel();
        }
    }
}
