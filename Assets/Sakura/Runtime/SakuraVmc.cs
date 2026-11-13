using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using UniVRM10;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(32000)]
    public sealed class SakuraVmc : MonoBehaviour
    {
        public readonly struct Pose
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public Pose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        }
        public sealed class Frame
        {
            public Pose Root;
            public readonly Dictionary<string, Pose> Bones = new Dictionary<string, Pose>();
            public readonly Dictionary<string, Pose> Trackers = new Dictionary<string, Pose>();
            public readonly Dictionary<string, float> Blendshapes = new Dictionary<string, float>();
        }
        [Serializable] sealed class Options
        {
            public string host = "127.0.0.1";
            public int port = 39539, frameRate = 30;
            public float unitScale = .08f;
            public bool mirror;
        }
        static readonly Dictionary<string, string[]> Aliases = CreateAliases();
        static readonly Dictionary<string, string> MirroredNames = new Dictionary<string, string> { ["Left"] = "Right", ["Right"] = "Left", ["left"] = "right", ["right"] = "left", ["_L"] = "_R", ["_R"] = "_L" };
        static readonly Dictionary<string, string[]> Vowels = new Dictionary<string, string[]>
        {
            ["A"] = new[] { "あ", "a", "A", "ああ", "口_あ", "mouth_a", "Mouth_A", "mouthOpen", "jawOpen" },
            ["I"] = new[] { "い", "i", "I", "口_い", "mouth_i", "Mouth_I" }, ["U"] = new[] { "う", "u", "U", "口_う", "mouth_u", "Mouth_U" },
            ["E"] = new[] { "え", "e", "E", "口_え", "mouth_e", "Mouth_E" }, ["O"] = new[] { "お", "o", "O", "口_お", "mouth_o", "Mouth_O" },
            ["Blink"] = new[] { "まばたき", "blink", "Blink", "目_まばたき", "ウィンク" }
        };
        static readonly Dictionary<string, string> Presets = new Dictionary<string, string>
        {
            ["A"] = "aa", ["I"] = "ih", ["U"] = "ou", ["E"] = "ee", ["O"] = "oh", ["Blink"] = "blink", ["Blink_L"] = "blinkLeft", ["Blink_R"] = "blinkRight",
            ["Joy"] = "happy", ["Angry"] = "angry", ["Sorrow"] = "sad", ["Fun"] = "relaxed", ["Neutral"] = "neutral", ["LookUp"] = "lookUp", ["LookDown"] = "lookDown", ["LookLeft"] = "lookLeft", ["LookRight"] = "lookRight"
        };
        Options options = new Options();
        SakuraAvatarViewport viewport;
        UdpClient socket;
        IPEndPoint target;
        HashSet<string> previousBlends = new HashSet<string>();
        Dictionary<string, Transform> mappedBones;
        GameObject mappedAvatar;
        Pose? origin;
        double nextFrame;
        int generation;
        bool initialized;
        public bool Sending => socket != null;
        public string Status { get; private set; } = "Off. Sends supported humanoid bones and expressions over OSC/UDP.";
        public long SentFrames { get; private set; }
        public long SentPackets { get; private set; }
        string SettingsFile => Path.Combine(Application.persistentDataPath, "vmc-send.json");

        public void Initialize(SakuraAvatarViewport avatarViewport)
        {
            if (initialized) return;
            viewport = avatarViewport; initialized = true;
            try
            {
                if (File.Exists(SettingsFile) && new FileInfo(SettingsFile).Length <= 8192)
                {
                    var saved = JsonUtility.FromJson<Options>(File.ReadAllText(SettingsFile));
                    if (saved != null) { Validate(saved.host, saved.port, saved.unitScale, saved.frameRate); options = saved; }
                }
            }
            catch (Exception error) { Status = "VMC settings could not be restored. " + error.Message; }
        }
        static void Finite(float value, float limit = 10000) { if (float.IsNaN(value) || float.IsInfinity(value) || Mathf.Abs(value) > limit) throw new ArgumentException("VMC numbers must be finite and within bounds."); }
        static void Validate(string host, int port, float units, int fps)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown) throw new ArgumentException("Enter a valid VMC IP address or host name.");
            if (port < 1 || port > 65535) throw new ArgumentException("VMC port must be between 1 and 65535.");
            Finite(units); if (units < .001f || units > 10) throw new ArgumentException("PMX meters per model unit must be between .001 and 10.");
            if (fps < 1 || fps > 60) throw new ArgumentException("VMC frame rate must be between 1 and 60.");
        }
        void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            string temporary = SettingsFile + ".tmp"; File.WriteAllText(temporary, JsonUtility.ToJson(options, true));
            if (File.Exists(SettingsFile)) File.Replace(temporary, SettingsFile, null); else File.Move(temporary, SettingsFile);
        }
        public async Task ConfigureAsync(string host, int port, bool enabled)
        {
            Stop(); if (!enabled) return;
            host = (host ?? "").Trim(); Validate(host, port, options.unitScale, options.frameRate);
            int version = generation;
            Status = "Resolving VMC receiver…";
            try
            {
                IPAddress address;
                if (!IPAddress.TryParse(host, out address))
                {
                    var addresses = await Dns.GetHostAddressesAsync(host);
                    address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                    if (address == null) throw new SocketException((int)SocketError.HostNotFound);
                }
                if (this == null || version != generation || !isActiveAndEnabled) return;
                var client = new UdpClient(address.AddressFamily);
                client.Client.Blocking = false;
                try { options.host = host; options.port = port; Save(); }
                catch { client.Dispose(); throw; }
                socket = client; target = new IPEndPoint(address, port); nextFrame = 0;
                Status = "Sending avatar VMC to " + host + ":" + port + ".";
            }
            catch (Exception error) { if (version == generation) { Status = "VMC could not start. " + error.Message; throw; } }
        }
        public void Stop()
        {
            generation++; socket?.Dispose(); socket = null; target = null; previousBlends.Clear(); Status = "VMC send is off.";
        }
        public void Recenter()
        {
            var frame = CurrentFrame(false);
            if (frame == null) throw new InvalidOperationException("Load a humanoid PMX or VRM avatar first.");
            origin = frame.Root; Status = "VMC root recentered. Match the receiver avatar and verify its scale.";
        }
        Frame CurrentFrame(bool calibrated)
        {
            if (viewport == null || !viewport.HasAvatar) return null;
            if (mappedAvatar != viewport.AvatarRoot)
            {
                mappedAvatar = viewport.AvatarRoot; mappedBones = MapBones(mappedAvatar); origin = null;
            }
            return Capture(mappedAvatar, viewport.LoadedFormat, options.unitScale, options.mirror, calibrated ? origin : null, mappedBones);
        }
        void LateUpdate()
        {
            if (!Sending || viewport == null || Time.realtimeSinceStartupAsDouble < nextFrame) return;
            nextFrame = Time.realtimeSinceStartupAsDouble + 1d / options.frameRate;
            try
            {
                var frame = CurrentFrame(true);
                if (frame == null) { Status = "VMC is waiting for a humanoid PMX or VRM avatar."; return; }
                var packets = EncodeFrame(frame, previousBlends, (float)(Time.realtimeSinceStartupAsDouble % 86400));
                foreach (var packet in packets) { socket.Send(packet, packet.Length, target); SentPackets++; }
                previousBlends = new HashSet<string>(frame.Blendshapes.Keys); SentFrames++;
                Status = "Sending avatar VMC to " + options.host + ":" + options.port + " · " + SentFrames + " frames.";
            }
            catch (Exception error) { Status = "VMC send failed. " + error.Message; }
        }
        void OnDisable() => Stop();
        void OnDestroy() => Stop();

        public void BuildPanel(VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner); owner.Add(new Label("VMC send"));
            var enabled = new Toggle("Send tracking to another app (VMC)") { value = Sending }; owner.Add(enabled);
            var host = new TextField("VMC host") { value = options.host, isDelayed = true }; owner.Add(host);
            var port = new IntegerField("VMC port") { value = options.port, isDelayed = true }; owner.Add(port);
            var units = new FloatField("PMX meters per model unit") { value = options.unitScale, isDelayed = true }; owner.Add(units);
            var mirror = new Toggle("Mirror VMC movement") { value = options.mirror }; owner.Add(mirror);
            var rate = new SliderInt("Send rate", 1, 60) { value = options.frameRate, showInputField = true }; owner.Add(rate);
            var status = new Label(Status); status.AddToClassList("description"); owner.Add(status);
            void Run(Action action) { try { action(); } catch (Exception error) { Status = error.Message; status.text = Status; } }
            async void Configure()
            {
                try { await ConfigureAsync(host.value, port.value, enabled.value); }
                catch (Exception error) { Status = error.Message; }
                enabled.SetValueWithoutNotify(Sending); status.text = Status;
            }
            enabled.RegisterValueChangedCallback(_ => Configure());
            void DestinationChanged()
            {
                if (enabled.value) Configure();
                else Run(() => { Validate(host.value, port.value, options.unitScale, options.frameRate); options.host = host.value.Trim(); options.port = port.value; Save(); });
            }
            host.RegisterValueChangedCallback(_ => DestinationChanged()); port.RegisterValueChangedCallback(_ => DestinationChanged());
            units.RegisterValueChangedCallback(change => Run(() => { Validate(host.value, port.value, change.newValue, rate.value); options.unitScale = change.newValue; origin = null; Save(); }));
            mirror.RegisterValueChangedCallback(change => Run(() => { options.mirror = change.newValue; origin = null; Save(); }));
            rate.RegisterValueChangedCallback(change => Run(() => { Validate(host.value, port.value, units.value, change.newValue); options.frameRate = change.newValue; Save(); }));
            owner.Add(new Button(() => Run(Recenter)) { text = "Recenter VMC root" });
            owner.Add(new Button(() => { origin = null; Status = "VMC root calibration reset."; }) { text = "Reset VMC origin" });
            var poll = status.schedule.Execute(() => { if (this != null) status.text = Status; }).Every(500);
            owner.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
        }
        public static Frame Capture(GameObject avatar, string format, float units, bool mirror, Pose? calibration = null)
            => avatar == null ? null : Capture(avatar, format, units, mirror, calibration, MapBones(avatar));
        static Frame Capture(GameObject avatar, string format, float units, bool mirror, Pose? calibration, Dictionary<string, Transform> bones)
        {
            if (format == "Live2D" || bones.Count == 0) return null;
            Finite(units); if (units < .001f || units > 10) throw new ArgumentException("Invalid PMX unit scale.");
            float scale = format == "PMX" ? units / .08f : 1;
            Pose World(Transform node) => Reflect(new Pose(node.position * scale, node.rotation), mirror);
            var frame = new Frame { Root = World(avatar.transform) };
            foreach (var entry in bones)
            {
                var bone = entry.Value; Vector3 position; Quaternion rotation;
                if (format == "PMX")
                {
                    var parent = CanonicalParent(entry.Key, bones, avatar.transform); var inverse = Quaternion.Inverse(parent.rotation);
                    position = inverse * (bone.position - parent.position); rotation = inverse * bone.rotation;
                }
                else { position = Vector3.Scale(bone.localPosition, bone.parent == null ? Vector3.one : bone.parent.lossyScale); rotation = bone.localRotation; }
                frame.Bones[mirror ? Swap(entry.Key) : entry.Key] = Reflect(new Pose(position * scale, rotation), mirror);
            }
            foreach (string name in new[] { "Head", "Spine", "LeftHand", "RightHand", "LeftFoot", "RightFoot" })
                if (bones.TryGetValue(name, out var bone)) frame.Trackers[mirror ? Swap(name) : name] = World(bone);
            if (calibration.HasValue)
            {
                frame.Root = Relative(frame.Root, calibration.Value);
                foreach (string name in frame.Trackers.Keys.ToArray()) frame.Trackers[name] = Relative(frame.Trackers[name], calibration.Value);
            }
            var vrm = avatar.GetComponent<Vrm10Instance>();
            if (vrm != null)
                foreach (var key in vrm.Runtime.Expression.ExpressionKeys) frame.Blendshapes[key.Name] = vrm.Runtime.Expression.GetWeight(key);
            else
                foreach (var mesh in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (mesh.sharedMesh == null) continue;
                    for (int index = 0; index < mesh.sharedMesh.blendShapeCount; index++)
                    {
                        string name = mesh.sharedMesh.GetBlendShapeName(index); float value = mesh.GetBlendShapeWeight(index) / 100;
                        if (!frame.Blendshapes.TryGetValue(name, out float previous) || value > previous) frame.Blendshapes[name] = value;
                    }
                }
            var values = frame.Blendshapes.ToArray(); frame.Blendshapes.Clear();
            var original = values.ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var preset in Presets)
            {
                string resolved = vrm == null && Vowels.TryGetValue(preset.Key, out var aliases) ? aliases.FirstOrDefault(original.ContainsKey) : null;
                string source = resolved ?? (original.ContainsKey(preset.Value) ? preset.Value : preset.Key.ToLowerInvariant());
                if (original.TryGetValue(source, out float value) && !float.IsNaN(value) && !float.IsInfinity(value)) frame.Blendshapes[mirror ? Swap(preset.Key) : preset.Key] = Mathf.Clamp01(value);
            }
            foreach (var entry in values)
                if (ValidName(entry.Key) && !float.IsNaN(entry.Value) && !float.IsInfinity(entry.Value) && frame.Blendshapes.Count < 256) frame.Blendshapes[mirror ? Swap(entry.Key) : entry.Key] = Mathf.Clamp01(entry.Value);
            return frame;
        }
        static Pose Reflect(Pose pose, bool mirror) => mirror ? new Pose(new Vector3(-pose.Position.x, pose.Position.y, pose.Position.z), new Quaternion(pose.Rotation.x, -pose.Rotation.y, -pose.Rotation.z, pose.Rotation.w)) : pose;
        static Pose Relative(Pose pose, Pose parent) { var inverse = Quaternion.Inverse(parent.Rotation); return new Pose(inverse * (pose.Position - parent.Position), inverse * pose.Rotation); }
        static string Swap(string name) => Regex.Replace(name, "Left|Right|left|right|_L\\b|_R\\b", match => MirroredNames[match.Value]);
        static string Normalize(string name) => Regex.Replace(Regex.Replace(name.ToLowerInvariant(), "^(?:mixamorig|armature|bip\\d*)[._ :\\-]*", ""), "[\\s_.:\\-]+", "");
        static Dictionary<string, Transform> MapBones(GameObject avatar)
        {
            var result = new Dictionary<string, Transform>();
            var vrm = avatar.GetComponent<Vrm10Instance>();
            if (vrm != null)
            {
                foreach (HumanBodyBones name in Enum.GetValues(typeof(HumanBodyBones)))
                    if (name != HumanBodyBones.LastBone && vrm.TryGetBoneTransform(name, out var bone) && bone != null) result[name.ToString()] = bone;
                return result;
            }
            var animator = avatar.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                foreach (HumanBodyBones name in Enum.GetValues(typeof(HumanBodyBones)))
                { if (name == HumanBodyBones.LastBone) continue; var bone = animator.GetBoneTransform(name); if (bone != null) result[name.ToString()] = bone; }
                return result;
            }
            var byName = new Dictionary<string, Transform>();
            foreach (var bone in avatar.GetComponentsInChildren<Transform>(true)) { string name = Normalize(bone.name); if (!byName.ContainsKey(name)) byName.Add(name, bone); }
            foreach (var pair in Aliases)
                foreach (string name in new[] { pair.Key }.Concat(pair.Value)) if (byName.TryGetValue(Normalize(name), out var bone)) { result[pair.Key] = bone; break; }
            return result;
        }
        static Transform CanonicalParent(string name, Dictionary<string, Transform> bones, Transform root)
        {
            Transform Find(params string[] names) { foreach (string key in names) if (bones.TryGetValue(key, out var bone)) return bone; return root; }
            switch (name)
            {
                case "Hips": return root;
                case "Spine": return Find("Hips");
                case "Chest": return Find("Spine");
                case "UpperChest": return Find("Chest", "Spine");
                case "Neck": return Find("UpperChest", "Chest", "Spine", "Hips");
                case "Head": return Find("Neck", "UpperChest", "Chest", "Spine", "Hips");
                case "Jaw": case "LeftEye": case "RightEye": return Find("Head");
            }
            string side = name.StartsWith("Left", StringComparison.Ordinal) ? "Left" : "Right";
            string part = name.Substring(side.Length);
            switch (part)
            {
                case "Shoulder": return Find("UpperChest", "Chest", "Spine", "Hips");
                case "UpperArm": return Find(side + "Shoulder", "UpperChest", "Chest", "Spine", "Hips");
                case "LowerArm": return Find(side + "UpperArm");
                case "Hand": return Find(side + "LowerArm");
                case "UpperLeg": return Find("Hips");
                case "LowerLeg": return Find(side + "UpperLeg");
                case "Foot": return Find(side + "LowerLeg");
                case "Toes": return Find(side + "Foot");
            }
            if (part.EndsWith("Intermediate", StringComparison.Ordinal)) return Find(side + part.Replace("Intermediate", "Proximal"));
            if (part.EndsWith("Distal", StringComparison.Ordinal)) return Find(side + part.Replace("Distal", "Intermediate"));
            return Find(side + "Hand");
        }
        static Dictionary<string, string[]> CreateAliases()
        {
            var aliases = new Dictionary<string, string[]>
            {
                ["Hips"] = new[] { "下半身", "腰", "骨盤", "pelvis" }, ["Spine"] = new[] { "上半身", "spine1" }, ["Chest"] = new[] { "上半身2", "上半身２", "spine2" }, ["UpperChest"] = new[] { "上半身3", "上半身３", "spine3" },
                ["Head"] = new[] { "頭" }, ["Neck"] = new[] { "首" }, ["Jaw"] = new[] { "あご", "顎" }, ["LeftEye"] = new[] { "左目", "eye.L", "J_Adj_L_FaceEye" }, ["RightEye"] = new[] { "右目", "eye.R", "J_Adj_R_FaceEye" }
            };
            foreach (string side in new[] { "Left", "Right" })
            {
                string jp = side == "Left" ? "左" : "右", suffix = side == "Left" ? "L" : "R";
                aliases[side + "Shoulder"] = new[] { jp + "肩", jp + "鎖骨", "shoulder." + suffix, side + "Clavicle" };
                aliases[side + "UpperArm"] = new[] { jp + "腕", side + "Arm", "upper_arm." + suffix };
                aliases[side + "LowerArm"] = new[] { jp + "ひじ", jp + "肘", side + "ForeArm", "forearm." + suffix };
                aliases[side + "Hand"] = new[] { jp + "手首", "hand." + suffix };
                aliases[side + "UpperLeg"] = new[] { jp + "足", jp + "腿", side + "UpLeg", side + "Leg", "thigh." + suffix };
                aliases[side + "LowerLeg"] = new[] { jp + "ひざ", jp + "膝", side + "Knee", "shin." + suffix };
                aliases[side + "Foot"] = new[] { jp + "足首", "foot." + suffix };
                aliases[side + "Toes"] = new[] { jp + "つま先", side + "ToeBase", "toe." + suffix };
                string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" }, japanese = { "親指", "人指", "中指", "薬指", "小指" }, joints = { "Proximal", "Intermediate", "Distal" };
                for (int finger = 0; finger < fingers.Length; finger++)
                    for (int joint = 0; joint < joints.Length; joint++)
                    { int digit = finger == 0 ? joint : joint + 1; aliases[side + fingers[finger] + joints[joint]] = new[] { jp + japanese[finger] + digit, jp + japanese[finger] + "０１２３"[digit], side + fingers[finger] + (joint + 1) }; }
            }
            return aliases;
        }

        static bool ValidName(string name) => !string.IsNullOrEmpty(name) && name.IndexOf('\0') < 0 && Encoding.UTF8.GetByteCount(name) <= 128;
        static void WriteString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value); stream.Write(bytes, 0, bytes.Length); stream.WriteByte(0);
            while (stream.Length % 4 != 0) stream.WriteByte(0);
        }
        static void WriteBigEndian(Stream stream, byte[] bytes) { if (BitConverter.IsLittleEndian) Array.Reverse(bytes); stream.Write(bytes, 0, bytes.Length); }
        public static byte[] EncodeMessage(string address, params object[] values)
        {
            if (string.IsNullOrEmpty(address) || address[0] != '/' || address.IndexOf('\0') >= 0) throw new ArgumentException("Invalid OSC address.");
            using (var stream = new MemoryStream())
            {
                WriteString(stream, address); var tags = new StringBuilder(",");
                foreach (var value in values) { if (value is string) tags.Append('s'); else if (value is float) tags.Append('f'); else if (value is int) tags.Append('i'); else throw new ArgumentException("Unsupported OSC argument."); }
                WriteString(stream, tags.ToString());
                foreach (var value in values)
                {
                    if (value is string text) { if (!ValidName(text)) throw new ArgumentException("Invalid VMC name."); WriteString(stream, text); }
                    else if (value is float number) { Finite(number, float.MaxValue); WriteBigEndian(stream, BitConverter.GetBytes(number)); }
                    else WriteBigEndian(stream, BitConverter.GetBytes((int)value));
                }
                return stream.ToArray();
            }
        }
        static byte[] TransformMessage(string address, string name, Pose pose)
        {
            foreach (float value in new[] { pose.Position.x, pose.Position.y, pose.Position.z, pose.Rotation.x, pose.Rotation.y, pose.Rotation.z, pose.Rotation.w }) Finite(value);
            if (Quaternion.Dot(pose.Rotation, pose.Rotation) < 1e-12f) throw new ArgumentException("Invalid VMC quaternion.");
            var rotation = pose.Rotation.normalized;
            return EncodeMessage(address, name, pose.Position.x, pose.Position.y, pose.Position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        }
        public static IReadOnlyList<byte[]> EncodeFrame(Frame frame, IEnumerable<string> previousNames, float time)
        {
            Finite(time, 86400);
            if (frame == null || frame.Bones.Count > 55 || frame.Trackers.Count > 16 || frame.Blendshapes.Count > 256) throw new ArgumentException("Invalid VMC frame collections.");
            var messages = new List<byte[]>();
            foreach (string name in previousNames.Distinct().OrderBy(name => name)) if (!frame.Blendshapes.ContainsKey(name)) messages.Add(EncodeMessage("/VMC/Ext/Blend/Val", name, 0f));
            messages.Add(TransformMessage("/VMC/Ext/Root/Pos", "root", frame.Root));
            foreach (var bone in frame.Bones) { if (!Aliases.ContainsKey(bone.Key)) throw new ArgumentException("Unknown VMC humanoid bone."); messages.Add(TransformMessage("/VMC/Ext/Bone/Pos", bone.Key, bone.Value)); }
            foreach (var tracker in frame.Trackers) messages.Add(TransformMessage("/VMC/Ext/Tra/Pos", tracker.Key, tracker.Value));
            foreach (var blend in frame.Blendshapes) { Finite(blend.Value); messages.Add(EncodeMessage("/VMC/Ext/Blend/Val", blend.Key, Mathf.Clamp01(blend.Value))); }
            messages.Add(EncodeMessage("/VMC/Ext/Blend/Apply")); messages.Add(EncodeMessage("/VMC/Ext/OK", 1)); messages.Add(EncodeMessage("/VMC/Ext/T", time % 86400));
            var packets = new List<byte[]>();
            using (var stream = new MemoryStream())
            {
                void Header() { stream.SetLength(0); stream.Position = 0; WriteString(stream, "#bundle"); stream.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, 0, 8); }
                Header();
                foreach (var message in messages)
                {
                    if (message.Length + 20 > 1400) throw new ArgumentException("VMC message exceeds packet size.");
                    if (stream.Length + message.Length + 4 > 1400) { packets.Add(stream.ToArray()); Header(); }
                    WriteBigEndian(stream, BitConverter.GetBytes(message.Length)); stream.Write(message, 0, message.Length);
                }
                if (stream.Length > 16) packets.Add(stream.ToArray());
            }
            return packets;
        }
    }
}
