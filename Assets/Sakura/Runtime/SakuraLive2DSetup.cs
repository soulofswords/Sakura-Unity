using System;
using System.IO;
using System.Linq;
using Live2D.Cubism.Framework.Json;
using Live2D.Cubism.Framework.Physics;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity
{
    [Serializable]
    public sealed class SakuraLive2DSettings
    {
        public int physicsFps;
        public bool legacy, whiteOutlineFix, limitMovement;
        public float xMin = -100, xMax = 100, yMin = -100, yMax = 100;
        public float zoomMin = 10, zoomMax = 500, rotationMin = -180, rotationMax = 180;
        public void Validate()
        {
            if (physicsFps != 0 && physicsFps != 30 && physicsFps != 60 && physicsFps != 120) physicsFps = 0;
            static void Clamp(ref float min, ref float max, float low, float high)
            {
                if (float.IsNaN(min) || float.IsInfinity(min) || float.IsNaN(max) || float.IsInfinity(max)) throw new ArgumentException("Live2D movement bounds must be finite.");
                min = Mathf.Clamp(min, low, high); max = Mathf.Clamp(max, min, high);
            }
            Clamp(ref xMin, ref xMax, -200, 200); Clamp(ref yMin, ref yMax, -200, 200);
            Clamp(ref zoomMin, ref zoomMax, 10, 1000); Clamp(ref rotationMin, ref rotationMax, -360, 360);
        }
        public SakuraLive2DSettings Copy() => JsonUtility.FromJson<SakuraLive2DSettings>(JsonUtility.ToJson(this));
    }

    public sealed class SakuraLive2DSetup
    {
        readonly CubismPhysicsRig rig;
        public bool PhysicsAvailable => rig != null;
        public CubismPhysicsRig Rig => rig;
        public string Warning { get; private set; }
        public SakuraLive2DSetup(GameObject model, string modelPath)
        {
            var controller = model.GetComponent<CubismPhysicsController>();
            if (controller == null || !File.Exists(modelPath)) return;
            try
            {
                var manifest = Read(modelPath);
                string reference = manifest["FileReferences"]?.Value<string>("Physics");
                if (string.IsNullOrEmpty(reference)) return;
                string physicsPath = SakuraModelImport.ResolveFile(Path.GetDirectoryName(modelPath), reference);
                var json = Read(physicsPath);
                var settings = json["PhysicsSettings"] as JArray;
                if (settings == null || settings.Count > 500) throw new InvalidDataException("Live2D physics must have at most 500 sub-rigs.");
                foreach (var setting in settings.OfType<JObject>())
                    foreach (string field in new[] { "Input", "Output", "Vertices" })
                        if ((setting[field] as JArray)?.Count > 5000) throw new InvalidDataException("Live2D physics contains an oversized rig.");
                var authored = CubismPhysics3Json.LoadFrom(json.ToString());
                if (authored == null) throw new InvalidDataException("The authored Live2D physics could not be read.");
                var runtimeRig = authored.ToRig();
                // Recreate the imported rig through the SDK API so its public per-rig cadence stays available.
                controller.Initialize(runtimeRig); rig = runtimeRig;
            }
            catch (Exception error) { Warning = "Physics cadence controls are unavailable. " + error.Message; }
        }
        static JObject Read(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Live2D setup metadata is missing or exceeds 8 MB.");
            return JObject.Parse(File.ReadAllText(path));
        }
        public void Apply(SakuraLive2DSettings settings) { if (rig != null) ConfigureRig(rig, settings); }
        public static void ConfigureRig(CubismPhysicsRig rig, SakuraLive2DSettings settings)
        {
            if (rig == null || settings == null) throw new ArgumentNullException("Native physics rig and settings are required.");
            settings.Validate(); rig.Fps = settings.legacy ? 0 : settings.physicsFps;
        }
        public static Color32[] FixAlphaEdges(Color32[] pixels, int width, int height)
        {
            if (pixels == null || width < 1 || height < 1 || width > 4096 || height > 4096 || pixels.Length != (long)width * height) throw new ArgumentException("Use an RGBA image up to 4096 × 4096.");
            var result = (Color32[])pixels.Clone();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x; if (pixels[index].a != 0) continue;
                    int red = 0, green = 0, blue = 0, weight = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy; if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                            var neighbor = pixels[ny * width + nx];
                            red += neighbor.r * neighbor.a; green += neighbor.g * neighbor.a; blue += neighbor.b * neighbor.a; weight += neighbor.a;
                        }
                    if (weight > 0) result[index] = new Color32((byte)(red / weight), (byte)(green / weight), (byte)(blue / weight), 0);
                }
            return result;
        }
    }
}
