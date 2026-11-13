#if SAKURA_PREVIEW_VERIFY && UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraPlayerVerification : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Environment.GetCommandLineArgs().Contains("--sakura-verify")) new GameObject("Owned preview verification").AddComponent<SakuraPlayerVerification>();
        }
        IEnumerator Start()
        {
            string folder = @"C:\AI\Unity-Verification";
            Directory.CreateDirectory(folder);
            var report = new JObject { ["edition"] = SakuraInstances.IsVtuber ? "vtuber" : "companion", ["instance"] = SakuraInstances.Index };
            float deadline = Time.realtimeSinceStartup + 40;
            SakuraStudio studio = null;
            while ((studio = FindFirstObjectByType<SakuraStudio>()) == null && Time.realtimeSinceStartup < deadline) yield return null;
            yield return null;
            if (studio == null) { report["error"] = "Studio did not start."; Finish(report, folder); yield break; }
            var backend = studio.GetComponent<SakuraBackend>(); var viewport = studio.GetComponent<SakuraAvatarViewport>(); var window = studio.GetComponent<SakuraWindow>();
            while ((!backend.Connected || viewport.Loading || !SakuraInstances.IsSecondary && !viewport.HasAvatar) && Time.realtimeSinceStartup < deadline) yield return null;
            report["connected"] = backend.Connected; report["mode"] = studio.CurrentMode;
            report["catalogCount"] = studio.CharacterCount;
            if (!SakuraInstances.IsSecondary && !viewport.HasAvatar && backend.Connected)
            {
                var load = viewport.LoadModel(backend.SourceFolder, "pmx/Ringo/Ringo.pmx");
                while (!load.IsCompleted) yield return null;
                report["fixtureImportError"] = viewport.LastImportError;
            }
            report["avatarLoaded"] = viewport.HasAvatar; report["microphoneOff"] = !studio.GetComponent<SakuraVoice>().Listening;
            if (SakuraInstances.IsVtuber)
            {
                studio.SwitchMode("Companion"); report["standaloneModeIsolated"] = studio.CurrentMode == "VTuber";
                report["freshSecondary"] = !SakuraInstances.IsSecondary || !viewport.HasAvatar && studio.GetComponent<SakuraStage>().Settings.items.Count == 0;
            }
            else
            {
                Screen.SetResolution(460, 740, FullScreenMode.Windowed); yield return new WaitForSecondsRealtime(1);
                studio.SwitchMode("Companion"); window.SetTransparent(true); yield return new WaitForEndOfFrame(); yield return null;
                report["transparent"] = window.NativeWindowState();
                var root = studio.GetComponent<UIDocument>().rootVisualElement;
                report["transparentUiAlpha"] = root.resolvedStyle.backgroundColor.a;
                report["compactMicrophone"] = root.Q("compact-microphone")?.resolvedStyle.display == DisplayStyle.Flex;
                var before = window.NativeWindowState(); studio.SwitchMode("Agent"); yield return new WaitForSecondsRealtime(.5f);
                report["agentOpaque"] = !window.Transparent && root.resolvedStyle.backgroundColor.a > .99f;
                studio.SwitchMode("Companion"); yield return new WaitForSecondsRealtime(.5f);
                report["transparentRestored"] = window.Transparent;
                var restored = window.NativeWindowState();
                report["compactGeometryRestored"] = before["bounds"]?["width"]?.Value<int>() == restored["bounds"]?["width"]?.Value<int>() && before["bounds"]?["height"]?.Value<int>() == restored["bounds"]?["height"]?.Value<int>();
                window.SetTransparent(false); yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "built-companion-compact.png")); yield return new WaitForSecondsRealtime(.2f);
            }
            Finish(report, folder);
            if (!Environment.GetCommandLineArgs().Contains("--sakura-verify-hold")) Application.Quit();
        }
        static void Finish(JObject report, string folder)
        {
            string file = Path.Combine(folder, "built-" + report.Value<string>("edition") + "-" + SakuraInstances.Index + ".json");
            File.WriteAllText(file, report.ToString()); Debug.Log("Owned player verification written to " + file);
        }
    }
}
#endif
