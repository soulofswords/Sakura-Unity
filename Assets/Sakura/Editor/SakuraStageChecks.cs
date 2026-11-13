using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraStageChecks
    {
        [MenuItem("Sakura/Validation/Check stage isolation and room routes")]
        public static void Run()
        {
            string temp = Path.Combine(Path.GetTempPath(), "sakura-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var owner = new GameObject("Disposable stage checks");
            try
            {
                var stage = owner.AddComponent<SakuraStage>(); stage.Initialize(temp, false);
                Require(SakuraStage.ResolveAsset(temp, "pmx/Stages/room/model.pmx").StartsWith(temp, StringComparison.OrdinalIgnoreCase), "dedicated stage library");
                Reject(() => SakuraStage.ResolveAsset(temp, "pmx/character/model.pmx"));
                Reject(() => SakuraStage.ResolveAsset(temp, "pmx/Stages/../../../../private.pmx"));
                Require(!SakuraStage.Finite(new Vector3(float.NaN, 0, 0)), "nonfinite placement");
                var item = new SakuraStage.Item { id = "fixture", path = "pmx/Stages/fixture/model.pmx", name = "Fixture" };
                stage.Settings.items.Add(item); stage.Settings.selected = item.id;
                item.root = new GameObject("Disposable scenery"); item.root.transform.SetParent(owner.transform);
                stage.SetTransform(new Vector3(2,3,4), 2, 90);
                Require(item.root.transform.localPosition == new Vector3(2,3,4) && item.root.transform.localScale == Vector3.one * 2, "transform applies to scenery");
                Reject(() => stage.SetTransform(Vector3.zero, float.NaN, 0));
                Reject(() => stage.SetTransform(Vector3.zero, 0, 0));
                var marker = new SakuraStage.Marker { action = "bed", position = new Vector3(1,0,0) }; item.markers.Add(marker);
                Vector3 marked = item.root.transform.TransformPoint(marker.position);
                Require(Vector3.Distance(marked, new Vector3(2,3,2)) < .001f, "marker follows room scale and facing");
                var viewport = owner.AddComponent<SakuraAvatarViewport>();
                var actions = owner.AddComponent<SakuraAvatarActions>(); actions.Stage = stage; actions.Viewport = viewport;
                Require(!actions.Act("bed") && !actions.Gesture("unknown") && !actions.Dispatch(new JObject { ["avatar_motion"] = "room:bed" }), "unavailable rig refuses chat movement");
                Require(actions.Dispatch(new JObject { ["avatar_motion"] = "stop" }), "source stop contract");
                var rig = GameObject.CreatePrimitive(PrimitiveType.Cube); rig.name = "Disposable rig"; rig.transform.SetParent(owner.transform); rig.transform.localScale = new Vector3(1,2,1);
                foreach (string bone in new[] { "spine", "head", "leftThigh", "rightThigh", "leftKnee", "rightKnee", "leftArm", "rightArm" }) { var child = new GameObject(bone); child.transform.SetParent(rig.transform, false); }
                typeof(SakuraAvatarViewport).GetField("avatar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(viewport, rig);
                actions.EnableRoom(false); Vector3 proportions = rig.transform.localScale;
                Require(actions.Act("bed"), "rigged bed action starts");
                Vector3 previous = rig.transform.position;
                for (int frame = 0; frame < 400; frame++) { actions.Tick(.05f); Require(Vector3.Distance(previous, rig.transform.position) < .35f, "sleep placement moves continuously"); previous = rig.transform.position; }
                Require(actions.Sleeping && rig.transform.localScale == proportions, "sleep preserves body proportions");
                Require(!actions.Act("desk"), "sleep permits home only"); Require(actions.Act("home"), "wake starts");
                actions.Tick(.05f); Require(Vector3.Distance(previous, rig.transform.position) < .35f, "wake starts continuously");
                for (int frame = 0; frame < 400; frame++) actions.Tick(.05f);
                Require(rig.transform.position.sqrMagnitude < .00001f && rig.transform.localScale == proportions, "wake returns home preserving proportions"); actions.DisableRoom();
                stage.RemoveSelected(); Require(stage.Settings.items.Count == 0, "remove selected scenery");
                Debug.Log("PASS: stage path boundaries, finite transforms, stage-local marker transform, unavailable-rig gating, source stop dispatch, rigid sleep and continuous wake. Disposable fixtures only; no owner profile modified.");
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); Directory.Delete(temp); }
        }
        static void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Stage check failed: " + name); }
        static void Reject(Action action) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Require(rejected, "invalid input rejected"); }
    }
}
