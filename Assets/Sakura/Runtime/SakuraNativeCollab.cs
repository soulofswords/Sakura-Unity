using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Live2D.Cubism.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraNativeCollab : MonoBehaviour
    {
        sealed class Peer : IDisposable
        {
            public string Id, Session, ModelPath;
            public RTCPeerConnection Connection;
            public RTCDataChannel Control, Pose;
            public readonly Queue<RTCIceCandidateInit> Ice = new Queue<RTCIceCandidateInit>();
            public bool RemoteDescription, Sending, Importing, Closed;
            public FileStream Receive;
            public long Expected;
            public string Entry, Name, Zip;
            public GameObject Owner;
            public SakuraAvatarViewport Viewport;
            public SakuraNativeCollabPose Driver;
            public VisualElement Seat;
            public JObject LatestPose;
            public CancellationTokenSource Lifetime = new CancellationTokenSource();
            public double ChatAt;
            public void Dispose()
            {
                Closed = true; Lifetime.Cancel(); Receive?.Dispose(); Receive = null;
                Control?.Close(); Control?.Dispose(); Pose?.Close(); Pose?.Dispose(); Connection?.Close(); Connection?.Dispose(); Seat?.RemoveFromHierarchy();
                if (Owner != null) UnityEngine.Object.Destroy(Owner);
                Lifetime.Dispose();
                if (Session != null) try { SakuraNativeCollabPackage.DeleteSession(Session); } catch (IOException) { }
            }
        }
        SakuraBackend backend;
        SakuraAvatarViewport viewport;
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        readonly ConcurrentQueue<Action> pending = new ConcurrentQueue<Action>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        ClientWebSocket socket;
        CancellationTokenSource lifetime;
        Coroutine webRtcUpdates;
        VisualElement seats;
        string ownId;
        int generation, slot;
        RTCConfiguration rtcConfig;
        double nextPose;
        bool joined;
        public bool Connected => joined;
        public int Participants => joined ? peers.Count + 1 : 0;
        public string Status { get; private set; } = "Collab is off.";
        public event Action Changed;
        public bool ReceiveSharedMessages { get; set; }
        readonly List<(string speaker, string text)> shared = new List<(string, string)>();
        public IReadOnlyList<(string speaker, string text)> SharedMessages => shared;

        public void Initialize(SakuraBackend service, SakuraAvatarViewport avatars) { backend = service; viewport = avatars; }
        void Say(string message) { Status = message; Changed?.Invoke(); }
        public static Uri RelayEndpoint(string relay, string room)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(room ?? "", @"\A[A-Za-z0-9_-]{8,32}\z")) throw new ArgumentException("Enter an 8–32 character room code.");
            if (!Uri.TryCreate((relay ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.Scheme != "wss" && !(uri.Scheme == "ws" && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))) throw new ArgumentException("Enter a secure WSS relay URL. Local WS supports localhost tests.");
            return new Uri(uri, "ws/" + Uri.EscapeDataString(room));
        }
        public static string GenerateRoom()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; var bytes = new byte[10];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return new string(bytes.Select(value => alphabet[value % alphabet.Length]).ToArray());
        }
        public async Task ConnectAsync(string relay, string room, string turn, string user, string password, bool shareSelectedModel)
        {
            if (!shareSelectedModel) throw new InvalidOperationException("Confirm sharing the selected model files and live movement with room participants.");
            if (backend == null || viewport == null || !backend.Connected || !viewport.HasAvatar || backend.Mode != "VTuber") throw new InvalidOperationException("Connect the backend, load a character and switch to VTuber mode first.");
            var endpoint = RelayEndpoint(relay, room);
            var servers = new List<RTCIceServer> { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } };
            if (!string.IsNullOrWhiteSpace(turn))
            {
                if (turn.Length > 1024 || !(turn.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || turn.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)) || user.Length > 256 || password.Length > 256) throw new ArgumentException("Enter a TURN or TURNS server with bounded credentials.");
                servers.Add(new RTCIceServer { urls = new[] { turn }, username = user, credential = password });
            }
            Disconnect(); int revision = generation; lifetime = new CancellationTokenSource();
            rtcConfig = new RTCConfiguration { iceServers = servers.ToArray() }; ownId = Guid.NewGuid().ToString(); socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                Say("Connecting to the Collab relay…"); await socket.ConnectAsync(endpoint, lifetime.Token);
                if (revision != generation) return;
                webRtcUpdates = StartCoroutine(WebRTC.Update());
                await SendSignal(new JObject { ["type"] = "join", ["peer"] = ownId }, lifetime.Token);
                _ = ReceiveSignals(socket, revision, lifetime.Token);
                PlayerPrefs.SetString("sakura-native-collab-relay", relay);
            }
            catch { if (revision == generation) Disconnect(); throw; }
        }
        async Task SendSignal(JObject message, CancellationToken cancel)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            if (bytes.Length > 65536) throw new InvalidDataException("Collab signaling exceeds 64 KB.");
            await sendLock.WaitAsync(cancel);
            try { if (socket?.State == WebSocketState.Open) await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancel); }
            finally { sendLock.Release(); }
        }
        async Task ReceiveSignals(ClientWebSocket connection, int revision, CancellationToken cancel)
        {
            try
            {
                var buffer = new byte[8192];
                while (!cancel.IsCancellationRequested && connection.State == WebSocketState.Open)
                {
                    using (var bytes = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("The relay disconnected.");
                            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > 65536) throw new InvalidDataException("Invalid Collab signaling message.");
                            bytes.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        JObject message;
                        using (var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes.ToArray()))) using (var json = new JsonTextReader(text) { MaxDepth = 16 }) message = JObject.Load(json);
                        pending.Enqueue(() => { if (revision == generation && !cancel.IsCancellationRequested) AcceptSignal(message); });
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { pending.Enqueue(() => { if (revision == generation) { Disconnect(); Say(error.Message); } }); }
        }
        void AcceptSignal(JObject message)
        {
            try
            {
                string type = message.Value<string>("type");
                if (type == "joined") { joined = true; Say("Room ready · waiting for collaborators…"); return; }
                if (type == "room_full") { Disconnect(); Say("This room already has six participants."); return; }
                if (type == "peer_joined") { MakePeer(message.Value<string>("peer"), true); return; }
                string from = message.Value<string>("from");
                if (type == "peer_left") { RemovePeer(from); return; }
                if (type != "offer" && type != "answer" && type != "ice") return;
                var peer = MakePeer(from, false);
                if (type == "ice")
                {
                    var candidate = message["data"]?.ToObject<RTCIceCandidateInit>() ?? throw new ArgumentException("Invalid ICE candidate.");
                    if (peer.RemoteDescription) { using (var ice = new RTCIceCandidate(candidate)) peer.Connection.AddIceCandidate(ice); }
                    else { if (peer.Ice.Count >= 256) throw new InvalidDataException("Too many pending ICE candidates."); peer.Ice.Enqueue(candidate); }
                }
                else StartCoroutine(Description(peer, message["data"] as JObject, type == "offer"));
            }
            catch (Exception error) { Say("Collab sync failed. " + error.Message); }
        }
        Peer MakePeer(string id, bool initiator)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64 || id == ownId || id.Any(char.IsControl)) throw new ArgumentException("Invalid collaborator ID.");
            if (peers.TryGetValue(id, out var existing)) return existing;
            if (peers.Count >= 5) throw new InvalidOperationException("Sakura supports up to six participants.");
            var config = rtcConfig; var peer = new Peer { Id = id, Connection = new RTCPeerConnection(ref config), Session = Guid.NewGuid().ToString("N") }; peers.Add(id, peer);
            Directory.CreateDirectory(SakuraNativeCollabPackage.SessionFolder(peer.Session));
            peer.Connection.OnIceCandidate = candidate => { if (!peer.Closed && lifetime != null) _ = SendSignal(new JObject { ["type"] = "ice", ["to"] = id, ["data"] = new JObject { ["candidate"] = candidate.Candidate, ["sdpMid"] = candidate.SdpMid, ["sdpMLineIndex"] = candidate.SdpMLineIndex } }, lifetime.Token); };
            peer.Connection.OnConnectionStateChange = state => { if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Closed) pending.Enqueue(() => RemovePeer(id)); };
            peer.Connection.OnDataChannel = channel => InstallChannel(peer, channel);
            if (initiator)
            {
                InstallChannel(peer, peer.Connection.CreateDataChannel("sakura-control", new RTCDataChannelInit()));
                InstallChannel(peer, peer.Connection.CreateDataChannel("sakura-pose", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 }));
                StartCoroutine(Offer(peer));
            }
            Say("A participant joined · syncing avatars…"); return peer;
        }
        IEnumerator Offer(Peer peer)
        {
            var offer = peer.Connection.CreateOffer(); yield return offer;
            if (peer.Closed) yield break;
            if (offer.IsError) { Say("Could not create a Collab offer. " + offer.Error.message); yield break; }
            var description = offer.Desc; var local = peer.Connection.SetLocalDescription(ref description); yield return local;
            if (!peer.Closed && !local.IsError) _ = SendSignal(new JObject { ["type"] = "offer", ["to"] = peer.Id, ["data"] = new JObject { ["type"] = "offer", ["sdp"] = description.sdp } }, lifetime.Token);
        }
        IEnumerator Description(Peer peer, JObject data, bool offer)
        {
            string sdp = data?.Value<string>("sdp"); if (string.IsNullOrEmpty(sdp) || sdp.Length > 60000) { Say("Invalid peer session description."); yield break; }
            var description = new RTCSessionDescription { type = offer ? RTCSdpType.Offer : RTCSdpType.Answer, sdp = sdp };
            var remote = peer.Connection.SetRemoteDescription(ref description); yield return remote;
            if (peer.Closed || remote.IsError) yield break; peer.RemoteDescription = true;
            while (peer.Ice.Count > 0) { using (var ice = new RTCIceCandidate(peer.Ice.Dequeue())) peer.Connection.AddIceCandidate(ice); }
            if (!offer) yield break;
            var answer = peer.Connection.CreateAnswer(); yield return answer; if (peer.Closed || answer.IsError) yield break;
            description = answer.Desc; var local = peer.Connection.SetLocalDescription(ref description); yield return local;
            if (!peer.Closed && !local.IsError) _ = SendSignal(new JObject { ["type"] = "answer", ["to"] = peer.Id, ["data"] = new JObject { ["type"] = "answer", ["sdp"] = description.sdp } }, lifetime.Token);
        }
        void InstallChannel(Peer peer, RTCDataChannel channel)
        {
            if (channel.Label == "sakura-control" && peer.Control == null)
            {
                peer.Control = channel; channel.OnOpen = () => _ = SendModel(peer);
                channel.OnMessage = bytes => ReceiveControl(peer, bytes);
            }
            else if (channel.Label == "sakura-pose" && peer.Pose == null)
            {
                peer.Pose = channel; channel.OnMessage = bytes =>
                {
                    if (peer.Closed || bytes.Length >= 60000) return;
                    try { var pose = Parse(bytes, 60000); SakuraNativeCollabPose.Validate(pose); peer.LatestPose = pose; } catch (Exception) { }
                };
            }
            else { channel.Close(); channel.Dispose(); }
        }
        static JObject Parse(byte[] bytes, int maximum)
        {
            if (bytes.Length > maximum) throw new InvalidDataException("Collab control message is too large.");
            using (var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes))) using (var json = new JsonTextReader(text) { MaxDepth = 16 }) return JObject.Load(json);
        }
        async Task SendModel(Peer peer)
        {
            if (peer.Closed || peer.Sending || peer.Control?.ReadyState != RTCDataChannelState.Open || !viewport.HasAvatar) return;
            peer.Sending = true; string path = viewport.LoadedModelPath; string zip = Path.Combine(SakuraNativeCollabPackage.SessionFolder(peer.Session), "outgoing.zip");
            try
            {
                var package = await backend.DownloadNativeCollabPackage(path, zip, peer.Lifetime.Token);
                if (peer.Closed) return;
                long size = new FileInfo(package.file).Length;
                peer.Control.Send(new JObject { ["type"] = "package-start", ["size"] = size, ["entry"] = package.entry, ["name"] = package.name }.ToString(Formatting.None));
                using (var file = File.OpenRead(package.file))
                {
                    var buffer = new byte[16 * 1024]; int count;
                    while ((count = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        float deadline = Time.realtimeSinceStartup + 30;
                        while (peer.Control.BufferedAmount > 1024 * 1024) { peer.Lifetime.Token.ThrowIfCancellationRequested(); if (Time.realtimeSinceStartup > deadline) throw new IOException("Collaborator transfer stalled."); await Task.Delay(20, peer.Lifetime.Token); }
                        peer.Lifetime.Token.ThrowIfCancellationRequested(); peer.Control.Send(count == buffer.Length ? (byte[])buffer.Clone() : buffer.Take(count).ToArray());
                    }
                }
                peer.Control.Send("{\"type\":\"package-end\"}"); peer.ModelPath = path; Say("Selected model shared with " + peer.Id.Substring(0, Math.Min(8, peer.Id.Length)) + ".");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!peer.Closed) Say("Model share failed. " + error.Message); }
            finally { peer.Sending = false; if (File.Exists(zip)) File.Delete(zip); }
        }
        void ReceiveControl(Peer peer, byte[] bytes)
        {
            if (peer.Closed) return;
            try
            {
                // Reliable channel package chunks are binary until the declared byte count is complete.
                if (peer.Receive != null && peer.Receive.Length < peer.Expected)
                {
                    if (bytes.Length > 16 * 1024 || bytes.Length > peer.Expected - peer.Receive.Length) throw new InvalidDataException("Invalid or excessive model package chunk.");
                    peer.Receive.Write(bytes, 0, bytes.Length); return;
                }
                var message = Parse(bytes, 10000); string type = message.Value<string>("type");
                if (type == "package-start")
                {
                    if (peer.Importing || peer.Receive != null) throw new InvalidDataException("A collaborator model transfer is already pending.");
                    long size = message.Value<long>("size"); if (size < 1 || size > SakuraNativeCollabPackage.MaximumBytes) throw new InvalidDataException("Collab model exceeds 300 MB.");
                    peer.Entry = SakuraNativeCollabPackage.SafeEntry(message.Value<string>("entry")); peer.Name = (message.Value<string>("name") ?? "Avatar").Substring(0, Math.Min(200, (message.Value<string>("name") ?? "Avatar").Length));
                    peer.Expected = size; peer.Zip = Path.Combine(SakuraNativeCollabPackage.SessionFolder(peer.Session), "incoming.zip"); peer.Receive = new FileStream(peer.Zip, FileMode.Create, FileAccess.Write);
                }
                else if (type == "package-end")
                {
                    if (peer.Receive == null || peer.Receive.Length != peer.Expected) throw new InvalidDataException("Incomplete collaborator model transfer.");
                    peer.Receive.Dispose(); peer.Receive = null; _ = ImportModel(peer);
                }
                else if (type == "shared-chat" && ReceiveSharedMessages && Time.realtimeSinceStartupAsDouble - peer.ChatAt >= .5)
                {
                    string text = message.Value<string>("text"); if (text == null || text.Length > 500) return; peer.ChatAt = Time.realtimeSinceStartupAsDouble; AddMessage(peer.Id.Substring(0, Math.Min(8, peer.Id.Length)), text);
                }
            }
            catch (Exception error) { peer.Receive?.Dispose(); peer.Receive = null; Say("Collab receive failed. " + error.Message); }
        }
        async Task ImportModel(Peer peer)
        {
            peer.Importing = true;
            try
            {
                string folder = SakuraNativeCollabPackage.SessionFolder(peer.Session); string version = Guid.NewGuid().ToString("N"); string modelFolder = Path.Combine(folder, version);
                Directory.CreateDirectory(modelFolder);
                await Task.Run(() => SakuraNativeCollabPackage.Extract(peer.Zip, peer.Entry, modelFolder, peer.Lifetime.Token), peer.Lifetime.Token);
                if (peer.Closed) return;
                var owner = new GameObject("Collab avatar " + peer.Id); owner.transform.SetParent(transform, false); owner.transform.localPosition = new Vector3(1000 + ++slot * 100, 1000, 1000);
                var remote = owner.AddComponent<SakuraAvatarViewport>();
                var seat = new VisualElement(); seat.style.width = Length.Percent(48); seat.style.height = 260; seat.style.position = Position.Relative; seats?.Add(seat); remote.Attach(seat);
                await remote.LoadModel(modelFolder, peer.Entry, peer.Lifetime.Token);
                if (peer.Closed || !remote.HasAvatar) { Destroy(owner); seat.RemoveFromHierarchy(); throw new IOException(remote.LastImportError ?? "Remote avatar load was canceled."); }
                if (peer.Owner != null) Destroy(peer.Owner); peer.Seat?.RemoveFromHierarchy(); peer.Owner = owner; peer.Viewport = remote; peer.Seat = seat;
                if (remote.Idle != null) remote.Idle.enabled = false; if (remote.Expressions != null) { remote.Expressions.SuspendHotkeys = true; remote.Expressions.enabled = false; }
                var driver = remote.AvatarRoot.AddComponent<SakuraNativeCollabPose>(); driver.Initialize(remote.AvatarRoot); peer.Driver = driver;
                var label = new Label(peer.Id.Substring(0, Math.Min(8, peer.Id.Length)) + " · " + peer.Name); label.style.position = Position.Absolute; label.style.bottom = 0; seat.Add(label);
                Say("Shared avatar loaded · " + Participants + " participants.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!peer.Closed) Say("Could not load shared avatar. " + error.Message); }
            finally { peer.Importing = false; if (File.Exists(peer.Zip)) File.Delete(peer.Zip); }
        }
        void AddMessage(string speaker, string text) { shared.Add((speaker, text)); while (shared.Count > 12) shared.RemoveAt(0); Changed?.Invoke(); }
        public void SendSharedMessage(string text)
        {
            text = (text ?? "").Trim(); if (text.Length < 1 || text.Length > 500) throw new ArgumentException("Use a shared message of 1–500 characters.");
            ReceiveSharedMessages = true; AddMessage("Me", text); string json = new JObject { ["type"] = "shared-chat", ["text"] = text }.ToString(Formatting.None);
            foreach (var peer in peers.Values) if (peer.Control?.ReadyState == RTCDataChannelState.Open && !peer.Sending) peer.Control.Send(json);
        }
        void Update()
        {
            for (int index = 0; index < 16 && pending.TryDequeue(out var action); index++) action();
            if (lifetime == null) return;
            if (backend.Mode != "VTuber") { Disconnect(); Say("Collab ended when VTuber mode ended."); return; }
            if (!joined || Time.realtimeSinceStartupAsDouble < nextPose || !viewport.HasAvatar) return;
            nextPose = Time.realtimeSinceStartupAsDouble + .05;
            byte[] pose = Encoding.UTF8.GetBytes(SakuraNativeCollabPose.Capture(viewport.AvatarRoot).ToString(Formatting.None));
            foreach (var peer in peers.Values.ToArray())
            {
                if (!peer.Sending && peer.ModelPath != viewport.LoadedModelPath) _ = SendModel(peer);
                if (pose.Length < 60000 && peer.Pose?.ReadyState == RTCDataChannelState.Open && peer.Pose.BufferedAmount < 60000) peer.Pose.Send(pose);
                if (peer.Driver != null && peer.LatestPose != null) { peer.Driver.Receive(peer.LatestPose); peer.LatestPose = null; }
            }
        }
        void RemovePeer(string id) { if (id != null && peers.Remove(id, out var peer)) { peer.Dispose(); Say("A participant disconnected. " + Participants + " participants remain."); } }
        public void Disconnect()
        {
            generation++; joined = false; lifetime?.Cancel(); socket?.Abort(); socket?.Dispose(); socket = null;
            foreach (var peer in peers.Values.ToArray()) peer.Dispose(); peers.Clear();
            lifetime?.Dispose(); lifetime = null; if (webRtcUpdates != null) StopCoroutine(webRtcUpdates); webRtcUpdates = null;
            while (pending.TryDequeue(out _)) { } Say("Collab is off.");
        }
        public void BuildPanel(VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner); owner.Add(new Label("VTuber Collab · up to six participants"));
            var relay = new TextField("Relay WSS URL") { value = PlayerPrefs.GetString("sakura-native-collab-relay", "") }; owner.Add(relay);
            var room = new TextField("Room code"); owner.Add(room);
            owner.Add(new Button(() => room.value = GenerateRoom()) { text = "Create room code" });
            owner.Add(new Button(() => GUIUtility.systemCopyBuffer = room.value) { text = "Copy room code" });
            var advanced = new Foldout { text = "TURN server" }; owner.Add(advanced);
            var turn = new TextField("TURN URL"); var user = new TextField("Username"); var password = new TextField("Password") { isPasswordField = true }; advanced.Add(turn); advanced.Add(user); advanced.Add(password);
            var consent = new Toggle("Share my selected model files and live movement with each room participant"); owner.Add(consent);
            var status = new Label(Status); owner.Add(status);
            var connect = new Button(async () => { try { await ConnectAsync(relay.value, room.value, turn.value, user.value, password.value, consent.value); } catch (Exception error) { Say(error.Message); } }) { text = "Connect" }; owner.Add(connect);
            owner.Add(new Button(Disconnect) { text = "Disconnect" });
            var sharedToggle = new Toggle("Receive shared messages") { value = ReceiveSharedMessages }; owner.Add(sharedToggle); sharedToggle.RegisterValueChangedCallback(change => { ReceiveSharedMessages = change.newValue; if (!change.newValue) shared.Clear(); Changed?.Invoke(); });
            var chat = new TextField("Message to collaborators") { maxLength = 500 }; owner.Add(chat); owner.Add(new Button(() => { try { SendSharedMessage(chat.value); chat.value = ""; } catch (Exception error) { Say(error.Message); } }) { text = "Send to room" });
            var log = new Label(); log.style.whiteSpace = WhiteSpace.Normal; owner.Add(log);
            var stage = new VisualElement(); stage.style.flexDirection = FlexDirection.Row; stage.style.flexWrap = Wrap.Wrap; owner.Add(stage); seats = stage;
            foreach (var peer in peers.Values) if (peer.Seat != null) stage.Add(peer.Seat);
            void Refresh() { status.text = Status; connect.SetEnabled(lifetime == null); log.text = string.Join("\n", shared.Select(row => row.speaker + ": " + row.text)); }
            Changed += Refresh; owner.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= Refresh); Refresh();
        }
        void OnDisable() => Disconnect();
        void OnDestroy() => Disconnect();
    }
}
