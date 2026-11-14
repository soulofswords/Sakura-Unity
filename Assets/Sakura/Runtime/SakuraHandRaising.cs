using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity
{
    /// <summary>Plays the web app's imported Hand Raising clip on PMX and VRM rigs.</summary>
    public sealed class SakuraHandRaising : MonoBehaviour
    {
        private sealed class BoneState
        {
            public string Key;
            public Transform Bone, End;
            public Quaternion RestLocal, RestWorld, ParentRest;
            public Vector3 RestDirection, BindDirection;
        }

        private readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
        private readonly List<BoneState> bones = new List<BoneState>();
        private JObject clip;
        private SakuraAvatarActions actions;
        private Quaternion body = Quaternion.identity, inverse = Quaternion.identity;
        private float time;
        private bool playing;
        private GameObject root;

        public static bool Play(GameObject root, SakuraAvatarActions actions)
        {
            if (root == null) return false;
            var asset = Resources.Load<TextAsset>("Sakura/Animations/hand-raising");
            if (asset == null) return false;
            var player = root.GetComponent<SakuraHandRaising>() ?? root.AddComponent<SakuraHandRaising>();
            try { return player.Begin(asset.text, actions); }
            catch (Exception error) { Debug.LogWarning("Imported greeting could not start: " + error.Message); return false; }
        }

        private bool Begin(string json, SakuraAvatarActions owner)
        {
            Stop();
            clip = JObject.Parse(json);
            if ((int?)clip["version"] != 1 || (clip["frames"] as JArray)?.Count < 2) return false;
            root = gameObject;
            actions = owner;
            time = 0; playing = false;
            var animator = root.GetComponentInChildren<Animator>();
            var all = root.GetComponentsInChildren<Transform>(true);
            Transform Find(HumanBodyBones human, params string[] names)
            {
                if (animator != null && animator.isHuman)
                {
                    var bone = animator.GetBoneTransform(human);
                    if (bone != null) return bone;
                }
                return all.FirstOrDefault(item => names.Any(name => string.Equals(item.name, name, StringComparison.OrdinalIgnoreCase)));
            }
            var map = new Dictionary<string, Transform>();
            void Add(string key, HumanBodyBones human, params string[] names)
            { var bone = Find(human, names); if (bone != null) map[key] = bone; }
            Add("torso", HumanBodyBones.Spine, "上半身", "spine");
            Add("chest", HumanBodyBones.Chest, "上半身2", "chest");
            Add("neck", HumanBodyBones.Neck, "首", "neck");
            Add("head", HumanBodyBones.Head, "頭", "head");
            Add("leftShoulder", HumanBodyBones.LeftShoulder, "左肩", "leftShoulder");
            Add("rightShoulder", HumanBodyBones.RightShoulder, "右肩", "rightShoulder");
            Add("leftArm", HumanBodyBones.LeftUpperArm, "左腕", "leftArm");
            Add("rightArm", HumanBodyBones.RightUpperArm, "右腕", "rightArm");
            Add("leftElbow", HumanBodyBones.LeftLowerArm, "左ひじ", "左肘", "leftElbow");
            Add("rightElbow", HumanBodyBones.RightLowerArm, "右ひじ", "右肘", "rightElbow");
            Add("leftWrist", HumanBodyBones.LeftHand, "左手首", "leftWrist");
            Add("rightWrist", HumanBodyBones.RightHand, "右手首", "rightWrist");
            var fingers = new[]
            {
                (HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal, "左親指"),
                (HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal, "左人指"),
                (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal, "左中指"),
                (HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal, "左薬指"),
                (HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal, "左小指"),
                (HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal, "右親指"),
                (HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal, "右人指"),
                (HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal, "右中指"),
                (HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal, "右薬指"),
                (HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal, "右小指")
            };
            for (int side = 0; side < 2; side++)
            {
                string prefix = side == 0 ? "left" : "right";
                for (int finger = 0; finger < 5; finger++)
                {
                    var item = fingers[side * 5 + finger];
                    for (int segment = 0; segment < 3; segment++)
                    {
                        HumanBodyBones human = segment == 0 ? item.Item1 : segment == 1 ? item.Item2 : item.Item3;
                        var bone = Find(human, item.Item4 + (finger == 0 ? (segment + 1).ToString() : (segment + 1).ToString()));
                        if (bone == null)
                        {
                            string suffix = finger == 0 ? new[] { "０", "１", "２" }[segment] : new[] { "１", "２", "３" }[segment];
                            bone = all.FirstOrDefault(value => value.name.IndexOf(item.Item4 + suffix, StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        if (bone != null) map[prefix + "Finger" + finger + "_" + segment] = bone;
                    }
                }
            }
            if (!map.ContainsKey("torso") || !map.ContainsKey("leftArm") || !map.ContainsKey("rightArm")) return false;
            var directions = clip["bindDirections"] as JObject ?? new JObject();
            var frame = (clip["frames"] as JArray)?.First as JObject;
            var rotationKeys = (frame?["rotations"] as JObject)?.Properties().Select(property => property.Name).ToList() ?? new List<string>();
            var endpoints = new Dictionary<string, string>
            {
                ["torso"] = "chest", ["chest"] = "neck", ["neck"] = "head",
                ["leftShoulder"] = "leftArm", ["rightShoulder"] = "rightArm",
                ["leftArm"] = "leftElbow", ["rightArm"] = "rightElbow",
                ["leftElbow"] = "leftWrist", ["rightElbow"] = "rightWrist"
            };
            for (int finger = 0; finger < 5; finger++)
                for (int side = 0; side < 2; side++)
                {
                    string prefix = side == 0 ? "left" : "right";
                    endpoints[prefix + "Wrist"] = prefix + "Finger" + finger + "_0";
                    for (int segment = 0; segment < 2; segment++)
                        endpoints[prefix + "Finger" + finger + "_" + segment] = prefix + "Finger" + finger + "_" + (segment + 1);
                }
            var torso = map["torso"];
            var neck = map.TryGetValue("neck", out var neckBone) ? neckBone : map.TryGetValue("head", out var headBone) ? headBone : torso;
            Vector3 left = map["leftArm"].position - map["rightArm"].position;
            Vector3 up = neck.position - torso.position;
            if (left.sqrMagnitude < .0001f || up.sqrMagnitude < .0001f) return false;
            left.Normalize(); up -= left * Vector3.Dot(up, left); if (up.sqrMagnitude < .0001f) return false; up.Normalize();
            Vector3 forward = Vector3.Cross(left, up).normalized;
            body = Quaternion.LookRotation(forward, up);
            inverse = Quaternion.Inverse(body);
            bones.Clear(); rest.Clear();
            foreach (string key in rotationKeys)
            {
                if (!map.TryGetValue(key, out var bone)) continue;
                Transform end = null;
                if (endpoints.TryGetValue(key, out string endKey) && map.TryGetValue(endKey, out var endBone)) end = endBone;
                Vector3 direction = end != null ? (end.position - bone.position).normalized : bone.forward;
                if (direction.sqrMagnitude < .0001f) direction = bone.forward;
                Vector3 bind = direction;
                if (directions[key] is JArray values && values.Count >= 3)
                    bind = new Vector3((float)values[0], (float)values[1], (float)values[2]).normalized;
                rest[bone] = bone.localRotation;
                bones.Add(new BoneState { Key = key, Bone = bone, End = end, RestLocal = bone.localRotation, RestWorld = bone.rotation, ParentRest = bone.parent == null ? Quaternion.identity : bone.parent.rotation, RestDirection = direction, BindDirection = bind });
            }
            if (bones.Count == 0) return false;
            playing = true;
            return true;
        }

        private void Update()
        {
            if (!playing || clip == null) return;
            if (root == null || actions == null) { Stop(); return; }
            if (actions.TrackingOwnsMotion || actions.Active) { Stop(); return; }
            float duration = (float?)clip["duration"] ?? 4f;
            time += Time.deltaTime;
            if (time >= duration) { Stop(); return; }
            float blend = Mathf.Min(1f, time / .25f, (duration - time) / .35f);
            blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01(blend));
            var frames = clip["frames"] as JArray;
            int index = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(time, duration) * ((float?)clip["fps"] ?? 30f)), 0, frames.Count - 2);
            var first = frames[index] as JObject; var next = frames[index + 1] as JObject;
            var a = first?["rotations"] as JObject; var b = next?["rotations"] as JObject;
            float mix = Mathf.Clamp01((time - ((float?)first?["time"] ?? 0)) / Mathf.Max(1e-6f, ((float?)next?["time"] ?? 0) - ((float?)first?["time"] ?? 0)));
            foreach (var state in bones)
            {
                if (state.Bone == null) continue;
                if (!(a?[state.Key] is JArray leftValues) || leftValues.Count < 4) continue;
                var q = new Quaternion((float)leftValues[0], (float)leftValues[1], (float)leftValues[2], (float)leftValues[3]);
                if (b?[state.Key] is JArray rightValues && rightValues.Count >= 4)
                    q = Quaternion.Slerp(q, new Quaternion((float)rightValues[0], (float)rightValues[1], (float)rightValues[2], (float)rightValues[3]), mix).normalized;
                Vector3 direction = body * q * inverse * state.BindDirection;
                Quaternion targetWorld = Quaternion.FromToRotation(state.RestDirection, direction) * state.RestWorld;
                Quaternion targetLocal = Quaternion.Inverse(state.ParentRest) * targetWorld;
                state.Bone.localRotation = Quaternion.Slerp(state.RestLocal, targetLocal, blend);
            }
        }

        private void Stop()
        {
            playing = false;
            foreach (var pair in rest) if (pair.Key != null) pair.Key.localRotation = pair.Value;
            rest.Clear(); bones.Clear(); clip = null; actions = null; root = null;
        }

        private void OnDisable() { Stop(); }
    }
}
