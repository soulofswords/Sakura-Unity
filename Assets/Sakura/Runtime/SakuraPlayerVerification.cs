#if SAKURA_PREVIEW_VERIFY && UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
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
            else if (Environment.GetCommandLineArgs().Contains("--sakura-frame-sample"))
                new GameObject("Frame timing sampler").AddComponent<SakuraPlayerVerification>();
        }
        IEnumerator Start()
        {
            string folder = @"C:\AI\Unity-Verification";
            Directory.CreateDirectory(folder);
            if (Environment.GetCommandLineArgs().Contains("--sakura-frame-sample"))
            {
                yield return new WaitForSecondsRealtime(25);
                var frames = new System.Collections.Generic.List<float>(1500);
                float until = Time.realtimeSinceStartup + 15;
                while (Time.realtimeSinceStartup < until)
                {
                    yield return null;
                    if (Application.isFocused) frames.Add(Time.unscaledDeltaTime * 1000);
                }
                frames.Sort();
                var sampledAvatar = FindFirstObjectByType<SakuraAvatarViewport>();
                var summary = new JObject {
                    ["frames"] = frames.Count, ["focusedSeconds"] = frames.Sum() / 1000,
                    ["width"] = Screen.width, ["height"] = Screen.height,
                    ["modelFormat"] = sampledAvatar?.LoadedFormat, ["avatarLoaded"] = sampledAvatar?.HasAvatar == true,
                    ["targetFps"] = Application.targetFrameRate
                };
                if (frames.Count > 0)
                {
                    summary["fps"] = 1000 / frames.Average();
                    summary["medianMs"] = frames[frames.Count / 2];
                    summary["p95Ms"] = frames[Mathf.CeilToInt(frames.Count * .95f) - 1];
                    summary["over33Ms"] = frames.Count(frame => frame > 33.33f);
                }
                File.WriteAllText(Path.Combine(folder, "parity-frame-sample-" + (SakuraInstances.IsVtuber ? "vtuber" : "companion") + ".json"), summary.ToString());
                yield break;
            }
            var report = new JObject { ["edition"] = SakuraInstances.IsVtuber ? "vtuber" : "companion", ["instance"] = SakuraInstances.Index };
            float deadline = Time.realtimeSinceStartup + 40;
            SakuraStudio studio = null;
            while ((studio = FindFirstObjectByType<SakuraStudio>()) == null && Time.realtimeSinceStartup < deadline) yield return null;
            yield return null;
            if (studio == null) { report["error"] = "Studio did not start."; Finish(report, folder); yield break; }
            Screen.SetResolution(1440, 900, FullScreenMode.Windowed);
            yield return new WaitForSecondsRealtime(0.4f);
            report["splashPresent"] = studio.GetComponent<UIDocument>().rootVisualElement.Q("startup-splash") != null;
            yield return Shot(folder, "match-splash.png");
            var backend = studio.GetComponent<SakuraBackend>(); var viewport = studio.GetComponent<SakuraAvatarViewport>(); var window = studio.GetComponent<SakuraWindow>();
            while ((!backend.Connected || viewport.Loading || !SakuraInstances.IsSecondary && !viewport.HasAvatar) && Time.realtimeSinceStartup < deadline) yield return null;
            report["connected"] = backend.Connected; report["mode"] = studio.CurrentMode;
            report["catalogCount"] = studio.CharacterCount;
            if (!SakuraInstances.IsSecondary && !viewport.HasAvatar && backend.Connected)
            {
                // Give the normal startup model load a moment before substituting the test fixture.
                yield return new WaitForSecondsRealtime(5);
            }
            if (!SakuraInstances.IsSecondary && !viewport.HasAvatar && backend.Connected)
            {
                var live2d = viewport.LoadModel(backend.SourceFolder, "live2d/Amika/AmikaV4.model3.json");
                while (!live2d.IsCompleted) yield return null;
                report["live2dImportError"] = viewport.LastImportError;
                yield return new WaitForSecondsRealtime(1);
            }
            if (!SakuraInstances.IsSecondary && !viewport.HasAvatar && backend.Connected)
            {
                var load = viewport.LoadModel(backend.SourceFolder, "pmx/Ringo/Ringo.pmx");
                while (!load.IsCompleted) yield return null;
                report["fixtureImportError"] = viewport.LastImportError;
            }
            if (!SakuraInstances.IsSecondary && backend.Connected)
            {
                // VRM shaders are only reached through Shader.Find at runtime, so a build that
                // strips them renders nothing; count real pixels instead of trusting the import.
                var vrm = viewport.LoadModel(backend.SourceFolder, "vrm/ArisuMaid.vrm");
                while (!vrm.IsCompleted) yield return null;
                report["vrmImportError"] = viewport.LastImportError;
                yield return new WaitForSecondsRealtime(1);
                if (viewport.HasAvatar && viewport.LoadedFormat == "VRM")
                {
                    report["vrmVisiblePixels"] = viewport.ReadAvatarPixels(256).Count(pixel => pixel.a > 32);
                    report["vrmShaders"] = new JArray(viewport.AvatarRoot.GetComponentsInChildren<Renderer>()
                        .SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null)
                        .Select(material => material.shader == null ? "null" : material.shader.name).Distinct());
                    yield return Shot(folder, "match-vrm.png");
                }
            }
            report["avatarLoaded"] = viewport.HasAvatar; report["microphoneOff"] = !studio.GetComponent<SakuraVoice>().Listening;
            if (viewport.HasAvatar)
            {
                var greeting = SakuraHandRaising.Play(viewport.AvatarRoot, studio.GetComponent<SakuraAvatarActions>());
                report["greetingPlayed"] = greeting;
                yield return new WaitForSecondsRealtime(1.2f);
                yield return Shot(folder, "match-greeting.png");
                yield return new WaitForSecondsRealtime(3.2f);
                var danceActions = studio.GetComponent<SakuraAvatarActions>();
                if (danceActions != null && danceActions.Gesture("disco"))
                {
                    yield return new WaitForSecondsRealtime(1.3f);
                    yield return Shot(folder, "match-dance.png");
                    danceActions.Stop();
                    yield return new WaitForSecondsRealtime(.4f);
                }
            }
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
            if (!SakuraInstances.IsVtuber)
            {
                Screen.SetResolution(1440, 900, FullScreenMode.Windowed);
                window.SetTransparent(false); studio.SwitchMode("Companion");
                yield return new WaitForSecondsRealtime(1f);
                var uiRoot = studio.GetComponent<UIDocument>().rootVisualElement;
                var startupVoiceModal = typeof(SakuraStudio).GetField("voiceModal", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(studio) as VisualElement;
                if (startupVoiceModal != null) startupVoiceModal.style.display = DisplayStyle.None;
                yield return new WaitForSecondsRealtime(.2f);
                yield return Shot(folder, "match-chat.png");
                studio.ShowPanel("Character"); yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-models.png");
                studio.ShowPanel("Settings"); yield return new WaitForSecondsRealtime(1f); yield return Shot(folder, "match-settings.png");
                var modelsTab = uiRoot.Q(className: "settings-tabs").Query<Button>().ToList().Find(b => (string)b.userData == "Models");
                if (modelsTab != null)
                {
                    var field = typeof(Button).GetField("m_Clickable", BindingFlags.NonPublic | BindingFlags.Instance);
                    var clickable = field?.GetValue(modelsTab);
                    var invoke = clickable?.GetType().GetMethod("Invoke", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(EventBase) }, null);
                    invoke?.Invoke(clickable, new object[] { new ClickEvent() });
                    yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-settings-models.png");
                    report["settingsModelsTab"] = invoke != null;
                }
                studio.ShowPanel("Voice & speech"); yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-voice.png");
                studio.ShowPanel("Camera"); yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-camera.png");
                studio.ShowPanel("Background"); yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-background.png");
                studio.ShowPanel("Model Settings"); yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-model-settings.png");
                studio.ShowPanel("Memory & privacy"); yield return new WaitForSecondsRealtime(.8f); yield return Shot(folder, "match-memory.png");
                var voiceModal = typeof(SakuraStudio).GetField("voiceModal", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(studio) as VisualElement;
                if (voiceModal != null)
                {
                    if (typeof(SakuraStudio).GetField("voiceMessage", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(studio) is Label msg)
                        msg.text = "Downloading local Kokoro speech files… 42%";
                    voiceModal.style.display = DisplayStyle.Flex;
                    yield return new WaitForSecondsRealtime(.3f);
                    yield return Shot(folder, "match-voicemodal.png");
                    voiceModal.style.display = DisplayStyle.None;
                    report["voiceModalForced"] = true;
                }
                report["panelsCaptured"] = true;
            }
            if (!SakuraInstances.IsVtuber && Environment.GetCommandLineArgs().Contains("--sakura-verify-voice"))
                yield return VerifyVoice(backend, studio.GetComponent<SakuraVoice>(), report);
            Finish(report, folder);
            if (!Environment.GetCommandLineArgs().Contains("--sakura-verify-hold")) Application.Quit();
        }
        static IEnumerator VerifyVoice(SakuraBackend backend, SakuraVoice voice, JObject report)
        {
            string response = null, failure = null; bool done = false;
            backend.Api("/api/unity/start-services", "{}", (json, error) => { failure = error; done = true; });
            float deadline = Time.realtimeSinceStartup + 120;
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (!done || failure != null) { report["voiceError"] = failure ?? "Voice services timed out."; yield break; }
            done = false;
            backend.Api("/api/tts/speak", "{\"text\":\"Sakura voice playback check.\"}", (json, error) => { response = json; failure = error; done = true; });
            deadline = Time.realtimeSinceStartup + 120;
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (!done || failure != null) { report["voiceError"] = failure ?? "TTS timed out."; yield break; }
            string audio = null;
            try { audio = JObject.Parse(response).Value<string>("audio"); }
            catch (Exception) { failure = "TTS response was not JSON."; }
            if (!SakuraBackend.ValidAudioPath(audio)) { report["voiceError"] = failure ?? "TTS returned no local audio."; yield break; }
            report["ttsGeneratedAudio"] = true;
            voice.PlayCohost(audio);
            deadline = Time.realtimeSinceStartup + 15;
            while (!voice.Speaking && Time.realtimeSinceStartup < deadline) yield return null;
            report["voicePlaybackStarted"] = voice.Speaking;
            report["voiceListenerActive"] = FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Any(item => item.isActiveAndEnabled);
            if (!voice.Speaking) { report["voiceError"] = voice.Status; yield break; }
            report["voiceClipSeconds"] = voice.SpeechSource.clip.length;
            while (voice.Speaking && Time.realtimeSinceStartup < deadline) yield return null;
        }
        static IEnumerator Shot(string folder, string name)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name));
            yield return new WaitForSecondsRealtime(0.3f);
        }
        static void Finish(JObject report, string folder)
        {
            string file = Path.Combine(folder, "built-" + report.Value<string>("edition") + "-" + SakuraInstances.Index + ".json");
            File.WriteAllText(file, report.ToString()); Debug.Log("Owned player verification written to " + file);
        }
    }
}
#endif
