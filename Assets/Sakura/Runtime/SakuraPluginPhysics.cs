using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Live2D.Cubism.Framework.Physics;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity
{
    public sealed class SakuraPluginPhysics
    {
        sealed class Override { public float Value, Until; }
        GameObject model;
        CubismPhysicsRig rig;
        string[] ids;
        float[][] angles;
        Vector2[][] translations;
        Vector2 wind;
        string owner, ownerName;
        readonly Dictionary<string, Override> overrides = new Dictionary<string, Override>();
        void Ensure(SakuraAvatarViewport viewport, SakuraBackend backend)
        {
            var current = viewport.AvatarRoot.GetComponent<SakuraModelTools>()?.Live2DPhysicsRig;
            if (current == null) throw new NotSupportedException("Native physics overrides require a Live2D model with authored physics.");
            if (model == viewport.AvatarRoot && rig == current) return;
            Clear();
            string path = SakuraAvatarViewport.ResolveCharacterPath(backend.SourceFolder, viewport.LoadedModelPath);
            JObject Read(string file)
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("Authored physics metadata is missing or oversized.");
                return JObject.Parse(File.ReadAllText(file));
            }
            string reference = Read(path)["FileReferences"]?.Value<string>("Physics");
            if (string.IsNullOrEmpty(reference)) throw new InvalidDataException("The authored physics group reference is missing.");
            var metadata = Read(SakuraModelImport.ResolveFile(Path.GetDirectoryName(path), reference));
            var rows = metadata["PhysicsSettings"] as JArray;
            if (rows == null || rows.Count != current.SubRigs.Length || rows.Count > 500) throw new InvalidDataException("Physics group metadata does not match the native rig.");
            ids = rows.Select(row => row.Value<string>("Id")).ToArray();
            if (ids.Any(string.IsNullOrEmpty) || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Physics group IDs must be unique.");
            model = viewport.AvatarRoot; rig = current; wind = rig.Wind;
            angles = rig.SubRigs.Select(group => group.Output.Select(output => output.AngleScale).ToArray()).ToArray();
            translations = rig.SubRigs.Select(group => group.Output.Select(output => output.TranslationScale).ToArray()).ToArray();
        }
        float Value(string key, float fallback) => overrides.TryGetValue(key, out var entry) ? entry.Value : fallback;
        public void Tick(SakuraAvatarViewport viewport)
        {
            if (model != null && (viewport == null || viewport.AvatarRoot != model)) { Clear(); return; }
            var expired = overrides.Where(entry => entry.Value.Until <= Time.unscaledTime).Select(entry => entry.Key).ToArray();
            foreach (string key in expired) overrides.Remove(key);
            if (expired.Length > 0) Apply(); if (overrides.Count == 0) { owner = ownerName = null; }
        }
        void Apply()
        {
            if (model == null || rig == null) return;
            for (int group = 0; group < rig.SubRigs.Length; group++)
                for (int index = 0; index < rig.SubRigs[group].Output.Length; index++)
                {
                    float factor = Value("strength:", 50) / 50 * Value("strength:" + ids[group], 1);
                    rig.SubRigs[group].Output[index].AngleScale = angles[group][index] * factor;
                    rig.SubRigs[group].Output[index].TranslationScale = translations[group][index] * factor;
                    rig.SubRigs[group].Output[index].InitializeGetter();
                }
            rig.Wind = wind + Vector2.right * (Value("wind:", 0) / 100);
        }
        public JObject Query(SakuraAvatarViewport viewport, SakuraBackend backend)
        {
            Ensure(viewport, backend); Tick(viewport);
            return new JObject { ["baseStrength"] = 50, ["baseWind"] = 0, ["apiPhysicsOverrideActive"] = overrides.Count > 0, ["apiPhysicsOverridePluginName"] = overrides.Count > 0 ? ownerName ?? "" : "", ["physicsGroups"] = new JArray(ids.Select((id, index) => new JObject { ["groupID"] = id, ["groupName"] = rig.SubRigs[index].Name ?? "", ["strengthMultiplier"] = 1, ["windMultiplier"] = 1 })) };
        }
        public JObject Set(SakuraAvatarViewport viewport, SakuraBackend backend, JObject args)
        {
            Ensure(viewport, backend); Tick(viewport);
            string identity = args.Value<string>("_pluginIdentity") ?? "Sakura API local";
            if (owner != null && owner != identity) throw new InvalidOperationException("Physics is controlled by another plugin: " + ownerName);
            var pending = new List<(string key, Override value)>();
            foreach (string kind in new[] { "strength", "wind" })
            {
                JToken token = args[kind + "Overrides"]; if (token == null) continue;
                if (!(token is JArray rows) || rows.Count > 256) throw new ArgumentException("Physics overrides must be arrays with at most 256 entries.");
                foreach (var tokenRow in rows)
                {
                    if (!(tokenRow is JObject row) || row["value"] == null || row["value"].Type != JTokenType.Float && row["value"].Type != JTokenType.Integer) throw new ArgumentException("Physics overrides require a finite numeric value.");
                    if (row["setBaseValue"] != null && row["setBaseValue"].Type != JTokenType.Boolean) throw new ArgumentException("setBaseValue must be a boolean.");
                    bool baseValue = row.Value<bool?>("setBaseValue") == true; string id = baseValue ? "" : row.Value<string>("id");
                    if (!baseValue && !ids.Contains(id)) throw new ArgumentException("Unknown physics group: " + id);
                    if (kind == "wind" && !baseValue) throw new NotSupportedException("Group-specific wind is unavailable in the native Cubism SDK. Base wind overrides are supported.");
                    float value = row.Value<float>("value"), seconds = row.Value<float?>("overrideSeconds") ?? 1;
                    if (!float.IsFinite(value) || !float.IsFinite(seconds)) throw new ArgumentException("Physics override values and durations must be finite.");
                    pending.Add((kind + ":" + id, new Override { Value = Mathf.Clamp(value, 0, baseValue ? 100 : 2), Until = Time.unscaledTime + Mathf.Clamp(seconds, .5f, 5) }));
                }
            }
            foreach (var entry in pending) overrides[entry.key] = entry.value;
            if (pending.Count > 0) { owner = identity; ownerName = args.Value<string>("_pluginName") ?? "Sakura API"; }
            Apply(); return new JObject();
        }
        public void Clear()
        {
            overrides.Clear(); Apply(); model = null; rig = null; ids = null; angles = null; translations = null; owner = ownerName = null;
        }
    }
}
