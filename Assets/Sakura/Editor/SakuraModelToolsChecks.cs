using System;
using System.IO;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraModelToolsChecks
    {
        public static void ValidateAuthoredGlow()
        {
            var root = new GameObject("Authored glow fixture");
            string key = "authored-glow-check-" + Guid.NewGuid();
            string profile = SakuraModelTools.ProfilePath(key);
            try
            {
                var tools = SakuraModelTools.GetOrCreate(root, key);
                if (!tools.Effects.authoredGlow) throw new Exception("Authored glow did not default to enabled.");
                tools.SetEffects(new SakuraModelTools.EffectSettings { authoredGlow = false, glow = .5f });
                if (tools.Effects.authoredGlow || Mathf.Abs(tools.Effects.glow - .5f) > .001f) throw new Exception("Authored glow toggle did not apply alongside bloom.");
                UnityEngine.Object.DestroyImmediate(tools);
                tools = SakuraModelTools.GetOrCreate(root, key);
                if (tools.Effects.authoredGlow || Mathf.Abs(tools.Effects.glow - .5f) > .001f) throw new Exception("Authored glow setting did not persist.");
                tools.SetEffects(new SakuraModelTools.EffectSettings());
                if (!tools.Effects.authoredGlow) throw new Exception("Reset effects did not restore authored glow.");
                Debug.Log("SAKURA_AUTHORED_GLOW_CHECKS_OK: default-on, toggle round-trip with bloom, persistence across reload, reset restores default. Additive ArtMesh hiding is verified on a real Live2D model in Play mode.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                string folder = Path.GetDirectoryName(profile); if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        public static void VerifyEffects()
        {
            if (!Application.isPlaying) throw new Exception("Run model effects verification in Play mode.");
            var root = new GameObject("Model effects fixture");
            var pixels = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var before = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
            var after = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
            var readback = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            string key = "model-effects-check-" + Guid.NewGuid();
            var previous = RenderTexture.active;
            try
            {
                pixels.SetPixels(new[] { new Color(.15f, .05f, 0, .25f), new Color(.5f, .2f, 0, .75f), Color.clear, Color.white }); pixels.Apply();
                before.Create(); after.Create(); Graphics.Blit(pixels, before);
                var tools = SakuraModelTools.GetOrCreate(root, key);
                tools.SetEffects(new SakuraModelTools.EffectSettings { saturation = 0, contrast = 1, glow = .5f });
                tools.ApplyEffects(before, after);
                RenderTexture.active = after; readback.ReadPixels(new Rect(0, 0, 2, 2), 0, 0); readback.Apply();
                var colors = readback.GetPixels();
                if (Mathf.Abs(colors[0].a - .25f) > .01f || Mathf.Abs(colors[1].a - .75f) > .01f || colors[2].a > .01f) throw new Exception("Model effects changed source alpha.");
                if (Mathf.Abs(colors[0].r - colors[0].g) > .02f || Mathf.Abs(colors[0].g - colors[0].b) > .02f) throw new Exception("Saturation did not reach native output.");
                tools.ApplyEffects(after, after);
                RenderTexture.active = after; readback.ReadPixels(new Rect(0, 0, 2, 2), 0, 0); readback.Apply();
                if (Mathf.Abs(readback.GetPixel(0, 0).a - .25f) > .01f) throw new Exception("In-place effects changed alpha.");
                UnityEngine.Object.DestroyImmediate(tools); tools = SakuraModelTools.GetOrCreate(root, key);
                if (tools.Effects.saturation != 0 || tools.Effects.glow != .5f) throw new Exception("Effect settings did not persist.");
                bool rejected = false;
                try { tools.SetEffects(new SakuraModelTools.EffectSettings { contrast = float.NaN }); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("Nonfinite effect accepted.");
                Debug.Log("SAKURA_MODEL_EFFECTS_CHECKS_OK: native shader, saturation, alpha preservation, in-place blit, persistence, finite validation.");
            }
            finally
            {
                RenderTexture.active = previous; before.Release(); after.Release();
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(pixels); UnityEngine.Object.DestroyImmediate(readback); UnityEngine.Object.DestroyImmediate(before); UnityEngine.Object.DestroyImmediate(after);
                string folder = Path.GetDirectoryName(SakuraModelTools.ProfilePath(key)); if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        public static void Verify()
        {
            if (!Application.isPlaying) throw new Exception("Run model tools verification in Play mode.");
            var root = new GameObject("Model tools fixture");
            var source = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            var texture = new Texture2D(2, 2);
            texture.SetPixels(new[] { Color.red, Color.clear, Color.red, Color.red }); texture.Apply();
            source.mainTexture = texture;
            string key = "model-tools-check-" + Guid.NewGuid();
            string profile = SakuraModelTools.ProfilePath(key);
            try
            {
                var first = new GameObject("First"); first.transform.SetParent(root.transform);
                var second = new GameObject("Second"); second.transform.SetParent(root.transform);
                var renderer = first.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMaterials = new[] { source }; renderer.bones = new[] { first.transform, second.transform };
                var other = second.AddComponent<MeshRenderer>(); other.sharedMaterials = new[] { source };
                var tools = SakuraModelTools.GetOrCreate(root, key);
                if (tools.GetMaterialIndex(renderer, 0) == tools.GetMaterialIndex(other, 0)) throw new Exception("Renderer-local materials collided.");
                int part = tools.GetMaterialIndex(renderer, 0);
                tools.SetColor(part, Color.blue, .75f);
                tools.SetTransparency(part, .5f);
                if (Mathf.Abs(tools.Parts[part].Settings.strength - .75f) > .001f) throw new Exception("Transparency overwrote color.");
                tools.SetColor(part, Color.green, .25f);
                if (Mathf.Abs(tools.Parts[part].Settings.transparency - .5f) > .001f) throw new Exception("Color overwrote transparency.");
                if (source.mainTexture != texture || source.color != Color.white) throw new Exception("Source material was changed.");
                int bone = tools.GetBoneIndex(first.transform);
                tools.Freeze(bone, true); first.transform.localPosition = Vector3.one * 20; tools.ApplyFrozenBones();
                if (first.transform.localPosition != Vector3.zero) throw new Exception("Frozen bone left its rest pose.");
                bool rejected = false;
                try { tools.SetTransparency(part, float.NaN); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("Nonfinite transparency accepted.");
                rejected = false;
                try { SakuraModelTools.DecodeImage(Array.Empty<byte>()); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("Empty replacement accepted.");
                byte[] oversized = texture.EncodeToPNG(); oversized[16] = 0; oversized[17] = 0; oversized[18] = 32; oversized[19] = 0;
                rejected = false;
                try { SakuraModelTools.DecodeImage(oversized); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("Oversized PNG was decoded.");
                tools.ImportAppearance(new[] { new SakuraModelTools.AppearanceChange {
                    index = part, hasColor = true, color = Color.green, strength = .25f,
                    hasOpacity = true, opacity = .5f, textureBytes = texture.EncodeToPNG() } });
                if (tools.Parts[part].Replacement == null || tools.Parts[part].Settings.color != Color.green ||
                    Mathf.Abs(tools.Parts[part].Settings.transparency - .5f) > .001f)
                    throw new Exception("Appearance import did not apply independent channels.");
                rejected = false;
                try { tools.ImportAppearance(new[] {
                    new SakuraModelTools.AppearanceChange { index = part, hasColor = true, color = Color.red, strength = 1 },
                    new SakuraModelTools.AppearanceChange { index = part, hasOpacity = true, opacity = 0 } }); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || tools.Parts[part].Settings.color != Color.green)
                    throw new Exception("Rejected appearance import partially changed the model.");
                tools.RestoreColor(part);
                if (tools.Parts[part].Settings.strength != 0 || tools.Parts[part].Settings.transparency != .5f) throw new Exception("Color reset cleared opacity.");
                tools.ReplaceTexture(part, texture.EncodeToPNG());
                if (!File.Exists(profile)) throw new Exception("Native profile was not saved.");
                UnityEngine.Object.DestroyImmediate(tools);
                tools = SakuraModelTools.GetOrCreate(root, key);
                if (tools.Parts[part].Settings.transparency != .5f || !tools.IsFrozen(bone) || tools.Parts[part].Replacement == null) throw new Exception("Profile did not restore.");
                tools.RestoreTexture(part);
                if (tools.Parts[part].Replacement != null) throw new Exception("Replacement remained after restore.");
                Debug.Log("SAKURA_MODEL_TOOLS_CHECKS_OK: material identity, independent color/opacity, source safety, bone freeze, finite inputs, bounded PNG decode, image replacement, persistent reload.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(texture);
                if (Directory.Exists(Path.GetDirectoryName(profile))) Directory.Delete(Path.GetDirectoryName(profile), true);
            }
        }
    }
}
