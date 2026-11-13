using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Sakura.Unity
{
    // Scenery lives beside the avatar, so switching characters never destroys a room.
    public sealed class SakuraStage : MonoBehaviour
    {
        [Serializable] public sealed class Marker { public string action; public Vector3 position; public Quaternion rotation = Quaternion.identity; }
        [Serializable] public sealed class Item
        {
            public string id, path, name;
            public Vector3 position = new Vector3(0, 0, -.8f);
            public float scale = 1, yaw;
            public List<Marker> markers = new List<Marker>();
            public List<Vector3> route = new List<Vector3>();
            [NonSerialized] public GameObject root;
        }
        [Serializable] public sealed class Profile { public bool enabled; public string selected; public List<Item> items = new List<Item>(); }
        public event Action<string> StatusChanged;
        public Profile Settings { get; private set; } = new Profile();
        public Item Selected => Settings.items.FirstOrDefault(item => item.id == Settings.selected);
        public string SourceFolder { get; private set; }
        public bool Loading { get; private set; }
        CancellationTokenSource lifetime = new CancellationTokenSource();
        int revision;
        string profileKey;
        public void Initialize(string source, bool restore = true)
        {
            string resolved = Path.GetFullPath(source);
            if (string.Equals(SourceFolder, resolved, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var item in Settings.items) if (item.root != null) Destroy(item.root);
            ++revision; SourceFolder = resolved;
            profileKey = restore ? "sakura-native-stage:" + SourceFolder.ToLowerInvariant() : null;
            if (!restore) return;
            try { Settings = JsonUtility.FromJson<Profile>(PlayerPrefs.GetString(profileKey, "{}")) ?? new Profile(); }
            catch (ArgumentException) { Settings = new Profile(); }
            Settings.items = (Settings.items ?? new List<Item>()).Where(Valid).Take(4).ToList();
            foreach (var item in Settings.items) { item.markers = (item.markers ?? new List<Marker>()).Where(m => m != null && Finite(m.position)).Take(5).ToList(); item.route = (item.route ?? new List<Vector3>()).Where(Finite).Take(24).ToList(); }
            if (Settings.enabled) _ = Sync();
        }
        public static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
        static bool Valid(Item item) => item != null && !string.IsNullOrEmpty(item.id) && !string.IsNullOrEmpty(item.path) && Finite(item.position) && Mathf.Abs(item.position.x) <= 10000 && Mathf.Abs(item.position.y) <= 10000 && Mathf.Abs(item.position.z) <= 10000 && item.scale >= .01f && item.scale <= 100 && !float.IsNaN(item.yaw) && !float.IsInfinity(item.yaw);
        public static string ResolveAsset(string source, string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            if (!(normalized.StartsWith("pmx/Stages/", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("pmx/Tools & Props/", StringComparison.OrdinalIgnoreCase)) || !normalized.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a PMX from Stages or Tools & Props.");
            return SakuraModelImport.ResolveFile(Path.Combine(source, "models"), normalized);
        }
        public void Save()
        {
            if (profileKey == null) return;
            PlayerPrefs.SetString(profileKey, JsonUtility.ToJson(Settings)); PlayerPrefs.Save();
        }
        public async Task SetEnabled(bool enabled)
        {
            Settings.enabled = enabled; ++revision; Save();
            foreach (var item in Settings.items) if (item.root != null) item.root.SetActive(enabled);
            if (enabled) await Sync();
        }
        public async Task Add(string path, string name = null)
        {
            ResolveAsset(SourceFolder, path);
            if (Settings.items.Count >= 4) throw new InvalidOperationException("Remove an object first (limit four).");
            var item = new Item { id = Guid.NewGuid().ToString("N"), path = path, name = name ?? Path.GetFileNameWithoutExtension(path) };
            Settings.items.Add(item); Settings.selected = item.id; Save();
            if (Settings.enabled) await Sync();
        }
        public void Select(string id) { Settings.selected = id; Save(); }
        public void RemoveSelected()
        {
            var item = Selected; if (item == null) return;
            if (item.root != null) Destroy(item.root);
            Settings.items.Remove(item); Settings.selected = Settings.items.FirstOrDefault()?.id; ++revision; Save();
        }
        public void SetTransform(Vector3 position, float scale, float yaw)
        {
            var item = Selected; if (item == null) return;
            var proposed = new Item { id = item.id, path = item.path, position = position, scale = scale, yaw = yaw };
            if (!Valid(proposed)) throw new ArgumentException("Use finite positions within 10,000 units and scale .01 to 100.");
            item.position = position; item.scale = scale; item.yaw = yaw; Apply(item); Save();
        }
        static void Apply(Item item)
        {
            if (item.root == null) return;
            item.root.transform.localPosition = item.position; item.root.transform.localRotation = Quaternion.Euler(0, item.yaw, 0); item.root.transform.localScale = Vector3.one * item.scale;
        }
        public async Task Sync()
        {
            if (Loading || !Settings.enabled) return;
            Loading = true;
            try
            {
                foreach (var item in Settings.items.ToArray())
                {
                    if (!Settings.enabled || lifetime.IsCancellationRequested) break;
                    if (item.root != null) continue;
                    int generation = revision;
                    GameObject loaded = null;
                    try
                    {
                        loaded = await SakuraModelImport.LoadPmx(ResolveAsset(SourceFolder, item.path), lifetime.Token, text => StatusChanged?.Invoke(text));
                        if (this == null || generation != revision || !Settings.enabled || !Settings.items.Contains(item)) { Destroy(loaded); continue; }
                        loaded.name = "Stage · " + item.name; loaded.transform.SetParent(transform, false);
                        foreach (var child in loaded.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
                        item.root = loaded; Apply(item); StatusChanged?.Invoke("Loaded " + item.name + ". Static scenery; room collision is not automatic.");
                    }
                    catch (OperationCanceledException) { if (loaded != null) Destroy(loaded); }
                    catch (Exception error) { if (loaded != null) Destroy(loaded); StatusChanged?.Invoke(error.Message); }
                }
            }
            finally { Loading = false; }
        }
        void OnDestroy() { lifetime.Cancel(); lifetime.Dispose(); }
    }
}
