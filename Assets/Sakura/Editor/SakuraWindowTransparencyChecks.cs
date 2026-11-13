using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sakura.Unity.Editor
{
    public static class SakuraWindowTransparencyChecks
    {
        public static void Verify()
        {
            if (SakuraWindow.TransparencyReason(true, RuntimePlatform.WindowsEditor, GraphicsDeviceType.Direct3D11, true, true, true, false) == null) throw new Exception("Editor was advertised as a transparent player.");
            if (SakuraWindow.TransparencyReason(false, RuntimePlatform.WindowsPlayer, GraphicsDeviceType.Direct3D12, true, true, true, false) == null) throw new Exception("D3D12 flip presentation was accepted.");
            if (SakuraWindow.TransparencyReason(false, RuntimePlatform.WindowsPlayer, GraphicsDeviceType.Direct3D11, false, true, true, false) == null) throw new Exception("Framebuffer alpha was optional.");
            if (SakuraWindow.TransparencyReason(false, RuntimePlatform.WindowsPlayer, GraphicsDeviceType.Direct3D11, true, true, true, true) == null) throw new Exception("Forced flip swap chain was accepted.");
            if (SakuraWindow.TransparencyReason(false, RuntimePlatform.WindowsPlayer, GraphicsDeviceType.Direct3D11, true, true, true, false, false) == null) throw new Exception("An unverified swap-chain build profile was accepted.");
            if (SakuraWindow.TransparencyReason(false, RuntimePlatform.WindowsPlayer, GraphicsDeviceType.Direct3D11, true, true, true, false) != null) throw new Exception("Compatible DWM alpha policy was rejected.");
            if (!SakuraWindow.EffectiveTransparency(true, false) || SakuraWindow.EffectiveTransparency(true, true) || SakuraWindow.EffectiveTransparency(false, false)) throw new Exception("Agent opacity policy failed.");
            var root = new GameObject("Window transparency policy fixture");
            try
            {
                var window = root.AddComponent<SakuraWindow>(); window.SetTransparent(true);
                if (window.Transparent || string.IsNullOrWhiteSpace(window.Status)) throw new Exception("Editor window was modified or returned no explanation.");
                window.SetAgentMode(true); if (window.Transparent) throw new Exception("Agent requested a transparent window.");
                window.SetAgentMode(false); if (window.Transparent) throw new Exception("Returning from Agent modified the Editor window.");
                window.SetTransparent(false);
                Debug.Log("SAKURA_WINDOW_TRANSPARENCY_CHECKS_OK: Windows/D3D11/alpha/windowed/DWM policy, flip rejection, Agent opacity/restore intent, explicit Editor status. Native HWND and desktop remain untouched.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
