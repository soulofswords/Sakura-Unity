using System;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraOutputChecks
    {
        [MenuItem("Sakura/Validation/Check output frame boundaries")]
        public static void Run()
        {
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
