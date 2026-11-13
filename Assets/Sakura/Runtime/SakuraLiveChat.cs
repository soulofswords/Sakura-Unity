using System;
using System.Collections.Generic;
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
                            if (entry is JObject message) messages.Enqueue((JObject)message.DeepClone());
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
            void Refresh() { status.text = chat.Status; }
            chat.Changed += Refresh; parent.RegisterCallback<DetachFromPanelEvent>(_ => chat.Changed -= Refresh);
        }
    }
}
