using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    [InitializeOnLoad]
    public static class SakuraParityFixChecks
    {
        const string Pending = "sakura-parity-fixes-check";
        const string Folder = @"C:\AI\Unity-Verification\ui-parity-2026-11-16";
        static SakuraParityFixChecks()
        {
            EditorApplication.update += () =>
            {
                if (EditorApplication.isPlaying && SessionState.GetBool(Pending + "-running", false))
                { EditorApplication.isPaused = false; EditorApplication.QueuePlayerLoopUpdate(); }
            };
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                { SessionState.SetBool(Pending, false); Run(); }
            };
        }

        public static void RunBatch()
        {
            SessionState.SetBool(Pending, true);
            SakuraSetup.CreateStudio();
            EditorApplication.isPlaying = true;
        }

        static async void Run()
        {
            SessionState.SetBool(Pending + "-running", true); Application.runInBackground = true;
            var results = new JArray();
            RenderTexture target = null;
            string selected = PlayerPrefs.GetString("sakura-selected-model", "");
            async Task Check(string name, Func<Task> check)
            {
                try { await check(); results.Add(new JObject { ["check"] = name, ["status"] = "passed" }); }
                catch (Exception error) { results.Add(new JObject { ["check"] = name, ["status"] = "failed", ["error"] = error.ToString() }); Debug.LogException(error); }
                File.WriteAllText(Path.Combine(Folder, "runtime-checks.json"), results.ToString());
            }
            try
            {
                await Task.Delay(1000);
                var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
                if (studio == null) throw new Exception("Studio missing.");
                var document = studio.GetComponent<UIDocument>();
                target = new RenderTexture(1440, 900, 24); target.Create(); document.panelSettings.targetTexture = target;
                var viewport = studio.GetComponent<SakuraAvatarViewport>();
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (viewport.Loading && DateTime.UtcNow < deadline) await Task.Delay(100);
                foreach (string format in new[] { "PMX", "VRM" })
                {
                    await Check("converted-" + format.ToLowerInvariant() + "-import", async () =>
                    {
                        const string source = @"C:\AI\Unity-Verification\converter-2026-11-16";
                        string name = "Irida_to_" + format + (format == "VRM" ? "_unity" : "_final");
                        await viewport.LoadModel(source, "converted/" + name + "/" + name + "." + format.ToLowerInvariant());
                        if (!viewport.HasAvatar || viewport.LastImportError != null || viewport.LoadedFormat != format)
                            throw new Exception(viewport.LastImportError ?? "Converted model did not import.");
                        await Task.Delay(500);
                        var pixels = viewport.ReadAvatarPixels(192);
                        if (pixels.Count(pixel => pixel.a > 20) < 200) throw new Exception("Imported model has no meaningful rendered pixels.");
                        viewport.SetZoomPercent(155); await Task.Delay(500);
                        var framing = viewport.PluginPosition().ToString();
                        string image = await viewport.SaveTextureDiagnosticSnapshot("converted-" + format.ToLowerInvariant());
                        if (viewport.PluginPosition().ToString() != framing) throw new Exception("Diagnostic snapshot changed model framing.");
                        File.Copy(image, Path.Combine(Folder, "converted-" + format.ToLowerInvariant() + ".png"), true);
                        var report = SakuraTextureDiagnostics.Inspect(viewport.AvatarRoot);
                        if (report.Value<int>("checked") == 0 || ((JArray)report["issues"]).Count != 0)
                            throw new Exception("Converted texture images failed inspection: " + report);
                    });
                }
                await Check("pmx-material-controls", SakuraPmxChecks.Verify);
                await Check("model-settings-ui", SakuraUiParityChecks.VerifyModelSettings);
                await Check("agent-ui", SakuraAgentUiParityChecks.VerifyShell);
                await Check("compact-ui", SakuraUiParityChecks.VerifyCompactLayout);
                await Check("voice-encoding", () => { SakuraVoiceChecks.Run(); return Task.CompletedTask; });
            }
            catch (Exception error) { results.Add(new JObject { ["check"] = "startup", ["status"] = "failed", ["error"] = error.ToString() }); }
            finally
            {
                PlayerPrefs.SetString("sakura-selected-model", selected); PlayerPrefs.Save();
                SessionState.SetBool(Pending + "-running", false);
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, "runtime-checks.json"), results.ToString());
                Debug.Log("Parity fixes runtime checks: " + results);
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                EditorApplication.Exit(results.Any(result => result.Value<string>("status") == "failed") ? 1 : 0);
            }
        }
    }
}
