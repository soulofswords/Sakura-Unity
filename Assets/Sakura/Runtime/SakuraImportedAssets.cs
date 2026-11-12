using System.Collections.Generic;
using UnityEngine;

namespace Sakura.Unity
{
    public sealed class SakuraImportedAssets : MonoBehaviour
    {
        public readonly List<Object> Owned = new List<Object>();

        private void OnDestroy()
        {
            foreach (var asset in Owned) if (asset != null) Destroy(asset);
            Owned.Clear();
        }
    }
}
