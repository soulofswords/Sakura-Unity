using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    [InitializeOnLoad]
    public static class SakuraParitySweep
    {
        const string Request = @"C:\AI\Unity-Verification\run-parity-sweep.request";
        const string Result = @"C:\AI\Unity-Verification\parity-sweep.json";
        static bool running;
        static bool browserOnly;
        static bool voiceOnly;
        static SakuraParitySweep() { EditorApplication.update += Tick; }

        [MenuItem("Sakura/Checks/Run desktop parity sweep")]
        public static void Start()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Request));
            File.WriteAllText(Request, "run");
        }

        static void Tick()
        {
            if (running && EditorApplication.isPlaying)
            {
                EditorApplication.isPaused = false;
                EditorApplication.QueuePlayerLoopUpdate();
                return;
            }
            if (running || EditorApplication.isCompiling || EditorApplication.isUpdating || !File.Exists(Request)) return;
            if (!EditorApplication.isPlaying)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    SakuraSetup.CreateStudio();
                    EditorApplication.isPaused = false;
                    EditorApplication.isPlaying = true;
                }
                return;
            }
            browserOnly = File.ReadAllText(Request).Trim() == "browser";
            voiceOnly = File.ReadAllText(Request).Trim() == "voice";
            running = true; File.Delete(Request); Run();
        }

        static async void Run()
        {
            var results = new JArray();
            UnityEngine.UIElements.PanelSettings panelSettings = null;
            RenderTexture previousTarget = null, desktopTarget = null;
            string previousModel = PlayerPrefs.GetString("sakura-selected-model", "");
            async Task Check(string name, Func<Task> action)
            {
                try { await action(); results.Add(new JObject { ["check"] = name, ["status"] = "passed" }); }
                catch (Exception error) { results.Add(new JObject { ["check"] = name, ["status"] = "failed", ["error"] = error.ToString() }); Debug.LogException(error); }
                File.WriteAllText(Result, results.ToString());
            }
            try
            {
                if (voiceOnly)
                {
                    await Check("voice-playback", VerifyVoicePlayback);
                    return;
                }
                if (browserOnly)
                {
                    await Task.Delay(1500);
                    await Check("embedded-browser", SakuraBrowserLiveChecks.Verify);
                    return;
                }
                var deadline = DateTime.UtcNow.AddSeconds(90);
                SakuraBackend backend;
                do
                {
                    await Task.Delay(250); backend = UnityEngine.Object.FindFirstObjectByType<SakuraBackend>();
                    if (backend != null && !backend.Connected) backend.Connect();
                }
                while ((backend == null || !backend.Connected) && DateTime.UtcNow < deadline);
                if (backend == null || !backend.Connected) throw new Exception("Backend did not connect within 90 seconds.");
                // The docked Editor Game view can be smaller than a supported app window.
                // Desktop checks use a fixed viewport; compact-layout checks supply their own.
                panelSettings = backend.GetComponent<UnityEngine.UIElements.UIDocument>().panelSettings;
                previousTarget = panelSettings.targetTexture;
                desktopTarget = new RenderTexture(1440, 900, 24);
                desktopTarget.Create(); panelSettings.targetTexture = desktopTarget;
                await Task.Delay(250);
                await Check("shift-jis-encoding", () => { SakuraEncodingChecks.Verify(); return Task.CompletedTask; });
                await Check("desktop-shell", SakuraUiParityChecks.VerifyShell);
                await Check("compact-layout", SakuraUiParityChecks.VerifyCompactLayout);
                await Check("model-settings", SakuraUiParityChecks.VerifyModelSettings);
                await Check("agent-shell", SakuraAgentUiParityChecks.VerifyShell);
                await Check("guide-navigation", SakuraSetupGuideChecks.VerifyNavigation);
                await Check("rig-monitor", () => { SakuraRigMonitorChecks.Validate(); return Task.CompletedTask; });
                await Check("native-origin-route", VerifyNativeOriginRoute);
                await Check("phone-transfer", () => { SakuraTrackingChecks.Verify(); return Task.CompletedTask; });
                await Check("collab-boundaries", () => { SakuraNativeCollabChecks.Verify(); return Task.CompletedTask; });
                await Check("stage-profile", () => { SakuraStageItemChecks.Validate(); return Task.CompletedTask; });
                await Check("vmc-packets", () => { SakuraVmcChecks.Verify(); return Task.CompletedTask; });
                await Check("embedded-browser", SakuraBrowserLiveChecks.Verify);
            }
            catch (Exception error) { results.Add(new JObject { ["check"] = "startup", ["status"] = "failed", ["error"] = error.ToString() }); }
            finally
            {
                if (panelSettings != null) panelSettings.targetTexture = previousTarget;
                if (desktopTarget != null) { desktopTarget.Release(); UnityEngine.Object.Destroy(desktopTarget); }
                File.WriteAllText(Result, results.ToString());
                PlayerPrefs.SetString("sakura-selected-model", previousModel); PlayerPrefs.Save();
                Debug.Log("Desktop parity sweep finished: " + Result);
                running = false;
            }
        }

        static async Task VerifyNativeOriginRoute()
        {
            var backend = UnityEngine.Object.FindFirstObjectByType<SakuraBackend>();
            if (backend == null || !backend.Connected) throw new Exception("Authenticated Unity backend is unavailable.");
            var completion = new TaskCompletionSource<string>();
            backend.Api("/api/mobile/link", null, (_, error) => completion.TrySetResult(error));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (finished != completion.Task) throw new TimeoutException("Unity's read-only phone-link route did not respond.");
            string error = await completion.Task;
            if (!string.IsNullOrEmpty(error)) throw new Exception("Unity's native request was rejected by the local-origin guard: " + error);
        }

        static async Task VerifyVoicePlayback()
        {
            var backend = UnityEngine.Object.FindFirstObjectByType<SakuraBackend>();
            var voice = UnityEngine.Object.FindFirstObjectByType<SakuraVoice>();
            if (backend == null || voice == null) throw new Exception("Companion voice components missing.");
            backend.SetMode("Companion");
            async Task<string> Call(string route, string body)
            {
                var done = new TaskCompletionSource<string>();
                backend.Api(route, body, (json, error) => { if (error != null) done.TrySetException(new Exception(error)); else done.TrySetResult(json); });
                if (await Task.WhenAny(done.Task, Task.Delay(120000)) != done.Task) throw new TimeoutException(route);
                return await done.Task;
            }
            await Call("/api/unity/start-services", "{}");
            var reply = JObject.Parse(await Call("/api/tts/speak", "{\"text\":\"Sakura voice playback check.\"}"));
            string path = reply.Value<string>("audio");
            if (!SakuraBackend.ValidAudioPath(path)) throw new Exception("TTS returned no valid audio path: " + reply);
            voice.PlayCohost(path);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!voice.Speaking && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!voice.Speaking) throw new Exception("Unity did not start speech playback: " + voice.Status);
            if (voice.SpeechSource.clip == null || voice.SpeechSource.clip.length <= 0) throw new Exception("Speech clip is empty.");
            if (!UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Any(listener => listener.enabled && listener.gameObject.activeInHierarchy))
                throw new Exception("No enabled AudioListener for voice playback.");
            Debug.Log("PASS: live TTS route, Unity audio download, nonempty clip and AudioSource playback. Physical audibility requires listening.");
        }
    }
}
