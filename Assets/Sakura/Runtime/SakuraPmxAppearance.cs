using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity
{
    public static class SakuraPmxAppearance
    {
        public static float[] Statistics(Color32[] pixels)
        {
            var histogram = new int[3,256]; int count = 0;
            foreach (var pixel in pixels)
            {
                if (pixel.a == 0 || Math.Max(pixel.r, Math.Max(pixel.g, pixel.b)) < 8) continue;
                count++; histogram[0,pixel.r]++; histogram[1,pixel.g]++; histogram[2,pixel.b]++;
            }
            if (count < 40) throw new InvalidOperationException("Not enough visible model pixels for appearance matching.");
            var values = new List<float>();
            for (int channel = 0; channel < 3; channel++)
            {
                double sum = 0; for (int index = 0; index < 256; index++) sum += index * (double)histogram[channel,index];
                values.Add((float)(sum / count / 255));
                foreach (float quantile in new[] { .1f, .25f, .5f, .75f, .9f })
                {
                    int total = 0, index = 0; while (index < 255 && (total += histogram[channel,index]) < count * quantile) index++;
                    values.Add(index / 255f);
                }
            }
            return values.ToArray();
        }
        public static float Loss(float[] first, float[] second) => first.Zip(second, (a,b) => (a-b)*(a-b)).Average();
        static float[] Reference(string image)
        {
            const string prefix = "data:image/png;base64,";
            if (image == null || !image.StartsWith(prefix, StringComparison.Ordinal) || image.Length > 24000000) throw new InvalidOperationException("Invalid editor image.");
            var texture = SakuraPmxModel.Decode(Convert.FromBase64String(image.Substring(prefix.Length)));
            try { return Statistics(texture.GetPixels32()); } finally { UnityEngine.Object.Destroy(texture); }
        }
        public static async Task<string> Match(SakuraAvatarViewport viewport, string response, Action<string> progress)
        {
            var model = viewport.Pmx; if (model == null) throw new InvalidOperationException("Select a PMX model.");
            if (model.AppearanceMatching) throw new InvalidOperationException("An appearance comparison is already running.");
            var initial = JsonUtility.ToJson(model.Settings); var idle = viewport.Idle; bool owned = idle != null && idle.MotionOwned;
            var data = JObject.Parse(response);
            var targets = new List<(int material, float[] stats)> { (-1, Reference(data.Value<string>("image"))) };
            foreach (var entry in ((JArray)data["parts"] ?? new JArray()).Take(6))
            {
                int index = entry.Value<int>("material");
                if (index < 0 || index >= model.Instance.Materials.Length) continue;
                try { targets.Add((index, Reference(entry.Value<string>("image")))); } catch (InvalidOperationException) { }
            }
            var materials = model.Instance.Materials;
            void Show(int index) { for (int i = 0; i < materials.Length; i++) materials[i].SetFloat("_BodyVisible", index < 0 || i == index ? 1 : 0); }
            async Task<float[]> Measure()
            {
                var values = new List<float>();
                foreach (var target in targets)
                {
                    if (model == null || viewport.Pmx != model) throw new InvalidOperationException("Model changed during appearance matching.");
                    Show(target.material);
                    int frame = Time.frameCount;
                    float deadline = Time.realtimeSinceStartup + 8;
                    while (Time.frameCount <= frame + 1)
                    {
                        if (Time.realtimeSinceStartup > deadline) throw new InvalidOperationException("The renderer stopped advancing during comparison.");
                        await Task.Yield();
                    }
                    try { values.Add(Loss(Statistics(viewport.ReadAvatarPixels(192)), target.stats)); }
                    catch (InvalidOperationException) { values.Add(float.PositiveInfinity); }
                }
                Show(-1); return values.ToArray();
            }
            float[] Read() { var s = model.Settings; return new[] { s.brightness,s.ambient,s.diffuse,s.toon,s.directional,s.sphere,s.specular,s.saturation }; }
            void Apply(float[] values)
            {
                var s = model.Settings; s.brightness=values[0];s.ambient=values[1];s.diffuse=values[2];s.toon=values[3];s.directional=values[4];s.sphere=values[5];s.specular=values[6];s.saturation=values[7]; model.Apply();
            }
            try
            {
                model.AppearanceMatching = true;
                if (idle != null) idle.MotionOwned = true;
                var baseline = await Measure();
                for (int i = targets.Count - 1; i > 0; i--) if (!float.IsFinite(baseline[i])) targets.RemoveAt(i);
                baseline = await Measure();
                if (!float.IsFinite(baseline[0])) throw new InvalidOperationException("The current view contains no measurable avatar.");
                float before = baseline.Average(), bestLoss = before; var start = Read(); var best = (float[])start.Clone(); int evaluated = 0;
                foreach (float step in new[] { .3f, .12f, .05f })
                    for (int key = 0; key < best.Length; key++)
                    {
                        if (key == 4) continue; // Preserve directional shading instead of fitting a pose-dependent shadow.
                        var chosen = (float[])best.Clone(); float chosenLoss = bestLoss;
                        foreach (int sign in new[] { -1, 1 })
                        {
                            var candidate = (float[])best.Clone(); candidate[key] = Mathf.Clamp(candidate[key] + step * sign, 0, key == 2 || key == 3 || key == 4 ? 1 : 2);
                            Apply(candidate); var losses = await Measure(); float loss = losses.Average(); evaluated++;
                            if (loss < chosenLoss && losses.Select((value,index) => value <= baseline[index] + .0001f).All(value => value)) { chosen = candidate; chosenLoss = loss; }
                        }
                        best = chosen; bestLoss = chosenLoss; progress("Matching editor colours… " + evaluated + " measurements");
                    }
                bool improved = bestLoss < before * .995f && before - bestLoss > .00001f;
                Apply(improved ? best : start); model.Save();
                return improved ? "Appearance matched and saved. Measured colour error " + before.ToString("F5") + " → " + bestLoss.ToString("F5") + "." : "Current sliders already give the closest measured match. No changes saved.";
            }
            catch { if (model != null) { JsonUtility.FromJsonOverwrite(initial, model.Settings); model.Apply(); } throw; }
            finally { if (model != null) { Show(-1); model.AppearanceMatching = false; } if (idle != null) idle.MotionOwned = owned; }
        }
    }
}
