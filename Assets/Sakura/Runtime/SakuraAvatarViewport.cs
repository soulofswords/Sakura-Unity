using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using UniVRM10;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering;

namespace Sakura.Unity
{
    public sealed class SakuraAvatarViewport : MonoBehaviour
    {
        public event Action<string> StatusChanged;
        public event Action ModelChanged;
        public bool Loading { get; private set; }
        public bool HasAvatar => avatar != null;
        public string LoadedFormat { get; private set; }
        public string LastImportError { get; private set; }
        public string ImportDetails { get; private set; }
        public string LoadedModelPath { get; private set; }
        public SakuraPmxModel Pmx => avatar == null ? null : avatar.GetComponent<SakuraPmxModel>();
        public SakuraIdleMotion Idle => avatar == null ? null : avatar.GetComponent<SakuraIdleMotion>();
        public SakuraAvatarExpressions Expressions => avatar == null ? null : avatar.GetComponent<SakuraAvatarExpressions>();
        private GameObject avatar;
        private Camera avatarCamera;
        private RenderTexture target;
        private CancellationTokenSource lifetime;
        private Image image;
        private float yaw, live2DRoll, zoom = 1, targetZoom = 1;
        private Vector3 framingOffset;
        public bool PositionLocked { get; set; }
        private Bounds? modelBounds;
        private Bounds? framingBounds;
        private Action<int> pickedPart;
        private bool pickBone;
        private int renderLimit = 1280, renderSamples = 2, viewportWidth = 1024, viewportHeight = 1024;

        public void BeginPartPick(bool bone, Action<int> completed) { pickBone = bone; pickedPart = completed; }
        public void CancelPartPick() { pickedPart = null; }

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
            avatarCamera.allowMSAA = true;
            avatarCamera.nearClipPlane = .01f;
            avatarCamera.farClipPlane = 100;
            image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.style.position = Position.Absolute;
            image.style.left = 0; image.style.right = 0; image.style.top = 0; image.style.bottom = 0;
            stage.Insert(0, image);
            RenderPipelineManager.endCameraRendering -= ApplyModelEffects;
            RenderPipelineManager.endCameraRendering += ApplyModelEffects;
            stage.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || pickedPart == null || avatar == null) return;
                var point = stage.WorldToLocal(evt.position);
                int result = PickPart(new Vector2(point.x / Mathf.Max(1,stage.layout.width),1 - point.y / Mathf.Max(1,stage.layout.height)),pickBone);
                if (result >= 0) { var callback = pickedPart; pickedPart = null; callback(result); }
                else StatusChanged?.Invoke("Click a visible part of the model to select it.");
                evt.StopImmediatePropagation();
            });
            Vector2 dragStart = default; Vector3 offsetStart = default; float yawStart = 0; int dragButton = -1;
            Vector2 Normalized(Vector2 point) => new Vector2(point.x / Mathf.Max(1,stage.layout.width),1 - point.y / Mathf.Max(1,stage.layout.height));
            bool Hit(Vector2 local)
            {
                if (avatar == null) return false;
                var normalized = Normalized(local);
                if (LoadedFormat != "Live2D") return PickPart(normalized,false) >= 0;
                var previous = RenderTexture.active; var pixel = new Texture2D(1,1,TextureFormat.RGBA32,false);
                try
                {
                    RenderTexture.active = target;
                    pixel.ReadPixels(new Rect(Mathf.Clamp((int)(normalized.x * target.width),0,target.width - 1),Mathf.Clamp((int)(normalized.y * target.height),0,target.height - 1),1,1),0,0);
                    pixel.Apply(); return pixel.GetPixel(0,0).a > .1f;
                }
                finally { RenderTexture.active = previous; Destroy(pixel); }
            }
            stage.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (pickedPart != null || PositionLocked || evt.button > 1 || !Hit(stage.WorldToLocal(evt.position))) return;
                dragStart = stage.WorldToLocal(evt.position); offsetStart = framingOffset; yawStart = LoadedFormat == "Live2D" ? live2DRoll : yaw; dragButton = evt.button;
                stage.CapturePointer(evt.pointerId); evt.StopPropagation();
            });
            stage.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (dragButton < 0 || !stage.HasPointerCapture(evt.pointerId)) return;
                Vector2 delta = stage.WorldToLocal(evt.position) - dragStart;
                if (dragButton == 1)
                {
                    if (LoadedFormat == "Live2D") live2DRoll = yawStart + delta.x * .35f;
                    else yaw = yawStart + delta.x * .35f;
                    ApplyRotation();
                }
                else
                {
                    float units = 2 * (avatarCamera.transform.position - (modelBounds?.center ?? avatar.transform.position) - framingOffset).magnitude * Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f) / Mathf.Max(1,stage.layout.height);
                    framingOffset = offsetStart - avatarCamera.transform.right * delta.x * units + avatarCamera.transform.up * delta.y * units;
                    FrameAvatar();
                }
                evt.StopPropagation();
            });
            stage.RegisterCallback<PointerUpEvent>(evt => { if (stage.HasPointerCapture(evt.pointerId)) { stage.ReleasePointer(evt.pointerId); dragButton = -1; evt.StopPropagation(); } });
            stage.RegisterCallback<PointerCaptureOutEvent>(_ => dragButton = -1);
            stage.RegisterCallback<WheelEvent>(evt => { if (!PositionLocked && Hit(stage.WorldToLocal(evt.mousePosition))) { Zoom(Mathf.Exp(-evt.delta.y * .035f)); evt.StopPropagation(); } });
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
            viewportWidth = Mathf.Max(128, width); viewportHeight = Mathf.Max(128, height);
            var dimensions = SakuraOutput.Dimensions(viewportWidth, viewportHeight, renderLimit);
            width = dimensions.x; height = dimensions.y;
            if (target != null && target.width == width && target.height == height && target.antiAliasing == renderSamples) return;
            if (target != null) { avatarCamera.targetTexture = null; target.Release(); Destroy(target); }
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = renderSamples, filterMode = FilterMode.Bilinear };
            target.Create(); avatarCamera.targetTexture = target; image.image = target;
            if (avatar != null) FrameAvatar();
        }

        public void SetRenderQuality(SakuraOutput.Quality quality)
        {
            renderLimit = quality == SakuraOutput.Quality.Low ? 960 : quality == SakuraOutput.Quality.High ? 1920 : 1280;
            renderSamples = quality == SakuraOutput.Quality.Low ? 1 : quality == SakuraOutput.Quality.High ? 4 : 2;
            if (avatarCamera != null) Resize(viewportWidth, viewportHeight);
        }

        public static string ResolveCharacterPath(string source, string relative)
        {
            string path = SakuraModelImport.ResolveFile(Path.Combine(source, "models"), relative);
            if (!path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a PMX, VRM or Live2D model from the existing library.");
            return path;
        }

        public Task LoadVrm(string source, string relative) => LoadModel(source, relative);

        public GameObject AvatarRoot => avatar;
        public RenderTexture OutputTexture => target;
        public Camera RenderCamera => avatarCamera;
        public float ZoomPercent => targetZoom * 100;

        public int PickPart(Vector2 normalized, bool bone)
        {
            if (avatar == null || avatarCamera == null) return -1;
            var ray = avatarCamera.ViewportPointToRay(new Vector3(normalized.x,normalized.y,0));
            float closest = float.MaxValue; int selected = -1;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!renderer.enabled || renderer.sharedMesh == null) continue;
                var baked = new Mesh();
                var temporary = new GameObject("Temporary model picking surface") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    renderer.BakeMesh(baked);
                    temporary.transform.SetParent(renderer.transform,false);
                    var collider = temporary.AddComponent<MeshCollider>(); collider.sharedMesh = baked;
                    if (!collider.Raycast(ray,out var hit,avatarCamera.farClipPlane) || hit.distance >= closest) continue;
                    if (bone)
                    {
                        int offset = hit.triangleIndex * 3;
                        var triangles = baked.triangles; var weights = renderer.sharedMesh.boneWeights;
                        if (offset + 2 >= triangles.Length || weights.Length == 0) continue;
                        int vertex = triangles[offset];
                        if (hit.barycentricCoordinate.y > hit.barycentricCoordinate.x) vertex = triangles[offset + 1];
                        if (hit.barycentricCoordinate.z > Mathf.Max(hit.barycentricCoordinate.x,hit.barycentricCoordinate.y)) vertex = triangles[offset + 2];
                        if (vertex >= weights.Length) continue;
                        var weight = weights[vertex]; int index = weight.boneIndex0; float strength = weight.weight0;
                        if (weight.weight1 > strength) { strength = weight.weight1; index = weight.boneIndex1; }
                        if (weight.weight2 > strength) { strength = weight.weight2; index = weight.boneIndex2; }
                        if (weight.weight3 > strength) index = weight.boneIndex3;
                        if (index >= renderer.bones.Length) continue;
                        selected = Pmx == null ? avatar.GetComponent<SakuraModelTools>()?.GetBoneIndex(renderer.bones[index]) ?? -1 : Array.IndexOf(Pmx.Instance.BoneTransforms,renderer.bones[index]);
                    }
                    else
                    {
                        int triangle = hit.triangleIndex * 3, first = 0;
                        for (int part = 0; part < baked.subMeshCount; part++)
                        {
                            int count = (int)baked.GetIndexCount(part);
                            if (triangle >= first && triangle < first + count) { selected = Pmx == null ? avatar.GetComponent<SakuraModelTools>()?.GetMaterialIndex(renderer,part) ?? -1 : part; break; }
                            first += count;
                        }
                    }
                    closest = hit.distance;
                }
                finally { Destroy(temporary); Destroy(baked); }
            }
            return selected;
        }

        public void SetZoomPercent(float percent)
        {
            if (!float.IsFinite(percent)) return;
            targetZoom = Mathf.Clamp(percent / 100, LoadedFormat == "Live2D" ? .1f : .4f, LoadedFormat == "Live2D" ? 10 : 5);
        }

        public void UnloadModel()
        {
            if (Loading) throw new InvalidOperationException("Wait for the current model import to finish.");
            if (avatar != null) Destroy(avatar);
            avatar = null;
            LoadedModelPath = null;
            LoadedFormat = null;
            modelBounds = null;
        }

        public async Task LoadModel(string source, string relative, CancellationToken cancellation = default)
        {
            if (Loading) { StatusChanged?.Invoke("A character is already loading. Please wait."); return; }
            using var importLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
            var importToken = importLifetime.Token;
            Loading = true;
            LastImportError = null;
            try
            {
                importToken.ThrowIfCancellationRequested();
                string path = ResolveCharacterPath(source, relative);
                if (!File.Exists(path)) throw new FileNotFoundException("The selected model has moved or was removed.");
                string format = path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) ? "VRM" : path.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase) ? "PMX" : "Live2D";
                StatusChanged?.Invoke("Loading " + format + " into Unity…");
                GameObject loaded;
                if (format == "VRM")
                {
                    var instance = await Vrm10.LoadPathAsync(path, canLoadVrm0X: true,
                        materialGenerator: new UrpVrm10MaterialDescriptorGenerator(), ct: importToken);
                    loaded = instance == null ? null : instance.gameObject;
                }
                else if (format == "PMX") loaded = await SakuraModelImport.LoadPmx(path, importToken, text => { ImportDetails = text; StatusChanged?.Invoke(text); });
                else loaded = SakuraModelImport.LoadLive2D(path);
                if (loaded == null) throw new InvalidOperationException("The importer returned no model.");
                try
                {
                    var motion = loaded.AddComponent<SakuraIdleMotion>(); motion.Initialize(path);
                    loaded.AddComponent<SakuraAvatarExpressions>().Initialize(path, motion);
                    if (format != "PMX") SakuraModelTools.GetOrCreate(loaded, path);
                    else loaded.AddComponent<SakuraPmxPhysics>().Initialize(loaded.GetComponent<SakuraPmxModel>());
                    SakuraMaterialExpressions.GetOrCreate(loaded, path);
                    if (format != "Live2D") loaded.AddComponent<SakuraRigMonitor>().Initialize(path);
                    var studio = GetComponent<SakuraStudio>(); motion.Companion = studio == null || studio.CurrentMode == "Companion";
                }
                catch { Destroy(loaded); throw; }
                if (importToken.IsCancellationRequested) { Destroy(loaded); return; }
                var previous = avatar;
                avatar = loaded;
                LoadedFormat = format;
                LoadedModelPath = relative;
                avatarCamera.GetUniversalAdditionalCameraData().SetRenderer(-1);
                avatar.transform.SetParent(transform, false);
                avatar.transform.localPosition = Vector3.zero;
                yaw = format == "Live2D" ? 180 : 0; live2DRoll = 0; zoom = targetZoom = 1; framingOffset = Vector3.zero;
                ApplyRotation();
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
                ModelChanged?.Invoke();
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
            framingBounds = bounds;
            float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x / Mathf.Max(.1f, avatarCamera.aspect));
            float distance = halfHeight / Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.12f / zoom;
            avatarCamera.transform.position = bounds.center + framingOffset + Vector3.forward * distance;
            avatarCamera.transform.LookAt(bounds.center + framingOffset);
        }

        void ApplyRotation()
        {
            if (avatar == null) return;
            avatar.transform.localRotation = LoadedFormat == "Live2D"
                ? Quaternion.AngleAxis(live2DRoll, Vector3.forward) * Quaternion.Euler(0, 180, 0)
                : Quaternion.Euler(0, yaw, 0);
        }
        public void Rotate(float degrees)
        {
            if (avatar == null || !float.IsFinite(degrees)) return;
            if (LoadedFormat == "Live2D") live2DRoll += degrees; else yaw += degrees;
            ApplyRotation();
        }
        public void ApplyLive2DLimits(SakuraLive2DSettings settings)
        {
            if (LoadedFormat != "Live2D" || avatar == null || avatarCamera == null || settings == null || !settings.limitMovement) return;
            float previousTarget = targetZoom, previousZoom = zoom, previousRoll = live2DRoll;
            Vector3 previousOffset = framingOffset;
            targetZoom = Mathf.Clamp(targetZoom, settings.zoomMin / 100, settings.zoomMax / 100);
            zoom = Mathf.Clamp(zoom, settings.zoomMin / 100, settings.zoomMax / 100);
            live2DRoll = Mathf.Clamp(live2DRoll, settings.rotationMin, settings.rotationMax);
            float distance = Mathf.Abs(Vector3.Dot(avatarCamera.transform.position - avatar.transform.position - framingOffset, avatarCamera.transform.forward));
            float height = 2 * distance * Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f);
            Vector3 right = avatarCamera.transform.right, up = avatarCamera.transform.up;
            float horizontal = Vector3.Dot(framingOffset, right), vertical = Vector3.Dot(framingOffset, up);
            framingOffset += right * (Mathf.Clamp(horizontal, -settings.xMax * height * avatarCamera.aspect / 100, -settings.xMin * height * avatarCamera.aspect / 100) - horizontal)
                + up * (Mathf.Clamp(vertical, -settings.yMax * height / 100, -settings.yMin * height / 100) - vertical);
            if (previousTarget != targetZoom || previousZoom != zoom || previousRoll != live2DRoll || previousOffset != framingOffset)
            { ApplyRotation(); FrameAvatar(); }
        }
        public Newtonsoft.Json.Linq.JObject PluginMove(Newtonsoft.Json.Linq.JObject command)
        {
            if (avatar == null || Loading) throw new InvalidOperationException("Load a character first.");
            if (command.Value<bool?>("relativeToCurrentModelPosition") == true)
                throw new NotSupportedException("Relative plugin movement is not supported by this native build.");
            float Number(string key, float fallback)
            {
                if (command[key] == null) return fallback;
                if (command[key].Type != Newtonsoft.Json.Linq.JTokenType.Integer && command[key].Type != Newtonsoft.Json.Linq.JTokenType.Float)
                    throw new ArgumentException("Plugin movement values must be finite numbers.");
                float value = command.Value<float>(key);
                if (!float.IsFinite(value)) throw new ArgumentException("Plugin movement values must be finite numbers.");
                return value;
            }
            if (Number("timeInSeconds", 0) != 0) throw new NotSupportedException("Use timeInSeconds=0 for native plugin movement.");
            var before = PluginPosition();
            float requestedSize = Number("size", targetZoom);
            float rotation = Mathf.Clamp(Number("rotation", LoadedFormat == "Live2D" ? live2DRoll : yaw), -180, 180);
            float x = Mathf.Clamp(Number("positionX", before.Value<float>("positionX")), -2, 2), y = Mathf.Clamp(Number("positionY", before.Value<float>("positionY")), -2, 2);
            SetZoomPercent(requestedSize * 100); zoom = targetZoom;
            if (LoadedFormat == "Live2D") live2DRoll = rotation; else yaw = rotation;
            ApplyRotation(); FrameAvatar();
            float distance = Mathf.Abs(Vector3.Dot(avatarCamera.transform.position - (framingBounds?.center ?? avatar.transform.position) - framingOffset, avatarCamera.transform.forward));
            float height = 2 * distance * Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f);
            framingOffset = -avatarCamera.transform.right * x * height * avatarCamera.aspect / 2 - avatarCamera.transform.up * y * height / 2;
            FrameAvatar();
            if (LoadedFormat == "Live2D") ApplyLive2DLimits(avatar.GetComponent<SakuraModelTools>()?.Live2D);
            return PluginPosition();
        }
        public Newtonsoft.Json.Linq.JObject PluginPosition()
        {
            if (avatar == null || avatarCamera == null) return new Newtonsoft.Json.Linq.JObject();
            float distance = Mathf.Abs(Vector3.Dot(avatarCamera.transform.position - (framingBounds?.center ?? avatar.transform.position) - framingOffset, avatarCamera.transform.forward));
            float height = Mathf.Max(.001f, 2 * distance * Mathf.Tan(avatarCamera.fieldOfView * Mathf.Deg2Rad * .5f));
            return new Newtonsoft.Json.Linq.JObject {
                ["positionX"] = -2 * Vector3.Dot(framingOffset, avatarCamera.transform.right) / (height * Mathf.Max(.1f, avatarCamera.aspect)),
                ["positionY"] = -2 * Vector3.Dot(framingOffset, avatarCamera.transform.up) / height,
                ["rotation"] = LoadedFormat == "Live2D" ? live2DRoll : yaw, ["size"] = targetZoom };
        }
        public void Zoom(float factor) => SetZoomPercent(targetZoom * factor * 100);
        public void ResetFraming() { zoom = targetZoom = 1; framingOffset = Vector3.zero; if (avatar != null) FrameAvatar(); }
        private void Update()
        {
            if (avatar == null || Mathf.Abs(zoom - targetZoom) < .0001f) return;
            zoom = Mathf.Lerp(zoom,targetZoom,1 - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime,.1f) / .075f)); FrameAvatar();
        }

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

        public string CaptureVisionFrame()
        {
            if (avatar == null || target == null || Loading) return null;
            var size = SakuraOutput.Dimensions(target.width, target.height, 768);
            var temporary = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
            try
            {
                Graphics.Blit(target, temporary); RenderTexture.active = temporary;
                pixels.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); pixels.Apply();
                return Convert.ToBase64String(pixels.EncodeToJPG(85));
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(temporary); Destroy(pixels); }
        }

        public Vector2 CursorDirection(Vector2 pointer, Vector3? head)
        {
            if (avatar == null || avatarCamera == null || image == null) return Vector2.zero;
            Vector3 origin = avatar.transform.position;
            if (head.HasValue) origin = head.Value;
            else
            {
                if (framingBounds is Bounds bounds) origin = bounds.center + Vector3.up * bounds.size.y * .3f;
            }
            Vector3 projected = avatarCamera.WorldToViewportPoint(origin);
            Rect rect = image.worldBound;
            var screenOrigin = new Vector2(rect.x + projected.x * rect.width, Screen.height - rect.y - (1 - projected.y) * rect.height);
            return NormalizeCursor(pointer, screenOrigin, rect.size);
        }
        public static Vector2 NormalizeCursor(Vector2 pointer, Vector2 origin, Vector2 viewportSize) => new Vector2(
            Mathf.Clamp((pointer.x - origin.x) / Mathf.Max(1, viewportSize.x * .5f), -1, 1),
            Mathf.Clamp((pointer.y - origin.y) / Mathf.Max(1, viewportSize.y * .5f), -1, 1));

        private void OnDestroy()
        {
            RenderPipelineManager.endCameraRendering -= ApplyModelEffects;
            lifetime?.Cancel(); lifetime?.Dispose();
            if (avatar != null) Destroy(avatar);
            if (target != null) { if (avatarCamera != null) avatarCamera.targetTexture = null; target.Release(); Destroy(target); }
        }

        private void ApplyModelEffects(ScriptableRenderContext context, Camera camera)
        {
            if (camera != avatarCamera || target == null || avatar == null) return;
            var tools = avatar.GetComponent<SakuraModelTools>();
            if (tools != null && tools.HasEffects) tools.ApplyEffects(target, target);
        }
    }
}
