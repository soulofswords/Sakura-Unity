using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    [Serializable] public class CharacterEntry { public string name; public string path; public string format; }
    [Serializable] public class CharacterCatalog { public CharacterEntry[] items; public string error; }
    [Serializable] internal class BridgeIdentity { public int port; public string token; }
    [Serializable] internal class ChatRequest { public string text; }
    [Serializable] internal class ChatResponse { public string reply; public string error; }
    [Serializable] internal class ModelImportReply { public string[] models; public string error; public bool cancelled; }
    [Serializable] public class ApiReply { public bool ok; public bool configured; public bool confirmation_required; public string error; public string message; public string url; }

    public sealed class SakuraBackend : MonoBehaviour
    {
        public string SourceFolder = @"F:\AI\assistant\Sakura";
        public event Action<CharacterEntry[]> CatalogReceived;
        public event Action<string> StatusChanged;
        public event Action<string> ReplyReceived;
        public event Action<string> ModelImported;
        public bool Connected { get; private set; }
        public bool Busy { get; private set; }
        private Process server;
        private Process catalogProcess;
        private Process importProcess;
        private Process modelToolProcess;
        private BridgeIdentity identity;
        private readonly ConcurrentQueue<Action> pending = new ConcurrentQueue<Action>();

        private void Update()
        {
            while (pending.TryDequeue(out var action)) action();
        }

        private ProcessStartInfo Command(bool serve)
        {
            string python = Path.Combine(SourceFolder, "venv", "Scripts", "python.exe");
            if (!File.Exists(python)) throw new FileNotFoundException("Choose a Sakura folder with its Python environment.");
            string bridge = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "backend_bridge.py"));
            if (!File.Exists(bridge)) throw new FileNotFoundException("Development backend bridge is missing.");
            if (SourceFolder.Contains("\"") || bridge.Contains("\"")) throw new ArgumentException("Invalid source path.");
            return new ProcessStartInfo(python, "-u \"" + bridge + "\" --source \"" + SourceFolder + "\"" + (serve ? " --serve" : ""))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = SourceFolder
            };
        }

        public void RefreshCatalog()
        {
            if (catalogProcess != null) return;
            try
            {
                StatusChanged?.Invoke("Reading your existing model library…");
                var process = new Process { StartInfo = Command(false), EnableRaisingEvents = true };
                catalogProcess = process;
                process.OutputDataReceived += (_, args) =>
                {
                    if (string.IsNullOrWhiteSpace(args.Data)) return;
                    string json = args.Data;
                    pending.Enqueue(() =>
                    {
                        try
                        {
                            var catalog = JsonUtility.FromJson<CharacterCatalog>(json);
                            if (!string.IsNullOrEmpty(catalog.error)) throw new IOException(catalog.error);
                            CatalogReceived?.Invoke(catalog.items ?? Array.Empty<CharacterEntry>());
                            StatusChanged?.Invoke("Talk to Sakura…");
                        }
                        catch (Exception error) { StatusChanged?.Invoke(error.Message); }
                    });
                };
                process.ErrorDataReceived += (_, args) => { };
                process.Exited += (_, args) => pending.Enqueue(() => { catalogProcess?.Dispose(); catalogProcess = null; });
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch (Exception error) { catalogProcess?.Dispose(); catalogProcess = null; StatusChanged?.Invoke(error.Message); }
        }

        public void RunPmxTool(string tool, string model, Action<string, string> completed, float[] direction = null)
        {
            if (tool != "editor" && tool != "mmd" && tool != "reference" && tool != "texture") throw new ArgumentException("Invalid PMX tool.");
            if (modelToolProcess != null) { completed(null, "Finish the open PMX tool first."); return; }
            try
            {
                var command = Command(false); command.Arguments += " --pmx-tool " + tool;
                var process = new Process { StartInfo = command, EnableRaisingEvents = true };
                modelToolProcess = process;
                string response = null;
                process.OutputDataReceived += (_, args) => { if (args.Data != null && args.Data.StartsWith("{", StringComparison.Ordinal)) response = args.Data; };
                process.ErrorDataReceived += (_, args) => { };
                process.Exited += (_, args) =>
                {
                    process.WaitForExit();
                    pending.Enqueue(() =>
                    {
                        modelToolProcess = null;
                        try
                        {
                            if (response == null) completed(null, "The PMX tool exited without a result.");
                            else
                            {
                                var result = JsonUtility.FromJson<ApiReply>(response);
                                completed(string.IsNullOrEmpty(result.error) ? response : null, result.error);
                            }
                        }
                        finally { process.Dispose(); }
                    });
                };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                process.StandardInput.WriteLine(JsonUtility.ToJson(new PmxToolRequest { model = model, direction = direction ?? new[] { 0f, 0f, 1f } }));
                process.StandardInput.Close();
            }
            catch (Exception error) { modelToolProcess?.Dispose(); modelToolProcess = null; completed(null, error.Message); }
        }

        [Serializable] private class PmxToolRequest { public string model; public float[] direction; }

        public void ImportModel(string format)
        {
            format = format.ToLowerInvariant();
            if (format != "pmx" && format != "vrm" && format != "live2d") throw new ArgumentException("Unsupported model format.");
            if (importProcess != null) { StatusChanged?.Invoke("Finish the open model import first."); return; }
            try
            {
                StatusChanged?.Invoke("Choose your " + format.ToUpperInvariant() + " model package…");
                var command = Command(false);
                command.Arguments += " --import-format " + format;
                var process = new Process { StartInfo = command, EnableRaisingEvents = true };
                importProcess = process;
                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data == null || !args.Data.StartsWith("{", StringComparison.Ordinal)) return;
                    string json = args.Data;
                    pending.Enqueue(() =>
                    {
                        try
                        {
                            var result = JsonUtility.FromJson<ModelImportReply>(json);
                            if (!string.IsNullOrEmpty(result.error)) { StatusChanged?.Invoke(result.error); return; }
                            if (result.cancelled) { StatusChanged?.Invoke("Import cancelled."); return; }
                            if (result.models == null || result.models.Length == 0) { StatusChanged?.Invoke("No compatible character was found in this package."); return; }
                            RefreshCatalog();
                            ModelImported?.Invoke(result.models[0]);
                        }
                        catch (ArgumentException) { StatusChanged?.Invoke("The model importer returned an unreadable result."); }
                    });
                };
                process.ErrorDataReceived += (_, args) => { };
                process.Exited += (_, args) => pending.Enqueue(() => { importProcess?.Dispose(); importProcess = null; });
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch (Exception error) { importProcess?.Dispose(); importProcess = null; StatusChanged?.Invoke("Import failed. " + error.Message); }
        }

        public void Connect()
        {
            if (server != null) return;
            try
            {
                StatusChanged?.Invoke("Starting the existing Sakura services…");
                server = new Process { StartInfo = Command(true), EnableRaisingEvents = true };
                server.OutputDataReceived += (_, args) =>
                {
                    if (args.Data == null || !args.Data.StartsWith("SAKURA_BRIDGE ", StringComparison.Ordinal)) return;
                    string json = args.Data.Substring(14);
                    pending.Enqueue(() =>
                    {
                        identity = JsonUtility.FromJson<BridgeIdentity>(json);
                        StartCoroutine(CheckConnection());
                    });
                };
                server.ErrorDataReceived += (_, args) => { };
                server.Exited += (_, args) => pending.Enqueue(() =>
                {
                    Connected = false; Busy = false; identity = null;
                    server?.Dispose(); server = null;
                    StatusChanged?.Invoke("Backend stopped. Connect again to retry.");
                });
                server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
            }
            catch (Exception error) { server?.Dispose(); server = null; StatusChanged?.Invoke(error.Message); }
        }

        private UnityWebRequest Request(string route)
        {
            if (identity == null || identity.port < 1 || identity.port > 65535 || string.IsNullOrEmpty(identity.token))
                throw new InvalidOperationException("Connect the backend first.");
            var request = UnityWebRequest.Get("http://127.0.0.1:" + identity.port + route);
            request.redirectLimit = 0;
            request.timeout = 120;
            request.SetRequestHeader("X-Sakura-Desktop", identity.token);
            request.SetRequestHeader("Origin", "http://127.0.0.1:" + identity.port);
            return request;
        }

        private IEnumerator CheckConnection()
        {
            using (var request = Request("/api/identity"))
            {
                yield return request.SendWebRequest();
                Connected = request.result == UnityWebRequest.Result.Success;
                StatusChanged?.Invoke(Connected ? "Connected to Sakura’s authenticated local backend." : "Backend is unavailable. " + request.error);
            }
        }

        public void Api(string route, string body, Action<string, string> completed)
        {
            if (!route.StartsWith("/api/", StringComparison.Ordinal) || route.Contains("..") || route.Contains("\\"))
            { completed(null, "Invalid API route."); return; }
            StartCoroutine(CallApi(route, body, completed));
        }

        private IEnumerator CallApi(string route, string body, Action<string, string> completed)
        {
            if (!Connected)
            {
                Connect();
                float deadline = Time.realtimeSinceStartup + 120;
                while (!Connected && server != null && Time.realtimeSinceStartup < deadline) yield return null;
            }
            if (!Connected) { completed(null, "The existing Sakura backend could not start. Check its environment in Settings."); yield break; }
            using (var request = Request(route))
            {
                if (route.EndsWith("/install", StringComparison.Ordinal)) request.timeout = 1800;
                if (body != null)
                {
                    request.method = "POST";
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                yield return request.SendWebRequest();
                string json = request.downloadHandler.text;
                if (request.result == UnityWebRequest.Result.Success) { completed(json, null); yield break; }
                string error = "Sakura could not complete this request. " + request.error;
                try { var reply = JsonUtility.FromJson<ApiReply>(json); if (!string.IsNullOrEmpty(reply.error)) error = reply.error; }
                catch (ArgumentException) { }
                completed(null, error);
            }
        }

        public void SendChat(string text)
        {
            if (Busy || string.IsNullOrWhiteSpace(text)) return;
            if (!Connected) { StatusChanged?.Invoke("Use Connect backend before sending a message."); return; }
            Busy = true;
            Api("/api/unity/start-services", "{}", (json, error) =>
            {
                Busy = false;
                if (error != null) { StatusChanged?.Invoke(error); return; }
                StartCoroutine(Chat(text));
            });
        }

        private IEnumerator Chat(string text)
        {
            Busy = true;
            StatusChanged?.Invoke("Preparing reply…");
            using (var request = Request("/api/chat"))
            {
                request.method = "POST";
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ChatRequest { text = text })));
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();
                Busy = false;
                if (request.result != UnityWebRequest.Result.Success)
                { StatusChanged?.Invoke("Reply failed. " + request.error); yield break; }
                var response = JsonUtility.FromJson<ChatResponse>(request.downloadHandler.text);
                if (!string.IsNullOrEmpty(response.error)) StatusChanged?.Invoke(response.error);
                else { ReplyReceived?.Invoke(response.reply ?? "No reply returned."); StatusChanged?.Invoke("Ready"); }
            }
        }

        private void OnDestroy()
        {
            foreach (var process in new[] { server, catalogProcess, importProcess, modelToolProcess })
            {
                if (process == null) continue;
                try
                {
                    if (!process.HasExited)
                    {
                        process.StandardInput.Close();
                        if (!process.WaitForExit(1500)) process.Kill();
                    }
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            identity = null;
        }
    }
}
