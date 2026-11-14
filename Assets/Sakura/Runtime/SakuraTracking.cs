using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UniVRM10;

namespace Sakura.Unity
{
    // The transport remains on the same authenticated loopback connection as chat.
    public sealed partial class SakuraBackend
    {
        public void UploadTrackingFrame(byte[] jpeg, int width, int height, Action<string, string> completed, bool cpu = false, bool hands = true, bool pose = true)
        {
            if (!Connected || jpeg == null || jpeg.Length == 0 || jpeg.Length > 4 * 1024 * 1024 || width < 1 || height < 1 || width > 1920 || height > 1080)
            { completed(null, "Invalid or disconnected camera frame."); return; }
            StartCoroutine(UploadTracking(jpeg, width, height, completed, cpu, hands, pose));
        }
        IEnumerator UploadTracking(byte[] jpeg, int width, int height, Action<string, string> completed, bool cpu, bool hands, bool pose)
        {
            string route = (cpu ? "/api/unity/tracking/frame" : "/api/tracking/nvidia-ar/frame") + "?width=" + width + "&height=" + height + (cpu ? "&hands=" + hands.ToString().ToLowerInvariant() + "&pose=" + pose.ToString().ToLowerInvariant() : "");
            using (var request = Request(route))
            {
                request.method = "POST"; request.uploadHandler = new UploadHandlerRaw(jpeg); request.SetRequestHeader("Content-Type", "image/jpeg");
                yield return request.SendWebRequest();
                completed(request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : null,
                    request.result == UnityWebRequest.Result.Success ? null : ResponseError(request.downloadHandler?.text, request.error));
            }
        }
    }

    [DefaultExecutionOrder(-30)]
    public sealed class SakuraTracking : MonoBehaviour
    {
        public enum InputSource { Off, Cursor, Phone, NvidiaExpressionApp, NvidiaAR, VBridger, Webcam }
        public InputSource Source { get; private set; }
        public bool Hands = true, Pose = true, Mouth = true;
        public float Smoothing = .07f;
        public string Status { get; private set; } = "Tracking off";
        public bool FaceDetected => Source != InputSource.Off && Source != InputSource.Cursor && sample?["face"] is JObject && Time.unscaledTime - lastFrame < .6f;
        public string PhoneInfo { get; private set; } = "Phone link has not been read.";
        public JArray PhoneDevices { get; private set; } = new JArray();
        public event Action Changed;
        public event Action<string> PhoneModelRequested;
        private InputSource selectedSource = InputSource.Webcam;
        private string selectedCamera;
        SakuraBackend backend;
        SakuraAvatarViewport viewport;
        SakuraTrackingRig rig;
        int generation;
        bool pending, uploadPending;
        bool phoneTrackingEnabled;
        bool phoneLinkEnabled, phoneCommandPending;
        float nextPoll, lastFrame = -100, nextCamera;
        float nextPhoneCommandPoll;
        WebCamTexture camera;
        Texture2D cameraPixels;
        JObject sample;
        public string[] InputNames => new[] { "FaceAngleX", "FaceAngleY", "FaceAngleZ", "MouseX", "MouseY" }
            .Concat(sample?["face"]?["blendshapes"] is JObject scores ? scores.Properties().Select(property => property.Name)
                : sample?["face"]?["blendshapes"] is JArray values ? values.OfType<JObject>().Select(value => value.Value<string>("categoryName")).Where(value => !string.IsNullOrEmpty(value)) : Enumerable.Empty<string>()).Distinct().Take(520).ToArray();
        public bool GetInputValue(string name, out float value)
        {
            value = 0;
            if (Source == InputSource.Off || rig == null) return false;
            if (name == "MouseX" || name == "MouseY")
            {
                if (Source != InputSource.Cursor) return false;
                value = name == "MouseX" ? rig.CursorValue.x : rig.CursorValue.y; return true;
            }
            if (name == "FaceAngleX" || name == "FaceAngleY" || name == "FaceAngleZ")
            {
                if (Source != InputSource.Cursor && !FaceDetected) return false;
                var angles = rig.InputAngles;
                value = name == "FaceAngleX" ? angles.y : name == "FaceAngleY" ? -angles.x : angles.z; return true;
            }
            if (!FaceDetected) return false;
            var blend = sample?["face"]?["blendshapes"];
            JToken score = blend is JObject map ? map[name] : (blend as JArray)?.OfType<JObject>().FirstOrDefault(row => row.Value<string>("categoryName") == name)?["score"];
            if (score == null || score.Type != JTokenType.Float && score.Type != JTokenType.Integer) return false;
            value = score.Value<float>(); return float.IsFinite(value);
        }
        public void Initialize(SakuraBackend service, SakuraAvatarViewport avatars) { backend = service; viewport = avatars; }
        void Notify(string message) { Status = message; Changed?.Invoke(); }
        string Route(InputSource value) => value == InputSource.Webcam ? "/api/unity/tracking" : value == InputSource.NvidiaAR ? "/api/tracking/nvidia-ar" : value == InputSource.NvidiaExpressionApp ? "/api/tracking/nvidia-expressionapp" : "/api/tracking/vbridger";
        public static bool? PhoneTrackingStateForCommand(string kind)
        {
            if (kind == "tracking-start" || kind == "companion-tracking-start") return true;
            if (kind == "tracking-stop" || kind == "companion-tracking-stop" || kind == "camera-start") return false;
            return null;
        }
        public static string PhoneDeckPoseForCommand(string name)
        {
            if (name == "stop") return name;
            return new[] { "greeting_wave", "hands_both", "hand_left", "hand_right", "groove", "step", "wave", "twist", "disco", "march" }
                .Contains(name) ? name : null;
        }
        public static string PhoneModelUrlForPath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("/", StringComparison.Ordinal) || relative.Contains("\\")) return null;
            var parts = relative.Split('/');
            if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == "..")) return null;
            return "/models/" + string.Join("/", parts.Select(Uri.EscapeDataString));
        }
        public void SetSource(InputSource value)
        {
            if (value != InputSource.Off) selectedSource = value;
            var old = Source; int revision = ++generation; Source = value; pending = false; sample = null; lastFrame = -100;
            if (old == InputSource.Phone && value != InputSource.Phone) PhoneControl("stop", true);
            phoneTrackingEnabled = value != InputSource.Phone && value != InputSource.Off;
            rig?.ResetTracking(); StopCamera();
            if (backend != null && (old == InputSource.Webcam || old == InputSource.NvidiaAR || old == InputSource.NvidiaExpressionApp)) backend.Api(Route(old) + "/stop", "{}", (_, error) => { });
            Notify(value == InputSource.Off ? "Tracking off" : "Waiting for " + value);
            if (backend != null && (value == InputSource.Webcam || value == InputSource.NvidiaAR || value == InputSource.NvidiaExpressionApp))
            {
                pending = true;
                backend.Api(Route(value) + "/start", "{}", (_, error) =>
                {
                    if (revision != generation) return;
                    pending = false;
                    if (error != null) { SetSource(InputSource.Off); Notify(error); }
                    else Notify(value == InputSource.Webcam ? "CPU tracker ready. Start a native camera below." : value == InputSource.NvidiaAR ? "RTX tracker ready. Start a native camera below." : "NVIDIA tracker started; waiting for camera output.");
                });
            }
        }
        public void Calibrate() { rig?.Calibrate(sample); Notify("Neutral calibrated from the latest valid frame."); }
        public void SetHandsTracking(bool enabled)
        {
            if (Hands == enabled) return;
            if (Source == InputSource.Phone)
            {
                if (backend == null)
                {
                    Notify("Phone hand tracking unavailable: PC backend is not connected.");
                    return;
                }
                bool previous = Hands;
                Hands = enabled;
                Notify("Sending hand-tracking setting to Sakura Mobile…");
                backend.Api("/api/mobile/tracking-control", new JObject { ["action"] = "hands", ["enabled"] = enabled }.ToString(), (_, error) =>
                {
                    if (error != null)
                    {
                        Hands = previous;
                        Notify("Phone hand-tracking change failed; previous setting restored. " + error);
                        return;
                    }
                    Notify(enabled ? "Phone hand tracking on." : "Phone hand tracking off; face and body tracking remain active.");
                });
                return;
            }
            Hands = enabled;
            Notify(enabled ? "Hand tracking on." : "Hand tracking off; face and body tracking remain active.");
        }
        public void PhoneLink(string body = null)
        {
            backend.Api("/api/mobile/link", body, (json, error) =>
            {
                if (error != null) PhoneInfo = error;
                else
                {
                    try
                    {
                        var info = JObject.Parse(json);
                        phoneLinkEnabled = info.Value<bool>("enabled");
                        PhoneDevices = info["devices"] as JArray ?? new JArray();
                        PhoneInfo = info.Value<bool>("enabled")
                            ? "Choose this PC in Sakura Mobile: " + (info.Value<string>("name") ?? Environment.MachineName)
                              + "\nPairing code: " + (info.Value<string>("code") ?? "")
                              + "\nCertificate fingerprint: " + (info.Value<string>("fingerprint") ?? "Unavailable")
                              + "\nPaired: " + (info.Value<int?>("paired") ?? 0)
                            : "Phone link disabled";
                    }
                    catch (Exception exception) { PhoneInfo = exception.Message; }
                }
                Changed?.Invoke();
            });
        }
        public void PhoneControl(string action, bool enabled)
        {
            if (backend == null) { Notify("PC backend is unavailable."); return; }
            backend.Api("/api/mobile/tracking-control", new JObject { ["action"] = action, ["enabled"] = enabled }.ToString(), (_, error) => { if (error != null) Notify(error); });
        }
        bool RequestPhoneTracking(string action)
        {
            if (backend == null)
            {
                if (action == "start") { phoneTrackingEnabled = false; sample = null; rig?.Accept(null); }
                Notify("PC backend is unavailable.");
                return false;
            }
            backend.Api("/api/mobile/tracking-control", new JObject { ["action"] = action }.ToString(), (_, error) =>
            {
                if (error == null) return;
                if (action == "start")
                {
                    phoneTrackingEnabled = false;
                    sample = null;
                    rig?.Accept(null);
                }
                Notify(error);
            });
            return true;
        }
        public void StartSelectedTracking(string cameraDevice)
        {
            if (cameraDevice == "Sakura Mobile")
            {
                if (Source != InputSource.Phone) SetSource(InputSource.Phone);
                StartTracking();
                return;
            }
            if (Source == InputSource.Off || Source == InputSource.Phone) SetSource(InputSource.Webcam);
            StartCamera(cameraDevice);
        }
        public void StartCamera(string device)
        {
            StopCamera();
            if (Source != InputSource.NvidiaAR && Source != InputSource.Webcam) { Notify("Choose Webcam or NVIDIA AR before opening a native camera."); return; }
            if (!WebCamTexture.devices.Any(item => item.name == device)) { Notify("Choose an available camera."); return; }
            try { selectedCamera = device; camera = new WebCamTexture(device, 640, 480, 30); camera.Play(); Notify("Native camera opened for local " + (Source == InputSource.Webcam ? "CPU" : "RTX") + " inference."); }
            catch (Exception error) { StopCamera(); Notify(error.Message); }
        }
        void StopCamera() { if (camera != null) { camera.Stop(); Destroy(camera); camera = null; } if (cameraPixels != null) { Destroy(cameraPixels); cameraPixels = null; } }
        public void StartTracking()
        {
            if (Source == InputSource.Off) SetSource(selectedSource);
            if (Source == InputSource.Phone)
            {
                phoneTrackingEnabled = true;
                if (RequestPhoneTracking("start")) Notify("Phone tracking requested · waiting for Sakura Mobile");
                return;
            }
            if (Source != InputSource.Webcam && Source != InputSource.NvidiaAR) return;
            string device = WebCamTexture.devices.Any(item => item.name == selectedCamera) ? selectedCamera : WebCamTexture.devices.FirstOrDefault().name;
            StartCamera(device);
        }
        public void StopTracking()
        {
            if (Source == InputSource.Phone)
            {
                phoneTrackingEnabled = false; sample = null; rig?.Accept(null);
                if (RequestPhoneTracking("stop")) Notify("Phone tracking stopped · connection kept ready");
                return;
            }
            SetSource(InputSource.Off);
        }
        void Update()
        {
            if (viewport == null) return;
            var root = viewport.AvatarRoot;
            if (rig == null || rig.gameObject != root)
            {
                if (rig != null) rig.ResetTracking();
                rig = root == null ? null : root.GetComponent<SakuraTrackingRig>() ?? root.AddComponent<SakuraTrackingRig>();
                if (rig != null) rig.Initialize(this);
            }
            if (rig != null)
            {
                rig.Active = Source != InputSource.Off && (Source != InputSource.Phone || phoneTrackingEnabled);
                if (viewport.Idle != null) viewport.Idle.MotionOwned = rig.Active || GetComponent<SakuraAvatarActions>()?.Active == true;
            }
            if (Source != InputSource.Phone) PollPhoneCommands();
            if (Source == InputSource.Off) return;
            if (Source == InputSource.Cursor)
            {
                var position = Mouse.current?.position.ReadValue() ?? new Vector2(Screen.width / 2f, Screen.height / 2f);
                rig?.Cursor(viewport.CursorDirection(position, rig.CursorOrigin));
                return;
            }
            if (Time.unscaledTime - lastFrame > .6f)
            {
                rig?.Accept(null);
                if (Source == InputSource.Phone && phoneTrackingEnabled && Status != "Waiting for tracking frames from Sakura Mobile · tap Start camera tracking on the phone or allow PC camera control")
                    Notify("Waiting for tracking frames from Sakura Mobile · tap Start camera tracking on the phone or allow PC camera control");
            }
            if (Source == InputSource.Webcam) return;
            if (backend == null || pending || Time.unscaledTime < nextPoll) return;
            pending = true; nextPoll = Time.unscaledTime + (Source == InputSource.Phone ? .016f : .1f); int revision = generation;
            string body = Source == InputSource.Phone ? PhoneState().ToString() : null;
            backend.Api(Source == InputSource.Phone ? "/api/mobile/poll" : Route(Source), body, (json, error) =>
            {
                if (revision != generation) return; pending = false;
                if (error != null) { Notify(error); return; }
                try
                {
                    var data = JObject.Parse(json);
                    if (Source == InputSource.Phone)
                    {
                        foreach (var command in (data["commands"] as JArray ?? new JArray()).OfType<JObject>()) PhoneCommand(command);
                        if (phoneTrackingEnabled && data["frame"] is JObject frame) Receive(frame);
                    }
                    else if (data.Value<bool?>("connected") == true && data.Value<bool?>("face_detected") != false)
                        Receive(new JObject { ["face"] = new JObject { ["blendshapes"] = data["blendshapes"]?.DeepClone() ?? new JObject() } });
                    else { sample = null; rig?.Accept(null); Notify("Waiting for a detected face from " + Source); }
                }
                catch (Exception exception) { Notify("Invalid tracking response: " + exception.Message); }
            });
        }
        JObject PhoneState() => new JObject { ["edition"] = backend.Mode == "VTuber" ? "vtuber" : "companion", ["tracking"] = Source == InputSource.Phone && phoneTrackingEnabled, ["model"] = viewport.LoadedModelPath ?? "", ["expressions"] = new JArray(viewport.Expressions?.Entries.Select(entry => entry.Name) ?? Enumerable.Empty<string>()), ["active_expressions"] = new JArray(viewport.Expressions?.Entries.Where(entry => entry.Active).Select(entry => entry.Name) ?? Enumerable.Empty<string>()) };
        void PollPhoneCommands()
        {
            if (!phoneLinkEnabled || backend == null || phoneCommandPending || Time.unscaledTime < nextPhoneCommandPoll) return;
            phoneCommandPending = true; nextPhoneCommandPoll = Time.unscaledTime + .25f;
            backend.Api("/api/mobile/poll", PhoneState().ToString(), (json, error) =>
            {
                phoneCommandPending = false;
                if (error != null || string.IsNullOrEmpty(json)) return;
                try
                {
                    var data = JObject.Parse(json);
                    foreach (var command in (data["commands"] as JArray ?? new JArray()).OfType<JObject>()) PhoneCommand(command);
                }
                catch (Exception exception) { Notify("Invalid phone command response: " + exception.Message); }
            });
        }
        void PhoneCommand(JObject command)
        {
            string kind = command.Value<string>("type");
            if (kind == "request-model")
            {
                string requestId = command.Value<string>("request_id");
                if (ValidPhoneRequestId(requestId)) { Notify("Sakura Mobile requested a model. Choose one from the PC library."); PhoneModelRequested?.Invoke(requestId); }
                else Notify("Rejected a phone model request with an invalid id.");
                return;
            }
            bool? trackingState = PhoneTrackingStateForCommand(kind);
            if (trackingState.HasValue)
            {
                phoneTrackingEnabled = trackingState.Value;
                if (!phoneTrackingEnabled) { sample = null; rig?.Accept(null); }
                Notify(phoneTrackingEnabled ? "Phone tracking started" : "Phone tracking stopped");
            }
            if (kind == "calibrate") Calibrate();
            else if (kind == "tracking-stop" || kind == "companion-tracking-stop") { sample = null; rig?.Accept(null); }
            else if (kind == "clear-expressions") viewport.Expressions?.Clear();
            else if (kind == "expression" && viewport.Expressions != null)
            {
                var entry = viewport.Expressions.Entries.FirstOrDefault(item => item.Name == command.Value<string>("name"));
                if (entry != null) { if (command.Value<bool?>("enabled") != false) viewport.Expressions.Clear(); viewport.Expressions.SetActive(entry.Id, command.Value<bool?>("enabled") != false); }
            }
            else if (kind == "deck-pose")
            {
                string pose = PhoneDeckPoseForCommand(command.Value<string>("name"));
                var actions = GetComponent<SakuraAvatarActions>();
                if (pose == "stop") actions?.Dispatch(new JObject { ["avatar_motion"] = "stop" });
                else if (pose != null && actions != null && actions.Gesture(pose)) Notify("Phone hotkey · " + pose.Replace('_', ' '));
                else Notify("This phone pose needs a compatible 3D character with tracking stopped.");
            }
            else if (kind != "tracking-start" && kind != "companion-tracking-start" && kind != "tracking-stop" && kind != "companion-tracking-stop" && kind != "camera-start") Notify("Phone command not yet migrated: " + kind);
        }
        public static bool ValidPhoneRequestId(string value) => value != null && value.Length == 32 && value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
        public void StageModelForPhone(string path, string requestId, Action<bool, string> completed = null)
        {
            string url = PhoneModelUrlForPath(path);
            if (!ValidPhoneRequestId(requestId) || url == null || backend == null)
            { completed?.Invoke(false, "This phone model request is invalid or expired."); return; }
            backend.Api("/api/mobile/model", new JObject { ["model"] = url, ["request_id"] = requestId }.ToString(), (json, error) =>
            {
                if (error != null) { Notify(error); completed?.Invoke(false, error); return; }
                try
                {
                    var response = JObject.Parse(json);
                    string name = (string)response["model"]?["name"] ?? System.IO.Path.GetFileName(path);
                    Notify("Model ready for Sakura Mobile · " + name); completed?.Invoke(true, name);
                }
                catch (Exception exception) { Notify("Phone transfer could not be confirmed: " + exception.Message); completed?.Invoke(false, exception.Message); }
            });
        }
        public void SendCurrentModelToPhone()
        {
            if (backend == null || viewport == null || string.IsNullOrWhiteSpace(viewport.LoadedModelPath))
            { Notify("Load a model and connect the PC backend first."); return; }
            string url = PhoneModelUrlForPath(viewport.LoadedModelPath);
            if (url == null) { Notify("The selected model path is invalid for phone transfer."); return; }
            backend.Api("/api/mobile/model", new JObject { ["model"] = url }.ToString(), (json, error) =>
            {
                if (error != null) { Notify(error); return; }
                try
                {
                    var response = JObject.Parse(json);
                    Notify("Model ready for Sakura Mobile · " + (response["model"]?.Value<string>("name") ?? System.IO.Path.GetFileName(viewport.LoadedModelPath)));
                }
                catch (Exception exception) { Notify("Phone transfer could not be confirmed: " + exception.Message); }
            });
        }
        public void Receive(JObject frame)
        {
            if (!SakuraTrackingRig.ValidFrame(frame)) throw new ArgumentException("Tracking frame contains invalid or excessive signals.");
            sample = frame; lastFrame = Time.unscaledTime; rig?.Accept(frame); Notify("Receiving " + Source + " tracking");
        }
        void LateUpdate()
        {
            if (camera == null || !camera.isPlaying || !camera.didUpdateThisFrame || camera.width < 32 || uploadPending || Time.unscaledTime < nextCamera || backend == null) return;
            nextCamera = Time.unscaledTime + .066f;
            if (camera.width > 1920 || camera.height > 1080) { StopCamera(); Notify("Camera supplied oversized frames. Choose a lower camera resolution."); return; }
            try
            {
                if (cameraPixels == null || cameraPixels.width != camera.width || cameraPixels.height != camera.height) { if (cameraPixels != null) Destroy(cameraPixels); cameraPixels = new Texture2D(camera.width, camera.height, TextureFormat.RGB24, false); }
                cameraPixels.SetPixels32(camera.GetPixels32()); cameraPixels.Apply(); uploadPending = true; int revision = generation;
                bool cpu = Source == InputSource.Webcam;
                backend.UploadTrackingFrame(cameraPixels.EncodeToJPG(78), camera.width, camera.height, (json, error) =>
                {
                    uploadPending = false; if (revision != generation) return;
                    if (error != null) { Notify(error); return; }
                    if (cpu) { try { var result = JObject.Parse(json); if (result["frame"] is JObject received) Receive(received); } catch (Exception exception) { Notify(exception.Message); } }
                }, cpu, Hands, Pose);
            }
            catch (Exception error) { uploadPending = false; Notify(error.Message); }
        }
        void OnDisable() { SetSource(InputSource.Off); if (viewport?.Idle != null) viewport.Idle.MotionOwned = false; }
    }

    [DefaultExecutionOrder(-15)]
    public sealed class SakuraTrackingRig : MonoBehaviour, ICubismUpdatable
    {
        public bool Active;
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 850;
        public bool NeedsUpdateOnEditing => false;
        SakuraTracking owner;
        JObject frame;
        CubismModel cubism;
        Vrm10Instance vrm;
        Transform head, chest;
        public Vector3? CursorOrigin => head == null ? null : head.position;
        public Vector2 CursorValue => cursor;
        public Vector3 InputAngles => new Vector3(Mathf.DeltaAngle(0, smooth.eulerAngles.x), Mathf.DeltaAngle(0, smooth.eulerAngles.y), Mathf.DeltaAngle(0, smooth.eulerAngles.z));
        Quaternion neutral = Quaternion.identity;
        bool calibrated;
        Vector2 cursor;
        readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
        readonly Dictionary<Transform, Quaternion> writes = new Dictionary<Transform, Quaternion>();
        readonly Dictionary<CubismParameter, float> parameterRest = new Dictionary<CubismParameter, float>();
        readonly Dictionary<string, float> scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> previousScores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public void Initialize(SakuraTracking tracking)
        {
            if (owner != null) return; owner = tracking; cubism = GetComponent<CubismModel>(); vrm = GetComponent<Vrm10Instance>();
            head = Bone(HumanBodyBones.Head, "頭", "head"); chest = Bone(HumanBodyBones.Chest, "上半身", "chest");
            foreach (var bone in GetComponentsInChildren<Transform>(true)) rest[bone] = bone.localRotation;
            if (cubism != null) { foreach (var parameter in cubism.Parameters) parameterRest[parameter] = parameter.DefaultValue; var controller = GetComponent<CubismUpdateController>(); HasUpdateController = controller != null; controller?.Refresh(); }
        }
        Transform Bone(HumanBodyBones id, params string[] aliases)
        {
            var animator = GetComponent<Animator>();
            if (animator != null && animator.isHuman) { var bone = animator.GetBoneTransform(id); if (bone != null) return bone; }
            return GetComponentsInChildren<Transform>(true).FirstOrDefault(item => aliases.Any(alias => string.Equals(alias, item.name, StringComparison.OrdinalIgnoreCase)));
        }
        public static bool ValidFrame(JObject value)
        {
            if (value == null || value.ToString(Newtonsoft.Json.Formatting.None).Length > 512000) return false;
            bool Number(JToken token) => token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) && !double.IsNaN(token.Value<double>()) && !double.IsInfinity(token.Value<double>()) && Math.Abs(token.Value<double>()) < 1e8;
            bool Points(JToken token, int count) => token is JArray points && points.Count == count && points.All(point => point is JObject && Number(point["x"]) && Number(point["y"]) && Number(point["z"]) && (point["visibility"] == null || Number(point["visibility"])));
            if (value.Descendants().OfType<JValue>().Any(item => item.Type == JTokenType.Float && !Number(item))) return false;
            if (value["face"] != null && value["face"].Type != JTokenType.Null)
            {
                if (!(value["face"] is JObject face)) return false;
                var matrix = face["matrix"]; if (matrix != null && matrix.Type != JTokenType.Null && (!(matrix is JArray elements) || elements.Count != 16 || !elements.All(Number))) return false;
                var blend = face["blendshapes"];
                if (blend is JObject map) { if (map.Count > 512 || map.Properties().Any(property => property.Name.Length > 128 || !Number(property.Value))) return false; }
                else if (blend is JArray list) { if (list.Count > 512 || list.Any(item => !(item is JObject) || item["categoryName"]?.Type != JTokenType.String || item.Value<string>("categoryName").Length > 128 || !Number(item["score"]))) return false; }
                else if (blend != null && blend.Type != JTokenType.Null) return false;
            }
            if (value["hands"] != null && value["hands"].Type != JTokenType.Null)
            {
                if (!(value["hands"] is JArray hands) || hands.Count > 2 || hands.Any(hand => !(hand is JObject) || !Points(hand["landmarks"], 21) || (hand["world"] != null && hand["world"].Type != JTokenType.Null && !Points(hand["world"], 21)))) return false;
            }
            var pose = value["pose"];
            if (pose != null && pose.Type != JTokenType.Null && (!(pose is JObject) || !Points(pose["landmarks"], 33) || (pose["world"] != null && pose["world"].Type != JTokenType.Null && !Points(pose["world"], 33)))) return false;
            return value["handTrackingEnabled"] == null || value["handTrackingEnabled"].Type == JTokenType.Boolean;
        }
        public void Accept(JObject value) { frame = value; cursor = Vector2.zero; }
        public void Cursor(Vector2 point) { frame = null; cursor = point; }
        public static bool FaceRotation(JObject face, out Quaternion result)
        {
            result = Quaternion.identity; var values = face?["matrix"] as JArray; if (values == null || values.Count != 16) return false;
            var matrix = new Matrix4x4(); for (int index = 0; index < 16; index++) { if (!float.TryParse(values[index].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || float.IsInfinity(number)) return false; matrix[index] = number; }
            Vector3 forward = matrix.GetColumn(2), up = matrix.GetColumn(1); if (forward.sqrMagnitude < .1f || up.sqrMagnitude < .1f || Vector3.Cross(forward, up).sqrMagnitude < .01f) return false;
            // MediaPipe is right handed; Unity mirrors the Z axis.
            result = Quaternion.LookRotation(new Vector3(-forward.x, -forward.y, forward.z), new Vector3(up.x, up.y, -up.z)); return true;
        }
        public void Calibrate(JObject value) { calibrated = FaceRotation(value?["face"] as JObject, out neutral); }
        public void ResetTracking()
        {
            frame = null; cursor = Vector2.zero; calibrated = false; smooth = Quaternion.identity;
            foreach (var item in writes) if (item.Key != null) item.Key.localRotation = rest[item.Key]; writes.Clear();
            foreach (var name in previousScores) Morph(name, 0); previousScores.Clear();
            if (cubism != null) foreach (var parameter in parameterRest) parameter.Key.Value = parameter.Value;
        }
        void Update()
        {
            foreach (var item in writes) if (item.Key != null && Quaternion.Angle(item.Key.localRotation, item.Value) < .001f) item.Key.localRotation = rest[item.Key]; writes.Clear();
            if (Active && vrm != null) ApplyFace();
        }
        void Rotate(Transform bone, Quaternion rotation)
        {
            if (bone == null) return;
            bone.localRotation = rest[bone] * rotation; writes[bone] = bone.localRotation;
        }
        public static float Blink(float value) { float t = Mathf.Clamp01((value - .12f) / .48f); return t * t * (3 - 2 * t); }
        void ApplyFace()
        {
            scores.Clear();
            var blend = frame?["face"]?["blendshapes"];
            if (blend is JObject map) { foreach (var property in map.Properties().Take(512)) if (float.TryParse(property.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number)) scores[property.Name] = Mathf.Clamp01(number); }
            else if (blend is JArray list) { foreach (var item in list.OfType<JObject>().Take(512)) { string name = item.Value<string>("categoryName"); if (name != null) scores[name] = Mathf.Clamp01(item.Value<float?>("score") ?? 0); } }
            float Score(string key) => scores.TryGetValue(key, out float value) ? value : 0;
            float left = Blink(Score("eyeBlinkLeft")), right = Blink(Score("eyeBlinkRight"));
            scores["blinkLeft"] = left; scores["blinkRight"] = right; scores["blink"] = (left + right) / 2;
            scores["aa"] = owner.Mouth ? Score("jawOpen") : 0; scores["あ"] = scores["aa"]; scores["まばたき"] = scores["blink"];
            scores["ウィンク"] = left; scores["ウィンク右"] = right;
            foreach (var name in previousScores) if (!scores.ContainsKey(name)) Morph(name, 0);
            previousScores.Clear();
            foreach (var value in scores) { Morph(value.Key, !owner.Mouth && (value.Key.StartsWith("mouth", StringComparison.OrdinalIgnoreCase) || value.Key == "jawOpen") ? 0 : value.Value); previousScores.Add(value.Key); }
            if (cubism != null)
            {
                Parameter("ParamEyeLOpen", 1 - left); Parameter("ParamEyeROpen", 1 - right); Parameter("ParamMouthOpenY", scores["aa"]);
            }
        }
        void Morph(string name, float value)
        {
            var expressions = GetComponent<SakuraAvatarExpressions>();
            if (expressions != null && expressions.Entries.Any(entry => entry.Active && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            if (vrm != null) { foreach (var key in vrm.Runtime.Expression.ExpressionKeys) if (string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase)) vrm.Runtime.Expression.SetWeight(key, value); }
            else { foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>()) { if (renderer.sharedMesh == null) continue; int index = renderer.sharedMesh.GetBlendShapeIndex(name); if (index >= 0) renderer.SetBlendShapeWeight(index, value * 100); } }
        }
        void Parameter(string id, float value) { if (cubism == null) return; var parameter = cubism.Parameters.FirstOrDefault(item => item.Id == id); if (parameter != null) parameter.Value = Mathf.Clamp(value, parameter.MinimumValue, parameter.MaximumValue); }
        public void OnLateUpdate()
        {
            if (!Active || cubism == null) return; ApplyFace();
            var angles = HeadTarget().eulerAngles; Parameter("ParamAngleX", Mathf.DeltaAngle(0, angles.y)); Parameter("ParamAngleY", -Mathf.DeltaAngle(0, angles.x)); Parameter("ParamAngleZ", Mathf.DeltaAngle(0, angles.z));
        }
        Quaternion smooth = Quaternion.identity;
        Quaternion HeadTarget()
        {
            var target = Quaternion.Euler(-cursor.y * 15, cursor.x * 25, 0);
            if (FaceRotation(frame?["face"] as JObject, out var rotation)) { if (!calibrated) { neutral = rotation; calibrated = true; } target = Quaternion.Inverse(neutral) * rotation; }
            smooth = Quaternion.Slerp(smooth, target, 1 - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, .1f) / Mathf.Max(.01f, owner.Smoothing))); return smooth;
        }
        void LateUpdate()
        {
            if (!Active) return;
            if (cubism != null) { if (!HasUpdateController) OnLateUpdate(); return; }
            Rotate(head, HeadTarget()); ApplyFace();
            if (owner.Pose) ApplyPose();
            if (owner.Hands && frame?.Value<bool?>("handTrackingEnabled") != false) ApplyHands();
        }
        static bool Point(JArray array, int index, out Vector3 value)
        {
            value = default; if (array == null || index >= array.Count || !(array[index] is JObject point) || (point.Value<float?>("visibility") ?? 1) < .65f) return false;
            if (point["x"] == null || point["y"] == null || point["z"] == null) return false;
            value = new Vector3(-point.Value<float>("x"), -point.Value<float>("y"), point.Value<float>("z")); return true;
        }
        void ApplyPose()
        {
            var points = frame?["pose"]?["landmarks"] as JArray;
            if (Point(points, 11, out var left) && Point(points, 12, out var right)) Rotate(chest, Quaternion.Euler(0, 0, Mathf.Clamp(Mathf.Atan2(right.y - left.y, Mathf.Abs(right.x - left.x)) * Mathf.Rad2Deg, -20, 20)));
            foreach (bool isLeft in new[] { true, false })
            {
                int start = isLeft ? 11 : 12; string side = isLeft ? "左" : "右";
                if (!Point(points, start, out var shoulder) || !Point(points, start + 2, out var elbow) || !Point(points, start + 4, out var wrist)) continue;
                var upper = Bone(isLeft ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, side + "腕"); var lower = Bone(isLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, side + "ひじ", side + "肘");
                Vector3 restDirection = isLeft ? Vector3.left : Vector3.right;
                if ((elbow - shoulder).sqrMagnitude > .0001f) Rotate(upper, Quaternion.FromToRotation(restDirection, (elbow - shoulder).normalized));
                float bend = 180 - Vector3.Angle(shoulder - elbow, wrist - elbow); Rotate(lower, Quaternion.Euler(0, Mathf.Clamp(bend, 0, 145) * (isLeft ? 1 : -1), 0));
            }
        }
        void ApplyHands()
        {
            var hands = frame?["hands"] as JArray; if (hands == null) return;
            string[] fingers = { "親指", "人指", "中指", "薬指", "小指" }; string[] english = { "Thumb", "Index", "Middle", "Ring", "Little" };
            foreach (var hand in hands.OfType<JObject>().Take(2))
            {
                string side = hand.Value<string>("side") ?? ""; bool left = side.Equals("left", StringComparison.OrdinalIgnoreCase); if (!left && !side.Equals("right", StringComparison.OrdinalIgnoreCase)) continue;
                var points = hand["landmarks"] as JArray; if (points?.Count != 21) continue;
                for (int finger = 0; finger < 5; finger++) for (int segment = 0; segment < 3; segment++)
                {
                    int index = finger * 4 + 1 + segment;
                    if (!Point(points, segment == 0 ? 0 : index - 1, out var a) || !Point(points, index, out var b) || !Point(points, index + 1, out var c)) continue;
                    string human = (left ? "Left" : "Right") + english[finger] + (finger == 0 ? new[] { "Proximal", "Intermediate", "Distal" }[segment] : new[] { "Proximal", "Intermediate", "Distal" }[segment]);
                    if (!Enum.TryParse(human, out HumanBodyBones id)) continue;
                    int digit = finger == 0 ? segment : segment + 1;
                    var bone = Bone(id, (left ? "左" : "右") + fingers[finger] + digit, (left ? "左" : "右") + fingers[finger] + "０１２３"[digit]);
                    float curl = Mathf.Clamp(180 - Vector3.Angle(a - b, c - b), 0, 97);
                    Rotate(bone, Quaternion.Euler(0, 0, curl * (left ? -1 : 1)));
                }
            }
        }
        void OnDisable() { ResetTracking(); }
    }
}
