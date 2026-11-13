using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraMaterialExpressionChecks
    {
        public static void Validate()
        {
            var preset = new SakuraMaterialExpressions.Preset { id = Guid.NewGuid().ToString("N"), name = "Tint", part = "pmx:0:Hair", channel = "tint", value = new SakuraMaterialExpressions.Value() };
            if (!SakuraMaterialExpressions.ValidPreset(preset) || SakuraMaterialExpressions.MaximumPresets != 32 || SakuraMaterialExpressions.MaximumParts != 512) throw new Exception("Material preset defaults or bounds differ.");
            preset.value.transparency = -1; if (!SakuraMaterialExpressions.ValidPreset(preset)) throw new Exception("Negative transparency strengthening was rejected.");
            preset.value.transparency = -1.01f; if (SakuraMaterialExpressions.ValidPreset(preset)) throw new Exception("Out of range transparency was accepted.");
            preset.value.transparency = 0; preset.value.strength = float.NaN; if (SakuraMaterialExpressions.ValidPreset(preset)) throw new Exception("Material presets accepted nonfinite strength.");
            preset.value.strength = 0; preset.active = true; if (SakuraMaterialExpressions.ValidPreset(preset)) throw new Exception("An active material preset lacks restore values.");
            preset.before = new SakuraMaterialExpressions.Value(); if (!SakuraMaterialExpressions.ValidPreset(preset)) throw new Exception("Valid material restore values were rejected.");
            preset.channel = "texture"; if (SakuraMaterialExpressions.ValidPreset(preset)) throw new Exception("Material presets accepted texture replacement.");
            Debug.Log("PASS: material preset bounds, finite appearance values, independent channels and active restore-state validation.");
        }
        public static void VerifyLoadedModel()
        {
            Validate();
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            if (viewport?.AvatarRoot == null || viewport.Loading) throw new InvalidOperationException("Load a character before checking material presets.");
            var component = SakuraMaterialExpressions.GetOrCreate(viewport.AvatarRoot, SakuraAvatarViewport.ResolveCharacterPath(viewport.GetComponent<SakuraBackend>().SourceFolder, viewport.LoadedModelPath));
            if (component.Presets.Count > SakuraMaterialExpressions.MaximumPresets - 2) throw new InvalidOperationException("Leave two preset slots free for verification.");
            if (component.Presets.Any(preset => preset.active)) throw new InvalidOperationException("Turn off material expression presets before verification so their restore state is preserved.");
            string path = (string)typeof(SakuraMaterialExpressions).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
            byte[] saved = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var pmx = viewport.Pmx; var tools = viewport.AvatarRoot.GetComponent<SakuraModelTools>();
            Color color; float strength, transparency; bool colored;
            if (pmx != null) { var part = pmx.GetPart(0); color = part.color; strength = part.strength; transparency = part.transparency; colored = part.colored; }
            else { var part = tools.Parts[0].Settings; color = part.color; strength = part.strength; transparency = part.transparency; colored = strength > 0; }
            void Set(Color tint, float amount, float alpha)
            {
                if (pmx != null) { var part = pmx.GetPart(0); part.color = tint; part.strength = amount; part.colored = amount > 0; part.transparency = alpha; pmx.Apply(); pmx.Save(); }
                else { tools.SetColor(0, tint, amount); tools.SetTransparency(0, alpha); }
            }
            string tintId = null, opacityId = null;
            try
            {
                Set(Color.magenta, .4f, .6f); tintId = component.Capture("Generated tint verification", 0, "tint"); opacityId = component.Capture("Generated opacity verification", 0, "opacity");
                Set(Color.cyan, .2f, .1f); component.SetActive(tintId, true);
                float currentAlpha = pmx != null ? pmx.GetPart(0).transparency : tools.Parts[0].Settings.transparency;
                if (currentAlpha != .1f) throw new Exception("A tint preset changed transparency.");
                component.SetActive(opacityId, true); component.SetActive(tintId, false);
                Color restored = pmx != null ? pmx.GetPart(0).color : tools.Parts[0].Settings.color;
                currentAlpha = pmx != null ? pmx.GetPart(0).transparency : tools.Parts[0].Settings.transparency;
                if (restored != Color.cyan || currentAlpha != .6f) throw new Exception("Tint restoration overwrote the independent opacity preset.");
                component.SetActive(opacityId, false);
                currentAlpha = pmx != null ? pmx.GetPart(0).transparency : tools.Parts[0].Settings.transparency;
                if (currentAlpha != .1f) throw new Exception("Opacity preset did not restore the previous value.");
                Debug.Log("PASS: real model material capture/apply, independent tint/opacity and exact prior-value restoration. Original appearance and preset profile restored.");
            }
            finally
            {
                if (opacityId != null) component.Remove(opacityId); if (tintId != null) component.Remove(tintId);
                Set(color, strength, transparency); if (pmx != null) { pmx.GetPart(0).colored = colored; pmx.Apply(); pmx.Save(); }
                if (saved == null) { if (File.Exists(path)) File.Delete(path); } else File.WriteAllBytes(path, saved);
            }
        }
    }
}
