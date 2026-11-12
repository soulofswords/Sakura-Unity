using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Framework.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UniVRM10;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(-20)]
    public sealed class SakuraIdleMotion : MonoBehaviour, ICubismUpdatable
    {
        public bool Breathing = true, Blinking = true, IdleMotions = true, MotionOwned;
        public float BreathStrength = 1, BreathSpeed = 1;
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 560;
        public bool NeedsUpdateOnEditing => false;
        public int AuthoredIdleCount => clips.Count;
        public float BreathValue { get; private set; }
        public float BlinkValue { get; private set; }
        public bool Companion = true;
        CubismModel cubism;
        CubismParameter[] blinkParameters;
        readonly List<(CubismParameter parameter, float strength)> breathParameters = new List<(CubismParameter, float)>();
        Vrm10Instance vrm;
        SkinnedMeshRenderer face;
        int blinkIndex = -1;
        readonly List<AnimationClip> clips = new List<AnimationClip>();
        readonly Dictionary<Transform, (Quaternion before, Quaternion after)> offsets = new Dictionary<Transform, (Quaternion, Quaternion)>();
        Transform chest, head, neck;
        readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();
        float poseTime;
        int poseIndex, posePhase;
        static readonly Dictionary<string, Vector3>[] Poses =
        {
            new Dictionary<string, Vector3> { ["head"] = new Vector3(0,.025f,.045f), ["neck"] = new Vector3(0,0,-.018f), ["chest"] = new Vector3(0,-.012f,0) },
            new Dictionary<string, Vector3> { ["chest"] = new Vector3(-.025f,0,0), ["leftArm"] = new Vector3(.025f,-.03f,.055f), ["rightArm"] = new Vector3(.025f,.03f,-.055f) },
            new Dictionary<string, Vector3> { ["head"] = new Vector3(.025f,0,-.025f), ["leftElbow"] = new Vector3(0,-.14f,0), ["rightElbow"] = new Vector3(0,.14f,0), ["leftWrist"] = new Vector3(.04f,0,.025f), ["rightWrist"] = new Vector3(.04f,0,-.025f) },
            new Dictionary<string, Vector3> { ["torso"] = new Vector3(0,0,.018f), ["chest"] = new Vector3(0,.018f,-.01f), ["neck"] = new Vector3(0,0,-.008f), ["head"] = new Vector3(.018f,-.025f,-.012f), ["leftArm"] = new Vector3(.015f,0,.035f), ["rightArm"] = new Vector3(.01f,0,-.02f) },
            new Dictionary<string, Vector3> { ["chest"] = new Vector3(-.028f,0,0), ["neck"] = new Vector3(.012f,0,0), ["leftArm"] = new Vector3(-.03f,-.04f,.07f), ["rightArm"] = new Vector3(-.03f,.04f,-.07f), ["leftElbow"] = new Vector3(0,-.06f,0), ["rightElbow"] = new Vector3(0,.06f,0) },
            new Dictionary<string, Vector3> { ["torso"] = new Vector3(0,-.008f,-.012f), ["chest"] = new Vector3(0,.015f,.006f), ["neck"] = new Vector3(0,.025f,.008f), ["head"] = new Vector3(.008f,.055f,.018f), ["rightWrist"] = new Vector3(.025f,0,-.035f) }
        };
        float time, nextBlink = 3, blinkStart = -1;
        string preference;
        [Serializable] class Options { public bool breathing = true, blinking = true, idle = true; public float strength = 1, speed = 1; }

        public void Initialize(string path)
        {
            preference = "sakura-motion:" + Path.GetFullPath(path).ToLowerInvariant();
            if (PlayerPrefs.HasKey(preference))
            {
                try { var saved = JsonUtility.FromJson<Options>(PlayerPrefs.GetString(preference)); Breathing = saved.breathing; Blinking = saved.blinking; IdleMotions = saved.idle; BreathStrength = Mathf.Clamp(saved.strength, 0, 2); BreathSpeed = Mathf.Clamp(saved.speed, .25f, 3); }
                catch (ArgumentException) { }
            }
            cubism = GetComponent<CubismModel>();
            if (cubism != null)
            {
                blinkParameters = cubism.Parameters.Where(parameter => parameter.Id == "ParamEyeLOpen" || parameter.Id == "ParamEyeROpen" || parameter.GetComponent<CubismEyeBlinkParameter>() != null).ToArray();
                foreach (string kind in new[] { "breath", "shoulder", "chest" })
                {
                    var parameter = cubism.Parameters.FirstOrDefault(value => value.Id.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0 && (kind == "breath" || value.Id.IndexOf('Y') >= 0));
                    if (parameter != null) breathParameters.Add((parameter, kind == "breath" ? 1 : .15f));
                }
                if (breathParameters.Count == 0)
                {
                    var sway = cubism.Parameters.FirstOrDefault(parameter => parameter.Id == "ParamBodyAngleZ");
                    if (sway != null) breathParameters.Add((sway, .004f));
                }
                var json = JObject.Parse(File.ReadAllText(path));
                var motions = json["FileReferences"]?["Motions"] as JObject;
                if (motions != null)
                    foreach (var group in motions.Properties().Where(group => group.Name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0))
                        foreach (var entry in ((JArray)group.Value).Take(20))
                        {
                            string motionPath = SakuraModelImport.ResolveFile(Path.GetDirectoryName(path), entry.Value<string>("File"));
                            if (!File.Exists(motionPath) || new FileInfo(motionPath).Length > 8 * 1024 * 1024) continue;
                            try { clips.Add(CubismMotion3Json.LoadFrom(File.ReadAllText(motionPath)).ToAnimationClip()); }
                            catch (Exception error) { Debug.LogWarning("Idle motion not loaded: " + error.Message); }
                        }
                var update = GetComponent<CubismUpdateController>(); HasUpdateController = update != null; if (update != null) update.Refresh();
                return;
            }
            vrm = GetComponent<Vrm10Instance>();
            var animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);
                head = animator.GetBoneTransform(HumanBodyBones.Head); neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                bones["torso"] = animator.GetBoneTransform(HumanBodyBones.Spine);
                bones["leftArm"] = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm); bones["rightArm"] = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                bones["leftElbow"] = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm); bones["rightElbow"] = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                bones["leftWrist"] = animator.GetBoneTransform(HumanBodyBones.LeftHand); bones["rightWrist"] = animator.GetBoneTransform(HumanBodyBones.RightHand);
            }
            else
            {
                var transforms = GetComponentsInChildren<Transform>();
                Transform Find(params string[] names) => transforms.FirstOrDefault(bone => names.Any(name => bone.name.Equals(name, StringComparison.OrdinalIgnoreCase)));
                chest = Find("上半身2", "上半身", "chest", "upperchest"); head = Find("頭", "head"); neck = Find("首", "neck");
                bones["torso"] = Find("上半身", "spine"); bones["leftArm"] = Find("左腕", "leftArm"); bones["rightArm"] = Find("右腕", "rightArm");
                bones["leftElbow"] = Find("左ひじ", "左肘", "leftElbow"); bones["rightElbow"] = Find("右ひじ", "右肘", "rightElbow");
                bones["leftWrist"] = Find("左手首", "leftWrist"); bones["rightWrist"] = Find("右手首", "rightWrist");
            }
            bones["head"] = head; bones["neck"] = neck; bones["chest"] = chest;
            foreach (var mesh in GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                for (int index = 0; index < mesh.sharedMesh.blendShapeCount; index++)
                    if (new[] { "まばたき", "blink", "目_まばたき" }.Contains(mesh.sharedMesh.GetBlendShapeName(index).ToLowerInvariant()))
                    { face = mesh; blinkIndex = index; break; }
                if (face != null) break;
            }
        }
        public void Save()
        {
            PlayerPrefs.SetString(preference, JsonUtility.ToJson(new Options { breathing = Breathing, blinking = Blinking, idle = IdleMotions, strength = BreathStrength, speed = BreathSpeed })); PlayerPrefs.Save();
        }
        void Update()
        {
            foreach (var item in offsets) if (Quaternion.Angle(item.Key.localRotation, item.Value.after) < .001f) item.Key.localRotation = item.Value.before;
            offsets.Clear();
            if (MotionOwned) { poseTime = 0; posePhase = 0; return; }
            time += Mathf.Min(Time.deltaTime, .05f);
            poseTime += Mathf.Min(Time.deltaTime, .05f);
            float duration = posePhase == 0 ? 10 + poseIndex % 3 * 3 : posePhase == 1 ? 1.2f : posePhase == 2 ? 2.2f : 1.4f;
            if (poseTime >= duration) { poseTime = 0; posePhase = (posePhase + 1) % 4; if (posePhase == 0) poseIndex++; }
            if (blinkStart < 0 && time >= nextBlink) blinkStart = time;
            float progress = blinkStart < 0 ? 0 : (time - blinkStart) * 7;
            if (progress >= Mathf.PI) { blinkStart = -1; nextBlink = time + UnityEngine.Random.Range(2.5f, 6.5f); progress = 0; }
            BlinkValue = Blinking ? Mathf.Sin(progress) : 0;
            if (vrm != null) vrm.Runtime.Expression.SetWeight(ExpressionKey.Blink, BlinkValue);
            float wave = .5f - .5f * Mathf.Cos(time * Mathf.PI * 2 / 4.5f * BreathSpeed);
            BreathValue = Breathing ? wave * wave * (3 - 2 * wave) * BreathStrength : 0;
            if (cubism != null && IdleMotions && clips.Count > 0)
            {
                var clip = clips[(int)(time / 30) % clips.Count]; clip.SampleAnimation(gameObject, clip.length > 0 ? time % clip.length : 0);
            }
        }
        public void OnLateUpdate()
        {
            if (!isActiveAndEnabled || MotionOwned || cubism == null) return;
            if (Breathing) foreach (var entry in breathParameters)
            {
                var parameter = entry.parameter;
                parameter.Value = Mathf.Clamp(parameter.DefaultValue + (parameter.MaximumValue - parameter.DefaultValue) * BreathValue * entry.strength, parameter.MinimumValue, parameter.MaximumValue);
            }
            if (Blinking) foreach (var parameter in blinkParameters) parameter.Value = parameter.MaximumValue * (1 - BlinkValue);
        }
        void LateUpdate()
        {
            if (cubism != null) { if (!HasUpdateController) OnLateUpdate(); return; }
            if (MotionOwned) return;
            if (face != null && blinkIndex >= 0) face.SetBlendShapeWeight(blinkIndex, BlinkValue * 100);
            void Rotate(Transform bone, Vector3 radians)
            {
                if (bone == null) return;
                var before = bone.localRotation; var after = before * Quaternion.Euler(radians * Mathf.Rad2Deg);
                bone.localRotation = after;
                offsets[bone] = (offsets.TryGetValue(bone, out var previous) ? previous.before : before, after);
            }
            if (Breathing) Rotate(chest, new Vector3(Mathf.Sin(time * Mathf.PI * 2 / 3.2f * BreathSpeed) * .009f * BreathStrength, 0, 0));
            if (!Companion) return;
            if (IdleMotions)
            {
                float Ease(float value) { float t = Mathf.Clamp01(value); return t*t*t*(t*(t*6-15)+10); }
                float weight = posePhase == 0 ? 0 : posePhase == 1 ? Ease(poseTime / 1.2f) : posePhase == 2 ? 1 : 1-Ease(poseTime / 1.4f);
                foreach (var entry in Poses[poseIndex % Poses.Length]) if (bones.TryGetValue(entry.Key, out var bone)) Rotate(bone, entry.Value * weight);
            }
        }
        void OnDisable()
        {
            foreach (var item in offsets) if (item.Key != null && Quaternion.Angle(item.Key.localRotation, item.Value.after) < .001f) item.Key.localRotation = item.Value.before;
            offsets.Clear();
        }
        void OnDestroy() { foreach (var clip in clips) if (clip != null) Destroy(clip); }
    }
}
