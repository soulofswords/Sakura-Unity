using System;
using System.Collections.Generic;
using System.IO;
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
            running = true; File.Delete(Request); Run();
        }

        static async void Run()
        {
            var results = new JArray();
            string previousModel = PlayerPrefs.GetString("sakura-selected-model", "");
            async Task Check(string name, Func<Task> action)
            {
                try { await action(); results.Add(new JObject { ["check"] = name, ["status"] = "passed" }); }
                catch (Exception error) { results.Add(new JObject { ["check"] = name, ["status"] = "failed", ["error"] = error.ToString() }); Debug.LogException(error); }
                File.WriteAllText(Result, results.ToString());
            }
            try
            {
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
                await Check("desktop-shell", SakuraUiParityChecks.VerifyShell);
                await Check("compact-layout", SakuraUiParityChecks.VerifyCompactLayout);
                await Check("model-settings", SakuraUiParityChecks.VerifyModelSettings);
                await Check("agent-shell", SakuraAgentUiParityChecks.VerifyShell);
                await Check("guide-navigation", SakuraSetupGuideChecks.VerifyNavigation);
                await Check("rig-monitor", () => { SakuraRigMonitorChecks.Validate(); return Task.CompletedTask; });
                await Check("collab-boundaries", () => { SakuraNativeCollabChecks.Verify(); return Task.CompletedTask; });
                await Check("stage-profile", () => { SakuraStageItemChecks.Validate(); return Task.CompletedTask; });
                await Check("vmc-packets", () => { SakuraVmcChecks.Verify(); return Task.CompletedTask; });
                await Check("embedded-browser", SakuraBrowserLiveChecks.Verify);
            }
            catch (Exception error) { results.Add(new JObject { ["check"] = "startup", ["status"] = "failed", ["error"] = error.ToString() }); }
            finally
            {
                File.WriteAllText(Result, results.ToString());
                PlayerPrefs.SetString("sakura-selected-model", previousModel); PlayerPrefs.Save();
                Debug.Log("Desktop parity sweep finished: " + Result);
                running = false;
            }
        }
    }
}
