using System;
using UnityEngine;
using UnityEditor;

namespace Sakura.Unity.Editor
{
    public static class SakuraRigMonitorChecks
    {
        [MenuItem("Sakura/Checks/Validate Rig Monitor")]
        public static void Validate()
        {
            var stable = new SakuraRigMonitor.Measurement();
            Quaternion pose = Quaternion.Euler(170, 0, 0);
            for (int index = 0; index < 80; index++) stable.Add(Vector3.zero, Vector3.zero, pose, pose, pose, 1, 50, true);
            if (stable.Unstable) throw new Exception("A sustained authored pose was classified as jitter.");
            var jitter = new SakuraRigMonitor.Measurement();
            Quaternion previous = Quaternion.identity;
            for (int index = 0; index < 80; index++)
            {
                Quaternion rotation = index % 2 == 0 ? Quaternion.Euler(180, 0, 0) : Quaternion.identity;
                jitter.Add(Vector3.zero, Vector3.zero, rotation, Quaternion.identity, previous, 1, 50, true); previous = rotation;
            }
            if (!jitter.Unstable || jitter.jitter < 12) throw new Exception("Sustained extreme jitter was missed.");
            var stretched = new SakuraRigMonitor.Measurement();
            for (int index = 0; index < 3; index++) stretched.Add(Vector3.one, Vector3.zero, Quaternion.identity, Quaternion.identity, Quaternion.identity, .08f, 50, false);
            if (!stretched.Unstable) throw new Exception("Repeated PMX stretch was missed.");

            if (!SakuraRigMonitor.Measurement.ShouldRepair(30, 0, 0, 21, 0, false))
                throw new Exception("Sustained motion beyond the authored angle limit was missed.");
            if (SakuraRigMonitor.Measurement.ShouldRepair(30, 0, 0, 0, 30, false))
                throw new Exception("A persistent bend alone must not trigger automatic repair.");
            if (!SakuraRigMonitor.Measurement.ShouldRepair(30, 0, 0, 0, 12, true))
                throw new Exception("Manual repair did not use the persistent-bend threshold.");
            if (SakuraRigMonitor.Measurement.ShouldRepair(30, 0, 0, 0, 11, true))
                throw new Exception("Manual repair triggered below the persistent-bend threshold.");

            Debug.Log("SAKURA_RIG_MONITOR_CHECKS_PASS: authored pose, sustained jitter, PMX stretch, angle limit, and manual threshold.");
        }
    }
}
