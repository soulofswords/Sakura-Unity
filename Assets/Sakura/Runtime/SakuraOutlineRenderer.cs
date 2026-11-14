using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sakura.Unity
{
    public sealed class SakuraOutlineRenderer : MonoBehaviour
    {
        readonly List<(SkinnedMeshRenderer source, SkinnedMeshRenderer outline)> parts = new List<(SkinnedMeshRenderer, SkinnedMeshRenderer)>();
        GameObject root;
        Material material;
        public bool Enabled { get; private set; }

        public void SetEnabled(bool enabled, float width = .0025f, Color? color = null)
        {
            if (enabled && Enabled && material != null)
            {
                material.SetFloat("_Width", width);
                if (color != null) material.SetColor("_Color", color.Value);
                return;
            }
            if (!enabled) { Clear(); return; }
            Clear(); Enabled = true;
            var shader = Resources.Load<Shader>("Sakura/Outline");
            if (shader == null) { Enabled = false; return; }
            material = new Material(shader) { hideFlags = HideFlags.DontSave };
            material.SetFloat("_Width", width);
            material.SetColor("_Color", color ?? Color.black);
            root = new GameObject("Sakura outline");
            root.transform.SetParent(transform, false);
            root.layer = gameObject.layer;
            foreach (var source in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (source == null || source.sharedMesh == null || source.sharedMesh.vertexCount == 0) continue;
                var child = new GameObject("Outline " + source.name);
                child.transform.SetParent(root.transform, false);
                child.layer = source.gameObject.layer;
                var outline = child.AddComponent<SkinnedMeshRenderer>();
                outline.sharedMesh = source.sharedMesh;
                outline.bones = source.bones;
                outline.rootBone = source.rootBone;
                outline.sharedMaterial = material;
                outline.shadowCastingMode = ShadowCastingMode.Off;
                outline.receiveShadows = false;
                outline.updateWhenOffscreen = source.updateWhenOffscreen;
                outline.allowOcclusionWhenDynamic = false;
                int count = source.sharedMesh.blendShapeCount;
                for (int index = 0; index < count; index++) outline.SetBlendShapeWeight(index, source.GetBlendShapeWeight(index));
                parts.Add((source, outline));
            }
            if (parts.Count == 0) Clear();
        }

        void LateUpdate()
        {
            if (!Enabled) return;
            foreach (var (source, outline) in parts)
            {
                if (source == null || outline == null) continue;
                int count = (source.sharedMesh == null ? 0 : source.sharedMesh.blendShapeCount);
                for (int index = 0; index < count; index++) outline.SetBlendShapeWeight(index, source.GetBlendShapeWeight(index));
            }
        }

        void Clear()
        {
            foreach (var (_, outline) in parts) if (outline != null) Destroy(outline.gameObject);
            parts.Clear();
            if (root != null) Destroy(root);
            root = null;
            if (material != null) Destroy(material);
            material = null;
            Enabled = false;
        }

        void OnDestroy() => Clear();
    }
}
