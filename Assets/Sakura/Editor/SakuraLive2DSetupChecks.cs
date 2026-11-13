using System;
using System.IO;
using System.Reflection;
using Live2D.Cubism.Framework.Physics;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraLive2DSetupChecks
    {
        public static void VerifyViewport()
        {
            if (!Application.isPlaying) throw new Exception("Keep the current Play session running for viewport verification.");
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            if (viewport == null || viewport.LoadedFormat != "Live2D" || !viewport.HasAvatar) throw new Exception("Load a Live2D model with authored physics in the existing Play session first.");
            var tools = viewport.AvatarRoot.GetComponent<SakuraModelTools>();
            if (tools == null || !tools.HasLive2DPhysics) throw new Exception("The current Live2D model has no configured native physics rig.");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var viewportType = typeof(SakuraAvatarViewport);
            var original = tools.Live2D.Copy();
            var offset = viewportType.GetField("framingOffset", flags); var roll = viewportType.GetField("live2DRoll", flags);
            var zoom = viewportType.GetField("zoom", flags); var targetZoom = viewportType.GetField("targetZoom", flags);
            object oldOffset = offset.GetValue(viewport), oldRoll = roll.GetValue(viewport), oldZoom = zoom.GetValue(viewport), oldTarget = targetZoom.GetValue(viewport);
            Quaternion oldRotation = viewport.AvatarRoot.transform.localRotation;
            string storage = (string)typeof(SakuraModelTools).GetField("storage", flags).GetValue(tools);
            byte[] oldProfile = File.Exists(storage) ? File.ReadAllBytes(storage) : null;
            var setup = (SakuraLive2DSetup)typeof(SakuraModelTools).GetField("liveSetup", flags).GetValue(tools);
            var rig = (CubismPhysicsRig)typeof(SakuraLive2DSetup).GetField("rig", flags).GetValue(setup);
            var subRigs = rig.SubRigs; var gravity = rig.Gravity; var wind = rig.Wind; float oldFps = rig.Fps;
            try
            {
                var limited = original.Copy(); limited.physicsFps = 120; limited.legacy = false;
                limited.limitMovement = true; limited.xMin = limited.xMax = limited.yMin = limited.yMax = 0;
                limited.zoomMin = 80; limited.zoomMax = 90; limited.rotationMin = -10; limited.rotationMax = 10;
                tools.SetLive2DSettings(limited);
                if (rig.Fps != 120 || !ReferenceEquals(subRigs, rig.SubRigs) || rig.Gravity != gravity || rig.Wind != wind) throw new Exception("Cadence change replaced authored physics configuration.");
                viewport.SetZoomPercent(400); offset.SetValue(viewport, new Vector3(100, 100, 0)); viewport.Rotate(100);
                viewport.ApplyLive2DLimits(limited);
                if (viewport.ZoomPercent > 90.01f || ((Vector3)offset.GetValue(viewport)).sqrMagnitude > .001f || Mathf.Abs((float)roll.GetValue(viewport)) > 10.01f) throw new Exception("Native viewport movement bounds did not constrain pan, zoom and roll.");
                if (Vector3.Dot(viewport.AvatarRoot.transform.localRotation * Vector3.forward, Vector3.back) < .999f) throw new Exception("Live2D roll lost its camera-facing orientation.");
                limited.legacy = true; tools.SetLive2DSettings(limited); if (rig.Fps != 0) throw new Exception("Native viewport legacy setting did not update the SDK rig.");
            }
            finally
            {
                tools.SetLive2DSettings(original); rig.Fps = oldFps;
                offset.SetValue(viewport, oldOffset); roll.SetValue(viewport, oldRoll); zoom.SetValue(viewport, oldZoom); targetZoom.SetValue(viewport, oldTarget);
                viewport.AvatarRoot.transform.localRotation = oldRotation; viewportType.GetMethod("FrameAvatar", flags).Invoke(viewport, null);
                if (oldProfile == null) { if (File.Exists(storage)) File.Delete(storage); } else File.WriteAllBytes(storage, oldProfile);
            }
            Debug.Log("SAKURA_LIVE2D_VIEWPORT_CHECKS_OK: live authored rig 120 Hz/legacy, authored sub-rigs/gravity/wind retained, native pan/zoom/roll bounds, face orientation. Original runtime and saved settings restored.");
        }
        public static void Verify()
        {
            var settings = new SakuraLive2DSettings { physicsFps = 120, xMin = 250, xMax = -250, zoomMin = -2, zoomMax = 2000, rotationMin = -1000, rotationMax = 1000 };
            settings.Validate();
            if (settings.xMin != 200 || settings.xMax != 200 || settings.zoomMin != 10 || settings.zoomMax != 1000 || settings.rotationMin != -360 || settings.rotationMax != 360) throw new Exception("Live2D movement limits were not bounded.");
            var rig = new CubismPhysicsRig(); SakuraLive2DSetup.ConfigureRig(rig, settings);
            if (rig.Fps != 120) throw new Exception("Native Cubism rig cadence was not updated.");
            settings.legacy = true; SakuraLive2DSetup.ConfigureRig(rig, settings);
            if (rig.Fps != 0) throw new Exception("Legacy physics did not select render-step evaluation.");
            settings.legacy = false; settings.physicsFps = 0; SakuraLive2DSetup.ConfigureRig(rig, settings);
            if (rig.Fps != 0) throw new Exception("Match app physics was not selected.");
            settings.xMin = float.NaN; bool rejected = false;
            try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Nonfinite Live2D bound accepted.");
            var pixels = new[] { new Color32(255, 0, 0, 255), new Color32(255, 255, 255, 0), new Color32(255, 255, 255, 0) };
            var fixedPixels = SakuraLive2DSetup.FixAlphaEdges(pixels, 3, 1);
            if (fixedPixels[1].r != 255 || fixedPixels[1].g != 0 || fixedPixels[1].a != 0 || fixedPixels[0].a != 255 || pixels[1].g != 255) throw new Exception("White-edge correction changed source pixels or alpha.");
            Debug.Log("SAKURA_LIVE2D_SETUP_CHECKS_OK: bounded movement settings, native 120 Hz rig cadence, legacy/match-app cadence, finite validation, reversible alpha-edge RGB correction. No Play mode transition.");
        }
    }
}
