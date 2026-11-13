using System;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraLibraryPreviewChecks
    {
        public static void Verify()
        {
            SakuraLibraryPreview.ValidateMetadata(24, 6, 960, 640);
            var first = SakuraLibraryPreview.FrameUv(0, 24, 6);
            var last = SakuraLibraryPreview.FrameUv(23, 24, 6);
            if (Mathf.Abs(first.y - .75f) > .00001f || first.x != 0 || Mathf.Abs(last.x - 5f / 6) > .00001f || last.y != 0) throw new Exception("Sprite rows or columns were reversed.");
            if (SakuraLibraryPreview.FrameUv(0, 1, 1) != new Rect(0, 0, 1, 1)) throw new Exception("Portrait UV did not use the entire image.");
            int rejected = 0;
            foreach (var values in new[] { new[] { 0, 1, 160, 160 }, new[] { 25, 6, 960, 800 }, new[] { 4, 7, 1120, 160 }, new[] { 4, 2, 320, 160 }, new[] { 1, 1, 4096, 4096 } })
                try { SakuraLibraryPreview.ValidateMetadata(values[0], values[1], values[2], values[3]); } catch (ArgumentException) { rejected++; }
            if (rejected != 5) throw new Exception("Invalid preview metadata was accepted.");
            var pixels = new Texture2D(160, 160, TextureFormat.RGBA32, false);
            try
            {
                byte[] png = pixels.EncodeToPNG();
                using (var preview = SakuraLibraryPreview.Decode(png, 1, 1))
                    if (preview.Texture.width != 160 || preview.Frames != 1) throw new Exception("Valid preview failed to decode.");
                png[18] = 16;
                bool blocked = false;
                try { using (var preview = SakuraLibraryPreview.Decode(png, 1, 1)) { } } catch (ArgumentException) { blocked = true; }
                if (!blocked) throw new Exception("PNG dimensions were not validated before native decoding.");
                Debug.Log("SAKURA_LIBRARY_PREVIEW_CHECKS_OK: frame UV orientation, portrait UV, bounded frame/column/cell metadata, PNG predecode dimensions. No network requests.");
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
        }
    }
}
