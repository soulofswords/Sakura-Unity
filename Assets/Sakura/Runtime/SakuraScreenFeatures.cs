using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraGameVramGuard
    {
        public sealed class Warning { public string Adapter; public int Seconds, FreeMB; public bool Close; }
        private double? since, last, deadline;
        private string adapter;
        private bool cancelled;
        public int Revision { get; private set; }
        public bool ClearWarning { get; private set; }
        public void Reset() { Revision++; since = last = deadline = null; adapter = null; cancelled = false; ClearWarning = true; }
        public void Cancel() { Revision++; cancelled = true; deadline = null; }
        private static bool Number(JObject gpu, string key, out double value)
        {
            value = 0;
            var token = gpu[key];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            value = token.Value<double>(); return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        public static List<JObject> MeasuredGpus(JObject game, bool requireUsed = true)
        {
            return (game["gpus"] as JArray ?? new JArray()).OfType<JObject>().Where(gpu =>
                Number(gpu, "vram_total_bytes", out double total) && total > 0 && Number(gpu, "vram_percent", out double percent) && percent >= 0 && percent <= 100 &&
                (!requireUsed || Number(gpu, "vram_used_bytes", out double used) && used >= 0 && used <= total && gpu["name"]?.Type == JTokenType.String)).ToList();
        }
        private static string Normalize(string value) => System.Text.RegularExpressions.Regex.Replace((value ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
        public Warning Sample(JObject game, bool enabled, string rendererName, double now)
        {
            ClearWarning = false;
            if (!enabled || game.Value<bool?>("detected") != true) { Reset(); return null; }
            var measured = MeasuredGpus(game);
            var matches = string.IsNullOrEmpty(rendererName) ? measured : measured.Where(gpu => Normalize(rendererName).Contains(Normalize(gpu.Value<string>("name")))).ToList();
            var gpu = matches.Count == 1 ? matches[0] : null;
            double free = gpu == null ? 0 : gpu.Value<double>("vram_total_bytes") - gpu.Value<double>("vram_used_bytes");
            if (gpu == null || gpu.Value<double>("vram_percent") < 97 || free >= 256 * 1024 * 1024) { Reset(); return null; }
            string name = gpu.Value<string>("name");
            if (last != null && (now - last.Value > 6 || adapter != name)) Reset();
            last = now; adapter = name;
            if (cancelled) return null;
            if (since == null) since = now;
            if (now - since.Value < 8) return null;
            if (deadline == null) deadline = now + 10;
            var warning = new Warning { Adapter = name, Seconds = Math.Max(0, (int)Math.Ceiling(deadline.Value - now)), FreeMB = (int)Math.Floor(free / (1024 * 1024) + .5), Close = now >= deadline.Value };
            if (warning.Close) cancelled = true;
            return warning;
        }
        public static bool? CloudFallbackChange(JObject game, bool active)
        {
            var measured = MeasuredGpus(game, false);
            if (measured.Count == 0) return null;
            var gpu = measured.OrderByDescending(value => value.Value<double>("vram_percent")).First();
            double percent = gpu.Value<double>("vram_percent");
            double? free = Number(gpu, "vram_used_bytes", out double used) ? gpu.Value<double>("vram_total_bytes") - used : (double?)null;
            bool high = percent >= 90 || free != null && free.Value < 1024d * 1024 * 1024;
            bool recovered = percent <= 82 && (free == null || free.Value >= 1.5 * 1024 * 1024 * 1024);
            if (!active && high) return true;
            if (active && recovered) return false;
            return null;
        }
    }

    public sealed partial class SakuraBackend
    {
        private UnityWebRequest screenReactionRequest;
        private bool screenReactionStarting;
        private int screenRequestRevision;
        public void CancelScreenReaction()
        {
            screenRequestRevision++;
            if (screenReactionStarting) { screenReactionStarting = false; Busy = false; }
            if (screenReactionRequest == null) return;
            screenReactionRequest.Abort();
            if (activeChat == screenReactionRequest) { activeChat = null; Busy = false; }
            screenReactionRequest = null;
        }
        public void ReactToScreen(JObject payload, Func<bool> current, Action<string, string> completed)
        {
            if (Busy || Mode != "Companion" || !Connected) { completed(null, "Wait for the current reply and return to Companion mode."); return; }
            Busy = true; screenReactionStarting = true;
            int revision = ++screenRequestRevision;
            int generation = chatGeneration;
            Api("/api/unity/start-services", "{}", (_, error) =>
            {
                if (revision != screenRequestRevision) { completed(null, null); return; }
                screenReactionStarting = false;
                if (error != null || Mode != "Companion" || generation != chatGeneration || !current())
                { Busy = false; completed(null, error); return; }
                StartCoroutine(ScreenReaction(payload, generation, current, completed));
            });
        }

        private IEnumerator ScreenReaction(JObject payload, int generation, Func<bool> current, Action<string, string> completed)
        {
            using (var request = Request("/api/chat"))
            {
                activeChat = request;
                screenReactionRequest = request;
                request.method = "POST";
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                request.SetRequestHeader("Content-Type", "application/json");
                try
                {
                    yield return request.SendWebRequest();
                    if (Mode != "Companion" || generation != chatGeneration || !current()) { completed(null, null); yield break; }
                    if (request.result != UnityWebRequest.Result.Success) { completed(null, ResponseError(request.downloadHandler?.text, request.error)); yield break; }
                    JObject reply = null;
                    string parseError = null;
                    try { reply = JObject.Parse(request.downloadHandler.text); }
                    catch (JsonException) { parseError = "Screen analysis returned an invalid response."; }
                    if (parseError != null) { completed(null, parseError); yield break; }
                    string text = (string)reply["reply"];
                    if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith("[QUIET]", StringComparison.OrdinalIgnoreCase))
                    { completed("Screen analyzed · no comment needed.", null); yield break; }
                    ReplyReceived?.Invoke(text); ReplyDataReceived?.Invoke(reply);
                    completed("Recent screen frames analyzed.", null);
                    Api("/api/tts/speak", new JObject { ["text"] = (string)reply["speech_reply"] ?? text }.ToString(Formatting.None), (json, error) =>
                    {
                        if (error != null || Mode != "Companion" || generation != chatGeneration || !current()) return;
                        var voice = GetComponent<SakuraVoice>();
                        if (Busy || voice != null && (voice.SpeechInputBusy || voice.Speaking || voice.InputLevel > .008f)) return;
                        try { string audio = JObject.Parse(json).Value<string>("audio"); if (ValidAudioPath(audio)) AudioReplyReceived?.Invoke(audio); }
                        catch (JsonException) { }
                    });
                }
                finally
                {
                    if (screenReactionRequest == request) screenReactionRequest = null;
                    if (activeChat == request) { activeChat = null; Busy = false; }
                }
            }
        }
    }

    /// <summary>Explicit Companion screen capture and source-backed game model switching.</summary>
    public sealed class SakuraScreenFeatures : MonoBehaviour
    {
        private sealed class Frame { public string Image; public float Time; }
        public bool ScreenEnabled { get; private set; }
        public bool ReactionsEnabled { get; private set; }
        public bool AutoGameEnabled { get; private set; }
        public bool GameActive { get; private set; }
        public string Status { get; private set; } = "Screen off · capture stopped";
        public string GameStatus { get; private set; } = "Automatic game detection off";
        public event Action Changed;
        private SakuraBackend backend;
        private SakuraVoice voice;
        private readonly List<Frame> frames = new List<Frame>();
        private readonly Queue<Action> gameRequests = new Queue<Action>();
        private bool gameRequestActive;
        private bool starting, captureLeaseReady, capturing, reacting, pollingGame, switchingGame, configuringGame, gameStartedScreen, gamePriorReactions, gameForeground;
        private int generation;
        private float nextCapture, nextReaction, nextGamePoll, lastDirectReply, lastMicActivity = -100;
        private string gamePlayMode = "singleplayer";
        private string configuredProvider = "ollama", currentGameName;
        private bool cloudFallback, changingFallback, autoCloud = true, autoClose = true, settingsLoaded, confirmingQuit;
        private readonly SakuraGameVramGuard vramGuard = new SakuraGameVramGuard();
        private VisualElement vramOverlay;
        private Label vramWarning;

        private void Awake()
        {
            backend = GetComponent<SakuraBackend>(); voice = GetComponent<SakuraVoice>();
            if (backend != null) backend.ReplyReceived += DirectReply;
        }
        private void DirectReply(string _) { if (!reacting) lastDirectReply = Time.unscaledTime; }
        private void Notify(string message) { Status = message; Changed?.Invoke(); }
        public static bool ValidScreenImage(string image)
        {
            if (string.IsNullOrEmpty(image) || image.Length > 4000000 || !image.StartsWith("/9j/", StringComparison.Ordinal)) return false;
            try { byte[] bytes = Convert.FromBase64String(image); return bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[bytes.Length - 2] == 255 && bytes[bytes.Length - 1] == 217; }
            catch (FormatException) { return false; }
        }
        public static bool FreshFrame(float now, float captured) => now >= captured && now - captured < 2.5f;
        public static bool CaptureReady(bool enabled, bool starting, bool leaseReady, bool inFlight) => !inFlight && (enabled || starting && leaseReady);

        public void SetScreen(bool enabled)
        {
            if (!enabled) { StopScreen(); return; }
            if (backend == null || backend.Mode != "Companion") { Notify("Screen viewing requires Companion mode."); return; }
            if (ScreenEnabled || starting) return;
            int revision = ++generation; starting = true; captureLeaseReady = false; Notify("Requesting desktop screen capture…");
            backend.Api("/api/unity/screen", "{\"enabled\":true}", (_, error) =>
            {
                if (revision != generation || backend.Mode != "Companion") return;
                if (error != null) { starting = false; Notify(error); return; }
                captureLeaseReady = true;
                Capture(revision);
            });
        }

        public void StopScreen()
        {
            generation++; starting = false; captureLeaseReady = false; ScreenEnabled = false; ReactionsEnabled = false; frames.Clear();
            backend?.CancelScreenReaction();
            if (backend != null && backend.Connected) backend.Api("/api/unity/screen", "{\"enabled\":false}", (_, error) => { if (error != null && this != null) Notify("Screen off · " + error); });
            Notify("Screen off · capture stopped");
        }
        private void Capture(int revision)
        {
            if (capturing) return;
            capturing = true;
            backend.Api("/api/unity/screen/snapshot", null, (json, error) =>
            {
                capturing = false;
                if (this == null || revision != generation || backend.Mode != "Companion") return;
                string image = null;
                if (error == null)
                {
                    try { image = JObject.Parse(json).Value<string>("image"); }
                    catch (JsonException) { error = "Screen capture returned an invalid response."; }
                }
                if (error != null || !ValidScreenImage(image)) { StopScreen(); Notify(error ?? "Screen capture returned no valid image. Turn Screen on to retry."); return; }
                frames.Add(new Frame { Image = image, Time = Time.unscaledTime });
                frames.RemoveAll(frame => !FreshFrame(Time.unscaledTime, frame.Time));
                while (frames.Count > 8) frames.RemoveAt(0);
                bool first = starting;
                starting = false; ScreenEnabled = true; nextCapture = Time.unscaledTime + .333f;
                if (first && GameActive && AutoGameEnabled) SetReactions(true);
                if (first) Notify("Screen captured · ready for your next question");
            });
        }

        public void AddChatContext(JObject payload)
        {
            if (!ScreenEnabled || backend == null || backend.Mode != "Companion") return;
            var recent = frames.Where(frame => FreshFrame(Time.unscaledTime, frame.Time)).ToList();
            if (recent.Count == 0) return;
            payload["screen_image"] = recent[recent.Count - 1].Image;
            payload["screen_frames"] = new JArray(recent.Count <= 3 ? recent.Select(frame => frame.Image) : new[] { recent[0].Image, recent[recent.Count / 2].Image, recent[recent.Count - 1].Image });
        }
        public void SetReactions(bool enabled)
        {
            if (enabled && !ScreenEnabled) { Notify("Enable Screen and wait for a captured frame before enabling reactions."); return; }
            ReactionsEnabled = enabled; nextReaction = Time.unscaledTime + 1; Changed?.Invoke();
        }
        public void AnalyzeNow() { React(true); }
        private void React(bool manual)
        {
            if (!ScreenEnabled || reacting || backend.Busy || Time.unscaledTime - lastMicActivity < 6 || (voice != null && (voice.Speaking || voice.SpeechInputBusy))) return;
            if (GetComponent<SakuraAvatarActions>()?.Sleeping == true) return;
            var payload = new JObject { ["text"] = "Screen companion reaction", ["screen_reaction"] = true, ["screen_check"] = manual,
                ["game_watch"] = GameActive && gamePlayMode == "multiplayer", ["defer_audio"] = true, ["surface"] = "companion" };
            AddChatContext(payload);
            if (payload["screen_image"] == null) { Notify("Waiting for a fresh screen frame."); return; }
            int revision = generation; reacting = true; Notify("Submitting recent screen frames for analysis…");
            backend.ReactToScreen(payload, () => this != null && ScreenEnabled && revision == generation, (message, error) =>
            {
                if (this == null) return;
                reacting = false; nextReaction = Time.unscaledTime + (GameActive && gamePlayMode == "multiplayer" ? 10 : 20);
                if (revision == generation && message != null) Notify(message);
                if (revision == generation && error != null) Notify(error);
            });
        }

        public void SetAutoGame(bool enabled)
        {
            if (configuringGame || backend == null || backend.Mode != "Companion") return;
            configuringGame = true; Changed?.Invoke();
            backend.Api("/api/unity/screen", new JObject { ["enabled"] = ScreenEnabled || starting, ["auto_game"] = enabled }.ToString(Formatting.None), (_, error) =>
            {
                if (this == null) return;
                configuringGame = false;
                if (!isActiveAndEnabled || backend.Mode != "Companion") { Changed?.Invoke(); return; }
                if (error != null) { GameStatus = error; Changed?.Invoke(); return; }
                AutoGameEnabled = enabled; nextGamePoll = 0;
                if (!enabled) LeaveGame();
                GameStatus = enabled ? "Checking for a running game…" : "Automatic game detection off"; Changed?.Invoke();
            });
        }
        private void PollGame()
        {
            pollingGame = true; nextGamePoll = Time.unscaledTime + 3;
            backend.Api("/api/unity/game-status", null, (json, error) =>
            {
                pollingGame = false;
                if (this == null || !AutoGameEnabled || backend.Mode != "Companion") return;
                if (error != null) { GameStatus = error; Changed?.Invoke(); return; }
                try
                {
                    var game = JObject.Parse(json);
                    UpdateVramGuard(game);
                    gameForeground = game.Value<bool?>("foreground") ?? false;
                    if (game.Value<bool?>("detected") != true) { LeaveGame(); GameStatus = "No running game detected"; Changed?.Invoke(); return; }
                    string gameName = game.Value<string>("name") ?? "Running game";
                    if (!GameActive) { GameStatus = "Game detected · " + gameName; Changed?.Invoke(); }
                    if (!switchingGame && !backend.Busy && voice?.Speaking != true)
                    {
                        if (!GameActive || currentGameName != gameName) SwitchGameModel(gameName);
                        else UpdateCloudFallback(game);
                    }
                }
                catch (JsonException) { GameStatus = "Game detection returned an invalid response."; Changed?.Invoke(); }
            });
        }
        private void SwitchGameModel(string gameName)
        {
            switchingGame = true;
            backend.Api("/api/unity/start-services", "{}", (_, startupError) =>
            {
                if (this == null || !AutoGameEnabled || backend.Mode != "Companion") { switchingGame = false; return; }
                if (startupError != null) { switchingGame = false; GameStatus = startupError; Changed?.Invoke(); return; }
                // The backend chooses a safe model/provider using its existing VRAM policy.
                GameRequest("/api/game-mode/model", "{\"active\":true}", (json, error) =>
                {
                    switchingGame = false;
                    if (this == null) return;
                    if (!AutoGameEnabled || backend.Mode != "Companion") { LeaveGame(error == null); return; }
                    if (error != null) { GameStatus = error; Changed?.Invoke(); return; }
                    try
                    {
                        var result = JObject.Parse(json);
                        cloudFallback = result.Value<bool?>("cloud_fallback") ?? false;
                        string paused = result.Value<string>("local_ai_paused");
                        if (!string.IsNullOrEmpty(paused)) { GameStatus = paused; Changed?.Invoke(); return; }
                        if (!GameActive)
                        {
                            gamePriorReactions = ReactionsEnabled; gameStartedScreen = !ScreenEnabled;
                            GameActive = true; if (!ScreenEnabled) SetScreen(true);
                        }
                        currentGameName = gameName;
                        if (ScreenEnabled) SetReactions(true);
                        GameStatus = "Game mode · " + (result.Value<string>("provider") ?? "selected AI") + " · " + (result.Value<string>("model") ?? "saved model");
                    }
                    catch (JsonException) { GameStatus = "Game model selection returned an invalid response."; }
                    Changed?.Invoke();
                });
            });
        }
        private void LeaveGame(bool forceRestore = false)
        {
            vramGuard.Reset(); RemoveVramWarning();
            bool wasActive = GameActive;
            if (!wasActive && !forceRestore) return;
            GameActive = false; currentGameName = null; cloudFallback = false;
            if (backend != null && backend.Connected)
            {
                GameRequest("/api/game-mode/model", "{\"active\":false}", (_, error) => { if (error != null && this != null) { GameStatus = error; Changed?.Invoke(); } });
                GameRequest("/api/game-mode/cloud-fallback", "{\"active\":false}", (_, error) => { if (error != null && this != null) { GameStatus = error; Changed?.Invoke(); } });
            }
            if (wasActive)
            {
                if (gameStartedScreen) StopScreen(); else ReactionsEnabled = gamePriorReactions;
            }
            gameStartedScreen = false;
        }
        private void UpdateCloudFallback(JObject game)
        {
            if (changingFallback || configuredProvider != "ollama" || !autoCloud) return;
            bool? desired = SakuraGameVramGuard.CloudFallbackChange(game, cloudFallback);
            if (desired == null) return;
            changingFallback = true;
            GameRequest("/api/game-mode/cloud-fallback", new JObject { ["active"] = desired.Value }.ToString(Formatting.None), (json, error) =>
            {
                changingFallback = false;
                if (this == null) return;
                if (!AutoGameEnabled || !GameActive || backend.Mode != "Companion") { if (error == null && desired.Value) LeaveGame(true); return; }
                if (error != null) { GameStatus = error; Changed?.Invoke(); return; }
                try
                {
                    var result = JObject.Parse(json);
                    cloudFallback = result.Value<bool?>("active") ?? false;
                    GameStatus = cloudFallback ? "Game mode · VRAM pressure · " + (result.Value<string>("provider") ?? "saved cloud AI") :
                        result.Value<string>("reason") ?? "Game mode · local AI restored";
                }
                catch (JsonException) { GameStatus = "Game cloud fallback returned an invalid response."; }
                Changed?.Invoke();
            });
        }
        private void GameRequest(string route, string body, Action<string, string> completed)
        {
            // A late cloud activation must finish before the queued universal-model restore.
            gameRequests.Enqueue(() => backend.Api(route, body, (json, error) =>
            {
                gameRequestActive = false;
                try { if (this != null) completed(json, error); }
                finally { if (this != null) PumpGameRequests(); }
            }));
            PumpGameRequests();
        }
        private void PumpGameRequests()
        {
            if (gameRequestActive || gameRequests.Count == 0 || backend == null || !backend.Connected) return;
            gameRequestActive = true;
            gameRequests.Dequeue()();
        }
        private void UpdateVramGuard(JObject game)
        {
            var warning = vramGuard.Sample(game, settingsLoaded && autoClose && AutoGameEnabled, SystemInfo.graphicsDeviceName, Time.unscaledTimeAsDouble);
            if (warning == null) { if (vramGuard.ClearWarning) RemoveVramWarning(); return; }
            if (vramOverlay == null)
            {
                var root = GetComponent<UIDocument>()?.rootVisualElement;
                if (root == null) return;
                vramOverlay = new VisualElement { name = "game-vram-warning" };
                vramOverlay.style.position = Position.Absolute; vramOverlay.style.left = 12; vramOverlay.style.right = 12; vramOverlay.style.bottom = 80;
                vramOverlay.style.backgroundColor = new Color(.20f, .12f, .14f); vramOverlay.style.paddingTop = 16; vramOverlay.style.paddingBottom = 16;
                vramOverlay.style.paddingLeft = 16; vramOverlay.style.paddingRight = 16; vramOverlay.style.borderTopWidth = 2; vramOverlay.style.borderBottomWidth = 2;
                vramOverlay.style.borderLeftWidth = 2; vramOverlay.style.borderRightWidth = 2;
                vramOverlay.style.borderTopColor = vramOverlay.style.borderBottomColor = vramOverlay.style.borderLeftColor = vramOverlay.style.borderRightColor = new Color(1, .61f, .67f);
                vramWarning = new Label(); vramWarning.style.whiteSpace = WhiteSpace.Normal; vramWarning.style.color = Color.white; vramOverlay.Add(vramWarning);
                vramOverlay.Add(new Button(() => { vramGuard.Cancel(); RemoveVramWarning(); }) { text = "Keep Sakura open this time" });
                root.Add(vramOverlay);
            }
            vramOverlay.BringToFront();
            vramWarning.text = "Only " + warning.FreeMB + " MB VRAM remains. Sakura will close in " + warning.Seconds + " seconds to free memory for your game. Running tasks may stop. Saved settings are kept.";
            if (!warning.Close || confirmingQuit) return;
#if UNITY_EDITOR
            vramWarning.text = "Editor preview · Sakura would close now to free VRAM. The Unity Editor stays open.";
#else
            confirmingQuit = true;
            int guardRevision = vramGuard.Revision;
            backend.Api("/api/unity/game-vram/confirm", new JObject { ["adapter"] = warning.Adapter }.ToString(Formatting.None), (json, error) =>
            {
                confirmingQuit = false;
                if (this == null || guardRevision != vramGuard.Revision || !AutoGameEnabled || !autoClose || backend.Mode != "Companion") return;
                bool confirmed = false;
                if (error == null) { try { confirmed = JObject.Parse(json).Value<bool?>("ok") == true; } catch (JsonException) { } }
                if (confirmed) Application.Quit();
                else if (vramWarning != null) vramWarning.text = error ?? "VRAM pressure cleared. Sakura will stay open.";
            });
#endif
        }
        private void RemoveVramWarning() { vramOverlay?.RemoveFromHierarchy(); vramOverlay = null; vramWarning = null; }
        private void Update()
        {
            if (backend == null) return;
            if (voice != null && voice.InputLevel > .008f) lastMicActivity = Time.unscaledTime;
            if (backend.Mode != "Companion") { if (AutoGameEnabled || GameActive) { AutoGameEnabled = false; LeaveGame(); } if (ScreenEnabled || starting) StopScreen(); return; }
            if (CaptureReady(ScreenEnabled, starting, captureLeaseReady, capturing) && Time.unscaledTime >= nextCapture) Capture(generation);
            if (AutoGameEnabled && !pollingGame && Time.unscaledTime >= nextGamePoll) PollGame();
            if (ScreenEnabled && ReactionsEnabled && !reacting && Time.unscaledTime >= nextReaction &&
                (GameActive ? gameForeground : Time.unscaledTime - lastDirectReply >= 30)) React(false);
        }

        public void BuildPanel(SakuraBackend owner, VisualElement parent)
        {
            backend = owner; voice = GetComponent<SakuraVoice>();
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("Screen & games"); title.AddToClassList("card-title"); card.Add(title);
            var screen = new Toggle("Share my desktop with Sakura"); card.Add(screen);
            var reactions = new Toggle("Occasional screen reactions"); card.Add(reactions);
            var analyze = new Button(AnalyzeNow) { text = "Analyze screen now" }; card.Add(analyze);
            var screenStatus = new Label(Status); screenStatus.AddToClassList("description"); card.Add(screenStatus);
            var automatic = new Toggle("Detect games and enable screen reactions"); card.Add(automatic);
            var safeguard = new Toggle("Close Sakura after a warning if game VRAM is critically low") { value = autoClose }; card.Add(safeguard);
            var fallback = new Toggle("Automatically use saved cloud AI under game VRAM pressure") { value = autoCloud }; card.Add(fallback);
            var gameMode = new DropdownField("Game type", new List<string> { "Single-player", "Multiplayer" }, gamePlayMode == "multiplayer" ? 1 : 0); card.Add(gameMode);
            var gameStatus = new Label(GameStatus); gameStatus.AddToClassList("description"); card.Add(gameStatus);
            var note = new Label("Screen shares the visible desktop. Captured images reach your saved AI provider when you ask a question or request a reaction. Game model selection uses Sakura’s existing VRAM checks."); note.AddToClassList("description"); card.Add(note);
            screen.RegisterValueChangedCallback(evt => SetScreen(evt.newValue));
            reactions.RegisterValueChangedCallback(evt => SetReactions(evt.newValue));
            automatic.RegisterValueChangedCallback(evt => SetAutoGame(evt.newValue));
            safeguard.RegisterValueChangedCallback(evt =>
            {
                bool desired = evt.newValue; safeguard.SetEnabled(false);
                backend.Api("/api/settings", new JObject { ["game_vram_auto_close_enabled"] = desired }.ToString(Formatting.None), (_, error) =>
                {
                    if (this == null) return;
                    safeguard.SetEnabled(true);
                    if (error != null) { safeguard.SetValueWithoutNotify(autoClose); Notify(error); return; }
                    autoClose = desired;
                    if (!desired) { vramGuard.Reset(); RemoveVramWarning(); }
                });
            });
            fallback.RegisterValueChangedCallback(evt =>
            {
                bool desired = evt.newValue; fallback.SetEnabled(false);
                backend.Api("/api/settings", new JObject { ["game_cloud_auto_enabled"] = desired }.ToString(Formatting.None), (_, error) =>
                {
                    if (this == null) return;
                    fallback.SetEnabled(true);
                    if (error != null) { fallback.SetValueWithoutNotify(autoCloud); Notify(error); return; }
                    autoCloud = desired;
                });
            });
            gameMode.RegisterValueChangedCallback(_ =>
            {
                string choice = gameMode.index == 1 ? "multiplayer" : "singleplayer";
                backend.Api("/api/settings", new JObject { ["game_play_mode"] = choice }.ToString(Formatting.None), (_, error) =>
                { if (this == null) return; if (error != null) Notify(error); else gamePlayMode = choice; });
            });
            Action refresh = () =>
            {
                bool companion = backend.Mode == "Companion";
                screen.SetValueWithoutNotify(ScreenEnabled || starting); screen.SetEnabled(companion);
                reactions.SetValueWithoutNotify(ReactionsEnabled); reactions.SetEnabled(companion && ScreenEnabled);
                analyze.SetEnabled(companion && ScreenEnabled && !reacting && !backend.Busy);
                automatic.SetValueWithoutNotify(AutoGameEnabled); automatic.SetEnabled(companion && !configuringGame);
                gameMode.SetEnabled(companion); screenStatus.text = Status; gameStatus.text = GameStatus;
            };
            Changed += refresh; card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= refresh); refresh();
            backend.Api("/api/settings", null, (json, error) =>
            {
                if (this == null || error != null || gameMode.panel == null) return;
                try
                {
                    var saved = JObject.Parse(json);
                    gamePlayMode = saved.Value<string>("game_play_mode") ?? "singleplayer"; gameMode.SetValueWithoutNotify(gamePlayMode == "multiplayer" ? "Multiplayer" : "Single-player");
                    autoClose = saved.Value<bool?>("game_vram_auto_close_enabled") ?? true; safeguard.SetValueWithoutNotify(autoClose);
                    autoCloud = saved.Value<bool?>("game_cloud_auto_enabled") ?? true; fallback.SetValueWithoutNotify(autoCloud);
                    configuredProvider = saved.Value<string>("llm_provider") ?? "ollama"; settingsLoaded = true;
                }
                catch (JsonException) { Notify("Could not read the saved game type."); }
            });
        }
        private void OnDisable() { AutoGameEnabled = false; LeaveGame(); StopScreen(); }
        private void OnDestroy() { if (backend != null) backend.ReplyReceived -= DirectReply; }
    }
}
