using System;
using System.Threading;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraInstanceChecks
    {
        public static void Verify()
        {
            string identity = "fixture_" + Guid.NewGuid().ToString("N");
            var slots = new Mutex[3];
            try
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = SakuraInstances.Claim(identity, out int index);
                    if (index != i + 1) throw new Exception("Instance slot order differs.");
                }
                bool refused = false;
                try { using var fourth = SakuraInstances.Claim(identity, out _); }
                catch (InvalidOperationException) { refused = true; }
                if (!refused) throw new Exception("A fourth VTuber window was accepted.");
                slots[1].Dispose(); slots[1] = SakuraInstances.Claim(identity, out int reused);
                if (reused != 2) throw new Exception("Closed instance slot was not reclaimed.");
                Debug.Log("SAKURA_INSTANCE_CHECKS_OK: three OS-owned slots, fourth rejected, closed slot reclaimed.");
            }
            finally { foreach (var slot in slots) slot?.Dispose(); }
        }
    }
}
