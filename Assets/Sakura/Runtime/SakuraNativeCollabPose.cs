using System;
using System.Collections.Generic;
using System.Linq;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(32000)]
    public sealed class SakuraNativeCollabPose : MonoBehaviour, ICubismUpdatable
    {
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 990;
        public bool NeedsUpdateOnEditing => false;
        JObject latest;
        Transform[] bones;
        SkinnedMeshRenderer[] meshes;
        CubismModel cubism;
        bool unityCoordinates;
        public void Initialize(GameObject model)
        {
            cubism = model.GetComponent<CubismModel>(); bones = BoneList(model);
            meshes = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var update = model.GetComponent<CubismUpdateController>(); HasUpdateController = update != null; if (update != null) update.Refresh();
        }
        static Transform[] BoneList(GameObject model)
        {
            var pmx = model.GetComponent<SakuraPmxModel>();
            if (pmx != null) return pmx.Instance.BoneTransforms;
            var weighted = new HashSet<Transform>(model.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(mesh => mesh.bones).Where(bone => bone != null));
            foreach (var joint in model.GetComponentsInChildren<UniVRM10.VRM10SpringBoneJoint>(true)) weighted.Add(joint.transform);
            return model.GetComponentsInChildren<Transform>(true).Where(weighted.Contains).ToArray();
        }
        public static JObject Capture(GameObject model)
        {
            var cubism = model.GetComponent<CubismModel>();
            if (cubism != null) return new JObject { ["kind"] = "live2d", ["parameters"] = new JObject(cubism.Parameters.Take(2048).Select(parameter => new JProperty(parameter.Id, parameter.Value))) };
            return new JObject { ["kind"] = "3d", ["sakuraUnity"] = true,
                ["bones"] = new JArray(BoneList(model).Take(1024).Select(bone => new JObject { ["name"] = bone.name, ["q"] = new JArray(bone.localRotation.x, bone.localRotation.y, bone.localRotation.z, bone.localRotation.w), ["p"] = new JArray(bone.localPosition.x, bone.localPosition.y, bone.localPosition.z) })),
                ["morphs"] = new JArray(model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Take(64).Select(mesh => new JArray(Enumerable.Range(0, mesh.sharedMesh == null ? 0 : Math.Min(2048, mesh.sharedMesh.blendShapeCount)).Select(index => mesh.GetBlendShapeWeight(index) / 100)))) };
        }
        public static void Validate(JObject pose)
        {
            if (pose == null || pose.ToString(Newtonsoft.Json.Formatting.None).Length > 60000) throw new ArgumentException("Collab pose exceeds 60 KB.");
            void Numbers(JToken token)
            {
                IEnumerable<JToken> Walk(JToken node) { yield return node; if (node is JContainer container) foreach (var child in container.Children()) foreach (var item in Walk(child)) yield return item; }
                foreach (var value in Walk(token).OfType<JValue>().Where(value => value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
                    if (!double.IsFinite(value.Value<double>()) || Math.Abs(value.Value<double>()) > 100000) throw new ArgumentException("Collab pose numbers must be finite and bounded.");
            }
            if (pose.Value<string>("kind") == "live2d")
            {
                if (!(pose["parameters"] is JObject parameters) || parameters.Count > 2048 || parameters.Properties().Any(property => property.Name.Length > 200 || property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer)) throw new ArgumentException("Invalid Live2D collab pose.");
            }
            else if (pose.Value<string>("kind") == "3d")
            {
                if (!(pose["bones"] is JArray bones) || bones.Count > 1024 || !(pose["morphs"] is JArray morphs) || morphs.Count > 64) throw new ArgumentException("Invalid 3D collab pose.");
                foreach (var bone in bones)
                {
                    if (!(bone is JObject) || !(bone["p"] is JArray p) || p.Count != 3 || !(bone["q"] is JArray q) || q.Count != 4) throw new ArgumentException("Invalid collab bone transform.");
                    if (p.Concat(q).Any(value => value.Type != JTokenType.Float && value.Type != JTokenType.Integer) || q.Sum(value => Math.Pow(value.Value<double>(), 2)) < 1e-12) throw new ArgumentException("Invalid collab quaternion.");
                }
                if (morphs.Any(row => !(row is JArray list) || list.Count > 2048 || list.Any(value => value.Type != JTokenType.Float && value.Type != JTokenType.Integer))) throw new ArgumentException("Invalid collab morph weights.");
            }
            else throw new ArgumentException("Unknown collab pose kind.");
            Numbers(pose);
        }
        public void Receive(JObject pose) { Validate(pose); latest = pose; unityCoordinates = pose.Value<bool?>("sakuraUnity") == true; }
        public void OnLateUpdate()
        {
            if (latest == null || !isActiveAndEnabled) return;
            if (cubism != null)
            {
                var values = latest["parameters"] as JObject; if (values == null) return;
                foreach (var parameter in cubism.Parameters) if (values[parameter.Id] != null) parameter.Value = Mathf.Clamp(values.Value<float>(parameter.Id), parameter.MinimumValue, parameter.MaximumValue);
                return;
            }
            // Browser 3D bones use a different loader topology and coordinate basis.
            if (!unityCoordinates) return;
            var incoming = latest["bones"] as JArray; var morphs = latest["morphs"] as JArray; if (incoming == null || morphs == null) return;
            for (int index = 0; index < Math.Min(bones.Length, incoming.Count); index++)
            {
                var data = incoming[index]; if (data.Value<string>("name") != bones[index].name) continue;
                var p = (JArray)data["p"]; var q = (JArray)data["q"];
                bones[index].localPosition = new Vector3((float)p[0], (float)p[1], (float)p[2]); bones[index].localRotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]).normalized;
            }
            for (int mesh = 0; mesh < Math.Min(meshes.Length, morphs.Count); mesh++)
                for (int index = 0; meshes[mesh].sharedMesh != null && index < Math.Min(meshes[mesh].sharedMesh.blendShapeCount, ((JArray)morphs[mesh]).Count); index++) meshes[mesh].SetBlendShapeWeight(index, Mathf.Clamp01((float)morphs[mesh][index]) * 100);
        }
        void LateUpdate() { if (cubism == null || !HasUpdateController) OnLateUpdate(); }
    }
}
