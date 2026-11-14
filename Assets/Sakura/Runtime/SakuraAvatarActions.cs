using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(100)]
    public sealed class SakuraAvatarActions : MonoBehaviour
    {
        public SakuraAvatarViewport Viewport;
        public SakuraStage Stage;
        public bool TrackingOwnsMotion;
        public bool RoomEnabled { get; private set; }
        public bool ImportedRoom { get; private set; }
        public bool Sleeping => action == "bed" && elapsed >= duration;
        public bool Active => action != null;
        public string CurrentAction => action;
        public string[] Capabilities => !RoomEnabled || TrackingOwnsMotion || avatar == null || Viewport == null || avatar != Viewport.AvatarRoot ? Array.Empty<string>() : RoomActions.Where(a => (!Sleeping || a == "home") && (a == "roam" ? ImportedRoom && roomItem?.route?.Count >= 2 : new[] { "walk", "sit" }.Contains(a) ? ImportedRoom && roomItem?.markers?.Any(m => m.action == a) == true : !ImportedRoom || !new[] { "desk", "mirror", "bed" }.Contains(a) || roomItem?.markers?.Any(m => m.action == a) == true)).ToArray();
        public event Action<string> StatusChanged;
        public event Action<bool> SleepChanged;
        public JObject Context => RoomEnabled ? new JObject { ["enabled"] = true, ["mode"] = ImportedRoom ? "imported" : "simple", ["name"] = ImportedRoom ? roomItem?.name : "Simple Companion room", ["location"] = location, ["motion"] = action, ["route_points"] = roomItem?.route?.Count ?? 0, ["furniture"] = new JArray(ImportedRoom ? (roomItem?.markers ?? new List<SakuraStage.Marker>()).Select(m => m.action).Where(a => a != "walk") : new[] { "desk", "mirror", "bed" }) } : null;
        GameObject avatar, simple;
        SakuraStage.Item roomItem;
        SakuraIdleMotion idle;
        readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();
        readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
        readonly List<Vector3> route = new List<Vector3>();
        Vector3 home, start, target, bodyCenter;
        Quaternion homeRotation, startRotation, targetRotation;
        float height = 1.6f, bodyDepth, elapsed, duration, routeLength, riseTime;
        string action, location = "home";
        bool looping, wasSleeping;
        int roamIndex;
        GameObject greetedRoot;
        float greetAt;
        static readonly string[] RoomActions = { "home", "desk", "mirror", "bed", "sit", "walk", "roam", "explore", "nod", "shake_head", "stretch" };
        static readonly string[] Gestures = { "groove", "step", "wave", "twist", "disco", "march", "hands_both", "hand_left", "hand_right", "greeting_wave", "chest_tap", "laugh", "laugh_bend" };

        bool Bind()
        {
            var root = Viewport == null ? null : Viewport.AvatarRoot;
            if (root == null || Viewport.LoadedFormat == "Live2D") return false;
            if (avatar == root) return bones.ContainsKey("torso");
            Stop(); avatar = root; idle = root.GetComponent<SakuraIdleMotion>(); bones.Clear();
            var all = root.GetComponentsInChildren<Transform>(true);
            var animator = root.GetComponentInChildren<Animator>();
            void Bone(string key, HumanBodyBones human, params string[] names)
            {
                Transform bone = animator != null && animator.isHuman ? animator.GetBoneTransform(human) : all.FirstOrDefault(t => names.Any(n => string.Equals(t.name, n, StringComparison.OrdinalIgnoreCase)));
                if (bone != null) bones[key] = bone;
            }
            Bone("torso", HumanBodyBones.Spine, "上半身", "spine"); Bone("head", HumanBodyBones.Head, "頭", "head");
            Bone("leftArm", HumanBodyBones.LeftUpperArm, "左腕", "leftArm"); Bone("rightArm", HumanBodyBones.RightUpperArm, "右腕", "rightArm");
            Bone("leftElbow", HumanBodyBones.LeftLowerArm, "左ひじ", "左肘", "leftElbow"); Bone("rightElbow", HumanBodyBones.RightLowerArm, "右ひじ", "右肘", "rightElbow");
            Bone("leftThigh", HumanBodyBones.LeftUpperLeg, "左足", "leftLeg", "leftThigh"); Bone("rightThigh", HumanBodyBones.RightUpperLeg, "右足", "rightLeg", "rightThigh");
            Bone("leftKnee", HumanBodyBones.LeftLowerLeg, "左ひざ", "左膝", "leftKnee"); Bone("rightKnee", HumanBodyBones.RightLowerLeg, "右ひざ", "右膝", "rightKnee");
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return false;
            var bounds = renderers[0].bounds; foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            height = Mathf.Max(.01f, bounds.size.y); bodyDepth = bounds.size.z; bodyCenter = root.transform.InverseTransformPoint(bounds.center);
            home = root.transform.position; homeRotation = root.transform.rotation;
            return bones.ContainsKey("torso");
        }
        public void EnableRoom(bool imported)
        {
            DisableRoom();
            if (TrackingOwnsMotion || !Bind()) throw new InvalidOperationException("Load a rigged PMX or VRM character and stop tracking first.");
            roomItem = imported ? Stage.Selected : null;
            if (imported && (roomItem?.root == null || !Stage.Settings.enabled)) throw new InvalidOperationException("Enable scenery and select a loaded stage first.");
            ImportedRoom = imported; RoomEnabled = true; home = avatar.transform.position; homeRotation = avatar.transform.rotation;
            if (!imported) BuildSimple();
            StatusChanged?.Invoke("Room enabled. Mark clear walking paths; scenery has no automatic wall detection.");
        }
        public void DisableRoom() { Stop(); RoomEnabled = false; roomItem = null; if (simple != null) Destroy(simple); simple = null; }
        public void Stop()
        {
            Restore(); action = null; looping = false; route.Clear();
            if (idle != null) idle.MotionOwned = false;
            if (avatar != null) { avatar.transform.position = home; avatar.transform.rotation = homeRotation; }
            if (wasSleeping) { wasSleeping = false; SleepChanged?.Invoke(false); }
            location = "home";
        }
        void Restore() { foreach (var entry in rest) if (entry.Key != null) entry.Key.localRotation = entry.Value; rest.Clear(); }
        public void Mark(string kind)
        {
            if (!RoomEnabled || !ImportedRoom || Active || roomItem?.root == null || TrackingOwnsMotion) throw new InvalidOperationException("Enable imported room motion and stop movement before marking.");
            if (!new[] { "desk", "mirror", "bed", "sit", "walk" }.Contains(kind)) throw new ArgumentException("Unknown room point.");
            var room = roomItem.root.transform;
            Vector3 point = room.InverseTransformPoint(avatar.transform.position);
            if (!SakuraStage.Finite(point)) throw new ArgumentException("Room point is invalid.");
            if (kind == "walk") { if (roomItem.route.Count >= 24) throw new InvalidOperationException("Walking route limit is 24 points."); roomItem.route.Add(point); }
            roomItem.markers.RemoveAll(m => m.action == kind);
            roomItem.markers.Add(new SakuraStage.Marker { action = kind, position = point, rotation = Quaternion.Inverse(room.rotation) * avatar.transform.rotation }); Stage.Save();
        }
        public void ClearPoints() { Stop(); if (roomItem == null) return; roomItem.markers.Clear(); roomItem.route.Clear(); Stage.Save(); }
        public bool Dispatch(JObject reply)
        {
            if (reply == null) return false;
            string motion = (string)reply?["avatar_motion"];
            string room = reply?["room_action"]?.Type == JTokenType.String ? (string)reply["room_action"] : (string)reply?["room_action"]?["action"];
            if (motion == "stop") { Stop(); return true; }
            if (motion?.StartsWith("room:", StringComparison.Ordinal) == true) return Act(motion.Substring(5));
            if (!string.IsNullOrEmpty(room)) return Act(room);
            return !string.IsNullOrEmpty(motion) && Gesture(motion);
        }
        public bool Gesture(string name)
        {
            if (!Gestures.Contains(name) || TrackingOwnsMotion || !Bind()) return false;
            Stop(); Begin(name); duration = 0; looping = new[] { "groove", "step", "wave", "twist", "disco", "march", "hands_both", "hand_left", "hand_right" }.Contains(name); return true;
        }
        void Begin(string next)
        {
            Restore();
            if (idle != null) { idle.enabled = false; idle.enabled = true; idle.MotionOwned = true; }
            foreach (var bone in bones.Values.Distinct()) rest[bone] = bone.localRotation;
            action = next; elapsed = 0; start = avatar.transform.position; startRotation = avatar.transform.rotation; riseTime = 0; targetRotation = homeRotation; route.Clear(); looping = false;
        }
        public bool Act(string next)
        {
            if (!RoomActions.Contains(next) || !RoomEnabled || TrackingOwnsMotion || avatar == null || avatar != Viewport.AvatarRoot || Sleeping && next != "home") return false;
            if (ImportedRoom && (roomItem?.root == null || !Stage.Settings.enabled)) return false;
            if (new[] { "home", "desk", "mirror", "bed", "sit", "walk", "roam" }.Contains(next) && (!bones.ContainsKey("leftThigh") || !bones.ContainsKey("rightThigh") || !bones.ContainsKey("leftKnee") || !bones.ContainsKey("rightKnee"))) return false;
            bool rising = Sleeping;
            Vector3 destination = home; Quaternion facing = homeRotation;
            if (ImportedRoom && new[] { "desk", "mirror", "bed", "sit", "walk", "roam" }.Contains(next))
            {
                var marker = roomItem.markers.FirstOrDefault(m => m.action == next);
                if (next == "roam") { if (roomItem.route.Count < 2) return false; destination = roomItem.root.transform.TransformPoint(roomItem.route[roamIndex++ % roomItem.route.Count]); }
                else { if (marker == null) return false; destination = roomItem.root.transform.TransformPoint(marker.position); facing = roomItem.root.transform.rotation * marker.rotation; }
            }
            else if (!ImportedRoom)
            {
                if (next == "sit" || next == "walk" || next == "roam") return false;
                if (next == "desk") destination += new Vector3(-1.05f, 0, -.23f) * height;
                if (next == "mirror") destination += new Vector3(-.04f, 0, -1.3f) * height;
                if (next == "bed") destination += new Vector3(1.05f, 0, .08f) * height;
            }
            if (!SakuraStage.Finite(destination) || Vector3.Distance(destination, home) > height * 100) return false;
            Begin(next); targetRotation = facing; target = destination;
            if (rising) { riseTime = .8f; wasSleeping = false; SleepChanged?.Invoke(false); }
            if (new[] { "nod", "shake_head", "stretch", "explore" }.Contains(next)) { target = start; duration = 0; return true; }
            if (ImportedRoom && roomItem.route.Count > 0)
            {
                var points = roomItem.route.Select(p => roomItem.root.transform.TransformPoint(p)).ToList();
                if (points.Any(p => !SakuraStage.Finite(p) || Vector3.Distance(p, home) > height * 100)) { Stop(); return false; }
                int first = Enumerable.Range(0, points.Count).OrderBy(i => (points[i] - start).sqrMagnitude).First(), last = Enumerable.Range(0, points.Count).OrderBy(i => (points[i] - target).sqrMagnitude).First();
                for (int i = first; ; i += last >= first ? 1 : -1) { route.Add(points[i]); if (i == last) break; }
            }
            route.Add(target); routeLength = 0; Vector3 prev = start;
            foreach (var point in route) { routeLength += Vector3.Distance(prev, point); prev = point; }
            duration = Mathf.Max(.8f, routeLength / height * 3.6f); looping = next == "roam";
            return true;
        }
        static float Smooth(float v) => Mathf.SmoothStep(0, 1, Mathf.Clamp01(v));
        void Rotate(string key, float x = 0, float y = 0, float z = 0) { if (bones.TryGetValue(key, out var bone) && rest.TryGetValue(bone, out var rotation)) bone.localRotation = rotation * Quaternion.Euler(x * Mathf.Rad2Deg, y * Mathf.Rad2Deg, z * Mathf.Rad2Deg); }
        void LateUpdate()
        {
            var root = Viewport == null ? null : Viewport.AvatarRoot;
            if (root != greetedRoot) { greetedRoot = root; greetAt = root == null ? 0 : Time.unscaledTime + .8f; }
            if (root != null && greetAt > 0 && Time.unscaledTime >= greetAt && !TrackingOwnsMotion && !Active)
            { greetAt = 0; if (!SakuraHandRaising.Play(root, this)) Gesture("greeting_wave"); }
            Tick(Mathf.Clamp(Time.deltaTime, 0, .05f));
        }
        public void Tick(float delta)
        {
            if (avatar != null && (Viewport == null || avatar != Viewport.AvatarRoot || TrackingOwnsMotion)) { DisableRoom(); avatar = null; return; }
            if (RoomEnabled && ImportedRoom && (roomItem?.root == null || !Stage.Settings.enabled)) { DisableRoom(); return; }
            if (action == null || avatar == null) return;
            elapsed += Mathf.Clamp(delta, 0, .05f);
            if (riseTime > 0)
            {
                Vector3 standing = start; standing.y = home.y;
                avatar.transform.position = Vector3.Lerp(start, standing, Smooth(elapsed / riseTime)); avatar.transform.rotation = Quaternion.Slerp(startRotation, homeRotation, Smooth(elapsed / riseTime));
                if (elapsed < riseTime) return;
                start = standing; elapsed = 0; riseTime = 0;
                routeLength = 0; Vector3 last = start; foreach (var point in route) { routeLength += Vector3.Distance(last, point); last = point; }
                duration = Mathf.Max(.8f, routeLength / height * 3.6f);
            }
            foreach (var pair in rest) if (pair.Key != null) pair.Key.localRotation = pair.Value;
            if (duration > 0 && elapsed < duration)
            {
                float t = Smooth(elapsed / duration), remaining = t * routeLength; Vector3 previous = start, direction = target - start;
                foreach (var point in route) { float distance = Vector3.Distance(previous, point); if (remaining <= distance) { avatar.transform.position = Vector3.Lerp(previous, point, distance > 0 ? remaining / distance : 1); direction = point - previous; break; } remaining -= distance; previous = point; }
                float weight = Mathf.Min(1, elapsed / .25f, (duration - elapsed) / .25f), gait = Mathf.Sin(elapsed * 7) * .38f * weight;
                if (direction.sqrMagnitude > .0001f) avatar.transform.rotation = Quaternion.Slerp(homeRotation, Quaternion.LookRotation(new Vector3(direction.x, 0, direction.z)), weight);
                Rotate("leftThigh", gait); Rotate("rightThigh", -gait); Rotate("leftKnee", Mathf.Max(0, -gait) * 1.35f); Rotate("rightKnee", Mathf.Max(0, gait) * 1.35f); Rotate("leftArm", -gait * .4f); Rotate("rightArm", gait * .4f); return;
            }
            float after = Mathf.Max(0, elapsed - duration), blend = Smooth(after / .6f), wave = Mathf.Sin(after * 5);
            if (duration > 0) { avatar.transform.position = target; avatar.transform.rotation = Quaternion.Slerp(homeRotation, targetRotation, blend); }
            if (action == "bed")
            {
                Quaternion lying = targetRotation * Quaternion.Euler(-90, 0, 0);
                Vector3 mattress = ImportedRoom ? target : home + new Vector3(1.05f, .32f, -.62f) * height;
                Vector3 offset = lying * Vector3.Scale(bodyCenter, avatar.transform.lossyScale);
                Vector3 lyingOrigin = mattress + Vector3.up * Mathf.Min(bodyDepth, height * .25f) * .5f - offset;
                avatar.transform.position = Vector3.Lerp(target, lyingOrigin, blend); avatar.transform.rotation = Quaternion.Slerp(homeRotation, lying, blend);
                if (!wasSleeping && blend >= 1) { location = "bed"; wasSleeping = true; SleepChanged?.Invoke(true); } return;
            }
            if (action == "sit") { location = "sit"; avatar.transform.position = target - Vector3.up * height * .25f * blend; Rotate("leftThigh", -1.4f * blend); Rotate("rightThigh", -1.4f * blend); Rotate("leftKnee", 1.4f * blend); Rotate("rightKnee", 1.4f * blend); return; }
            if (action == "desk") { location = "desk"; Rotate("leftArm", .25f * blend, 0, -.35f * blend); Rotate("rightArm", .25f * blend, 0, .35f * blend); Rotate("leftElbow", 0, -.7f * blend); Rotate("rightElbow", 0, .7f * blend); return; }
            if (action == "mirror") { location = "mirror"; return; }
            if (action == "roam" && after > 6) { Act("roam"); return; }
            if (action == "nod") Rotate("head", wave * .12f);
            else if (action == "shake_head" || action == "explore") Rotate("head", 0, wave * (action == "explore" ? .35f : .12f));
            else if (action == "stretch") { float lift = Mathf.Sin(Mathf.Clamp01(after / 3) * Mathf.PI) * .8f; Rotate("leftArm", 0, 0, -lift); Rotate("rightArm", 0, 0, lift); }
            else if (Gestures.Contains(action))
            {
                float sign = Viewport.LoadedFormat == "VRM" ? -1 : 1;
                if (action == "chest_tap") { Rotate("rightArm", .3f, 0, -.65f * sign); Rotate("rightElbow", 0, .9f + wave * .08f); }
                else if (action.StartsWith("laugh")) { Rotate("torso", .12f + Mathf.Abs(wave) * .07f); Rotate("head", wave * .06f); }
                else { Rotate("torso", action == "twist" ? 0 : wave * .04f, action == "twist" ? wave * .22f : 0, wave * .035f); if (action != "hand_left") { Rotate("rightArm", 0, 0, (1.1f + wave * .12f) * sign); Rotate("rightElbow", 0, .6f + wave * .25f); } if (action == "hands_both" || action == "hand_left" || action == "disco") Rotate("leftArm", 0, 0, -(1.1f + wave * .12f) * sign); if (new[] { "step", "march", "groove" }.Contains(action)) { Rotate("leftThigh", wave * .22f); Rotate("rightThigh", -wave * .22f); } }
            }
            if (!looping && after > (duration > 0 ? .6f : 3)) { location = action; Restore(); action = null; if (idle != null) idle.MotionOwned = false; }
        }
        void BuildSimple()
        {
            simple = new GameObject("Simple Companion room"); simple.transform.SetParent(transform, false);
            void Box(Vector3 size, Vector3 position, Color color)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = "Room furniture"; box.transform.SetParent(simple.transform, false); box.transform.position = home + position * height; box.transform.localScale = size * height; box.layer = 30; Destroy(box.GetComponent<Collider>());
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.color = color; box.GetComponent<Renderer>().sharedMaterial = material;
                var assets = box.AddComponent<SakuraImportedAssets>(); assets.Owned.Add(material);
            }
            Box(new Vector3(3.5f,.04f,3.5f), new Vector3(0,-.03f,-.4f), new Color(.22f,.25f,.32f));
            Box(new Vector3(.95f,.06f,.5f), new Vector3(-1.05f,.53f,-.62f), new Color(.48f,.34f,.29f));
            Box(new Vector3(.75f,.25f,1.2f), new Vector3(1.05f,.125f,-.62f), new Color(.64f,.57f,.71f));
            Box(new Vector3(.7f,.07f,1.15f), new Vector3(1.05f,.285f,-.62f), new Color(.93f,.87f,.91f));
            Box(new Vector3(.56f,1.04f,.04f), new Vector3(-.04f,.6f,-1.85f), new Color(.55f,.68f,.76f));
            Box(new Vector3(.48f,.3f,.04f), new Vector3(-1.05f,.72f,-.8f), new Color(.08f,.15f,.2f));
        }
        void OnDisable() { DisableRoom(); }
    }
}
