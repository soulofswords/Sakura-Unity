using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraMobileChecks
    {
        public static void Run()
        {
            if (SakuraMobileCore.SpeechText("Hi 😊 [QUIET] [source](https://example.com/a) → hello") != "Hi source hello")
                throw new Exception("Speech markup leaked");
            if (SakuraMobileCore.FriendlyError(429, "quota") != "Your provider quota or rate limit has been reached. Please check your API account.")
                throw new Exception("Quota message lost");
            string root = Path.Combine(Path.GetTempPath(), "sakura-mobile-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string archive = Path.Combine(root, "unsafe.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("../escaped.vrm").Open())) writer.Write("unsafe");
                bool refused = false;
                try { SakuraMobileCore.ImportModel(archive, root); } catch (InvalidDataException) { refused = true; }
                if (!refused || File.Exists(Path.Combine(root, "escaped.vrm"))) throw new Exception("Archive escaped model boundary");
                string model = Path.Combine(root, "pink.vrm"); File.WriteAllBytes(model, new byte[] { 1, 2, 3 });
                string first = SakuraMobileCore.ImportModel(model, root);
                string second = SakuraMobileCore.ImportModel(model, root);
                if (first != second || !File.Exists(Path.Combine(root,"models",first))) throw new Exception("Duplicate import replaced data");
                if (SakuraMobileCore.FindModels(root).Length != 1) throw new Exception("Saved model discovery failed");
                SakuraMobileCore.DeleteModel(first, root);
                if (SakuraMobileCore.FindModels(root).Length != 0) throw new Exception("Model deletion failed");
                Debug.Log("PASS: mobile speech cleanup, quota errors, archive confinement, duplicate import and model deletion.");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
