using System;
using System.IO;
using Sakura.Unity;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraExportWatchChecks
    {
        public static void Verify()
        {
            string root = Path.Combine(Path.GetTempPath(), "sakura-export-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string manifest = Path.Combine(root, "fixture.model3.json"), asset = Path.Combine(root, "fixture.moc3");
            try
            {
                File.WriteAllText(manifest, "{\"FileReferences\":{\"Moc\":\"fixture.moc3\"}}"); File.WriteAllText(asset, "one");
                string first = SakuraExportWatch.Fingerprint(manifest);
                File.WriteAllText(asset, "second");
                if (first == SakuraExportWatch.Fingerprint(manifest)) throw new Exception("Changed exports were not detected.");
                first = SakuraExportWatch.Fingerprint(manifest);
                File.WriteAllText(Path.Combine(root, "new.exp3.json"), "{}");
                if (first == SakuraExportWatch.Fingerprint(manifest)) throw new Exception("An added unreferenced expression was not detected.");
                File.WriteAllText(manifest, "{\"FileReferences\":{\"Moc\":\"../outside.moc3\"}}");
                bool refused = false;
                try { SakuraExportWatch.Fingerprint(manifest); } catch (ArgumentException) { refused = true; }
                if (!refused) throw new Exception("Export watcher followed an outside-package reference.");
                File.WriteAllText(manifest, "{\"FileReferences\":{\"Moc\":\"missing.moc3\"}}");
                refused = false; try { SakuraExportWatch.Fingerprint(manifest); } catch (FileNotFoundException) { refused = true; }
                if (!refused) throw new Exception("Incomplete exports were accepted.");
                Debug.Log("PASS: export metadata change detection, incomplete-package waiting and confined references.");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
