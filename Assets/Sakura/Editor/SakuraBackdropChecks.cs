using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    public static class SakuraBackdropChecks
    {
        public static void Run()
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null || !Application.isPlaying) throw new Exception("Enter Studio Play mode first.");
            studio.SwitchMode("Companion"); studio.ShowPanel("Background");
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            var backdrop = root.Q<Image>("stage-backdrop");
            if (backdrop == null || backdrop.pickingMode != PickingMode.Ignore || backdrop.parent.IndexOf(backdrop) != 0)
                throw new Exception("Background is missing, covers the avatar or intercepts dragging.");
            var shader = Resources.Load<Shader>("Sakura/BackdropComposite");
            if (shader == null || !shader.isSupported) throw new Exception("Background output shader is unavailable.");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var material = new Material(shader);
            var target = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var read = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try
            {
                texture.SetPixels(new[] { Color.clear, Color.clear, Color.clear, Color.clear }); texture.Apply();
                material.SetColor("_Background", Color.green); target.Create(); Graphics.Blit(texture, target, material);
                RenderTexture.active = target; read.ReadPixels(new Rect(0, 0, 2, 2), 0, 0); read.Apply();
                Color pixel = read.GetPixel(0, 0);
                if (pixel.g < .98f || pixel.r > .02f || pixel.b > .02f || pixel.a < .98f) throw new Exception("Solid output did not replace transparent pixels.");
                texture.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red }); texture.Apply();
                Graphics.Blit(texture, target, material); read.ReadPixels(new Rect(0, 0, 2, 2), 0, 0); read.Apply(); pixel = read.GetPixel(0, 0);
                if (pixel.r < .98f || pixel.g > .02f || pixel.b > .02f) throw new Exception("Solid background changed opaque avatar pixels.");
                material.SetTexture("_Media", texture); material.SetFloat("_UseMedia", 1); material.SetVector("_MediaScale", new Vector4(1, 1, 0, 0));
                Graphics.Blit(Texture2D.blackTexture, target, material);
                // A transparent avatar exposes the media; an opaque avatar remains above it.
                var clear = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                try
                {
                    clear.SetPixel(0, 0, Color.clear); clear.Apply(); Graphics.Blit(clear, target, material);
                    read.ReadPixels(new Rect(0, 0, 2, 2), 0, 0); read.Apply(); pixel = read.GetPixel(0, 0);
                    if (pixel.r < .98f || pixel.g > .02f || pixel.b > .02f) throw new Exception("Background media was omitted from streaming compositing.");
                }
                finally { UnityEngine.Object.Destroy(clear); }
                Debug.Log("PASS: background placement, non-intercepting picking, solid output compositing and opaque avatar preservation.");
            }
            finally
            {
                RenderTexture.active = previous;
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(texture); UnityEngine.Object.Destroy(read); UnityEngine.Object.Destroy(material);
            }
        }
    }
}
