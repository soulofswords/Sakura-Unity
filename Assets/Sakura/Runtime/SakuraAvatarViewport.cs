using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using UniVRM10;
using UnityEngine.Rendering.Universal;

namespace Sakura.Unity
{
    public sealed class SakuraAvatarViewport : MonoBehaviour
    {
        public event Action<string> StatusChanged;
        public bool Loading { get; private set; }
        public bool HasAvatar => avatar != null;
        public string LoadedFormat { get; private set; }
        public string LastImportError { get; private set; }
        public string ImportDetails { get; private set; }
        public string LoadedModelPath { get; private set; }
        public SakuraPmxModel Pmx => avatar == null ? null : avatar.GetComponent<SakuraPmxModel>();
        public SakuraIdleMotion Idle => avatar == null ? null : avatar.GetComponent<SakuraIdleMotion>();
        private GameObject avatar;
        private Camera avatarCamera;
        private RenderTexture target;
        private CancellationTokenSource lifetime;
        private Image image;
        private float yaw, zoom = 1;
        private Bounds? modelBounds;

        public void Attach(VisualElement stage)
        {
            lifetime = new CancellationTokenSource();
            var cameraObject = new GameObject("Avatar viewport camera");
            cameraObject.transform.SetParent(transform);
            avatarCamera = cameraObject.AddComponent<Camera>();
            avatarCamera.clearFlags = CameraClearFlags.SolidColor;
            avatarCamera.backgroundColor = Color.clear;
            avatarCamera.cullingMask = 1 << 30;
            avatarCamera.fieldOfView = 30;
            avatarCamera.nearClipPlane = .01f;
            avatarCamera.farClipPlane = 100;
            image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.style.position = Position.Absolute;
            image.style.left = 0; image.style.right = 0; image.style.top = 0; image.style.bottom = 0;
            stage.Insert(0, image);
            stage.RegisterCallback<GeometryChangedEvent>(args => Resize((int)args.newRect.width, (int)args.newRect.height));
            Resize(1024, 1024);
            var lightObject = new GameObject("Avatar key light");
            lightObject.transform.SetParent(transform);
            lightObject.transform.rotation = Quaternion.Euler(25, -25, 0);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1; light.cullingMask = 1 << 30;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.75f, .75f, .75f);
        }

        private void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 128, 1920); height = Mathf.Clamp(height, 128, 1920);
            if (target != null && target.width == width && target.height == height) return;
            if (target != null) { avatarCamera.targetTexture = null; target.Release(); Destroy(target); }
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 2 };
            target.Create(); avatarCamera.targetTexture = target; image.image = target;
            if (avatar != null) FrameAvatar();
        }

        public static string ResolveCharacterPath(string source, string relative)
        {
            string path = SakuraModelImport.ResolveFile(Path.Combine(source, "models"), relative);
            if (!path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a PMX, VRM or Live2D model from the existing library.");
            return path;
        }

        public Task LoadVrm(string source, string relative) => LoadModel(source, relative);

        public async Task LoadModel(string source, string relative)
        {
            if (Loading) { StatusChanged?.Invoke("A character is already loading. Please wait."); return; }
            Loading = true;
            LastImportError = null;
            try
            {
                string path = ResolveCharacterPath(source, relative);
                if (!File.Exists(path)) throw new FileNotFoundException("The selected model has moved or was removed.");
                string format = path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) ? "VRM" : path.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase) ? "PMX" : "Live2D";
                StatusChanged?.Invoke("Loading " + format + " into Unity…");
                GameObject loaded;
                if (format == "VRM")
                {
                    var instance = await Vrm10.LoadPathAsync(path, canLoadVrm0X: true,
                        materialGenerator: new UrpVrm10MaterialDescriptorGenerator(), ct: lifetime.Token);
                    loaded = instance == null ? null : instance.gameObject;
                }
                else if (format == "PMX") loaded = await SakuraModelImport.LoadPmx(path, lifetime.Token, text => { ImportDetails = text; StatusChanged?.Invoke(text); });
                else loaded = SakuraModelImport.LoadLive2D(path);
                if (loaded == null) throw new InvalidOperationException("The importer returned no model.");
                try
                {
                    var motion = loaded.AddComponent<SakuraIdleMotion>(); motion.Initialize(path);
                    var studio = GetComponent<SakuraStudio>(); motion.Companion = studio == null || studio.CurrentMode == "Companion";
                }
                catch { Destroy(loaded); throw; }
                if (lifetime.IsCancellationRequested) { Destroy(loaded); return; }
                var previous = avatar;
                avatar = loaded;
                LoadedFormat = format;
                LoadedModelPath = relative;
                avatarCamera.GetUniversalAdditionalCameraData().SetRenderer(format == "Live2D" ? 1 : -1);
                avatar.transform.SetParent(transform, false);
                avatar.transform.localPosition = Vector3.zero;
                yaw = format == "Live2D" ? 180 : 0; zoom = 1;
                avatar.transform.localRotation = Quaternion.Euler(0, yaw, 0);
                foreach (var child in avatar.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
                modelBounds = null;
                if (format == "PMX")
                {
                    var skinned = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                    if (skinned != null)
                    {
                        var baked = new Mesh();
                        try { skinned.BakeMesh(baked); baked.RecalculateBounds(); modelBounds = baked.bounds; }
                        finally { Destroy(baked); }
                    }
                }
                // RuntimeGltfInstance owns and disposes imported meshes, materials and textures on destruction.
                if (previous != null) Destroy(previous);
                await Task.Yield();
                FrameAvatar();
                if (format != "PMX") ImportDetails = format + " loaded in the Unity renderer.";
                StatusChanged?.Invoke(ImportDetails);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { LastImportError = error.Message; StatusChanged?.Invoke("Could not load model. " + error.Message); }
            finally { Loading = false; }
        }

        private void FrameAvatar()
        {
            var renderers = avatar.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            if (modelBounds.HasValue) bounds = modelBounds.Value;
            float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x / Mathf.Max(.1f, avatarCamera.aspect));
            float distance = halfHeight / Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.12f / zoom;
            avatarCamera.transform.position = bounds.center + Vector3.forward * distance;
            avatarCamera.transform.LookAt(bounds.center);
        }

        public void Rotate(float degrees) { if (avatar == null) return; yaw += degrees; avatar.transform.localRotation = Quaternion.Euler(0, yaw, 0); }
        public void Zoom(float factor) { zoom = Mathf.Clamp(zoom * factor, .5f, 3); if (avatar != null) FrameAvatar(); }
        public void ResetFraming() { zoom = 1; if (avatar != null) FrameAvatar(); }

        public string SaveSnapshot(string purpose)
        {
            if (target == null || avatar == null) throw new InvalidOperationException("Load a model first.");
            if (string.IsNullOrEmpty(purpose) || purpose.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
                throw new ArgumentException("Invalid snapshot name.");
            string folder = System.IO.Path.Combine(Application.persistentDataPath, "snapshots");
            using (var digest = System.Security.Cryptography.SHA256.Create())
                folder = System.IO.Path.Combine(folder, BitConverter.ToString(digest.ComputeHash(System.Text.Encoding.UTF8.GetBytes(LoadedModelPath))).Replace("-", ""));
            System.IO.Directory.CreateDirectory(folder);
            string file = System.IO.Path.Combine(folder, purpose + ".png");
            var previous = RenderTexture.active;
            var pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                System.IO.File.WriteAllBytes(file, pixels.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; Destroy(pixels); }
            return file;
        }

        public async Task<string> SaveSnapshotNextFrame(string purpose)
        {
            var model = avatar;
            int frame = Time.frameCount;
            float deadline = Time.realtimeSinceStartup + 8;
            while (Time.frameCount <= frame + 1)
            {
                if (this == null || avatar != model || Time.realtimeSinceStartup > deadline) throw new InvalidOperationException("The model changed or renderer stopped before the snapshot was ready.");
                await Task.Yield();
            }
            return SaveSnapshot(purpose);
        }

        public float[] EditorDirection()
        {
            var direction = avatar.transform.InverseTransformDirection(-avatarCamera.transform.forward).normalized;
            // MMD loader rotates X/Z by 180 degrees; the editor bridge flips Z itself.
            return new[] { -direction.x, direction.y, direction.z };
        }

        public Color32[] ReadAvatarPixels(int size)
        {
            var temporary = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(target, temporary); RenderTexture.active = temporary;
                texture.ReadPixels(new Rect(0, 0, size, size), 0, 0); texture.Apply(); return texture.GetPixels32();
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(temporary); Destroy(texture); }
        }

        private void OnDestroy()
        {
            lifetime?.Cancel(); lifetime?.Dispose();
            if (avatar != null) Destroy(avatar);
            if (target != null) { if (avatarCamera != null) avatarCamera.targetTexture = null; target.Release(); Destroy(target); }
        }
    }
}
