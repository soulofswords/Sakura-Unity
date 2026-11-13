using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraScreenFeaturesChecks
    {
        [MenuItem("Sakura/Validation/Check screen frame gates")]
        public static void Run()
        {
            Require(!SakuraScreenFeatures.CaptureReady(false, true, false, false), "capture waits for backend opt-in acknowledgement");
            Require(!SakuraScreenFeatures.CaptureReady(false, true, true, true), "capture waits for an older in-flight snapshot");
            Require(SakuraScreenFeatures.CaptureReady(false, true, true, false), "new capture resumes after a discarded older snapshot");
            Require(!SakuraScreenFeatures.CaptureReady(false, false, false, false), "stopped capture stays off");
            Require(!SakuraScreenFeatures.ValidScreenImage(null) && !SakuraScreenFeatures.ValidScreenImage(""), "missing frames rejected");
            Require(!SakuraScreenFeatures.ValidScreenImage("https://example.com/image.jpg"), "remote image paths rejected");
            Require(!SakuraScreenFeatures.ValidScreenImage("/9j/invalid!"), "invalid base64 rejected");
            Require(!SakuraScreenFeatures.ValidScreenImage(Convert.ToBase64String(new byte[] { 255, 216, 255, 0 })), "incomplete JPEG rejected");
            Require(SakuraScreenFeatures.ValidScreenImage(Convert.ToBase64String(new byte[] { 255, 216, 255, 0, 255, 217 })), "JPEG envelope accepted");
            Require(SakuraScreenFeatures.FreshFrame(10, 10) && SakuraScreenFeatures.FreshFrame(10, 8), "recent frames accepted");
            Require(!SakuraScreenFeatures.FreshFrame(10, 7.5f) && !SakuraScreenFeatures.FreshFrame(10, 11), "stale and future frames rejected");
            CheckVramGuard();
            Debug.Log("PASS: screen frame gates, game VRAM guard clocks, cancellation, adapter selection and cloud fallback thresholds. No screen captured, service enabled or shutdown requested.");
        }
        private static JObject Game(double percent = 98, double freeMB = 128, string name = "NVIDIA RTX Test") => new JObject
        {
            ["detected"] = true, ["gpus"] = new JArray(new JObject { ["name"] = name, ["vram_total_bytes"] = 8d * 1024 * 1024 * 1024,
                ["vram_used_bytes"] = 8d * 1024 * 1024 * 1024 - freeMB * 1024 * 1024, ["vram_percent"] = percent })
        };
        private static void CheckVramGuard()
        {
            var guard = new SakuraGameVramGuard(); var game = Game(); const string renderer = "NVIDIA RTX Test/PCIe";
            Require(guard.Sample(game, true, renderer, 0) == null && guard.Sample(game, true, renderer, 4) == null, "sustained pressure delay");
            var warning = guard.Sample(game, true, renderer, 8);
            Require(warning != null && warning.Seconds == 10 && warning.FreeMB == 128 && !warning.Close, "warning after eight seconds");
            Require(guard.Sample(game, true, renderer, 13).Seconds == 5, "countdown clock");
            Require(guard.Sample(game, true, renderer, 18).Close, "close only after ten-second warning");
            Require(guard.Sample(game, true, renderer, 20) == null, "one close request per pressure episode");
            guard.Reset(); guard.Sample(game, true, renderer, 0); guard.Sample(game, true, renderer, 4); guard.Sample(game, true, renderer, 8); guard.Cancel();
            Require(guard.Sample(game, true, renderer, 12) == null, "user cancellation suppresses close");
            Require(guard.Sample(Game(80, 2000), true, renderer, 13) == null && guard.ClearWarning, "recovery clears warning and cancellation");
            guard.Sample(game, true, renderer, 14); guard.Sample(game, true, renderer, 18);
            Require(guard.Sample(game, true, renderer, 25) == null, "sample gap over six seconds resets clock");
            Require(guard.Sample(game, true, "Different adapter", 26) == null && guard.ClearWarning, "renderer adapter must match");
            Require(guard.Sample(Game(96, 128), true, renderer, 27) == null, "97-percent threshold");
            Require(guard.Sample(Game(98, 256), true, renderer, 28) == null, "free-memory threshold is strict");
            Require(guard.Sample(game, false, renderer, 29) == null, "saved disabled safeguard respected");
            var ambiguous = Game(); ((JArray)ambiguous["gpus"]).Add(((JArray)ambiguous["gpus"])[0].DeepClone());
            Require(guard.Sample(ambiguous, true, renderer, 30) == null, "ambiguous adapters rejected");
            var invalid = Game(); invalid["gpus"][0]["vram_percent"] = "98";
            Require(guard.Sample(invalid, true, renderer, 31) == null, "string GPU measurements rejected");
            Require(SakuraGameVramGuard.CloudFallbackChange(Game(90, 2000), false) == true, "cloud fallback high-percent threshold");
            Require(SakuraGameVramGuard.CloudFallbackChange(Game(85, 900), false) == true, "cloud fallback low-free-memory threshold");
            Require(SakuraGameVramGuard.CloudFallbackChange(Game(82, 1536), true) == false, "cloud recovery thresholds");
            Require(SakuraGameVramGuard.CloudFallbackChange(Game(86, 1300), true) == null, "fallback hysteresis prevents churn");
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Screen feature check failed: " + message); }
    }
}
