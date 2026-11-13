using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraSpeakerProfilesChecks
    {
        [MenuItem("Sakura/Validation/Check recognised voice controls")]
        public static void Run()
        {
            Require(!SakuraBackend.ValidSpeakerName(null) && !SakuraBackend.ValidSpeakerName("  "), "blank names rejected");
            Require(SakuraBackend.ValidSpeakerName("  Owner  ") && SakuraBackend.ValidSpeakerName("गौरंग"), "trimmed and Unicode labels supported");
            Require(SakuraBackend.ValidSpeakerName(new string('a', 40)) && !SakuraBackend.ValidSpeakerName(new string('a', 41)), "40-character label limit");
            var full = new List<string> { "Owner", "A", "B", "C", "D" };
            Require(!SakuraSpeakerProfiles.CanEnrol("New", full, true, false), "sixth profile blocked");
            Require(SakuraSpeakerProfiles.CanEnrol(" Owner ", full, true, false), "existing profile can be re-recorded at limit");
            Require(!SakuraSpeakerProfiles.CanEnrol("Owner", full, false, false), "runtime required");
            Require(!SakuraSpeakerProfiles.CanEnrol("Owner", full, true, true), "installation blocks recording");
            Require(SakuraSpeakerProfiles.CanEnrol("New", new List<string>(), true, false), "ready runtime accepts first profile");
            Require(SakuraSpeakerProfiles.InstallProgress(new JObject { ["busy"] = false }) == "", "idle progress hidden");
            Require(SakuraSpeakerProfiles.InstallProgress(new JObject { ["busy"] = true }).Contains("Installing"), "older runtime progress supported");
            var progress = JObject.Parse("{\"busy\":true,\"progress\":{\"phase\":\"Downloading\",\"total_bytes\":10000000,\"file_downloaded_bytes\":5000000,\"bytes_per_second\":1000000}}");
            Require(SakuraSpeakerProfiles.InstallProgress(progress).Contains("Downloading") && SakuraSpeakerProfiles.InstallProgress(progress).Contains("MB/s"), "download phase and transfer speed shown");
            Require(SakuraBackend.ValidRvcName("My Voice") && SakuraBackend.ValidRvcName(new string('a', 48)), "voice folder normalization");
            Require(!SakuraBackend.ValidRvcName("../voice") && !SakuraBackend.ValidRvcName("a/b") && !SakuraBackend.ValidRvcName(new string('a', 49)), "voice folder confinement");
            CheckMultipartFixture();
            Debug.Log("PASS: recognised voice name validation, five-profile limit, re-enrolment, runtime readiness, install progress, RVC folder validation and streaming multipart fixture. No microphone or saved settings touched.");
        }

        private static void CheckMultipartFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "sakura-multipart-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string model = Path.Combine(root, "voice.pth"), index = Path.Combine(root, "voice.index");
            string multipart = null;
            try
            {
                File.WriteAllBytes(model, new byte[] { 0, 1, 2, 255 });
                File.WriteAllText(index, "index fixture", Encoding.ASCII);
                multipart = SakuraBackend.CreateRvcMultipartFile("My Voice", model, index, "fixtureBoundary");
                byte[] bytes = File.ReadAllBytes(multipart);
                string body = Encoding.UTF8.GetString(bytes);
                Require(body.StartsWith("--fixtureBoundary\r\n"), "opening multipart boundary");
                Require(body.Contains("name=\"character_name\"\r\n\r\nMy Voice\r\n"), "character-name form field");
                Require(body.Contains("name=\"pth_file\"; filename=\"voice.pth\""), "model file form field");
                Require(body.Contains("name=\"index_file\"; filename=\"voice.index\""), "optional index form field");
                Require(body.EndsWith("--fixtureBoundary--\r\n"), "closing multipart boundary");
                int binaryStart = body.IndexOf("Content-Type: application/octet-stream\r\n\r\n", StringComparison.Ordinal) + "Content-Type: application/octet-stream\r\n\r\n".Length;
                Require(bytes[binaryStart] == 0 && bytes[binaryStart + 3] == 255, "binary payload preserved");
                Require(File.ReadAllBytes(model)[3] == 255 && File.ReadAllText(index) == "index fixture", "source files retained");
                File.Delete(multipart); multipart = null;
                multipart = SakuraBackend.CreateRvcMultipartFile("My Voice", model, "", "fixtureBoundary");
                Require(!File.ReadAllText(multipart).Contains("name=\"index_file\""), "missing optional index omitted");
                bool invalid = false;
                try { SakuraBackend.CreateRvcMultipartFile("My Voice", model, "", "bad\r\nboundary"); }
                catch (ArgumentException) { invalid = true; }
                Require(invalid, "boundary injection rejected");
                Require(!SakuraBackend.ValidRvcFileLength(0) && SakuraBackend.ValidRvcFileLength(512L * 1024 * 1024) &&
                    !SakuraBackend.ValidRvcFileLength(512L * 1024 * 1024 + 1), "source size bounds");
            }
            finally
            {
                if (multipart != null) File.Delete(multipart);
                foreach (string file in Directory.GetFiles(root)) File.Delete(file);
                Directory.Delete(root);
            }
        }

        private static void Require(bool valid, string message)
        { if (!valid) throw new InvalidOperationException("Recognised voice check failed: " + message); }
    }
}
