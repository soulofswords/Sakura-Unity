using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(32000)]
    public sealed class SakuraModelTools : MonoBehaviour, ICubismUpdatable
    {
        [Serializable] public sealed class PartSettings
        {
            public string id, replacement;
            public Color color = Color.white;
            public float strength, transparency;
            public bool hidden;
        }
        [Serializable] public sealed class ParameterSettings
        {
            public string id;
            public bool enabled, autoBlink, autoBreath;
            public float value, smoothing;
            [NonSerialized] internal float smoothed;
            [NonSerialized] internal bool started;
        }
        [Serializable] public sealed class EffectSettings
        {
            public float glow, saturation = 1, contrast = 1, overlayStrength;
            public Color overlayColor = Color.white;
            public bool outline;
            public bool authoredGlow = true;
        }
        public sealed class AppearanceChange
        {
            public int index;
            public bool hasColor, hasOpacity;
            public Color color;
            public float strength, opacity;
            public byte[] textureBytes;
        }
        [Serializable] public sealed class VrmSpringSettings { public float stiffness = 1, gravity = 1, drag = 1; }
        [Serializable] sealed class Profile
        {
            public VrmSpringSettings vrmSprings = new VrmSpringSettings();
            public List<PartSettings> parts = new List<PartSettings>();
            public List<string> frozen = new List<string>();
            public List<ParameterSettings> parameters = new List<ParameterSettings>();
            public EffectSettings effects = new EffectSettings();
            public SakuraLive2DSettings live2D = new SakuraLive2DSettings();
        }
        public sealed class Part
        {
            public string Id, Name;
            public Renderer Renderer;
            public int Slot;
            public CubismRenderer ArtMesh;
            public PartSettings Settings;
            public Material Original, Material;
            public Texture OriginalTexture;
            public Texture2D Replacement, Generated;
            internal Color originalColor;
        }
        public sealed class Bone
        {
            public string Id, Name;
            public Transform Transform;
            internal Vector3 position, scale;
            internal Quaternion rotation;
        }
        readonly List<Part> parts = new List<Part>();
        readonly List<Bone> bones = new List<Bone>();
        Profile profile = new Profile();
        CubismModel cubism;
        SakuraIdleMotion idle;
        UniVRM10.Vrm10Instance vrm;
        List<UniVRM10.Vrm10InstanceSpringBone.Spring> authoredSprings;
        readonly Dictionary<UniVRM10.VRM10SpringBoneJoint, (float stiffness, float gravity, float drag)> vrmSpringBase = new Dictionary<UniVRM10.VRM10SpringBoneJoint, (float, float, float)>();
        bool springsDirty;
        bool physicsEnabled = true;
        public bool PhysicsEnabled => physicsEnabled;
        string storage;
        Material effectMaterial;
        SakuraLive2DSetup liveSetup;
        readonly Dictionary<Texture, Texture2D> alphaFixed = new Dictionary<Texture, Texture2D>();
        public SakuraLive2DSettings Live2D => profile.live2D;
        public bool HasLive2DPhysics => liveSetup != null && liveSetup.PhysicsAvailable;
        public Live2D.Cubism.Framework.Physics.CubismPhysicsRig Live2DPhysicsRig => liveSetup?.Rig;
        public EffectSettings Effects => profile.effects;
        public bool HasEffects => Effects.glow > 0 || Effects.saturation != 1 || Effects.contrast != 1 || Effects.overlayStrength > 0;
        public string Warning { get; private set; }
        public IReadOnlyList<Part> Parts => parts;
        public IReadOnlyList<Bone> Bones => bones;
        public CubismParameter[] Parameters => cubism == null ? Array.Empty<CubismParameter>() : cubism.Parameters;
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 950;
        public bool NeedsUpdateOnEditing => false;

        public static string ProfilePath(string key)
        {
            using (var digest = SHA256.Create())
                return Path.Combine(Application.persistentDataPath, "model-tools", BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(key.Replace('\\', '/').ToLowerInvariant()))).Replace("-", ""), "profile.json");
        }
        public static SakuraModelTools GetOrCreate(GameObject model, string modelKey)
        {
            var tools = model.GetComponent<SakuraModelTools>();
            if (tools == null) { tools = model.AddComponent<SakuraModelTools>(); tools.Initialize(modelKey); }
            return tools;
        }
        public void Initialize(string modelKey)
        {
            if (storage != null) throw new InvalidOperationException("Model tools already initialized.");
            storage = ProfilePath(modelKey); cubism = GetComponent<CubismModel>(); idle = GetComponent<SakuraIdleMotion>();
            vrm = GetComponent<UniVRM10.Vrm10Instance>();
            if (vrm != null)
            {
                authoredSprings = vrm.SpringBone?.Springs?.ToList() ?? new List<UniVRM10.Vrm10InstanceSpringBone.Spring>();
                foreach (var joint in GetComponentsInChildren<UniVRM10.VRM10SpringBoneJoint>(true))
                    vrmSpringBase[joint] = (joint.m_stiffnessForce, joint.m_gravityPower, joint.m_dragForce);
            }
            if (File.Exists(storage))
            {
                try
                {
                    if (new FileInfo(storage).Length > 4 * 1024 * 1024) throw new InvalidDataException("Model tools profile exceeds 4 MB.");
                    profile = JsonUtility.FromJson<Profile>(File.ReadAllText(storage)) ?? new Profile();
                }
                catch (Exception error) { Warning = "Model settings could not be restored. " + error.Message; }
            }
            profile.parts ??= new List<PartSettings>(); profile.frozen ??= new List<string>(); profile.parameters ??= new List<ParameterSettings>();
            profile.effects ??= new EffectSettings();
            profile.vrmSprings ??= new VrmSpringSettings();
            ApplyVrmSprings();
            profile.live2D ??= new SakuraLive2DSettings();
            try { profile.live2D.Validate(); }
            catch (ArgumentException) { profile.live2D = new SakuraLive2DSettings(); Warning = "Invalid saved Live2D limits were reset."; }
            try { ValidateEffects(profile.effects); }
            catch (ArgumentException) { profile.effects = new EffectSettings(); Warning = "Invalid saved effects were reset."; }
            ApplyOutline(profile.effects.outline);
            profile.parts.RemoveAll(value => value == null); profile.parameters.RemoveAll(value => value == null);
            profile.parameters.RemoveAll(value => string.IsNullOrEmpty(value.id) || float.IsNaN(value.value) || float.IsInfinity(value.value) || float.IsNaN(value.smoothing) || float.IsInfinity(value.smoothing));
            foreach (var value in profile.parameters) value.smoothing = Mathf.Clamp(value.smoothing, 0, 100);
            if (cubism != null)
            {
                liveSetup = new SakuraLive2DSetup(gameObject, modelKey); liveSetup.Apply(profile.live2D);
                if (liveSetup.Warning != null) Warning = liveSetup.Warning;
                foreach (var art in GetComponentsInChildren<CubismRenderer>(true))
                {
                    if (art.MainTexture == null) continue;
                    parts.Add(new Part { Id = "art:" + parts.Count, Name = art.name, ArtMesh = art, OriginalTexture = art.MainTexture, originalColor = art.Color });
                }
            }
            else
            {
                foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        var original = materials[slot]; if (original == null) continue;
                        var material = new Material(original); materials[slot] = material;
                        parts.Add(new Part { Id = "mat:" + parts.Count, Name = renderer.name + " · " + original.name, Renderer = renderer, Slot = slot,
                            Original = original, Material = material, OriginalTexture = original.mainTexture, originalColor = original.HasProperty("_BaseColor") ? original.GetColor("_BaseColor") : original.HasProperty("_Color") ? original.color : Color.white });
                    }
                    renderer.sharedMaterials = materials;
                }
                var transforms = new HashSet<Transform>();
                foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    foreach (var bone in renderer.bones) if (bone != null && bone != transform) transforms.Add(bone);
                // Include unweighted spring joints so hair and accessory chains remain selectable.
                foreach (var joint in GetComponentsInChildren<UniVRM10.VRM10SpringBoneJoint>(true)) transforms.Add(joint.transform);
                foreach (var bone in GetComponentsInChildren<Transform>(true).Where(transforms.Contains))
                    bones.Add(new Bone { Id = BonePath(bone), Name = bone.name, Transform = bone, position = bone.localPosition, rotation = bone.localRotation, scale = bone.localScale });
            }
            var restoredImages = new Dictionary<string, Texture2D>();
            foreach (var part in parts)
            {
                part.Settings = profile.parts.FirstOrDefault(value => value.id == part.Id) ?? new PartSettings { id = part.Id };
                if (!profile.parts.Contains(part.Settings)) profile.parts.Add(part.Settings);
                try
                {
                    Finite(part.Settings.strength); Finite(part.Settings.transparency); CheckColor(part.Settings.color);
                    part.Settings.strength = Mathf.Clamp01(part.Settings.strength); part.Settings.transparency = Mathf.Clamp(part.Settings.transparency, -1, 1);
                    if (!string.IsNullOrEmpty(part.Settings.replacement))
                    {
                        string filename = Path.GetFileName(part.Settings.replacement);
                        if (filename != part.Settings.replacement || !filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid saved replacement path.");
                        string file = Path.Combine(Path.GetDirectoryName(storage), filename);
                        if (File.Exists(file))
                        {
                            if (!restoredImages.TryGetValue(file, out var texture))
                            {
                                if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Saved image exceeds 16 MB.");
                                texture = DecodeImage(File.ReadAllBytes(file)); restoredImages.Add(file, texture);
                            }
                            part.Replacement = texture;
                        }
                    }
                    ApplyPart(part);
                }
                catch (Exception error)
                {
                    Warning = "Some appearance settings could not be restored. " + error.Message;
                    part.Settings.color = Color.white; part.Settings.strength = part.Settings.transparency = 0;
                    ApplyPart(part);
                }
            }
            var update = GetComponent<CubismUpdateController>(); HasUpdateController = update != null; if (update != null) update.Refresh();
            ApplyFrozenBones(); if (profile.frozen.Count > 0) RebuildSprings();
        }
        string BonePath(Transform bone)
        {
            var path = new List<string>();
            for (var current = bone; current != null && current != transform; current = current.parent) path.Add(current.GetSiblingIndex() + "|" + current.name);
            path.Reverse(); return string.Join("/", path);
        }
        public int GetMaterialIndex(Renderer renderer, int slot) => parts.FindIndex(part => part.Renderer == renderer && part.Slot == slot);
        public int GetBoneIndex(Transform bone) => bones.FindIndex(entry => entry.Transform == bone);
        public bool IsFrozen(int index) => profile.frozen.Contains(bones[index].Id);
        public void Freeze(int index, bool frozen)
        {
            string id = bones[index].Id;
            if (frozen) { if (!profile.frozen.Contains(id)) profile.frozen.Add(id); } else profile.frozen.Remove(id);
            if (!frozen) GetComponent<SakuraRigMonitor>()?.Forget(id);
            ApplyFrozenBones(); RebuildSprings(); Save();
        }
        public void RestoreBones() { profile.frozen.Clear(); GetComponent<SakuraRigMonitor>()?.Forget(); RebuildSprings(); Save(); }
        public void SetPhysicsEnabled(bool enabled)
        {
            if (physicsEnabled == enabled) return;
            physicsEnabled = enabled; RebuildSprings();
        }
        void RebuildSprings()
        {
            if (vrm == null || vrm.SpringBone == null || authoredSprings == null) return;
            if (!physicsEnabled) { vrm.SpringBone.Springs = new List<UniVRM10.Vrm10InstanceSpringBone.Spring>(); springsDirty = true; return; }
            var frozen = new HashSet<Transform>(bones.Where(bone => profile.frozen.Contains(bone.Id)).Select(bone => bone.Transform));
            var springs = new List<UniVRM10.Vrm10InstanceSpringBone.Spring>();
            foreach (var original in authoredSprings)
            {
                var chain = new List<UniVRM10.VRM10SpringBoneJoint>();
                void AddChain()
                {
                    if (chain.Count > 1) springs.Add(new UniVRM10.Vrm10InstanceSpringBone.Spring(original.Name) { Center = original.Center, ColliderGroups = original.ColliderGroups.ToList(), Joints = chain.ToList() });
                    chain.Clear();
                }
                foreach (var joint in original.Joints)
                {
                    if (joint == null) continue;
                    if (frozen.Contains(joint.transform))
                    {
                        // A final tail defines the preceding joint's length and receives no simulated rotation.
                        if (chain.Count > 0) { chain.Add(joint); AddChain(); }
                    }
                    else chain.Add(joint);
                }
                AddChain();
            }
            if (vrm.SpringBone != null) vrm.SpringBone.Springs = frozen.Count == 0 ? authoredSprings.ToList() : springs;
            springsDirty = true;
        }
        public void ApplyFrozenBones()
        {
            foreach (var bone in bones)
                if (bone.Transform != null && profile.frozen.Contains(bone.Id))
                { bone.Transform.localPosition = bone.position; bone.Transform.localRotation = bone.rotation; bone.Transform.localScale = bone.scale; }
        }
        static void Finite(float value) { if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Use a finite value."); }
        static void CheckColor(Color value)
        {
            Finite(value.r); Finite(value.g); Finite(value.b); Finite(value.a);
            if (value.r < 0 || value.r > 1 || value.g < 0 || value.g > 1 || value.b < 0 || value.b > 1 || value.a < 0 || value.a > 1) throw new ArgumentException("Color channels must be between 0 and 1.");
        }
        public void SetColor(int index, Color color, float strength)
        {
            CheckColor(color); Finite(strength); var part = parts[index];
            Color previousColor = part.Settings.color; float previousStrength = part.Settings.strength;
            try { part.Settings.color = color; part.Settings.strength = Mathf.Clamp01(strength); ApplyPart(part); Save(); }
            catch { part.Settings.color = previousColor; part.Settings.strength = previousStrength; ApplyPart(part); throw; }
        }
        public void RestoreColor(int index) => SetColor(index, Color.white, 0);
        public int TintArtMeshes(IEnumerable<string> names, Color color)
        {
            CheckColor(color); var selected = new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.Ordinal); int painted = 0;
            foreach (var part in parts) if (part.ArtMesh != null && (selected.Count == 0 || selected.Contains(part.Name) || selected.Contains(part.Id)))
                { var tint = part.originalColor * color; tint.a = part.originalColor.a; part.ArtMesh.Color = tint; painted++; }
            return painted;
        }
        public bool HasVrmSprings => vrm != null && vrmSpringBase.Count > 0;
        public VrmSpringSettings VrmSprings => profile.vrmSprings;
        public void SetVrmSprings(float stiffness, float gravity, float drag)
        {
            Finite(stiffness); Finite(gravity); Finite(drag);
            profile.vrmSprings = new VrmSpringSettings { stiffness = Mathf.Clamp(stiffness, 0, 2), gravity = Mathf.Clamp(gravity, 0, 2), drag = Mathf.Clamp(drag, 0, 2) };
            ApplyVrmSprings(); Save();
        }
        void ApplyVrmSprings()
        {
            if (vrm == null) return;
            var settings = profile.vrmSprings ?? new VrmSpringSettings();
            foreach (var pair in vrmSpringBase)
            {
                var joint = pair.Key; if (joint == null) continue;
                joint.m_stiffnessForce = pair.Value.stiffness * settings.stiffness;
                joint.m_gravityPower = pair.Value.gravity * settings.gravity;
                joint.m_dragForce = Mathf.Clamp01(pair.Value.drag * settings.drag);
            }
        }
        public void SetPartVisible(int index, bool visible)
        {
            if (index < 0 || index >= parts.Count) throw new ArgumentException("Choose a model part.");
            var part = parts[index]; part.Settings.hidden = !visible; ApplyPart(part); Save();
        }
        public void SetTransparency(int index, float transparency)
        {
            Finite(transparency); var part = parts[index]; float previous = part.Settings.transparency;
            try { part.Settings.transparency = Mathf.Clamp(transparency, -1, 1); ApplyPart(part); Save(); }
            catch { part.Settings.transparency = previous; ApplyPart(part); throw; }
        }
        public int ImportAppearance(IReadOnlyList<AppearanceChange> changes)
        {
            if (changes == null || changes.Count == 0) return 0;
            if (changes.Count > 512) throw new InvalidDataException("Appearance package has too many entries.");
            var staged = new List<(AppearanceChange change, Part part, Texture2D texture, string filename, string file)>();
            var seen = new HashSet<int>();
            try
            {
                foreach (var change in changes)
                {
                    if (change == null || change.index < 0 || change.index >= parts.Count || !seen.Add(change.index))
                        throw new InvalidDataException("Appearance package contains an invalid or duplicate part mapping.");
                    if (change.hasColor) { CheckColor(change.color); Finite(change.strength); }
                    if (change.hasOpacity) Finite(change.opacity);
                    Texture2D texture = change.textureBytes == null ? null : DecodeImage(change.textureBytes);
                    string filename = texture == null ? null : "replacement-" + Guid.NewGuid().ToString("N") + ".png";
                    staged.Add((change, parts[change.index], texture, filename, texture == null ? null : Path.Combine(Path.GetDirectoryName(storage), filename)));
                }
            }
            catch
            {
                foreach (var entry in staged) if (entry.texture != null) Destroy(entry.texture);
                throw;
            }

            var previous = staged.Select(entry => (entry.part, color: entry.part.Settings.color, strength: entry.part.Settings.strength,
                transparency: entry.part.Settings.transparency, replacement: entry.part.Replacement, replacementName: entry.part.Settings.replacement)).ToArray();
            var oldNames = previous.Select(entry => entry.replacementName).Distinct().ToArray();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storage));
                foreach (var entry in staged) if (entry.texture != null) WriteFile(entry.file, entry.change.textureBytes);
                foreach (var entry in staged)
                {
                    if (entry.change.hasColor) { entry.part.Settings.color = entry.change.color; entry.part.Settings.strength = Mathf.Clamp01(entry.change.strength); }
                    if (entry.change.hasOpacity) entry.part.Settings.transparency = Mathf.Clamp(1 - entry.change.opacity, -1, 1);
                    if (entry.texture != null) { entry.part.Replacement = entry.texture; entry.part.Settings.replacement = entry.filename; }
                    ApplyPart(entry.part);
                }
                Save();
            }
            catch
            {
                foreach (var entry in previous)
                {
                    entry.part.Settings.color = entry.color; entry.part.Settings.strength = entry.strength;
                    entry.part.Settings.transparency = entry.transparency; entry.part.Replacement = entry.replacement;
                    entry.part.Settings.replacement = entry.replacementName;
                    try { ApplyPart(entry.part); } catch { }
                }
                foreach (var entry in staged)
                {
                    if (entry.texture != null && !parts.Any(part => part.Replacement == entry.texture)) Destroy(entry.texture);
                    if (entry.file != null && File.Exists(entry.file)) File.Delete(entry.file);
                }
                ReleaseUnusedAlphaTextures();
                throw;
            }
            foreach (var old in previous.Select(entry => entry.replacement).Distinct())
                if (old != null && !parts.Any(part => part.Replacement == old)) Destroy(old);
            foreach (string oldName in oldNames) DeleteReplacement(oldName);
            ReleaseUnusedAlphaTextures();
            return staged.Count;
        }
        public static Texture2D DecodeImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Replacement images must be under 16 MB.");
            // The native chooser normalizes supported source formats to PNG before returning bytes.
            if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71) throw new InvalidDataException("Choose a PNG image through the native file picker.");
            long width = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
            long height = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
            if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new InvalidDataException("Replacement images must be at most 4096 × 4096.");
            return SakuraPmxModel.Decode(bytes);
        }
        public void ReplaceTexture(int index, byte[] bytes)
            => ReplaceTextureGroup(new[] { index }, bytes);
        public void ReplaceTextureGroup(IEnumerable<int> indices, byte[] bytes)
        {
            var targets = indices.Distinct().Select(index => parts[index]).ToArray();
            if (targets.Length == 0) throw new ArgumentException("Choose a texture slot.");
            var replacement = DecodeImage(bytes);
            string name = "replacement-" + Guid.NewGuid().ToString("N") + ".png";
            string file = Path.Combine(Path.GetDirectoryName(storage), name);
            Directory.CreateDirectory(Path.GetDirectoryName(storage));
            var previous = targets.Select(part => (part, texture: part.Replacement, name: part.Settings.replacement)).ToArray();
            try
            {
                WriteFile(file, bytes);
                foreach (var part in targets) { part.Replacement = replacement; part.Settings.replacement = name; ApplyPart(part); }
                Save();
            }
            catch
            {
                foreach (var entry in previous) { entry.part.Replacement = entry.texture; entry.part.Settings.replacement = entry.name; ApplyPart(entry.part); }
                Destroy(replacement); if (File.Exists(file)) File.Delete(file); ReleaseUnusedAlphaTextures(); throw;
            }
            foreach (var texture in previous.Select(entry => entry.texture).Distinct()) if (texture != null && !parts.Any(part => part.Replacement == texture)) Destroy(texture);
            foreach (string filename in previous.Select(entry => entry.name).Distinct()) DeleteReplacement(filename);
            ReleaseUnusedAlphaTextures();
        }
        public void RestoreTexture(int index) => RestoreTextureGroup(new[] { index });
        public void RestoreTextureGroup(IEnumerable<int> indices)
        {
            var previous = indices.Distinct().Select(index => parts[index]).Select(part => (part, texture: part.Replacement, name: part.Settings.replacement)).ToArray();
            try { foreach (var entry in previous) { entry.part.Replacement = null; entry.part.Settings.replacement = null; ApplyPart(entry.part); } Save(); }
            catch { foreach (var entry in previous) { entry.part.Replacement = entry.texture; entry.part.Settings.replacement = entry.name; ApplyPart(entry.part); } throw; }
            foreach (var texture in previous.Select(entry => entry.texture).Distinct()) if (texture != null && !parts.Any(part => part.Replacement == texture)) Destroy(texture);
            foreach (string filename in previous.Select(entry => entry.name).Distinct()) DeleteReplacement(filename);
            ReleaseUnusedAlphaTextures();
        }
        void DeleteReplacement(string name)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || profile.parts.Any(part => part.replacement == name)) return;
            string file = Path.Combine(Path.GetDirectoryName(storage), name); if (File.Exists(file)) File.Delete(file);
        }
        static void WriteFile(string file, byte[] bytes)
        {
            string temporary = file + ".tmp"; File.WriteAllBytes(temporary, bytes);
            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storage)); WriteFile(storage, Encoding.UTF8.GetBytes(JsonUtility.ToJson(profile, true)));
        }
        public void SetLive2DSettings(SakuraLive2DSettings settings)
        {
            if (cubism == null) throw new InvalidOperationException("Load a Live2D model first.");
            settings.Validate(); var previous = profile.live2D; profile.live2D = settings.Copy();
            bool appearanceChanged = previous.whiteOutlineFix != profile.live2D.whiteOutlineFix;
            try
            {
                liveSetup?.Apply(profile.live2D);
                if (appearanceChanged) foreach (var part in parts) ApplyPart(part);
                Save(); ReleaseUnusedAlphaTextures();
            }
            catch
            {
                profile.live2D = previous; liveSetup?.Apply(previous);
                if (appearanceChanged) foreach (var part in parts) ApplyPart(part);
                ReleaseUnusedAlphaTextures(); throw;
            }
        }
        public int AutoSetupLive2D()
        {
            if (cubism == null) throw new InvalidOperationException("Load a Live2D model first.");
            int count = 0;
            foreach (var parameter in Parameters)
            {
                bool blink = parameter.Id == "ParamEyeLOpen" || parameter.Id == "ParamEyeROpen" || parameter.GetComponent<global::Live2D.Cubism.Framework.CubismEyeBlinkParameter>() != null;
                bool breath = parameter.Id.IndexOf("breath", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!blink && !breath) continue;
                var settings = GetParameter(parameter.Id); if (blink) settings.autoBlink = true; if (breath) settings.autoBreath = true; count++;
            }
            Save(); return count;
        }
        Texture SourceTexture(Part part)
        {
            Texture source = part.Replacement != null ? part.Replacement : part.OriginalTexture;
            if (part.ArtMesh == null || !Live2D.whiteOutlineFix || source == null) return source;
            if (source.width > 4096 || source.height > 4096) { Warning = "White-outline correction skipped an atlas above 4096 × 4096. Use a smaller replacement image to correct its edges."; return source; }
            if (!alphaFixed.TryGetValue(source, out var fixedTexture))
            {
                fixedTexture = ReadTexture(source);
                try
                {
                    fixedTexture.SetPixels32(SakuraLive2DSetup.FixAlphaEdges(fixedTexture.GetPixels32(), fixedTexture.width, fixedTexture.height)); fixedTexture.Apply();
                    fixedTexture.wrapMode = source.wrapMode; fixedTexture.filterMode = source.filterMode;
                    alphaFixed.Add(source, fixedTexture);
                }
                catch { Destroy(fixedTexture); throw; }
            }
            return fixedTexture;
        }
        void ReleaseUnusedAlphaTextures()
        {
            var sources = new HashSet<Texture>(parts.Select(part => (Texture)(part.Replacement != null ? part.Replacement : part.OriginalTexture)));
            foreach (var entry in alphaFixed.ToArray())
                if (!Live2D.whiteOutlineFix || !sources.Contains(entry.Key)) { Destroy(entry.Value); alphaFixed.Remove(entry.Key); }
        }
        static void ValidateEffects(EffectSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Finite(settings.glow); Finite(settings.saturation); Finite(settings.contrast); Finite(settings.overlayStrength); CheckColor(settings.overlayColor);
            settings.glow = Mathf.Clamp01(settings.glow); settings.saturation = Mathf.Clamp(settings.saturation, 0, 2);
            settings.contrast = Mathf.Clamp(settings.contrast, 0, 2); settings.overlayStrength = Mathf.Clamp01(settings.overlayStrength);
        }
        public void SetEffects(EffectSettings settings)
        {
            ValidateEffects(settings);
            var previous = profile.effects;
            profile.effects = new EffectSettings { glow = settings.glow, saturation = settings.saturation, contrast = settings.contrast, overlayColor = settings.overlayColor, overlayStrength = settings.overlayStrength, outline = settings.outline, authoredGlow = settings.authoredGlow };
            try { Save(); } catch { profile.effects = previous; throw; }
            ApplyOutline(profile.effects.outline);
            ApplyAuthoredGlow();
        }
        SakuraOutlineRenderer outlineRenderer;
        void ApplyOutline(bool enabled)
        {
            if (!enabled && outlineRenderer == null) return;
            outlineRenderer = outlineRenderer != null ? outlineRenderer : GetComponent<SakuraOutlineRenderer>() ?? gameObject.AddComponent<SakuraOutlineRenderer>();
            outlineRenderer.SetEnabled(enabled);
        }
        static bool IsAdditive(Part part)
        {
            var drawable = part.ArtMesh != null ? part.ArtMesh.Drawable : null;
            return drawable != null && drawable.BlendAdditive;
        }
        void ApplyAuthoredGlow()
        {
            foreach (var part in parts) if (IsAdditive(part)) ApplyPart(part);
        }
        public void ApplyEffects(RenderTexture source, RenderTexture destination)
        {
            if (source == null || destination == null) throw new ArgumentNullException("Model effects require source and destination textures.");
            if (!HasEffects)
            {
                var active = RenderTexture.active;
                try { if (source != destination) Graphics.Blit(source, destination); }
                finally { RenderTexture.active = active; }
                return;
            }
            if (effectMaterial == null)
            {
                var shader = Resources.Load<Shader>("Sakura/ModelEffects");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("The native model effects shader is unavailable on this renderer.");
                effectMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            effectMaterial.SetVector("_Effects", new Vector4(Effects.glow, Effects.saturation, Effects.contrast, Effects.overlayStrength));
            effectMaterial.SetColor("_Overlay", Effects.overlayColor);
            var previous = RenderTexture.active;
            RenderTexture temporary = null;
            try
            {
                if (source == destination)
                {
                    var descriptor = source.descriptor; descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1;
                    temporary = RenderTexture.GetTemporary(descriptor);
                    Graphics.Blit(source, temporary, effectMaterial); Graphics.Blit(temporary, destination);
                }
                else Graphics.Blit(source, destination, effectMaterial);
            }
            finally { RenderTexture.active = previous; if (temporary != null) RenderTexture.ReleaseTemporary(temporary); }
        }
        static Texture2D ReadTexture(Texture source)
        {
            if (source.width > 4096 || source.height > 4096) throw new InvalidDataException("Use a replacement image up to 4096 × 4096 for coloring.");
            var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active; var result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true, false);
            try { Graphics.Blit(source, target); RenderTexture.active = target; result.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); result.Apply(); return result; }
            catch { Destroy(result); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }
        void ApplyPart(Part part)
        {
            var settings = part.Settings; var source = SourceTexture(part);
            if (part.Renderer != null) part.Renderer.enabled = !settings.hidden;
            float opacity = 1 - settings.transparency; Texture2D generated = null;
            if (source != null && ((part.ArtMesh == null && settings.strength > 0) || opacity > 1))
            {
                generated = ReadTexture(source); var pixels = generated.GetPixels32();
                double sum = 0; int count = 0;
                foreach (var pixel in pixels) if (pixel.a > 0) { sum += (.2126 * pixel.r + .7152 * pixel.g + .0722 * pixel.b) / 255; count++; }
                float reference = Mathf.Max(.12f, (float)(sum / Math.Max(1, count)));
                for (int i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i]; if (pixel.a == 0) continue;
                    if (part.ArtMesh == null && settings.strength > 0)
                    {
                        float light = (.2126f * pixel.r + .7152f * pixel.g + .0722f * pixel.b) / 255 / reference;
                        pixel.r = (byte)Mathf.RoundToInt(Mathf.Lerp(pixel.r, Mathf.Min(255, 255 * settings.color.r * light), settings.strength));
                        pixel.g = (byte)Mathf.RoundToInt(Mathf.Lerp(pixel.g, Mathf.Min(255, 255 * settings.color.g * light), settings.strength));
                        pixel.b = (byte)Mathf.RoundToInt(Mathf.Lerp(pixel.b, Mathf.Min(255, 255 * settings.color.b * light), settings.strength));
                    }
                    if (opacity > 1) pixel.a = (byte)Mathf.Min(255, Mathf.RoundToInt(pixel.a * opacity));
                    pixels[i] = pixel;
                }
                generated.SetPixels32(pixels); generated.Apply(); generated.wrapMode = source.wrapMode; generated.filterMode = source.filterMode;
            }
            var old = part.Generated; part.Generated = generated;
            if (part.ArtMesh != null)
            {
                part.ArtMesh.MainTexture = (Texture2D)(generated != null ? generated : source);
                var color = part.originalColor * Color.Lerp(Color.white, settings.color, settings.strength);
                color.a = part.originalColor.a * Mathf.Min(1, opacity);
                if (!profile.effects.authoredGlow && IsAdditive(part)) color.a = 0;
                part.ArtMesh.Color = color;
            }
            else ApplyMaterial(part);
            if (old != null) Destroy(old);
        }
        static void ApplyMaterial(Part part)
        {
            var material = part.Material; material.CopyPropertiesFromMaterial(part.Original);
            var settings = part.Settings; float opacity = 1 - settings.transparency;
            var source = part.Replacement != null ? part.Replacement : part.OriginalTexture;
            // VRM expression bindings continue writing their imported runtime materials.
            var authored = part.Original.HasProperty("_BaseColor") ? part.Original.GetColor("_BaseColor") : part.Original.HasProperty("_Color") ? part.Original.color : part.originalColor;
            var color = source == null ? Color.Lerp(authored, settings.color, settings.strength) : Color.Lerp(authored, Color.white, settings.strength);
            color.a = opacity > 1 ? Mathf.Lerp(authored.a, 1, opacity - 1) : authored.a * opacity;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.mainTexture = part.Generated != null ? part.Generated : source;
            if (color.a < .999f) MakeTransparent(material);
        }
        static void MakeTransparent(Material material)
        {
            material.SetOverrideTag("RenderType", "Transparent"); material.renderQueue = (int)RenderQueue.Transparent;
            foreach (string property in new[] { "_M_SrcBlend", "_SrcBlend" }) if (material.HasProperty(property)) material.SetFloat(property, (float)BlendMode.SrcAlpha);
            foreach (string property in new[] { "_M_DstBlend", "_DstBlend" }) if (material.HasProperty(property)) material.SetFloat(property, (float)BlendMode.OneMinusSrcAlpha);
            foreach (string property in new[] { "_M_ZWrite", "_ZWrite", "_M_AlphaToMask" }) if (material.HasProperty(property)) material.SetFloat(property, 0);
            if (material.HasProperty("_AlphaMode")) material.SetFloat("_AlphaMode", 2);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        public ParameterSettings GetParameter(string id)
        {
            var parameter = Parameters.FirstOrDefault(value => value.Id == id) ?? throw new ArgumentException("Parameter no longer exists.");
            var settings = profile.parameters.FirstOrDefault(value => value.id == id);
            if (settings == null) { settings = new ParameterSettings { id = id, value = parameter.DefaultValue }; profile.parameters.Add(settings); }
            return settings;
        }
        public void SaveParameter(string id)
        {
            var parameter = Parameters.First(value => value.Id == id); var settings = GetParameter(id);
            Finite(settings.value); Finite(settings.smoothing); settings.value = Mathf.Clamp(settings.value, parameter.MinimumValue, parameter.MaximumValue); settings.smoothing = Mathf.Clamp(settings.smoothing, 0, 100); Save();
        }
        public void OnLateUpdate()
        {
            if (!isActiveAndEnabled || cubism == null) return;
            foreach (var settings in profile.parameters)
            {
                var parameter = Parameters.FirstOrDefault(value => value.Id == settings.id); if (parameter == null) continue;
                float target = parameter.Value;
                if (settings.autoBreath && idle != null && idle.Breathing) target = Mathf.Lerp(parameter.DefaultValue, parameter.MaximumValue, Mathf.Clamp01(idle.BreathValue));
                if (settings.autoBlink && idle != null && idle.Blinking) target = Mathf.Lerp(parameter.MaximumValue, parameter.MinimumValue, idle.BlinkValue);
                if (settings.enabled) target = settings.value;
                if (!settings.started) { settings.smoothed = target; settings.started = true; }
                settings.smoothed = Mathf.Lerp(settings.smoothed, target, settings.smoothing <= 0 ? 1 : 1 - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, .1f) / (settings.smoothing / 100)));
                parameter.Value = Mathf.Clamp(settings.smoothed, parameter.MinimumValue, parameter.MaximumValue);
            }
        }
        void LateUpdate()
        {
            ApplyFrozenBones();
            if (cubism != null && Live2D.limitMovement)
            {
                var viewport = GetComponentInParent<SakuraAvatarViewport>();
                if (viewport != null) viewport.ApplyLive2DLimits(Live2D);
            }
            if (springsDirty && vrm != null && vrm.Runtime.SpringBone != null) springsDirty = !vrm.Runtime.SpringBone.ReconstructSpringBone();
            foreach (var part in parts) if (part.Material != null && part.Original != null) ApplyMaterial(part);
            if (cubism != null && !profile.effects.authoredGlow)
                foreach (var part in parts) if (IsAdditive(part) && part.ArtMesh.Color.a != 0) { var hidden = part.ArtMesh.Color; hidden.a = 0; part.ArtMesh.Color = hidden; }
            if (cubism != null && !HasUpdateController) OnLateUpdate();
        }
        void OnDestroy()
        {
            if (effectMaterial != null) Destroy(effectMaterial);
            foreach (var texture in alphaFixed.Values) if (texture != null) Destroy(texture);
            alphaFixed.Clear();
            foreach (var group in parts.Where(part => part.Renderer != null).GroupBy(part => part.Renderer))
            {
                var materials = group.Key.sharedMaterials;
                foreach (var part in group) if (part.Slot < materials.Length && materials[part.Slot] == part.Material) materials[part.Slot] = part.Original;
                group.Key.sharedMaterials = materials;
            }
            foreach (var part in parts)
            {
                if (part.ArtMesh != null) { part.ArtMesh.MainTexture = part.OriginalTexture as Texture2D; part.ArtMesh.Color = part.originalColor; }
                if (part.Material != null) Destroy(part.Material); if (part.Generated != null) Destroy(part.Generated);
            }
            foreach (var texture in parts.Select(part => part.Replacement).Distinct()) if (texture != null) Destroy(texture);
        }
    }
}
