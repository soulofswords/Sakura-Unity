using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using UniVRM10;

namespace Sakura.Unity
{
    public sealed class SakuraAvatarViewport : MonoBehaviour
    {
        public event Action<string> StatusChanged;
        public bool Loading { get; private set; }
        public bool HasAvatar => avatar != null;
        private GameObject avatar;
        private Camera avatarCamera;
        private RenderTexture target;
        private CancellationTokenSource lifetime;
        private Image image;
        private float yaw, zoom = 1;

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
            string root = Path.GetFullPath(Path.Combine(source, "models")) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a VRM from the existing model library.");
            return path;
        }

        public async Task LoadVrm(string source, string relative)
        {
            if (Loading) { StatusChanged?.Invoke("A character is already loading. Please wait."); return; }
            Loading = true;
            try
            {
                string path = ResolveCharacterPath(source, relative);
                if (!File.Exists(path)) throw new FileNotFoundException("The selected VRM has moved or was removed.");
                StatusChanged?.Invoke("Loading VRM into Unity…");
                var instance = await Vrm10.LoadPathAsync(path, canLoadVrm0X: true,
                    materialGenerator: new UrpVrm10MaterialDescriptorGenerator(), ct: lifetime.Token);
                if (instance == null) throw new InvalidOperationException("VRM importer returned no model.");
                if (lifetime.IsCancellationRequested) { Destroy(instance.gameObject); return; }
                var previous = avatar;
                avatar = instance.gameObject;
                avatar.transform.SetParent(transform, false);
                avatar.transform.localPosition = Vector3.zero;
                yaw = 0; zoom = 1;
                avatar.transform.localRotation = Quaternion.Euler(0, yaw, 0);
                foreach (var child in avatar.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
                // RuntimeGltfInstance owns and disposes imported meshes, materials and textures on destruction.
                if (previous != null) Destroy(previous);
                FrameAvatar();
                StatusChanged?.Invoke("VRM loaded in the Unity renderer. Tracking and expression parity are pending.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { StatusChanged?.Invoke("Could not load VRM. " + error.Message); }
            finally { Loading = false; }
        }

        private void FrameAvatar()
        {
            var renderers = avatar.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x / Mathf.Max(.1f, avatarCamera.aspect));
            float distance = halfHeight / Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.12f / zoom;
            avatarCamera.transform.position = bounds.center + Vector3.forward * distance;
            avatarCamera.transform.LookAt(bounds.center);
        }

        public void Rotate(float degrees) { if (avatar == null) return; yaw += degrees; avatar.transform.localRotation = Quaternion.Euler(0, yaw, 0); }
        public void Zoom(float factor) { zoom = Mathf.Clamp(zoom * factor, .5f, 3); if (avatar != null) FrameAvatar(); }
        public void ResetFraming() { zoom = 1; if (avatar != null) FrameAvatar(); }

        private void OnDestroy()
        {
            lifetime?.Cancel(); lifetime?.Dispose();
            if (avatar != null) Destroy(avatar);
            if (target != null) { target.Release(); Destroy(target); }
        }
    }
}
