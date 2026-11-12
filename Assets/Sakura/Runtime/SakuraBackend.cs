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

    public sealed class SakuraBackend : MonoBehaviour
    {
        public string SourceFolder = @"F:\AI\assistant\Sakura";
        public event Action<CharacterEntry[]> CatalogReceived;
        public event Action<string> StatusChanged;
        public event Action<string> ReplyReceived;
        public bool Connected { get; private set; }
        public bool Busy { get; private set; }
        private Process server;
        private Process catalogProcess;
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
                            StatusChanged?.Invoke("Local model library loaded. Unity avatar rendering is the next migration step.");
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

        public void SendChat(string text)
        {
            if (Busy || string.IsNullOrWhiteSpace(text)) return;
            if (!Connected) { StatusChanged?.Invoke("Use Connect backend before sending a message."); return; }
            StartCoroutine(Chat(text));
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
            foreach (var process in new[] { server, catalogProcess })
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
