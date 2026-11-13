using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UniVRM10;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(31000)]
    public sealed class SakuraRigMonitor : MonoBehaviour
    {
        [Serializable] public sealed class Measurement
        {
            public string id, name;
            public int total, bad, jitter, outsideLimit, persistentBend;
            public bool Unstable => ShouldRepair(total, bad, jitter, outsideLimit, persistentBend, false);
            public static bool ShouldRepair(int total, int bad, int jitter, int outsideLimit, int persistentBend, bool manualRepair)
                => bad >= 3 || (total >= 30 && (jitter >= 12 || outsideLimit >= Math.Max(20, (total * 7 + 9) / 10) ||
                    (manualRepair && persistentBend * 5 >= total * 2)));
            public void Add(Vector3 position, Vector3 rest, Quaternion rotation, Quaternion reference, Quaternion previous,
                float unit, float maxAngle, bool hair)
            {
                total++;
                bool finite = float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z) &&
                    float.IsFinite(rotation.x) && float.IsFinite(rotation.y) && float.IsFinite(rotation.z) && float.IsFinite(rotation.w);
                if (!finite || Vector3.Distance(position, rest) > Mathf.Max(.5f * unit, rest.magnitude * 1.5f)) bad++;
                float angle = finite ? Quaternion.Angle(rotation, reference) : 180;
                if (!finite || (angle > 2.6f && Quaternion.Angle(rotation, previous) > 1.2f)) jitter++;
                if (finite && angle > maxAngle + 15) outsideLimit++;
                if (hair && finite && angle > Mathf.Max(25, maxAngle * .6f)) persistentBend++;
            }
        }
        [Serializable] sealed class Report
        {
            public int version = 1;
            public string fingerprint, checkedAt;
            public List<string> frozen = new List<string>();
            public List<Measurement> measurements = new List<Measurement>();
        }
        sealed class Node
        {
            public Transform bone;
            public Vector3 rest;
            public Quaternion reference, previous;
            public Measurement data;
            public float maxAngle;
            public Action freeze;
            public Func<bool> frozen;
            public bool ignored, hair;
        }
        readonly List<Node> nodes = new List<Node>();
        Report report;
        string storage;
        float started, next;
        bool running, repair, referenceCaptured;
        SakuraPmxModel pmx;
        public string Status { get; private set; } = "Physics diagnosis not started.";
        public string ReferenceSnapshot { get; private set; }
        public string DiagnosticSnapshot { get; private set; }

        public void Initialize(string modelFile)
        {
            string fingerprint;
            using (var sha = SHA256.Create()) fingerprint = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(modelFile))).Replace("-", "");
            storage = Path.Combine(Application.persistentDataPath, "rig-checks", fingerprint + ".json");
            report = new Report { fingerprint = fingerprint };
            pmx = GetComponent<SakuraPmxModel>();
            if (pmx != null)
            {
                var indices = pmx.Definition.physics.rigidbodies.Where(body => body.physicsKind == "dynamic" || body.physicsKind == "dynamicBone" || body.physicsKind == "dynamic-orientation")
                    .Select(body => body.boneIndex).Where(index => index >= 0 && index < pmx.Instance.BoneTransforms.Length).Distinct().Take(2048);
                foreach (int index in indices)
                {
                    var bone = pmx.Instance.BoneTransforms[index]; var (maxAngle, hair) = Classify(bone.name);
                    Add("pmx:" + index, bone, pmx.Instance.BindLocalPositions[index], pmx.Instance.BindLocalRotations[index], maxAngle, hair,
                        () => pmx.Freeze(index, true), () => pmx.Settings.frozen.Contains(index));
                }
            }
            else
            {
                var tools = GetComponent<SakuraModelTools>();
                if (tools != null)
                    foreach (var joint in GetComponentsInChildren<VRM10SpringBoneJoint>(true).Take(2048))
                    {
                        int index = tools.GetBoneIndex(joint.transform);
                        if (index < 0) continue;
                        var bone = tools.Bones[index];
                        var (maxAngle, hair) = Classify(bone.Name);
                        Add(bone.Id, bone.Transform, bone.position, bone.rotation, maxAngle, hair,
                            () => tools.Freeze(index, true), () => tools.IsFrozen(index));
                    }
            }
            try
            {
                if (File.Exists(storage) && new FileInfo(storage).Length <= 1024 * 1024)
                {
                    var saved = JsonUtility.FromJson<Report>(File.ReadAllText(storage));
                    if (saved != null && saved.version == 1 && saved.fingerprint == fingerprint && saved.frozen != null)
                    {
                        report.frozen = saved.frozen.Where(id => nodes.Any(node => node.data.id == id)).Distinct().ToList();
                        foreach (var node in nodes.Where(node => report.frozen.Contains(node.data.id))) node.freeze();
                    }
                }
                Check(false);
            }
            catch (Exception error) { Status = "Saved rig check could not be restored: " + error.Message; }
        }

        static (float maxAngle, bool hair) Classify(string name)
        {
            string value = (name ?? "").ToLowerInvariant();
            bool has(params string[] terms) => terms.Any(term => value.Contains(term));
            if (has("胸", "乳", "bust", "breast", "boob", "oppai")) return (18, false);
            if (has("髪", "发", "髮", "辫", "ツインテール", "おさげ", "ポニーテール", "hair", "ponytail", "twintail", "pigtail", "ahoge", "braid")) return (50, true);
            if (has("スカート", "裾", "下摆", "下擺", "フリル", "裙", "skirt", "frill", "scarf", "muffler")) return (38, false);
            if (has("cloth", "shirt", "jacket", "bodice", "blouse", "衣服", "上衣", "襟")) return (18, false);
            if (has("尻尾", "しっぽ", "尾巴", "tail")) return (35, false);
            if (has("ネクタイ", "领带", "領帶", "necktie", "neck_tie", "tie")) return (22, false);
            if (has("リボン", "丝带", "飘带", "ribbon", "earring", "pendant", "bow")) return (28, false);
            return (48, false);
        }

        void Add(string id, Transform bone, Vector3 rest, Quaternion rotation, float maxAngle, bool hair, Action freeze, Func<bool> frozen)
            => nodes.Add(new Node { bone = bone, rest = rest, reference = rotation, previous = rotation,
                maxAngle = maxAngle, hair = hair, data = new Measurement { id = id, name = bone.name }, freeze = freeze, frozen = frozen });

        public void Check(bool applyRepairs)
        {
            if (nodes.Count == 0) { Status = "No simulated physics bones were found."; return; }
            foreach (var node in nodes) { node.data.total = node.data.bad = node.data.jitter = node.data.outsideLimit = node.data.persistentBend = 0; node.previous = node.bone.localRotation; node.ignored = false; }
            started = Time.unscaledTime; next = started; repair = applyRepairs; running = true; referenceCaptured = false;
            ReferenceSnapshot = null; DiagnosticSnapshot = null;
            Status = "Checking simulated bones for eight seconds: stretch, jitter and spring-limit bends.";
        }

        async void Capture(bool reference)
        {
            try
            {
                var viewport = GetComponentInParent<SakuraAvatarViewport>();
                if (viewport == null || viewport.AvatarRoot != gameObject) return;
                string file = await viewport.SaveSnapshotNextFrame(reference ? "rig-reference" : "rig-diagnostic");
                if (this == null) return;
                if (reference) ReferenceSnapshot = file; else DiagnosticSnapshot = file;
            }
            catch (Exception error) { Debug.LogWarning("Rig snapshot unavailable: " + error.Message); }
        }

        void LateUpdate()
        {
            if (!running || Time.unscaledTime < next) return;
            next = Time.unscaledTime + .1f;
            if (!referenceCaptured && Time.unscaledTime - started >= 2) { referenceCaptured = true; Capture(true); }
            bool simulation = pmx == null ? GetComponent<SakuraModelTools>()?.PhysicsEnabled == true : GetComponent<SakuraPmxPhysics>()?.Simulating == true;
            if (simulation && (pmx == null || !pmx.AppearanceMatching))
                foreach (var node in nodes)
                {
                    if (node.bone == null || node.ignored || node.frozen()) continue;
                    node.data.Add(node.bone.localPosition, node.rest, node.bone.localRotation, node.reference, node.previous,
                        pmx == null ? 1 : pmx.Instance.ImportScale, node.maxAngle, node.hair);
                    node.previous = node.bone.localRotation;
                }
            if (Time.unscaledTime - started < 8) return;
            running = false;
            try
            {
                report.measurements = nodes.Select(node => node.data).ToList();
                report.frozen = report.frozen.Concat(nodes.Where(node => !node.ignored && Measurement.ShouldRepair(
                    node.data.total, node.data.bad, node.data.jitter, node.data.outsideLimit, node.data.persistentBend, repair))
                    .Select(node => node.data.id)).Distinct().ToList();
                report.checkedAt = DateTime.UtcNow.ToString("O"); Save();
                if (repair) foreach (var node in nodes.Where(node => report.frozen.Contains(node.data.id))) node.freeze();
                Status = report.frozen.Count == 0 ? "Physics check saved: no sustained instability detected." : report.frozen.Count + (repair ? " measured repairs applied and saved." : " measured repairs saved for the next model load.");
                Capture(false);
            }
            catch (Exception error) { Status = "Physics check could not be saved/applied: " + error.Message; }
        }

        void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storage));
            string temporary = storage + ".tmp"; File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
            if (File.Exists(storage)) File.Replace(temporary, storage, null); else File.Move(temporary, storage);
        }

        public void Forget(string id = null)
        {
            if (report == null) return;
            foreach (var node in nodes.Where(node => id == null || node.data.id == id)) node.ignored = true;
            report.frozen.RemoveAll(value => id == null || value == id);
            Save();
        }

        public static void BuildPanel(GameObject avatar, VisualElement parent)
        {
            var monitor = avatar.GetComponent<SakuraRigMonitor>();
            if (monitor == null) return;
            parent.Add(new Button(() => monitor.Check(false)) { text = "Check physics for eight seconds" });
            parent.Add(new Button(() => monitor.Check(true)) { text = "Check & repair for eight seconds" });
            var status = new Label(monitor.Status); status.AddToClassList("description"); parent.Add(status);
            var poll = status.schedule.Execute(() => { if (monitor != null) status.text = monitor.Status; }).Every(200);
            status.RegisterCallback<DetachFromPanelEvent>(_ => poll.Pause());
        }
    }
}
