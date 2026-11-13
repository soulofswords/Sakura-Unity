using System;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraOutputChecks
    {
        public static void VerifyRenderQuality()
        {
            var output = UnityEngine.Object.FindFirstObjectByType<SakuraOutput>();
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            if (output == null || viewport == null || !Application.isPlaying) throw new Exception("Enter Studio Play mode first.");
            var previous = output.Preset;
            bool saved = PlayerPrefs.HasKey("sakura.output.quality");
            try
            {
                output.SetQuality(SakuraOutput.Quality.Low);
                if (Mathf.Max(viewport.OutputTexture.width, viewport.OutputTexture.height) > 960 || viewport.OutputTexture.antiAliasing != 1 || Application.targetFrameRate != 30) throw new Exception("Low graphics did not reduce render work.");
                output.SetQuality(SakuraOutput.Quality.Balanced);
                if (Mathf.Max(viewport.OutputTexture.width, viewport.OutputTexture.height) > 1280 || Application.targetFrameRate != 60) throw new Exception("Balanced graphics unexpectedly caps the interactive app at 30 FPS.");
                output.SetQuality(SakuraOutput.Quality.High);
                if (Mathf.Max(viewport.OutputTexture.width, viewport.OutputTexture.height) > 1920 || Application.targetFrameRate != 60) throw new Exception("High graphics did not retain its 60 FPS target.");
                Debug.Log("PASS: native render presets bound resolution/MSAA and retain interactive 60 FPS for Balanced/High.");
            }
            finally { output.SetQuality(previous); if (!saved) PlayerPrefs.DeleteKey("sakura.output.quality"); }
        }
        [MenuItem("Sakura/Validation/Check output frame boundaries")]
        public static void Run()
        {
            var translucent = new byte[] { 128, 0, 0, 128, 20, 30, 40, 0, 25, 50, 75, 255 };
            SakuraOutput.StraightAlpha(translucent, false);
            Require(translucent[0] == 255 && translucent[3] == 128 && translucent[4] == 0 && translucent[8] == 25, "straight-alpha export preserves soft edges and opaque pixels");
            var gamma = new byte[] { 137, 0, 0, 128 };
            SakuraOutput.StraightAlpha(gamma, true);
            Require(Math.Abs(gamma[0] - 188) <= 2 && gamma[3] == 128, "sRGB unpremultiplication runs in linear space");
            Require(SakuraOutput.Dimensions(1920, 1080, 1280) == new Vector2Int(1280, 720), "aspect-preserving downscale");
            Require(SakuraOutput.Dimensions(101, 201, 640) == new Vector2Int(100, 200), "even NDI dimensions");
            Require(SakuraOutput.Dimensions(1, 1, 640) == new Vector2Int(2, 2), "minimum dimensions");
            var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            SakuraOutput.FlipRows(pixels, 1, 2); Require(pixels[0] == 5 && pixels[4] == 1 && pixels[7] == 4, "RGBA row flip preserves alpha");
            SakuraOutput.FlipRows(pixels, 1, 2); Require(pixels[0] == 1 && pixels[7] == 8, "row flip reversible");
            Require(SakuraBackend.ValidOutputUpload("/api/ndi/frame?width=2&height=2", 16, "application/octet-stream", "x-ndi-session", "test"), "NDI frame contract");
            Require(!SakuraBackend.ValidOutputUpload("/api/ndi/frame?width=3&height=2", 24, "application/octet-stream", "x-ndi-session", "test"), "odd NDI width denied");
            Require(!SakuraBackend.ValidOutputUpload("/api/virtual-camera/frame?width=1282&height=2", 1282 * 8, "application/octet-stream", "x-camera-session", "test"), "virtual camera dimension cap");
            Require(!SakuraBackend.ValidOutputUpload("/api/spout/frame?width=2&height=2", 15, "application/octet-stream", "x-spout-session", "test"), "truncated frame denied");
            Require(!SakuraBackend.ValidOutputUpload("/api/spout/frame?width=2&height=2", 16, "application/octet-stream", "x-ndi-session", "test"), "cross-protocol session denied");
            Require(!SakuraBackend.ValidOutputUpload("/api/spout/frame?width=2&height=2&url=https://example.com", 16, "application/octet-stream", "x-spout-session", "test"), "additional route parameter denied");
            Require(!SakuraBackend.ValidOutputUpload("https://example.com/api/spout/frame?width=2&height=2", 16, "application/octet-stream", "x-spout-session", "test"), "remote route denied");
            Debug.Log("PASS: output scaling, alpha row orientation and exact authenticated frame upload contracts. Sender hardware and recording remain live checks.");
        }
        private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException("Output check failed: " + name); }
    }
}
