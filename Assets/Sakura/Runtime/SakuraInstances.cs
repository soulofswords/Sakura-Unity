using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Sakura.Unity
{
    public static class SakuraInstances
    {
        static Mutex slot;
        public static int Index { get; private set; } = 1;
        public static string SessionProfile { get; private set; }
        public static bool IsSecondary => Index > 1;
        public static bool IsVtuber => Resources.Load<TextAsset>("Sakura/Edition")?.text.Trim() == "vtuber"
            || Environment.GetCommandLineArgs().Contains("--sakura-vtuber");

        public static Mutex Claim(string identity, out int index)
        {
            for (index = 1; index <= 3; index++)
            {
                var candidate = new Mutex(false, @"Local\SakuraUnityVTuber_" + identity + "_" + index, out bool created);
                if (created) return candidate;
                candidate.Dispose();
            }
            throw new InvalidOperationException("Three Sakura VTuber windows are already open. Close one before opening another.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            Index = 1;
            if (Application.isEditor || Application.platform != RuntimePlatform.WindowsPlayer || !IsVtuber) return;
            try
            {
                string identity;
                using (var digest = SHA256.Create())
                    identity = BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Application.dataPath).ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
                slot = Claim(identity, out int index); Index = index;
                if (IsSecondary)
                {
                    SessionProfile = Path.Combine(Path.GetTempPath(), "sakura-unity-session-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(SessionProfile);
                }
                Application.quitting += Release;
            }
            catch (Exception error) { UnityEngine.Debug.LogError(error.Message); Application.Quit(1); }
        }

        public static string Open()
        {
            if (Application.isEditor) return "New windows are available in the built Windows app.";
            try
            {
                string executable = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo(executable, "--sakura-vtuber") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) });
                return "Opening a fresh VTuber window. Up to three can run together.";
            }
            catch (Exception error) { return "Could not open another window: " + error.Message; }
        }

        static void Release()
        {
            slot?.Dispose(); slot = null;
            if (SessionProfile == null) return;
            string owned = Path.GetFullPath(SessionProfile);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (owned.StartsWith(temp + "sakura-unity-session-", StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(owned, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            SessionProfile = null;
        }
    }
}
