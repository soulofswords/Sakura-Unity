using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraStageItemChecks
    {
        public static void Validate()
        {
            var bounce = SakuraStageItems.SimulateThrow(new SakuraStageItems.ThrowState { Position = new Vector2(.99f, .99f), Velocity = new Vector2(3, 3) }, .05f);
            if (bounce.Position.x != 1 || bounce.Position.y != 1 || bounce.Velocity.x >= 0 || bounce.Velocity.y >= 0) throw new Exception("Stage throw did not bounce inside the viewport.");
            var stopped = SakuraStageItems.SimulateThrow(new SakuraStageItems.ThrowState { Position = new Vector2(.3f, .4f), Velocity = new Vector2(float.NaN, 1) }, .016f);
            if (stopped.Position != new Vector2(.3f, .4f) || stopped.Velocity != Vector2.zero) throw new Exception("Invalid throw velocity did not pause safely.");
            var settled = new SakuraStageItems.ThrowState { Position = new Vector2(.5f, .5f), Velocity = Vector2.zero };
            for (int step = 0; step < 1200; step++) settled = SakuraStageItems.SimulateThrow(settled, 1f / 60);
            if (settled.Position.y != 1 || settled.Velocity != Vector2.zero) throw new Exception("Stage throw did not settle at the viewport floor.");
            var item = new SakuraStageItems.Item { id = new string('a', 32), kind = "image", name = "Fixture", source = new string('a', 32) + ".png" };
            if (!SakuraStageItems.Valid(item) || SakuraStageItems.MaximumItems != 8 || SakuraStageItems.Live2DLayer == 30) throw new Exception("Native stage-item defaults or bounds differ.");
            item.x = float.NaN; if (SakuraStageItems.Valid(item)) throw new Exception("Stage item accepted a nonfinite position.");
            item.x = .5f; item.width = 2; if (SakuraStageItems.Valid(item)) throw new Exception("Stage item accepted excessive size.");
            item.width = .25f; item.id = "../outside"; if (SakuraStageItems.Valid(item)) throw new Exception("Stage item accepted an outside asset ID.");
            string folder = Path.Combine(Path.GetTempPath(), "sakura-stage-item-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "models", "live2d"));
            string path = Path.Combine(folder, "models", "live2d", "fixture.model3.json"); File.WriteAllText(path, "{}");
            try
            {
                if (SakuraStageItems.ResolveLive2D(folder, "live2d/fixture.model3.json") != path) throw new Exception("Imported stage-item path did not resolve.");
                if (SakuraStageItems.OwnImagePath(folder, new string('a', 32)) != Path.Combine(folder, new string('a', 32) + ".png")) throw new Exception("Stage image storage left its owned folder.");
                bool refused = false;
                try { SakuraStageItems.OwnImagePath(folder, "../settings"); } catch (ArgumentException) { refused = true; }
                if (!refused) throw new Exception("Stage-image deletion accepted a foreign asset path.");
                refused = false;
                try { SakuraStageItems.ResolveLive2D(folder, "../outside.model3.json"); } catch (ArgumentException) { refused = true; }
                if (!refused) throw new Exception("Live2D stage items accepted an outside-library path.");
                refused = false;
                try { SakuraStageItems.DecodeImage(new byte[40]); } catch (InvalidDataException) { refused = true; }
                if (!refused) throw new Exception("Stage images accepted a malformed PNG.");
            }
            finally { Directory.Delete(folder, true); }
            Debug.Log("PASS: bounded stage-item profiles, finite placement, owned image paths, confined imported Live2D paths and PNG validation.");
        }

        public static async Task VerifyImage()
        {
            Validate();
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (!Application.isPlaying || studio == null) throw new InvalidOperationException("Enter Studio Play mode first.");
            var manager = studio.GetComponent<SakuraStageItems>();
            if (manager == null) throw new InvalidOperationException("Initialize the native stage-item manager first.");
            if (manager.Items.Count >= SakuraStageItems.MaximumItems) throw new InvalidOperationException("All eight stage-item slots are occupied. Owner items were kept.");
            string folder = (string)typeof(SakuraStageItems).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            string profile = Path.Combine(folder, "items.json"); byte[] originalProfile = File.Exists(profile) ? File.ReadAllBytes(profile) : null;
            string selected = manager.Selected?.id, id = null;
            string name = "Generated verification " + Guid.NewGuid().ToString("N");
            string original = Path.Combine(Path.GetTempPath(), name + ".png");
            var fixture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            fixture.SetPixels(new[] { Color.magenta, Color.magenta, Color.magenta, Color.magenta }); fixture.Apply();
            File.WriteAllBytes(original, fixture.EncodeToPNG()); UnityEngine.Object.Destroy(fixture);
            try
            {
                id = await manager.AddImage(File.ReadAllBytes(original), Path.GetFileName(original));
                manager.Configure(id, .2f, .2f, .12f, 0, true, false, false);
                await Task.Delay(150);
                var item = manager.Items.Single(value => value.id == id);
                var quad = studio.GetComponentsInChildren<MeshRenderer>().Single(renderer => renderer.gameObject.name == "Stage item “" + item.name + "”");
                if (quad.gameObject.layer != 30 || quad.sharedMaterial.renderQueue < 4000 || quad.sharedMaterial.shader.name != "Hidden/Sakura/StageItem")
                    throw new Exception("Stage image did not use the main avatar output layer and above-model shader queue.");
                manager.Configure(id, .2f, .2f, .12f, 30, false, false, true);
                if (quad.sharedMaterial.renderQueue >= 2000 || !item.locked || item.rotation != 30) throw new Exception("Stage item layer, rotation or lock settings were lost.");
                manager.Configure(id, .2f, .2f, .12f, 0, true, true, false);
                if (!item.follow || !File.Exists(profile)) throw new Exception("Stage item attachment or profile persistence was lost.");
                await Task.Delay(150);
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-stage-items.png");
                manager.Remove(id);
                if (manager.Items.Any(value => value.id == id) || File.Exists(SakuraStageItems.OwnImagePath(folder, id)) || !File.Exists(original))
                    throw new Exception("Stage item removal retained its owned image or removed the original file.");
                id = null;
                Debug.Log("PASS: native stage image import, output shader/layer, above/below, rotation, lock, whole-model attachment, persistence and owned-file-only removal. Original item profile restored.");
            }
            finally
            {
                if (id != null && manager.Items.Any(item => item.id == id)) manager.Remove(id);
                if (selected != null) manager.Select(selected);
                if (originalProfile == null) { if (File.Exists(profile)) File.Delete(profile); } else File.WriteAllBytes(profile, originalProfile);
                if (File.Exists(original)) File.Delete(original);
            }
        }

        public static async Task VerifyLive2D(string relativePath = null)
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (!Application.isPlaying || studio == null) throw new InvalidOperationException("Enter Studio Play mode first.");
            var viewport = studio.GetComponent<SakuraAvatarViewport>();
            var manager = studio.GetComponent<SakuraStageItems>();
            if (manager == null || manager.Items.Count >= SakuraStageItems.MaximumItems) throw new InvalidOperationException("Initialize Stage Items with a free slot first.");
            relativePath ??= viewport.LoadedFormat == "Live2D" ? viewport.LoadedModelPath : null;
            if (relativePath == null) throw new InvalidOperationException("Pass an imported Live2D asset path or load a Live2D avatar first.");
            var mainAvatar = viewport.AvatarRoot;
            string folder = (string)typeof(SakuraStageItems).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            string profile = Path.Combine(folder, "items.json"); byte[] originalProfile = File.Exists(profile) ? File.ReadAllBytes(profile) : null;
            string selected = manager.Selected?.id, id = null;
            try
            {
                id = await manager.AddLive2D(relativePath, "Generated Live2D item verification");
                manager.Configure(id, .2f, .3f, .25f, 0, true, false, false);
                await Task.Delay(600);
                var item = manager.Items.Single(value => value.id == id);
                var texture = (RenderTexture)typeof(SakuraStageItems.Item).GetField("liveTexture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item);
                if (texture == null || !texture.IsCreated() || texture.width != 256 || texture.height != 256) throw new Exception("Live2D item did not create its bounded native render target.");
                var previous = RenderTexture.active;
                var pixels = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = texture; pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); pixels.Apply();
                    if (!pixels.GetPixels().Any(color => color.a > .01f)) throw new Exception("The imported native Live2D item rendered an empty texture.");
                }
                finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(pixels); }
                if (viewport.AvatarRoot != mainAvatar) throw new Exception("Adding a Live2D stage item replaced the avatar.");
                var motions = manager.MotionNames(id);
                if (motions.Count > 0)
                {
                    manager.PlayAnimation(id, motions[0], true);
                    await Task.Delay(150);
                    if (!item.animationPlaying || item.animation != motions[0]) throw new Exception("Authored Live2D item motion did not start.");
                    manager.StopAnimation(id);
                    if (item.animationPlaying) throw new Exception("Live2D item animation did not stop.");
                }
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\pc-stage-item-live2d.png");
                Debug.Log("PASS: real imported Live2D item pixels, bounded offscreen rendering and main-avatar preservation. Original stage-item profile restored.");
            }
            finally
            {
                if (id != null && manager.Items.Any(item => item.id == id)) manager.Remove(id);
                if (selected != null) manager.Select(selected);
                if (originalProfile == null) { if (File.Exists(profile)) File.Delete(profile); } else File.WriteAllBytes(profile, originalProfile);
            }
        }

        public static async Task VerifyMovement()
        {
            Validate();
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (!Application.isPlaying || studio == null) throw new InvalidOperationException("Enter Studio Play mode first.");
            var manager = studio.GetComponent<SakuraStageItems>();
            if (manager == null || manager.Items.Count > SakuraStageItems.MaximumItems - 2) throw new InvalidOperationException("Initialize Stage Items with two free slots first.");
            string folder = (string)typeof(SakuraStageItems).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            string file = Path.Combine(folder, "items.json"); byte[] saved = File.Exists(file) ? File.ReadAllBytes(file) : null;
            string selected = manager.Selected?.id, first = null, second = null;
            bool throws = manager.ThrowsEnabled, enabled = manager.enabled;
            var pixels = new Texture2D(1, 1, TextureFormat.RGBA32, false); pixels.SetPixel(0, 0, Color.magenta); pixels.Apply();
            byte[] png = pixels.EncodeToPNG(); UnityEngine.Object.Destroy(pixels);
            try
            {
                first = await manager.AddImage(png, "Generated movement check A"); second = await manager.AddImage(png, "Generated movement check B");
                manager.Reorder(first, 1);
                if (manager.Items.Last().id != first) throw new Exception("Stage-item order did not change.");
                manager.Reorder(first, -1);
                if (manager.Items.Last().id != second) throw new Exception("Stage-item order did not restore.");
                manager.SetThrows(true); manager.Configure(first, .3f, .3f, .1f, 0, true, false, false);
                if (!manager.Throw(first, new Vector2(1, -1))) throw new Exception("An unlocked stage item rejected a throw.");
                await Task.Delay(150);
                var item = manager.Items.Single(value => value.id == first);
                if (item.x <= .3f || item.y >= .3f) throw new Exception("Native stage-item movement did not integrate its velocity.");
                manager.enabled = false; float x = item.x, y = item.y;
                await Task.Delay(100);
                if (item.x != x || item.y != y) throw new Exception("Disabled stage-item simulation kept moving.");
                manager.enabled = true;
                manager.Configure(first, .3f, .3f, .1f, 0, true, false, true);
                if (manager.Throw(first, Vector2.one)) throw new Exception("A locked stage item accepted a throw.");
                manager.Configure(first, .3f, .3f, .1f, 0, true, true, false);
                if (manager.Throw(first, Vector2.one)) throw new Exception("An attached stage item accepted a throw.");
                manager.Configure(first, .3f, .3f, .1f, 0, true, false, false);
                manager.Throw(first, Vector2.one); manager.SetThrows(false);
                x = item.x; y = item.y; await Task.Delay(100);
                if (item.x != x || item.y != y) throw new Exception("Disabling item throws did not stop movement.");
                Debug.Log("PASS: native throw movement, persisted reordering, lock/follow constraints and disabled simulation pause. Owner stage-item profile restored.");
            }
            finally
            {
                manager.enabled = enabled;
                if (second != null && manager.Items.Any(item => item.id == second)) manager.Remove(second);
                if (first != null && manager.Items.Any(item => item.id == first)) manager.Remove(first);
                manager.SetThrows(throws); if (selected != null) manager.Select(selected);
                if (saved == null) { if (File.Exists(file)) File.Delete(file); } else File.WriteAllBytes(file, saved);
            }
        }
    }
}
