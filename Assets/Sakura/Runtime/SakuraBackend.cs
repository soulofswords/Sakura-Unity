using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    [Serializable] public class CharacterEntry { public string name; public string path; public string format; }
    [Serializable] public class CharacterCatalog { public CharacterEntry[] items; public string error; }
    [Serializable] internal class BridgeIdentity { public int port; public string token; }
    [Serializable] internal class ChatRequest { public string text; public bool defer_audio = true; public string surface; public string agent_task; }
    [Serializable] internal class ChatResponse { public string reply; public string speech_reply; public string error; }
    [Serializable] internal class ModelImportReply { public string[] models; public string error; public bool cancelled; }
    [Serializable] internal class ModeRequest { public string mode; public int revision; }
    [Serializable] public class ApiReply { public bool ok; public bool configured; public bool confirmation_required; public string error; public string message; public string url; }

    public sealed partial class SakuraBackend : MonoBehaviour
    {
        public string SourceFolder = @"F:\AI\assistant\Sakura";
        public bool VtuberEdition { get; set; }
        public event Action<CharacterEntry[]> CatalogReceived;
        public event Action<string> StatusChanged;
        public event Action<string> ReplyReceived;
        public event Action<string> ModelImported;
        public event Action<string> AudioReplyReceived;
        public event Action<Newtonsoft.Json.Linq.JObject> ReplyDataReceived;
        public bool Connected { get; private set; }
        public bool Busy { get; private set; }
        public string Mode { get; private set; } = "Companion";
        private int modeRevision;
        private int chatGeneration;
        private System.Threading.CancellationTokenSource chatLifetime = new System.Threading.CancellationTokenSource();
        private UnityWebRequest activeChat;
        private Process server;
        private Process catalogProcess;
        private Process importProcess;
        private Process modelToolProcess;
        private BridgeIdentity identity;
        private readonly ConcurrentQueue<Action> pending = new ConcurrentQueue<Action>();

        private void Awake()
        {
            if (Application.isEditor) return;
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sakura");
            if (File.Exists(Path.Combine(bundled, "runtime", "python.exe"))) SourceFolder = bundled;
        }

        public static string PythonExecutable(string folder)
        {
            foreach (string relative in new[] { "runtime/python.exe", "venv/Scripts/python.exe" })
            {
                string file = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file)) return file;
            }
            throw new FileNotFoundException("Sakura's Python runtime is missing. Repair or reinstall this application.");
        }

        private void Update()
        {
            while (pending.TryDequeue(out var action)) action();
        }

        private ProcessStartInfo Command(bool serve)
        {
            string python = PythonExecutable(SourceFolder);
            string bridge = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "backend_bridge.py"));
            if (!File.Exists(bridge)) throw new FileNotFoundException("Development backend bridge is missing.");
            if (SourceFolder.Contains("\"") || bridge.Contains("\"")) throw new ArgumentException("Invalid source path.");
            var command = new ProcessStartInfo(python, "-u \"" + bridge + "\" --source \"" + SourceFolder + "\"" + (serve ? " --serve --edition " + (VtuberEdition ? "vtuber" : "companion") : ""))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = SourceFolder
            };
            string tcl = Path.Combine(Path.GetDirectoryName(python), "tcl");
            if (Directory.Exists(tcl))
            {
                command.EnvironmentVariables["TCL_LIBRARY"] = Path.Combine(tcl, "tcl8.6");
                command.EnvironmentVariables["TK_LIBRARY"] = Path.Combine(tcl, "tk8.6");
            }
            if (serve && VtuberEdition && SakuraInstances.IsSecondary)
            {
                command.EnvironmentVariables["SAKURA_INSTANCE_INDEX"] = SakuraInstances.Index.ToString();
                command.EnvironmentVariables["SAKURA_INSTANCE_PROFILE"] = SakuraInstances.SessionProfile;
                command.EnvironmentVariables["SAKURA_VTUBER_INSTANCE"] = "1";
            }
            return command;
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
                process.Exited += (_, args) =>
                {
                    // Drain redirected output before releasing the fast catalog process.
                    process.WaitForExit();
                    pending.Enqueue(() => { if (catalogProcess == process) catalogProcess = null; process.Dispose(); });
                };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch (Exception error) { catalogProcess?.Dispose(); catalogProcess = null; StatusChanged?.Invoke(error.Message); }
        }

        public void RunPmxTool(string tool, string model, Action<string, string> completed, float[] direction = null)
        {
            if (tool != "editor" && tool != "mmd" && tool != "reference" && tool != "texture" && tool != "backdrop-image" && tool != "backdrop-video" && tool != "appearance-import") throw new ArgumentException("Invalid model tool.");
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
                process.Exited += (_, args) =>
                {
                    process.WaitForExit();
                    pending.Enqueue(() => { if (importProcess == process) importProcess = null; process.Dispose(); });
                };
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
                    Connected = false; Busy = false; identity = null; agentToken = null;
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
                if (Connected) SetMode(Mode);
            }
        }

        public void SetMode(string mode)
        {
            if (mode != "Companion" && mode != "VTuber" && mode != "Agent") throw new ArgumentException("Invalid mode.");
            if (VtuberEdition && mode != "VTuber") throw new InvalidOperationException("This is the standalone VTuber edition.");
            if (Mode != mode) InterruptChat();
            Mode = mode; modeRevision++;
            activeChat?.Abort();
            if (!Connected) return;
            Api("/api/unity/mode", JsonUtility.ToJson(new ModeRequest { mode = mode, revision = modeRevision }), (json, error) =>
            { if (error != null) StatusChanged?.Invoke(error); });
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
                else if (route == "/api/unity/wake/check") request.timeout = 45;
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
                completed(null, ResponseError(json, error));
            }
        }

        public void SendChat(string text, string task = null)
        {
            if (Busy || string.IsNullOrWhiteSpace(text)) return;
            if (Mode == "VTuber") { StatusChanged?.Invoke("AI conversation stays off in VTuber mode."); return; }
            var room = GetComponent<SakuraAvatarActions>();
            if (Mode == "Companion" && room != null && room.Sleeping)
            {
                if (!SakuraVoice.WakeFromSleep(text, GetComponent<SakuraVoice>()?.WakePhrase ?? "hey sakura", out string command))
                { StatusChanged?.Invoke("Sleeping · say wake up Sakura or your wake phrase."); return; }
                if (!room.Act("home")) { StatusChanged?.Invoke("Use Stop & return home in Background to wake the character."); return; }
                text = command;
                if (string.IsNullOrWhiteSpace(text)) { StatusChanged?.Invoke("Awake · ready to listen."); return; }
            }
            Busy = true;
            StatusChanged?.Invoke("Starting AI for your reply…");
            string mode = Mode; int revision = ++modeRevision;
            int generation = ++chatGeneration;
            chatLifetime.Cancel(); chatLifetime.Dispose(); chatLifetime = new System.Threading.CancellationTokenSource();
            GetComponent<SakuraVoice>()?.StopSpeaking();
            Api("/api/unity/mode", JsonUtility.ToJson(new ModeRequest { mode = mode, revision = revision }), (modeJson, modeError) =>
            {
                if (mode != Mode || generation != chatGeneration) return;
                if (modeError != null) { Busy = false; ChatError(modeError); return; }
                Api("/api/unity/start-services", "{}", (json, error) =>
                {
                    if (mode != Mode || generation != chatGeneration) return;
                    if (error != null) { Busy = false; ChatError(error); return; }
                    StartCoroutine(Chat(text, mode, generation, task));
                });
            });
        }

        public void InterruptChat()
        {
            chatGeneration++;
            chatLifetime.Cancel();
            CancelScreenReaction();
            activeChat?.Abort(); activeChat = null;
            Busy = false;
        }

        private void ChatError(string error) { StatusChanged?.Invoke(error); ReplyReceived?.Invoke("I could not reply: " + error); }

        public void UploadAudio(byte[] wav, Action<string, string> completed)
        {
            if (Mode != "Companion") { completed(null, "Voice conversation is available in Companion mode."); return; }
            if (wav == null || wav.Length < 44 || wav.Length > 20 * 1024 * 1024) { completed(null, "Audio must be a nonempty WAV under 20 MB."); return; }
            int generation = chatGeneration;
            Api("/api/unity/start-services", "{}", (_, error) =>
            {
                if (error != null) completed(null, error);
                else if (Mode != "Companion" || generation != chatGeneration) completed(null, "Mode changed before recognition.");
                else StartCoroutine(RecognizeAudio(wav, completed));
            });
        }

        private IEnumerator RecognizeAudio(byte[] wav, Action<string, string> completed)
        {
            var form = new WWWForm(); form.AddBinaryData("audio", wav, "speech.wav", "audio/wav");
            using (var request = Request("/api/stt"))
            {
                request.method = "POST"; request.uploadHandler = new UploadHandlerRaw(form.data);
                foreach (var header in form.headers) request.SetRequestHeader(header.Key, header.Value);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success) completed(request.downloadHandler.text, null);
                else
                {
                    completed(null, ResponseError(request.downloadHandler?.text, request.error));
                }
            }
        }

        public static bool ValidAudioPath(string relative) => relative != null && Regex.IsMatch(relative, @"\A/audio/[A-Za-z0-9_-]+\.(?:wav|mp3|ogg)\z", RegexOptions.IgnoreCase);

        public static string ResponseError(string json, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var reply = Newtonsoft.Json.Linq.JObject.Parse(json);
                    foreach (string key in new[] { "error", "detail" })
                        if (reply[key]?.Type == Newtonsoft.Json.Linq.JTokenType.String && !string.IsNullOrWhiteSpace((string)reply[key])) return (string)reply[key];
                }
                catch (Newtonsoft.Json.JsonException) { }
            }
            return string.IsNullOrWhiteSpace(fallback) ? "The backend returned no response. Check the selected model and retry." : fallback;
        }

        public void DownloadAudio(string relative, Action<AudioClip, string> completed)
        {
            if (!Connected || !ValidAudioPath(relative)) { completed(null, "Speech must come from Sakura’s local audio folder."); return; }
            StartCoroutine(GetSpeechAudio(relative, completed));
        }

        private IEnumerator GetSpeechAudio(string relative, Action<AudioClip, string> completed)
        {
            AudioType type = relative.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? AudioType.MPEG : relative.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : AudioType.WAV;
            using (var request = UnityWebRequestMultimedia.GetAudioClip("http://127.0.0.1:" + identity.port + relative, type))
            {
                request.redirectLimit = 0; request.timeout = 120;
                request.SetRequestHeader("X-Sakura-Desktop", identity.token);
                request.SetRequestHeader("Origin", "http://127.0.0.1:" + identity.port);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) completed(null, request.error);
                else completed(DownloadHandlerAudioClip.GetContent(request), null);
            }
        }

        private IEnumerator Chat(string text, string mode, int generation, string task)
        {
            Busy = true;
            StatusChanged?.Invoke("Preparing reply…");
            using (var request = Request("/api/chat"))
            {
                activeChat = request;
                request.method = "POST";
                var payload = Newtonsoft.Json.Linq.JObject.FromObject(new ChatRequest { text = text, surface = mode == "Agent" ? "agent" : "companion", agent_task = mode == "Agent" ? task : null });
                if (mode == "Companion" && !Regex.IsMatch(text, @"^(?:hello|hi|hey|hii+|hiya|namaste|नमस्ते)[!.\s]*$", RegexOptions.IgnoreCase))
                    GetComponent<SakuraScreenFeatures>()?.AddChatContext(payload);
                var actions = GetComponent<SakuraAvatarActions>();
                var viewport = GetComponent<SakuraAvatarViewport>();
                if (mode == "Companion" && viewport != null)
                {
                    payload["avatar_model"] = viewport.LoadedModelPath;
                    payload["avatar_format"] = viewport.LoadedFormat;
                    payload["avatar_can_dance"] = viewport.HasAvatar && viewport.LoadedFormat != "Live2D";
                    if (actions != null)
                    {
                        payload["room_context"] = actions.Context;
                        payload["avatar_room"] = new Newtonsoft.Json.Linq.JArray(actions.Capabilities);
                        if (actions.RoomEnabled && IsRoomQuestion(text))
                        {
                            string frame = viewport.CaptureVisionFrame();
                            if (frame != null)
                            {
                                payload["screen_image"] = frame;
                                payload.Remove("screen_frames");
                                payload["screen_source"] = "virtual_room";
                            }
                        }
                    }
                    if (payload["screen_image"] != null && payload["screen_source"] == null
                        && Regex.IsMatch(text, @"\b(?:your|ur|hair|eyes?|clothes|outfit|avatar|appearance|that girl)\b|बाल|आँख|कपड़े", RegexOptions.IgnoreCase))
                        payload["avatar_image"] = viewport.CaptureVisionFrame();
                }
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)));
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();
                if (activeChat == request) activeChat = null;
                if (Mode != mode || generation != chatGeneration) yield break;
                Busy = false;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ChatError(ResponseError(request.downloadHandler?.text, request.error)); yield break;
                }
                ChatResponse response = null;
                try { response = JsonUtility.FromJson<ChatResponse>(request.downloadHandler.text); }
                catch (ArgumentException) { }
                if (response == null) ChatError("The backend returned an invalid response.");
                else if (!string.IsNullOrWhiteSpace(response.error)) ChatError(response.error);
                else if (string.IsNullOrWhiteSpace(response.reply)) ChatError("The selected model returned an empty reply.");
                else
                {
                    var reply = Newtonsoft.Json.Linq.JObject.Parse(request.downloadHandler.text);
                    if (mode == "Companion" && reply["avatar_model"] != null)
                    {
                        Busy = true;
                        var load = ApplyModelSelection(reply, chatLifetime.Token);
                        while (!load.IsCompleted) yield return null;
                        if (mode != Mode || generation != chatGeneration) yield break;
                        Busy = false;
                        response.reply = load.IsFaulted ? "The outfit did not load. Please choose it in Models." : load.Result;
                        response.speech_reply = response.reply;
                        reply["reply"] = response.reply;
                    }
                    ReplyReceived?.Invoke(response.reply); StatusChanged?.Invoke("Ready");
                    ReplyDataReceived?.Invoke(reply);
                    if (mode != "Companion") yield break;
                    Api("/api/tts/speak", JsonUtility.ToJson(new ChatRequest { text = response.speech_reply ?? response.reply }), (json, error) =>
                    {
                        if (mode != Mode || generation != chatGeneration) return;
                        if (error != null) { StatusChanged?.Invoke("Reply ready; speech unavailable: " + error); return; }
                        try { var audio = Newtonsoft.Json.Linq.JObject.Parse(json).Value<string>("audio"); if (ValidAudioPath(audio)) AudioReplyReceived?.Invoke(audio); }
                        catch (Exception) { StatusChanged?.Invoke("Reply ready; invalid speech response."); }
                    });
                }
            }
        }

        public async System.Threading.Tasks.Task<string> ApplyModelSelection(Newtonsoft.Json.Linq.JObject reply, System.Threading.CancellationToken cancellation = default)
        {
            if (Mode != "Companion" || VtuberEdition) return "Character changes through chat are available in Companion mode.";
            var viewport = GetComponent<SakuraAvatarViewport>();
            if (viewport == null || viewport.Loading) return "A character is already loading. Please try again when it finishes.";
            string model = reply?["avatar_model"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)reply["avatar_model"] : null;
            try
            {
                SakuraAvatarViewport.ResolveCharacterPath(SourceFolder, model);
                await viewport.LoadModel(SourceFolder, model, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (viewport.LoadedModelPath != model || viewport.LastImportError != null || !viewport.HasAvatar)
                    return "The outfit did not load: " + (viewport.LastImportError ?? "The requested character could not be loaded.");
                return "Changed to " + System.IO.Path.GetFileName(model).Replace(".model3.json", "").Replace(".pmx", "").Replace(".vrm", "") + ".";
            }
            catch (Exception) { return "The outfit did not load. Choose an installed character in Models."; }
        }

        public static bool IsRoomQuestion(string text) => Regex.IsMatch(text ?? "", @"\b(?:room|surroundings|where (?:are|do) (?:you|u) (?:live|stay)?|look around)\b|कमरा|कमरे", RegexOptions.IgnoreCase);

        private void OnDestroy()
        {
            chatLifetime.Cancel(); chatLifetime.Dispose();
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
            identity = null; agentToken = null;
        }
    }
}
