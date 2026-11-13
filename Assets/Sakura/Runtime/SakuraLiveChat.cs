using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraLiveChat : MonoBehaviour
    {
        public bool Active { get; private set; }
        public string Status { get; private set; } = "Disconnected";
        public event Action Changed;
        private SakuraBackend backend;
        private SakuraVoice voice;
        private readonly Queue<JObject> messages = new Queue<JObject>();
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly Queue<string> seenOrder = new Queue<string>();
        private string mode;
        private bool pending, polling;
        private int generation;
        private float nextPoll, nextRead;
        private SakuraTwitchEffects effects;
        private bool effectsInitialized;
        private bool emoteDrops = PlayerPrefs.GetInt("sakura_twitch_emotes", 1) == 1;
        private bool pointsEnabled;
        private string pointsStatus = "Disconnected";
        private float nextPoints;
        private bool pointsPolling;
        private int pointsGeneration;
        private readonly HashSet<string> pointsSeen = new HashSet<string>();
        public bool EmoteDrops => emoteDrops;
        public bool PointsEnabled => pointsEnabled;
        public string PointsStatus => pointsStatus;

        private void Awake() { backend = GetComponent<SakuraBackend>(); voice = GetComponent<SakuraVoice>(); }
        private void Notify(string value) { Status = value; Changed?.Invoke(); }
        public void Connect(string service, string youtube, string twitch)
        {
            if (backend == null || backend.Mode == "Agent") { Notify("Return to Companion or VTuber mode first."); return; }
            int revision = ++generation; mode = backend.Mode; pending = true; Active = false;
            messages.Clear(); seen.Clear(); seenOrder.Clear(); Notify("Connecting livestream chat…");
            backend.Api(mode == "VTuber" ? "/api/unity/start-voice" : "/api/unity/start-services","{}",(_,error) =>
            {
                if (revision != generation || backend.Mode != mode) return;
                if (error != null) { pending = false; Notify(error); return; }
                string link = service == "twitch" ? twitch : youtube;
                backend.Api(mode == "VTuber" ? "/api/chat-reader/connect" : "/api/vtuber/connect",
                    new JObject { ["service"] = service,["link"] = link,["twitch_link"] = twitch }.ToString(),(json,failure) =>
                {
                    if (revision != generation || backend.Mode != mode) return;
                    pending = false; Active = failure == null;
                    Notify(failure ?? (mode == "VTuber" ? "Chat Reader connected" : "AI VTuber cohost connected"));
                });
            });
        }
        public void Disconnect()
        {
            generation++; Active = false; pending = false; polling = false; messages.Clear();
            voice?.StopSpeaking();
            if (backend != null && backend.Connected) backend.Api("/api/vtuber/disconnect","{}",(_,error) => { if (error != null) Notify(error); });
            Notify("Disconnected");
        }
        private void Update()
        {
            EnsureEffects();
            PollPoints();
            if (!Active) return;
            if (backend.Mode != mode) { Disconnect(); return; }
            int revision = generation;
            if (!polling && Time.unscaledTime >= nextPoll)
            {
                polling = true; nextPoll = Time.unscaledTime + 2;
                backend.Api("/api/vtuber/messages",null,(json,error) =>
                {
                    if (revision != generation) return;
                    polling = false;
                    if (error != null) { Notify(error); return; }
                    try
                    {
                        var state = JObject.Parse(json); Notify((string)state["status"] ?? "Connected");
                        foreach (var entry in state["messages"] as JArray ?? new JArray())
                        {
                            string id = (string)entry["id"];
                            if (string.IsNullOrEmpty(id) || id.Length > 200 || !seen.Add(id)) continue;
                            seenOrder.Enqueue(id);
                            while (seenOrder.Count > 500) seen.Remove(seenOrder.Dequeue());
                            if (entry is JObject message)
                            {
                                if (emoteDrops && effects != null && message["emotes"] is JArray emoteIds) effects.DropEmotes(emoteIds.Values<string>());
                                messages.Enqueue((JObject)message.DeepClone());
                            }
                            while (messages.Count > 20) messages.Dequeue();
                        }
                    }
                    catch (Exception) { Notify("The chat service returned an unreadable message."); }
                });
            }
            if (pending || messages.Count == 0 || voice == null || voice.Speaking || backend.Busy || Time.unscaledTime < nextRead) return;
            var viewer = messages.Dequeue();
            string text = (string)viewer["text"] ?? "", author = (string)viewer["author"] ?? "Viewer";
            if (text.Length == 0) return;
            pending = true;
            var body = mode == "VTuber"
                ? new JObject { ["text"] = (author + ": " + text).Substring(0,Math.Min(600,author.Length + 2 + text.Length)) }
                : new JObject { ["author"] = author.Substring(0,Math.Min(80,author.Length)),["text"] = text.Substring(0,Math.Min(500,text.Length)),["avatar_can_dance"] = GetComponent<SakuraAvatarViewport>()?.LoadedFormat != "Live2D" };
            backend.Api(mode == "VTuber" ? "/api/chat-reader/speak" : "/api/vtuber/reply",body.ToString(),(json,error) =>
            {
                if (revision != generation || !Active || backend.Mode != mode) return;
                if (error != null) { pending = false; nextRead = Time.unscaledTime + 4; Notify(error); return; }
                try
                {
                    var result = JObject.Parse(json);
                    if (mode == "VTuber")
                    {
                        voice.PlayReadout((string)result["audio"]); pending = false; nextRead = Time.unscaledTime + 2;
                    }
                    else
                    {
                        GetComponent<SakuraAvatarActions>()?.Dispatch(result);
                        string reply = (string)result["speech_reply"] ?? (string)result["reply"];
                        Notify(reply ?? "The cohost returned no reply.");
                        backend.Api("/api/tts/speak",new JObject { ["text"] = reply }.ToString(),(audioJson,failure) =>
                        {
                            if (revision != generation) return;
                            pending = false; nextRead = Time.unscaledTime + 8;
                            if (failure != null) { Notify(failure); return; }
                            try { voice.PlayCohost((string)JObject.Parse(audioJson)["audio"]); }
                            catch (Exception) { Notify("Could not play the cohost voice."); }
                        });
                    }
                }
                catch (Exception) { pending = false; Notify("The chat reply was unreadable."); }
            });
        }
        void EnsureEffects()
        {
            if (effectsInitialized) return;
            var studio = GetComponent<SakuraStudio>();
            if (studio?.StageElement == null) return;
            effects = GetComponent<SakuraTwitchEffects>() ?? gameObject.AddComponent<SakuraTwitchEffects>();
            effects.Initialize(studio.StageElement);
            effectsInitialized = true;
        }
        public void SetEmoteDrops(bool enabled)
        {
            emoteDrops = enabled; PlayerPrefs.SetInt("sakura_twitch_emotes", enabled ? 1 : 0); EnsureEffects();
        }
        public void SetPoints(bool enabled)
        {
            if (pointsEnabled == enabled) return;
            pointsEnabled = enabled; pointsGeneration++; pointsSeen.Clear();
            if (backend == null || !backend.Connected) { pointsStatus = "Connect the backend first."; pointsEnabled = false; return; }
            if (enabled)
            {
                pointsStatus = "Connecting to Twitch points…"; nextPoints = 0;
                backend.Api("/api/points/configure", new JObject { ["enabled"] = true }.ToString(), (_, error) => { pointsStatus = error ?? "Connecting to Twitch points…"; });
            }
            else
            {
                pointsStatus = "Disconnected";
                backend.Api("/api/points/configure", new JObject { ["enabled"] = false }.ToString(), null);
            }
        }
        void PollPoints()
        {
            if (!pointsEnabled || pointsPolling || backend == null || !backend.Connected || Time.unscaledTime < nextPoints) return;
            pointsPolling = true; nextPoints = Time.unscaledTime + 2.5f; int revision = pointsGeneration;
            backend.Api("/api/points/status", null, (json, error) =>
            {
                pointsPolling = false;
                if (revision != pointsGeneration) return;
                if (error != null) { pointsStatus = error; return; }
                try
                {
                    var data = JObject.Parse(json); pointsStatus = (string)data["status"] ?? "Connected";
                    foreach (var entry in data["events"] as JArray ?? new JArray())
                    {
                        if (!(entry is JObject reward)) continue;
                        string id = (string)reward["id"]; if (string.IsNullOrEmpty(id) || !pointsSeen.Add(id)) continue;
                        string user = (string)reward["user"] ?? "Viewer", title = (string)reward["reward"] ?? "reward";
                        pointsStatus = "★ " + user + " redeemed " + title;
                        effects?.ShowBanner(pointsStatus);
                        TriggerReward(reward);
                    }
                }
                catch (Exception) { pointsStatus = "The points service returned an unreadable response."; }
            });
        }
        void TriggerReward(JObject reward)
        {
            string id = (string)reward["reward_id"] ?? "";
            var actions = RewardActions();
            string action = actions.TryGetValue(id, out string value) ? value : "none";
            if (action == "emotes") effects?.DropRewardEmotes();
            else if (action == "wave") GetComponent<SakuraAvatarActions>()?.Gesture("wave");
            else if (action == "dance") GetComponent<SakuraAvatarActions>()?.Gesture("groove");
        }
        static Dictionary<string, string> RewardActions()
        {
            try
            {
                var saved = JObject.Parse(PlayerPrefs.GetString("sakura_twitch_reward_actions", "{}"));
                return saved.Properties().Where(property => property.Value.Type == JTokenType.String).ToDictionary(property => property.Name, property => (string)property.Value);
            }
            catch (Exception) { return new Dictionary<string, string>(); }
        }
        public void SaveReward(string id, string action)
        {
            id = (id ?? "").Trim();
            if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,64}\z")) throw new ArgumentException("Enter the reward ID from a redemption.");
            if (!new[] { "none", "wave", "dance", "emotes" }.Contains(action)) throw new ArgumentException("Choose a supported reward action.");
            var actions = RewardActions();
            if (actions.Count >= 50 && !actions.ContainsKey(id)) throw new InvalidOperationException("Up to 50 reward actions can be saved.");
            actions[id] = action;
            PlayerPrefs.SetString("sakura_twitch_reward_actions", JObject.FromObject(actions).ToString());
            PlayerPrefs.Save();
        }
        private void OnDisable() { if (Active || pending) Disconnect(); }

        public static void BuildPanel(SakuraLiveChat chat, VisualElement parent, bool reader)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card); parent = card;
            var title = new Label(reader ? "Chat Reader" : "AI VTuber cohost"); title.AddToClassList("card-title"); parent.Add(title);
            var service = new DropdownField("Livestream source",new List<string> { "YouTube","Twitch","YouTube + Twitch" },0); parent.Add(service);
            var youtube = new TextField("YouTube livestream URL"); parent.Add(youtube);
            var twitch = new TextField("Twitch channel URL"); parent.Add(twitch);
            parent.Add(new Button(() => chat.Connect(new[] { "youtube","twitch","both" }[service.index],youtube.value,twitch.value)) { text = "Connect" });
            parent.Add(new Button(chat.Disconnect) { text = "Disconnect" });
            var status = new Label(chat.Status); status.style.whiteSpace = WhiteSpace.Normal; parent.Add(status);
            var effectsCard = new VisualElement(); effectsCard.AddToClassList("card"); parent.Add(effectsCard);
            var effectsTitle = new Label("Twitch effects"); effectsTitle.AddToClassList("card-title"); effectsCard.Add(effectsTitle);
            var emoteToggle = new Toggle("Drop emotes onto the stage") { value = chat.EmoteDrops }; effectsCard.Add(emoteToggle);
            emoteToggle.RegisterValueChangedCallback(evt => chat.SetEmoteDrops(evt.newValue));
            var pointsToggle = new Toggle("Channel points banner") { value = chat.PointsEnabled }; effectsCard.Add(pointsToggle);
            pointsToggle.RegisterValueChangedCallback(evt => chat.SetPoints(evt.newValue));
            var points = new Label(chat.PointsStatus); points.style.whiteSpace = WhiteSpace.Normal; effectsCard.Add(points);
            var rewardId = new TextField("Reward ID") { maxLength = 64 }; effectsCard.Add(rewardId);
            var rewardAction = new DropdownField("Reward action", new List<string> { "Banner only", "Wave", "Dance", "Emote shower" }, 0); effectsCard.Add(rewardAction);
            var rewardStatus = new Label(); rewardStatus.style.whiteSpace = WhiteSpace.Normal; effectsCard.Add(rewardStatus);
            effectsCard.Add(new Button(() =>
            {
                try { chat.SaveReward(rewardId.value, new[] { "none", "wave", "dance", "emotes" }[rewardAction.index]); rewardStatus.text = "Saved reward action."; }
                catch (Exception error) { rewardStatus.text = error.Message; }
            }) { text = "Save reward action" });
            void Refresh() { status.text = chat.Status; points.text = chat.PointsStatus; pointsToggle.SetValueWithoutNotify(chat.PointsEnabled); emoteToggle.SetValueWithoutNotify(chat.EmoteDrops); }
            chat.Changed += Refresh; parent.RegisterCallback<DetachFromPanelEvent>(_ => chat.Changed -= Refresh); Refresh();
        }
    }
}
