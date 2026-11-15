using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework.Json;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(1200)]
    public sealed class SakuraStageItems : MonoBehaviour
    {
        [Serializable]
        public sealed class Item
        {
            public string id, kind, name, source;
            public string pinKind, pinTarget, pinModel;
            public float anchorAngle;
            public float x = .5f, y = .5f, width = .25f, rotation;
            public bool above = true, follow, locked;
            public float anchorX, anchorY, anchorWidth = .25f;
            public string animation;
            public bool loop = true, animationPlaying;
            [NonSerialized] internal GameObject quad, model;
            [NonSerialized] internal Texture2D image;
            [NonSerialized] internal RenderTexture liveTexture;
            [NonSerialized] internal Material material;
            [NonSerialized] internal Vector3 center;
            [NonSerialized] internal float modelHeight;
            [NonSerialized] internal Vector2 velocity;
            [NonSerialized] internal Dictionary<string, string> motions;
            [NonSerialized] internal AnimationClip clip;
            [NonSerialized] internal string loadedMotion;
            [NonSerialized] internal float animationTime;
            [NonSerialized] internal bool pinResolved;
            [NonSerialized] internal Vector2 lastPosition;
            [NonSerialized] internal float lastWidth, lastRotation;
        }
        public struct ThrowState { public Vector2 Position, Velocity; }
        public readonly struct PinChoice
        {
            public readonly string Kind, Target, Name;
            public PinChoice(string kind, string target, string name) { Kind = kind; Target = target; Name = name; }
        }
        public readonly struct AttachmentFrame
        {
            public readonly Vector2 Position;
            public readonly float Scale, Rotation;
            public AttachmentFrame(Vector2 position, float scale, float rotation = 0) { Position = position; Scale = scale; Rotation = rotation; }
        }
        private sealed class PinNode { public PinChoice Choice; public Transform Bone; public CubismDrawable Drawable; }
        private readonly List<PinNode> pinNodes = new List<PinNode>();
        private GameObject pinAvatar;
        private int pinSampleFrame = -1;
        private readonly Dictionary<string, AttachmentFrame> pinFrames = new Dictionary<string, AttachmentFrame>();
        [Serializable] private sealed class Profile { public string selected; public bool throwEnabled = true; public List<Item> items = new List<Item>(); }
        public const int MaximumItems = 8;
        public const int Live2DLayer = 28;
        public event Action Changed;
        public string Status { get; private set; } = "Add an image or imported Live2D asset. Drag an unlocked item to position it.";
        public bool Hidden { get; private set; }
        public void SetHidden(bool hidden) { Hidden = hidden; LayoutItems(); Changed?.Invoke(); }
        public IReadOnlyList<Item> Items => profile.items;
        public Item Selected => profile.items.FirstOrDefault(item => item.id == profile.selected);
        private Profile profile = new Profile();
        private SakuraBackend backend;
        private SakuraAvatarViewport viewport;
        private string storage;
        private readonly SemaphoreSlim importGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Camera liveCamera;
        private VisualElement stage;
        private int dragPointer = -1;
        private Vector2 dragStart, itemStart;
        private bool adding, destroyed;
        private float nextLiveFrame;
        private int liveSlot;
        private Vector2 lastDragPoint, dragVelocity;
        private float lastDragTime, nextPhysicsSave;
        private bool physicsDirty;
        public bool ThrowsEnabled => profile.throwEnabled;

        public static bool Valid(Item item)
        {
            return item != null && System.Text.RegularExpressions.Regex.IsMatch(item.id ?? "", "\\A[a-f0-9]{32}\\z")
                && (item.kind == "image" || item.kind == "live2d") && !string.IsNullOrWhiteSpace(item.source)
                && Finite(item.x) && Finite(item.y) && item.x >= -.5f && item.x <= 1.5f && item.y >= -.5f && item.y <= 1.5f
                && Finite(item.width) && item.width >= .03f && item.width <= 1.5f && Finite(item.rotation) && Mathf.Abs(item.rotation) <= 180
                && Finite(item.anchorX) && Finite(item.anchorY) && Finite(item.anchorWidth) && item.anchorWidth > 0 && item.anchorWidth <= 15
                && Finite(item.anchorAngle) && Mathf.Abs(item.anchorAngle) <= 360
                && (string.IsNullOrEmpty(item.pinKind) && string.IsNullOrEmpty(item.pinTarget) && string.IsNullOrEmpty(item.pinModel)
                    || (item.pinKind == "bone" || item.pinKind == "artmesh") && !string.IsNullOrEmpty(item.pinTarget) && item.pinTarget.Length <= 2048
                    && !string.IsNullOrEmpty(item.pinModel) && item.pinModel.Length <= 4096 && item.follow);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);

        public static ThrowState SimulateThrow(ThrowState state, float seconds)
        {
            if (!Finite(state.Position)) return new ThrowState { Position = new Vector2(.5f, .5f) };
            if (!Finite(state.Velocity) || !Finite(seconds) || seconds <= 0) { state.Velocity = Vector2.zero; return state; }
            float dt = Mathf.Min(seconds, .05f);
            state.Velocity.y += 1.8f * dt;
            state.Position += state.Velocity * dt;
            state.Velocity *= Mathf.Pow(.985f, dt * 60);
            if (state.Position.x < 0) { state.Position.x = 0; state.Velocity.x = Mathf.Abs(state.Velocity.x) * .55f; }
            if (state.Position.x > 1) { state.Position.x = 1; state.Velocity.x = -Mathf.Abs(state.Velocity.x) * .55f; }
            if (state.Position.y < 0) { state.Position.y = 0; state.Velocity.y = Mathf.Abs(state.Velocity.y) * .55f; }
            if (state.Position.y > 1)
            {
                state.Position.y = 1; state.Velocity.y = -Mathf.Abs(state.Velocity.y) * .55f;
                if (Mathf.Abs(state.Velocity.y) < .05f && Mathf.Abs(state.Velocity.x) < .01f) state.Velocity = Vector2.zero;
            }
            return state;
        }

        public static string OwnImagePath(string folder, string id)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "\\A[a-f0-9]{32}\\z")) throw new ArgumentException("Invalid stage item ID.");
            return SakuraModelImport.ResolveFile(folder, id + ".png");
        }

        public static string ResolveLive2D(string sourceFolder, string relative)
        {
            if (string.IsNullOrEmpty(relative) || !relative.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an imported Live2D model.");
            string path = SakuraAvatarViewport.ResolveCharacterPath(sourceFolder, relative);
            if (!File.Exists(path)) throw new FileNotFoundException("The imported Live2D model is unavailable.");
            return path;
        }

        public void Initialize(SakuraBackend service, SakuraAvatarViewport avatars)
        {
            if (backend != null) return;
            backend = service; viewport = avatars;
            string hash;
            using (var digest = SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(service.SourceFolder).ToLowerInvariant()))).Replace("-", "");
            storage = Path.Combine(SakuraInstances.IsSecondary ? SakuraInstances.SessionProfile : Application.persistentDataPath, "stage-items", hash);
            try
            {
                Directory.CreateDirectory(storage);
                string file = Path.Combine(storage, "items.json");
                if (File.Exists(file) && new FileInfo(file).Length <= 256 * 1024)
                {
                    var saved = JObject.Parse(File.ReadAllText(file));
                    if (saved["throwEnabled"] == null) saved["throwEnabled"] = true;
                    foreach (var entry in (saved["items"] as JArray ?? new JArray()).OfType<JObject>()) if (entry["loop"] == null) entry["loop"] = true;
                    profile = JsonUtility.FromJson<Profile>(saved.ToString()) ?? new Profile();
                }
                profile.items = (profile.items ?? new List<Item>()).Where(Valid).GroupBy(item => item.id).Select(group => group.First()).Take(MaximumItems).ToList();
                Restore();
            }
            catch (Exception error) { Notify("Stage items could not be restored. " + error.Message); }
        }

        private async void Restore()
        {
            foreach (var item in profile.items.ToArray())
            {
                if (destroyed) return;
                try { await Load(item); }
                catch (OperationCanceledException) { return; }
                catch (Exception error) { Notify("Could not load “" + item.name + "”. " + error.Message); }
            }
            Changed?.Invoke();
        }

        private async Task Load(Item item)
        {
            await importGate.WaitAsync(lifetime.Token);
            GameObject imported = null;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!profile.items.Contains(item)) return;
                if (item.kind == "image")
                {
                    string file = OwnImagePath(storage, item.id);
                    if (item.source != item.id + ".png") throw new InvalidDataException("Stage image must use its owned asset file.");
                    if (new FileInfo(file).Length > 20 * 1024 * 1024) throw new InvalidDataException("The saved item image exceeds 20 MB.");
                    item.image = DecodeImage(File.ReadAllBytes(file));
                }
                else
                {
                    imported = SakuraModelImport.LoadLive2D(ResolveLive2D(backend.SourceFolder, item.source));
                    lifetime.Token.ThrowIfCancellationRequested();
                    imported.transform.SetParent(transform, false);
                    imported.transform.localPosition = new Vector3(2000 + ++liveSlot * 100, 2000, 2000);
                    imported.transform.localRotation = Quaternion.Euler(0, 180, 0);
                    foreach (var child in imported.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = Live2DLayer;
                    foreach (var collider in imported.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                    foreach (var camera in imported.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
                    foreach (var light in imported.GetComponentsInChildren<Light>(true)) light.enabled = false;
                    foreach (var audio in imported.GetComponentsInChildren<AudioSource>(true)) { audio.Stop(); audio.enabled = false; }
                    foreach (var listener in imported.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
                    item.model = imported; imported = null;
                    ReadMotions(item);
                    await Task.Yield();
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (!profile.items.Contains(item)) return;
                    var renderers = item.model.GetComponentsInChildren<Renderer>();
                    if (renderers.Length == 0) throw new InvalidDataException("The Live2D asset has no visible drawables.");
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                    item.center = bounds.center; item.modelHeight = Mathf.Max(bounds.size.y, bounds.size.x) * 1.12f;
                    item.liveTexture = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32) { name = "Stage Live2D “" + item.name + "”" };
                    item.liveTexture.Create(); CreateLiveCamera();
                }
                if (!profile.items.Contains(item)) return;
                var shader = Resources.Load<Shader>("Sakura/StageItem");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("The native stage-item shader is unavailable.");
                item.material = new Material(shader) { mainTexture = item.kind == "image" ? (Texture)item.image : item.liveTexture };
                item.material.SetFloat("_Premultiplied", item.kind == "live2d" ? 1 : 0);
                item.quad = GameObject.CreatePrimitive(PrimitiveType.Quad); item.quad.name = "Stage item “" + item.name + "”";
                item.quad.transform.SetParent(transform, false); item.quad.layer = 30;
                var colliderOnQuad = item.quad.GetComponent<Collider>(); colliderOnQuad.enabled = false; Destroy(colliderOnQuad);
                var quadRenderer = item.quad.GetComponent<MeshRenderer>(); quadRenderer.sharedMaterial = item.material;
                quadRenderer.shadowCastingMode = ShadowCastingMode.Off; quadRenderer.receiveShadows = false;
                item.quad.SetActive(isActiveAndEnabled); if (item.model != null) item.model.SetActive(isActiveAndEnabled);
                LayoutItems();
                if (item.animationPlaying && item.kind == "live2d")
                {
                    if (item.motions.ContainsKey(item.animation ?? ""))
                    {
                        try { PlayAnimation(item.id, item.animation, item.loop); }
                        catch (Exception error) { item.animationPlaying = false; Notify("Saved item animation could not start. " + error.Message); }
                    }
                    else item.animationPlaying = false;
                }
            }
            catch { DisposeItem(item); throw; }
            finally { if (imported != null) Destroy(imported); importGate.Release(); }
        }

        public static Texture2D DecodeImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 33 || bytes.Length > 20 * 1024 * 1024 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
                || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
                throw new InvalidDataException("Choose a PNG image under 20 MB.");
            uint Dimension(int offset) => (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3];
            uint width = Dimension(16), height = Dimension(20);
            if (width == 0 || height == 0 || (ulong)width * height > 16777216 || width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize) throw new InvalidDataException("The item image exceeds the renderer’s image size limit.");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try { if (!ImageConversion.LoadImage(texture, bytes, true)) throw new InvalidDataException("The item image could not be decoded."); return texture; }
            catch { Destroy(texture); throw; }
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private void Save() => AtomicWrite(Path.Combine(storage, "items.json"), Encoding.UTF8.GetBytes(JsonUtility.ToJson(profile)));
        private void Notify(string message) { Status = message; if (!destroyed) Changed?.Invoke(); }

        private void ReadMotions(Item item)
        {
            item.motions = new Dictionary<string, string>();
            string model = ResolveLive2D(backend.SourceFolder, item.source);
            var groups = JObject.Parse(File.ReadAllText(model))["FileReferences"]?["Motions"] as JObject;
            if (groups == null) return;
            int examined = 0;
            foreach (var group in groups.Properties())
            {
                foreach (var entry in (group.Value as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (++examined > 100) return;
                    string relative = entry.Value<string>("File");
                    if (string.IsNullOrEmpty(relative) || !relative.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)) continue;
                    string file;
                    try { file = SakuraModelImport.ResolveFile(Path.GetDirectoryName(model), relative); }
                    catch (ArgumentException) { continue; }
                    if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) continue;
                    string label = group.Name + " · " + Path.GetFileName(file).Replace(".motion3.json", "");
                    if (item.motions.ContainsKey(label)) label += " · " + item.motions.Count;
                    item.motions[label] = file;
                }
            }
        }
        public IReadOnlyList<string> MotionNames(string id) => profile.items.FirstOrDefault(item => item.id == id)?.motions?.Keys.ToArray() ?? Array.Empty<string>();
        public void PlayAnimation(string id, string motion, bool loop)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id);
            if (item?.model == null || item.motions == null || !item.motions.TryGetValue(motion ?? "", out string file)) throw new ArgumentException("Choose one of this item’s authored Live2D motions.");
            if (item.loadedMotion != motion || item.clip == null)
            {
                if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("The authored motion exceeds 8 MB.");
                string model = ResolveLive2D(backend.SourceFolder, item.source);
                file = SakuraModelImport.ResolveFile(Path.GetDirectoryName(model), Path.GetRelativePath(Path.GetDirectoryName(model), file));
                var clip = CubismMotion3Json.LoadFrom(File.ReadAllText(file)).ToAnimationClip();
                if (clip == null || !Finite(clip.length) || clip.length <= 0) { if (clip != null) Destroy(clip); throw new InvalidDataException("The authored motion has no valid duration."); }
                if (item.clip != null) Destroy(item.clip);
                item.clip = clip; item.loadedMotion = motion;
            }
            item.animation = motion; item.loop = loop; item.animationPlaying = true; item.animationTime = 0; Save();
            Notify("Playing “" + motion + "” on “" + item.name + "”.");
        }
        public void StopAnimation(string id)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id); if (item == null) return;
            item.animationPlaying = false; item.animationTime = 0;
            var model = item.model?.GetComponent<CubismModel>();
            if (model != null) foreach (var parameter in model.Parameters) parameter.Value = parameter.DefaultValue;
            Save(); Notify("Item animation stopped.");
        }
        public void SetThrows(bool enabled)
        {
            profile.throwEnabled = enabled;
            if (!enabled) foreach (var item in profile.items) item.velocity = Vector2.zero;
            Save(); Changed?.Invoke();
        }
        public bool Throw(string id, Vector2 velocity)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id);
            if (item != null && !Finite(velocity)) item.velocity = Vector2.zero;
            if (!isActiveAndEnabled || !profile.throwEnabled || item == null || item.locked || item.follow || !Finite(velocity)) return false;
            item.velocity = new Vector2(Mathf.Clamp(velocity.x, -3, 3), Mathf.Clamp(velocity.y, -3, 3)); return true;
        }
        public void StopMovement(string id)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id); if (item == null) return;
            item.velocity = Vector2.zero; Save(); Changed?.Invoke();
        }
        public void Reorder(string id, int delta)
        {
            int from = profile.items.FindIndex(item => item.id == id);
            if (from < 0 || (delta != -1 && delta != 1)) return;
            int to = from + delta; if (to < 0 || to >= profile.items.Count) return;
            var item = profile.items[from]; profile.items.RemoveAt(from); profile.items.Insert(to, item);
            try { Save(); } catch { profile.items.RemoveAt(to); profile.items.Insert(from, item); throw; }
            LayoutItems(); Changed?.Invoke();
        }

        public async Task<string> AddImage(byte[] bytes, string name)
        {
            if (profile.items.Count >= MaximumItems) throw new InvalidOperationException("Remove an item before adding another. Up to eight stage items are supported.");
            var check = DecodeImage(bytes); Destroy(check);
            var item = new Item { id = Guid.NewGuid().ToString("N"), kind = "image", name = Path.GetFileName(name ?? "Image") };
            item.source = item.id + ".png";
            string file = OwnImagePath(storage, item.id); AtomicWrite(file, bytes);
            return await Add(item, file);
        }

        public async Task<string> AddLive2D(string relative, string name)
        {
            if (profile.items.Count >= MaximumItems) throw new InvalidOperationException("Remove an item before adding another. Up to eight stage items are supported.");
            ResolveLive2D(backend.SourceFolder, relative);
            return await Add(new Item { id = Guid.NewGuid().ToString("N"), kind = "live2d", name = name ?? Path.GetFileName(relative), source = relative }, null);
        }
        private async Task<string> Add(Item item, string ownFile)
        {
            profile.items.Add(item);
            string previousSelected = profile.selected;
            try { await Load(item); lifetime.Token.ThrowIfCancellationRequested(); if (!profile.items.Contains(item) || item.quad == null) throw new OperationCanceledException(); profile.selected = item.id; Save(); Notify("Added “" + item.name + "”."); return item.id; }
            catch { profile.items.Remove(item); profile.selected = previousSelected; DisposeItem(item); if (ownFile != null && File.Exists(ownFile)) File.Delete(ownFile); throw; }
        }

        public void Select(string id) { if (profile.items.Any(item => item.id == id)) { profile.selected = id; Changed?.Invoke(); } }
        public static Vector2 RotateOffset(Vector2 offset, float angle)
        {
            float radians = angle * Mathf.Deg2Rad;
            return new Vector2(offset.x * Mathf.Cos(radians) - offset.y * Mathf.Sin(radians), offset.x * Mathf.Sin(radians) + offset.y * Mathf.Cos(radians));
        }
        public static Vector2 ResolvePosition(AttachmentFrame frame, Vector2 offset) => frame.Position + RotateOffset(offset * frame.Scale, frame.Rotation);
        public static Vector2 ResolveOffset(AttachmentFrame frame, Vector2 position) => RotateOffset(position - frame.Position, -frame.Rotation) / Mathf.Max(.0001f, frame.Scale);
        private string ModelIdentity => (viewport?.LoadedModelPath ?? "").Replace('\\', '/').ToLowerInvariant();
        private void RefreshPins()
        {
            var avatar = viewport?.AvatarRoot;
            if (pinAvatar == avatar) return;
            pinAvatar = avatar; pinNodes.Clear(); pinFrames.Clear(); pinSampleFrame = -1;
            if (avatar == null) return;
            if (viewport.LoadedFormat == "Live2D")
            {
                var model = avatar.GetComponent<CubismModel>();
                if (model != null) foreach (var drawable in model.Drawables.Take(2000))
                    if (drawable != null && !string.IsNullOrEmpty(drawable.Id)) pinNodes.Add(new PinNode { Choice = new PinChoice("artmesh", drawable.Id, drawable.Id), Drawable = drawable });
            }
            else if (viewport.Pmx != null)
            {
                var pmx = viewport.Pmx;
                for (int i = 0; i < Math.Min(4096, pmx.Instance.BoneTransforms.Length); i++)
                {
                    var bone = pmx.Instance.BoneTransforms[i]; if (bone == null) continue;
                    pinNodes.Add(new PinNode { Choice = new PinChoice("bone", i + ":" + bone.name, i + " · " + bone.name), Bone = bone });
                }
            }
            else
            {
                var tools = avatar.GetComponent<SakuraModelTools>();
                if (tools != null) foreach (var bone in tools.Bones.Take(4096))
                    if (bone.Transform != null) pinNodes.Add(new PinNode { Choice = new PinChoice("bone", bone.Id, bone.Name), Bone = bone.Transform });
            }
            // Ambiguous identifiers must never silently select a different rig part.
            foreach (var group in pinNodes.GroupBy(node => node.Choice.Kind + ":" + node.Choice.Target).Where(group => group.Count() > 1).ToArray())
                foreach (var node in group.ToArray()) pinNodes.Remove(node);
        }
        public IReadOnlyList<PinChoice> PinChoices() { RefreshPins(); return pinNodes.Select(node => node.Choice).ToArray(); }
        private bool TryFrame(Item item, out AttachmentFrame frame)
        {
            frame = default;
            if (viewport?.RenderCamera == null || !viewport.HasAvatar) return false;
            var camera = viewport.RenderCamera;
            float zoom = Mathf.Max(.1f, viewport.ZoomPercent / 100);
            if (string.IsNullOrEmpty(item.pinKind))
            {
                var rootPoint = camera.WorldToViewportPoint(viewport.AvatarRoot.transform.position);
                frame = new AttachmentFrame(new Vector2(rootPoint.x, 1 - rootPoint.y), zoom); return rootPoint.z > 0;
            }
            if (item.pinModel != ModelIdentity) return false;
            RefreshPins();
            if (pinSampleFrame != Time.frameCount) { pinFrames.Clear(); pinSampleFrame = Time.frameCount; }
            string key = item.pinKind + ":" + item.pinTarget;
            if (pinFrames.TryGetValue(key, out frame)) return true;
            var node = pinNodes.FirstOrDefault(value => value.Choice.Kind == item.pinKind && value.Choice.Target == item.pinTarget);
            if (node == null) return false;
            Vector3 world; float angle = 0;
            if (node.Drawable != null)
            {
                var vertices = node.Drawable.VertexPositions;
                if (vertices == null || vertices.Length == 0 || vertices.Length > 100000) return false;
                Vector3 center = Vector3.zero;
                foreach (var vertex in vertices) { if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z)) return false; center += vertex; }
                world = node.Drawable.transform.TransformPoint(center / vertices.Length);
            }
            else if (node.Bone != null)
            {
                world = node.Bone.position;
                var origin = camera.WorldToViewportPoint(world);
                var axis = camera.WorldToViewportPoint(world + node.Bone.right * .1f);
                var delta = new Vector2(axis.x - origin.x, (origin.y - axis.y) / Mathf.Max(.01f, camera.aspect));
                if (delta.sqrMagnitude > .00000001f) angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            }
            else return false;
            var point = camera.WorldToViewportPoint(world);
            if (!Finite(point.x) || !Finite(point.y) || point.z <= camera.nearClipPlane) return false;
            frame = new AttachmentFrame(new Vector2(point.x, 1 - point.y), zoom, angle); pinFrames[key] = frame; return true;
        }
        private bool Resolved(Item item, out Vector2 position, out float width, out float rotation)
        {
            position = new Vector2(item.x, item.y); width = item.width; rotation = item.rotation;
            if (!item.follow) return true;
            if (!TryFrame(item, out var frame))
            {
                if (item.pinResolved) { position = item.lastPosition; width = item.lastWidth; rotation = item.lastRotation; }
                return string.IsNullOrEmpty(item.pinKind);
            }
            position = ResolvePosition(frame, new Vector2(item.anchorX, item.anchorY)); width = item.anchorWidth * frame.Scale;
            if (!string.IsNullOrEmpty(item.pinKind)) rotation = Mathf.DeltaAngle(0, item.anchorAngle + frame.Rotation);
            item.pinResolved = true; item.lastPosition = position; item.lastWidth = width; item.lastRotation = rotation; return true;
        }
        private Vector2 Position(Item item) { Resolved(item, out var position, out _, out _); return position; }
        private void Anchor(Item item)
        {
            if (!item.follow || !TryFrame(item, out var frame)) return;
            var offset = ResolveOffset(frame, new Vector2(item.x, item.y));
            item.anchorX = offset.x; item.anchorY = offset.y; item.anchorWidth = item.width / frame.Scale; item.anchorAngle = Mathf.DeltaAngle(frame.Rotation, item.rotation);
            item.pinResolved = true; item.lastPosition = new Vector2(item.x, item.y); item.lastWidth = item.width; item.lastRotation = item.rotation;
        }
        public void Pin(string id, string kind, string target)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id) ?? throw new ArgumentException("Select a stage item first.");
            Resolved(item, out var position, out var width, out var rotation);
            if (!string.IsNullOrEmpty(kind))
            {
                RefreshPins();
                if (pinNodes.Count(node => node.Choice.Kind == kind && node.Choice.Target == target) != 1) throw new ArgumentException("Choose an unambiguous part from the current model.");
                // Validate the target before changing either the saved attachment or its position.
                var candidate = new Item { pinKind = kind, pinTarget = target, pinModel = ModelIdentity };
                if (!TryFrame(candidate, out _)) throw new InvalidOperationException("That model part cannot be projected into the current view.");
            }
            item.x = position.x; item.y = position.y; item.width = width; item.rotation = rotation;
            item.pinKind = string.IsNullOrEmpty(kind) ? null : kind; item.pinTarget = string.IsNullOrEmpty(kind) ? null : target; item.pinModel = string.IsNullOrEmpty(kind) ? null : ModelIdentity;
            if (!string.IsNullOrEmpty(kind)) item.follow = true;
            item.velocity = Vector2.zero; Anchor(item); Save(); LayoutItems(); Changed?.Invoke();
        }
        public bool AttachmentAvailable(string id)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id);
            return item != null && (string.IsNullOrEmpty(item.pinKind) || item.pinModel == ModelIdentity && TryFrame(item, out _));
        }
        public void Sort(IList<(string id, bool? above)> order)
        {
            if (order == null) return;
            var ranks = order.Where(entry => !string.IsNullOrEmpty(entry.id)).Select((entry, index) => (entry.id, index)).GroupBy(entry => entry.id).ToDictionary(group => group.Key, group => group.First().index);
            var sorted = profile.items.Select((item, index) => (item, index)).OrderBy(pair => ranks.TryGetValue(pair.item.id, out int rank) ? rank : int.MaxValue - (profile.items.Count - pair.index)).Select(pair => pair.item).ToList();
            foreach (var entry in order) { var item = profile.items.FirstOrDefault(value => value.id == entry.id); if (item != null && entry.above != null) item.above = entry.above.Value; }
            profile.items.Clear(); profile.items.AddRange(sorted); Save(); LayoutItems(); Changed?.Invoke();
        }
        public void Configure(string id, float x, float y, float width, float rotation, bool above, bool follow, bool locked)
        {
            var item = profile.items.FirstOrDefault(value => value.id == id);
            if (item == null || !Finite(x) || !Finite(y) || !Finite(width) || !Finite(rotation)) return;
            if (item.follow)
            {
                Resolved(item, out var position, out var resolvedWidth, out var resolvedRotation);
                if (x == item.x) x = position.x;
                if (y == item.y) y = position.y;
                if (width == item.width) width = resolvedWidth;
                if (rotation == item.rotation) rotation = resolvedRotation;
            }
            if (!follow) { item.pinKind = null; item.pinTarget = null; item.pinModel = null; }
            item.x = Mathf.Clamp(x, -.5f, 1.5f); item.y = Mathf.Clamp(y, -.5f, 1.5f); item.width = Mathf.Clamp(width, .03f, 1.5f);
            item.rotation = Mathf.Clamp(rotation, -180, 180); item.above = above; item.follow = follow; item.locked = locked; item.velocity = Vector2.zero; Anchor(item); Save(); LayoutItems(); Changed?.Invoke();
        }
        public void Remove(string id)
        {
            int index = profile.items.FindIndex(item => item.id == id); if (index < 0) return;
            var item = profile.items[index]; string previousSelected = profile.selected; profile.items.RemoveAt(index);
            if (profile.selected == id) profile.selected = profile.items.FirstOrDefault()?.id;
            try { Save(); } catch { profile.items.Insert(index, item); profile.selected = previousSelected; throw; }
            DisposeItem(item);
            if (item.kind == "image") { string file = OwnImagePath(storage, item.id); if (File.Exists(file)) File.Delete(file); }
            Notify("Stage item removed.");
        }

        private void CreateLiveCamera()
        {
            if (liveCamera != null) return;
            var owner = new GameObject("Stage Live2D renderer"); owner.transform.SetParent(transform, false);
            liveCamera = owner.AddComponent<Camera>(); liveCamera.enabled = false; liveCamera.cullingMask = 1 << Live2DLayer;
            liveCamera.clearFlags = CameraClearFlags.SolidColor; liveCamera.backgroundColor = Color.clear; liveCamera.orthographic = true;
            liveCamera.nearClipPlane = .01f; liveCamera.farClipPlane = 50; liveCamera.allowHDR = false; liveCamera.allowMSAA = false;
            var data = liveCamera.GetUniversalAdditionalCameraData(); data.SetRenderer(1); data.renderPostProcessing = false; data.renderShadows = false;
        }
        private void LayoutItems()
        {
            if (Hidden) { foreach (var hidden in profile.items) hidden.quad?.SetActive(false); return; }
            var camera = viewport?.RenderCamera; if (camera == null) return;
            float depth = Mathf.Max(camera.nearClipPlane + .1f, 1);
            float worldWidth = Vector3.Distance(camera.ViewportToWorldPoint(new Vector3(0, .5f, depth)), camera.ViewportToWorldPoint(new Vector3(1, .5f, depth)));
            foreach (var item in profile.items)
            {
                if (item.quad == null) continue;
                bool visible = Resolved(item, out var position, out var width, out var rotation);
                item.quad.SetActive(isActiveAndEnabled && visible); if (!visible) continue;
                float aspect = item.image != null ? item.image.width / (float)item.image.height : 1;
                item.quad.transform.position = camera.ViewportToWorldPoint(new Vector3(position.x, 1 - position.y, depth));
                item.quad.transform.rotation = camera.transform.rotation * Quaternion.Euler(0, 0, -rotation);
                item.quad.transform.localScale = new Vector3(width * worldWidth, width * worldWidth / Mathf.Max(.01f, aspect), 1);
                item.material.renderQueue = (item.above ? 4000 : 1900) + profile.items.IndexOf(item);
            }
        }
        private void LateUpdate()
        {
            if (destroyed || viewport == null) return;
            LayoutItems();
            if (liveCamera == null || Time.unscaledTime < nextLiveFrame) return;
            nextLiveFrame = Time.unscaledTime + .1f;
            foreach (var item in profile.items)
            {
                if (item.model == null || item.liveTexture == null) continue;
                liveCamera.transform.position = item.center + Vector3.forward * 10; liveCamera.transform.LookAt(item.center);
                liveCamera.orthographicSize = Mathf.Max(.01f, item.modelHeight / 2);
                RenderPipeline.SubmitRenderRequest(liveCamera, new UniversalRenderPipeline.SingleCameraRequest { destination = item.liveTexture });
            }
        }

        private void Update()
        {
            if (destroyed || viewport == null) return;
            float dt = Time.unscaledDeltaTime;
            foreach (var item in profile.items)
            {
                if (item.locked || item.follow || !profile.throwEnabled) item.velocity = Vector2.zero;
                if (item.velocity != Vector2.zero && !(dragPointer >= 0 && Selected == item))
                {
                    var state = SimulateThrow(new ThrowState { Position = new Vector2(item.x, item.y), Velocity = item.velocity }, dt);
                    item.x = state.Position.x; item.y = state.Position.y; item.velocity = state.Velocity; physicsDirty = true;
                }
                if (!item.animationPlaying || item.clip == null || item.model == null) continue;
                if (!Finite(dt) || !Finite(item.animationTime)) { item.animationPlaying = false; continue; }
                item.animationTime += Mathf.Clamp(dt, 0, .05f);
                if (item.loop) item.animationTime %= item.clip.length;
                float time = Mathf.Min(item.animationTime, item.clip.length);
                try { item.clip.SampleAnimation(item.model, time); }
                catch (Exception error) { item.animationPlaying = false; Notify("Item animation paused. " + error.Message); continue; }
                var model = item.model.GetComponent<CubismModel>();
                if (model != null && model.Parameters.Any(parameter => !Finite(parameter.Value)))
                {
                    item.animationPlaying = false;
                    foreach (var parameter in model.Parameters) if (!Finite(parameter.Value)) parameter.Value = parameter.DefaultValue;
                    Notify("Item animation paused after invalid parameter data.");
                }
                if (!item.loop && item.animationTime >= item.clip.length)
                {
                    item.animationPlaying = false;
                    try { Save(); Changed?.Invoke(); } catch (Exception error) { Notify(error.Message); }
                }
            }
            if (physicsDirty && Time.unscaledTime >= nextPhysicsSave)
            {
                physicsDirty = false; nextPhysicsSave = Time.unscaledTime + .5f;
                try { Save(); } catch (Exception error) { Notify(error.Message); }
            }
        }

        public void AttachStage(VisualElement target)
        {
            if (stage == target) return;
            if (stage != null) { stage.UnregisterCallback<PointerDownEvent>(PointerDown, TrickleDown.TrickleDown); stage.UnregisterCallback<PointerMoveEvent>(PointerMove, TrickleDown.TrickleDown); stage.UnregisterCallback<PointerUpEvent>(PointerUp, TrickleDown.TrickleDown); stage.UnregisterCallback<PointerCaptureOutEvent>(CaptureLost); }
            stage = target;
            stage.RegisterCallback<PointerDownEvent>(PointerDown, TrickleDown.TrickleDown); stage.RegisterCallback<PointerMoveEvent>(PointerMove, TrickleDown.TrickleDown); stage.RegisterCallback<PointerUpEvent>(PointerUp, TrickleDown.TrickleDown);
            stage.RegisterCallback<PointerCaptureOutEvent>(CaptureLost);
        }
        private void CaptureLost(PointerCaptureOutEvent evt)
        {
            if (evt.pointerId != dragPointer) return;
            dragPointer = -1; dragVelocity = Vector2.zero;
            try { Save(); Changed?.Invoke(); } catch (Exception error) { Notify(error.Message); }
        }
        private Vector2 Pointer(Vector2 position) { var local = stage.WorldToLocal(position); return new Vector2(local.x / Mathf.Max(1, stage.layout.width), local.y / Mathf.Max(1, stage.layout.height)); }
        private void PointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || viewport?.RenderCamera == null) return;
            var point = Pointer(evt.position);
            var hit = profile.items.OrderByDescending(item => item.above).ThenByDescending(item => profile.items.IndexOf(item)).FirstOrDefault(item =>
            {
                if (item.quad == null) return false;
                if (!Resolved(item, out var center, out var width, out var rotation)) return false;
                float aspect = item.image != null ? item.image.width / (float)item.image.height : 1;
                Vector2 delta = point - center; float radians = -rotation * Mathf.Deg2Rad;
                var local = new Vector2(delta.x * Mathf.Cos(radians) - delta.y / viewport.RenderCamera.aspect * Mathf.Sin(radians), delta.x * Mathf.Sin(radians) + delta.y / viewport.RenderCamera.aspect * Mathf.Cos(radians));
                return Mathf.Abs(local.x) <= width / 2 && Mathf.Abs(local.y) <= width / Mathf.Max(.01f, aspect) / 2;
            });
            if (hit == null) return;
            Select(hit.id); evt.StopImmediatePropagation();
            if (hit.locked) return;
            hit.velocity = Vector2.zero; dragVelocity = Vector2.zero; lastDragPoint = point; lastDragTime = Time.unscaledTime;
            dragPointer = evt.pointerId; dragStart = point; itemStart = Position(hit); stage.CapturePointer(evt.pointerId);
        }
        private void PointerMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != dragPointer || !stage.HasPointerCapture(evt.pointerId)) return;
            evt.StopImmediatePropagation();
            if (Selected == null || Selected.locked) return;
            var point = itemStart + Pointer(evt.position) - dragStart;
            Resolved(Selected, out _, out var width, out var rotation);
            Selected.x = Mathf.Clamp(point.x, -.5f, 1.5f); Selected.y = Mathf.Clamp(point.y, -.5f, 1.5f); Selected.width = width; Selected.rotation = rotation; Anchor(Selected); LayoutItems();
            float elapsed = Time.unscaledTime - lastDragTime;
            var pointer = Pointer(evt.position);
            if (elapsed > .001f) dragVelocity = (pointer - lastDragPoint) / elapsed;
            lastDragPoint = pointer; lastDragTime = Time.unscaledTime;
        }
        private void PointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != dragPointer) return;
            dragPointer = -1; stage.ReleasePointer(evt.pointerId);
            if (Selected != null && Time.unscaledTime - lastDragTime <= .15f) Throw(Selected.id, dragVelocity);
            dragVelocity = Vector2.zero;
            try { Save(); Changed?.Invoke(); } catch (Exception error) { Notify(error.Message); }
            evt.StopImmediatePropagation();
        }

        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            Text("Stage items", card, "card-title");
            var add = new Foldout { text = "Add item" }; card.Add(add);
            add.Add(new Button(AddImagePicker) { text = "Add transparent image" });
            var library = new DropdownField("Imported Live2D asset", new List<string> { "Loading models…" }, 0); add.Add(library);
            var paths = new List<string>();
            backend.Api("/api/models", null, (json, error) =>
            {
                if (destroyed || library.panel == null) return;
                if (error != null) { Notify(error); return; }
                try
                {
                    var rows = (JObject.Parse(json)["live2d_models"] as JArray ?? new JArray()).Where(row => row["path"]?.Type == JTokenType.String).ToArray();
                    paths = rows.Select(row => (string)row["path"]).ToList();
                    library.choices = new[] { "Choose a Live2D asset…" }.Concat(rows.Select(row => (string)row["name"] ?? (string)row["path"])).ToList(); library.index = 0;
                }
                catch (Newtonsoft.Json.JsonException) { Notify("The local Live2D library could not be read."); }
            });
            add.Add(new Button(async () =>
            {
                if (adding || library.index <= 0 || library.index > paths.Count) return;
                adding = true;
                try { await AddLive2D(paths[library.index - 1], library.value); }
                catch (Exception error) { Notify(error.Message); }
                finally { adding = false; }
            }) { text = "Add selected Live2D asset" });
            var list = new DropdownField("Items", new List<string> { "No items" }, 0); card.Add(list);
            var throwing = new Toggle("Throw items (physics)") { value = profile.throwEnabled }; card.Add(throwing);
            throwing.RegisterValueChangedCallback(change => { try { SetThrows(change.newValue); } catch (Exception error) { Notify(error.Message); } });
            var body = new VisualElement(); card.Add(body);
            var status = Text(Status, card, "description");
            string[] ids = Array.Empty<string>();
            Item rendered = null;
            Action updateControls = null;
            list.RegisterValueChangedCallback(_ => { if (list.index >= 0 && list.index < ids.Length) Select(ids[list.index]); });
            void Refresh()
            {
                ids = profile.items.Select(item => item.id).ToArray(); list.choices = ids.Length == 0 ? new List<string> { "No items" } : profile.items.Select(item => item.name).ToList();
                throwing.SetValueWithoutNotify(profile.throwEnabled);
                list.SetValueWithoutNotify(list.choices[Math.Max(0, Array.IndexOf(ids, profile.selected))]); status.text = Status;
                var item = Selected;
                if (item != null && item == rendered) { updateControls?.Invoke(); return; }
                body.Clear(); rendered = item; updateControls = null; if (item == null) return;
                var layer = new DropdownField("Layer", new List<string> { "Above model", "Below model" }, item.above ? 0 : 1); body.Add(layer);
                var x = new SakuraSlider("Horizontal position", -50, 150) { value = item.x * 100, showInputField = true }; body.Add(x); x.SetEnabled(!item.locked);
                var y = new SakuraSlider("Vertical position", -50, 150) { value = item.y * 100, showInputField = true }; body.Add(y); y.SetEnabled(!item.locked);
                var size = new SakuraSlider("Size", 3, 150) { value = item.width * 100, showInputField = true }; body.Add(size);
                var rotation = new SakuraSlider("Rotation", -180, 180) { value = item.rotation, showInputField = true }; body.Add(rotation);
                var follow = new Toggle("Attach to model movement and size") { value = item.follow }; body.Add(follow);
                var choices = PinChoices().ToList();
                var pinLabels = new List<string> { "Whole model" }; pinLabels.AddRange(choices.Select(choice => (choice.Kind == "bone" ? "Bone · " : "ArtMesh · ") + choice.Name));
                int pinIndex = choices.FindIndex(choice => choice.Kind == item.pinKind && choice.Target == item.pinTarget);
                var pin = new DropdownField("Pin item to model part", pinLabels, pinIndex + 1); body.Add(pin);
                var pinStatus = new Label(); pinStatus.AddToClassList("description"); body.Add(pinStatus);
                pin.RegisterValueChangedCallback(_ => { try { if (pin.index <= 0) Pin(item.id, null, null); else { var choice = choices[pin.index - 1]; Pin(item.id, choice.Kind, choice.Target); } } catch (Exception error) { Notify(error.Message); } });
                var locked = new Toggle("Lock item position") { value = item.locked }; body.Add(locked);
                var order = new VisualElement(); order.AddToClassList("row"); body.Add(order);
                var up = new Button(() => { try { Reorder(item.id, -1); } catch (Exception error) { Notify(error.Message); } }) { text = "Move up" }; order.Add(up);
                var down = new Button(() => { try { Reorder(item.id, 1); } catch (Exception error) { Notify(error.Message); } }) { text = "Move down" }; order.Add(down);
                order.Add(new Button(() => { try { StopMovement(item.id); } catch (Exception error) { Notify(error.Message); } }) { text = "Stop movement" });
                var motionNames = MotionNames(item.id).ToList();
                var animation = new DropdownField("Animation", motionNames.Count > 0 ? motionNames : new List<string> { "No authored motions" }, Math.Max(0, motionNames.IndexOf(item.animation))); body.Add(animation);
                var loop = new Toggle("Loop animation") { value = item.loop }; body.Add(loop);
                loop.RegisterValueChangedCallback(change => { item.loop = change.newValue; try { Save(); } catch (Exception error) { Notify(error.Message); } });
                var playback = new VisualElement(); playback.AddToClassList("row"); body.Add(playback);
                var play = new Button(() => { try { PlayAnimation(item.id, animation.value, loop.value); } catch (Exception error) { Notify(error.Message); } }) { text = "Play" }; playback.Add(play);
                var stop = new Button(() => { try { StopAnimation(item.id); } catch (Exception error) { Notify(error.Message); } }) { text = "Stop animation" }; playback.Add(stop);
                updateControls = () =>
                {
                    layer.SetValueWithoutNotify(item.above ? "Above model" : "Below model"); x.SetValueWithoutNotify(item.x * 100); y.SetValueWithoutNotify(item.y * 100);
                    x.SetEnabled(!item.locked); y.SetEnabled(!item.locked); size.SetValueWithoutNotify(item.width * 100); rotation.SetValueWithoutNotify(item.rotation);
                    follow.SetValueWithoutNotify(item.follow); locked.SetValueWithoutNotify(item.locked);
                    pin.SetEnabled(viewport.HasAvatar); pinStatus.text = string.IsNullOrEmpty(item.pinKind) ? "Choose a specific ArtMesh or bone, or use whole-model following." : AttachmentAvailable(item.id) ? "Pinned to " + item.pinKind + ". The attachment is limited to this character." : "Pinned character or part is unavailable. This item is hidden until it returns; select Whole model to detach it.";
                    up.SetEnabled(profile.items.IndexOf(item) > 0); down.SetEnabled(profile.items.IndexOf(item) < profile.items.Count - 1);
                    play.SetEnabled(motionNames.Count > 0 && item.model != null); stop.SetEnabled(item.animationPlaying); loop.SetValueWithoutNotify(item.loop);
                };
                updateControls();
                void Apply() { try { Configure(item.id, x.value / 100, y.value / 100, size.value / 100, rotation.value, layer.index == 0, follow.value, locked.value); } catch (Exception error) { Notify(error.Message); } }
                layer.RegisterValueChangedCallback(_ => Apply()); x.RegisterValueChangedCallback(_ => Apply()); y.RegisterValueChangedCallback(_ => Apply());
                size.RegisterValueChangedCallback(_ => Apply()); rotation.RegisterValueChangedCallback(_ => Apply()); follow.RegisterValueChangedCallback(_ => Apply()); locked.RegisterValueChangedCallback(_ => Apply());
                body.Add(new Button(() => { try { Remove(item.id); } catch (Exception error) { Notify(error.Message); } }) { text = "Remove item" });
            }
            Changed += Refresh; card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= Refresh); Refresh();
            Text("Images and Live2D items appear in the avatar output. Drag and release an unlocked, unattached item to throw it. Authored Live2D motions can play or loop. Pinning follows the selected Live2D ArtMesh or 3D bone; a missing or different character hides its pinned items.", card, "description");
        }
        private void AddImagePicker()
        {
            if (adding || profile.items.Count >= MaximumItems) { Notify("Remove an item before adding another. Up to eight stage items are supported."); return; }
            adding = true;
            backend.RunPmxTool("backdrop-image", "", async (json, error) =>
            {
                if (destroyed) return;
                try
                {
                    if (error != null) throw new InvalidOperationException(error);
                    var result = JObject.Parse(json); if (result.Value<bool?>("cancelled") == true) return;
                    string encoded = result.Value<string>("image");
                    if (encoded == null || encoded.Length > 28 * 1024 * 1024) throw new InvalidDataException("Choose an item image under 20 MB.");
                    await AddImage(Convert.FromBase64String(encoded), result.Value<string>("name"));
                }
                catch (Exception failure) { Notify(failure.Message); }
                finally { adding = false; }
            });
        }
        private static Label Text(string text, VisualElement parent, string style) { var label = new Label(text); label.AddToClassList(style); parent.Add(label); return label; }
        private void DisposeItem(Item item)
        {
            item.velocity = Vector2.zero; item.animationPlaying = false;
            if (item.clip != null) { Destroy(item.clip); item.clip = null; }
            if (item.quad != null) { item.quad.SetActive(false); Destroy(item.quad); item.quad = null; }
            if (item.model != null) { item.model.SetActive(false); Destroy(item.model); item.model = null; }
            if (item.material != null) { Destroy(item.material); item.material = null; }
            if (item.image != null) { Destroy(item.image); item.image = null; }
            if (item.liveTexture != null) { item.liveTexture.Release(); Destroy(item.liveTexture); item.liveTexture = null; }
        }
        private void OnEnable()
        {
            foreach (var item in profile.items) { if (item.quad != null) item.quad.SetActive(true); if (item.model != null) item.model.SetActive(true); }
        }
        private void OnDisable()
        {
            int pointer = dragPointer; dragPointer = -1; dragVelocity = Vector2.zero;
            if (pointer >= 0 && stage != null && stage.HasPointerCapture(pointer)) stage.ReleasePointer(pointer);
            foreach (var item in profile.items) { if (item.quad != null) item.quad.SetActive(false); if (item.model != null) item.model.SetActive(false); }
            if (physicsDirty && storage != null)
            {
                physicsDirty = false;
                try { Save(); } catch (Exception error) { Notify(error.Message); }
            }
        }
        private void OnDestroy()
        {
            destroyed = true; lifetime.Cancel();
            if (stage != null) { stage.UnregisterCallback<PointerDownEvent>(PointerDown, TrickleDown.TrickleDown); stage.UnregisterCallback<PointerMoveEvent>(PointerMove, TrickleDown.TrickleDown); stage.UnregisterCallback<PointerUpEvent>(PointerUp, TrickleDown.TrickleDown); stage.UnregisterCallback<PointerCaptureOutEvent>(CaptureLost); }
            foreach (var item in profile.items) DisposeItem(item);
            if (liveCamera != null) Destroy(liveCamera.gameObject);
        }
    }
}
