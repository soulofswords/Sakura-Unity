using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sakura.Unity
{
    public sealed partial class SakuraWindow
    {
        public bool Transparent { get; private set; }
        public bool TransparencyRequested => transparencyRequested;
        public static bool BuiltForDwmAlpha
        {
            get
            {
#if SAKURA_DWM_ALPHA
                return true;
#else
                return false;
#endif
            }
        }
        public string Status { get; private set; } = "Window transparency off.";
        public event Action Changed;
        bool transparencyRequested, transparencyAgentMode;
        Camera transparencyCamera;
        CameraClearFlags savedClearFlags;
        Color savedBackground;
        int savedCullingMask;
        bool savedHdr, savedMsaa, savedPostProcessing;
        UniversalAdditionalCameraData cameraData;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int left, right, top, bottom; }
        delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
        [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        float nextTransparencyCheck;
        int transparentWidth, transparentHeight;
        FullScreenMode lastFullScreenMode;
        static IntPtr enumeratedPlayerWindow;
        static bool IsPlayerWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return false;
            GetWindowThreadProcessId(window, out uint process);
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            return process == GetCurrentProcessId() && name.ToString() == "UnityWndClass";
        }
        static IntPtr FindPlayerWindow()
        {
            var active = GetActiveWindow(); if (IsPlayerWindow(active)) return active;
            enumeratedPlayerWindow = IntPtr.Zero;
            EnumWindows(FindPlayerWindowCallback, IntPtr.Zero);
            return enumeratedPlayerWindow;
        }
        [AOT.MonoPInvokeCallback(typeof(EnumWindowsCallback))]
        static bool FindPlayerWindowCallback(IntPtr window, IntPtr parameter)
        {
            if (!IsPlayerWindow(window)) return true;
            enumeratedPlayerWindow = window; return false;
        }
        static bool DwmEnabled() => DwmIsCompositionEnabled(out bool enabled) >= 0 && enabled;
#endif
        public static bool EffectiveTransparency(bool requested, bool agentMode) => requested && !agentMode;
        public static string TransparencyReason(bool editor, RuntimePlatform platform, GraphicsDeviceType graphics, bool framebufferAlpha, bool windowed, bool composition, bool forcedFlip, bool bitBltBuild = true)
        {
            if (editor) return "Window transparency requires a Windows player build. This is the Unity Editor.";
            if (platform != RuntimePlatform.WindowsPlayer) return "Native window transparency is available in the Windows player.";
            if (graphics != GraphicsDeviceType.Direct3D11) return "Window transparency requires the Direct3D 11 Windows build.";
            if (!bitBltBuild) return "Use the Sakura Windows build profile with D3D11 BitBlt presentation for transparency.";
            if (!framebufferAlpha) return "This build needs framebuffer alpha enabled in its Windows build settings.";
            if (forcedFlip) return "Remove -force-d3d11-flip-model to use native window transparency.";
            if (!windowed) return "Use windowed mode for desktop transparency.";
            if (!composition) return "Windows desktop composition is unavailable.";
            return null;
        }
        public void SetTransparent(bool enabled)
        {
            transparencyRequested = enabled;
            RefreshTransparency();
        }
        void RefreshTransparency()
        {
            bool before = Transparent; string previousStatus = Status;
            void Notify() { if (before != Transparent || previousStatus != Status) Changed?.Invoke(); }
            bool wanted = EffectiveTransparency(transparencyRequested, transparencyAgentMode);
            if (!wanted)
            {
                DisableTransparency();
                Status = transparencyAgentMode ? "Agent mode uses an opaque window." : "Window transparency off.";
                Notify(); return;
            }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            string problem = TransparencyReason(false, Application.platform, SystemInfo.graphicsDeviceType, Graphics.preserveFramebufferAlpha,
                Screen.fullScreenMode == FullScreenMode.Windowed, DwmEnabled(), Environment.GetCommandLineArgs().Any(argument => argument.Equals("-force-d3d11-flip-model", StringComparison.OrdinalIgnoreCase)), BuiltForDwmAlpha);
            if (problem != null) { DisableTransparency(); Status = problem; Notify(); return; }
            if (!IsPlayerWindow(handle)) handle = FindPlayerWindow();
            if (handle == IntPtr.Zero) { DisableTransparency(); Status = "Waiting for the Windows player window."; Notify(); return; }
            if (Transparent) return;
            try
            {
                var mainCamera = Camera.main;
                if (mainCamera == null || mainCamera.targetTexture != null) throw new InvalidOperationException("The screen camera is unavailable for transparent presentation.");
                var margins = new Margins { left = -1, right = -1, top = -1, bottom = -1 };
                int result = DwmExtendFrameIntoClientArea(handle, ref margins);
                if (result < 0) throw new InvalidOperationException("Windows could not enable DWM alpha (0x" + result.ToString("X8") + ").");
                CaptureCamera(mainCamera);
                mainCamera.clearFlags = CameraClearFlags.SolidColor; mainCamera.backgroundColor = Color.clear;
                // The avatar and stage are displayed by UI render textures; hidden world geometry must stay outside the screen pass.
                mainCamera.cullingMask = 0; mainCamera.allowHDR = false; mainCamera.allowMSAA = false;
                if (cameraData != null) cameraData.renderPostProcessing = false;
                Transparent = true;
                transparentWidth = Screen.width; transparentHeight = Screen.height; lastFullScreenMode = Screen.fullScreenMode;
                Status = "Window transparency on · native DWM alpha.";
            }
            catch (Exception error) { DisableTransparency(); Status = error.Message; }
#else
            DisableTransparency();
            Status = TransparencyReason(Application.isEditor, Application.platform, SystemInfo.graphicsDeviceType, Graphics.preserveFramebufferAlpha, true, false, false);
#endif
            Notify();
        }
        void CaptureCamera(Camera camera)
        {
            transparencyCamera = camera; savedClearFlags = camera.clearFlags; savedBackground = camera.backgroundColor;
            savedCullingMask = camera.cullingMask; savedHdr = camera.allowHDR; savedMsaa = camera.allowMSAA;
            cameraData = camera.GetComponent<UniversalAdditionalCameraData>(); if (cameraData != null) savedPostProcessing = cameraData.renderPostProcessing;
        }
        void DisableTransparency()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (IsPlayerWindow(handle))
            {
                var margins = new Margins();
                DwmExtendFrameIntoClientArea(handle, ref margins);
            }
#endif
            if (transparencyCamera != null)
            {
                transparencyCamera.clearFlags = savedClearFlags; transparencyCamera.backgroundColor = savedBackground;
                transparencyCamera.cullingMask = savedCullingMask; transparencyCamera.allowHDR = savedHdr; transparencyCamera.allowMSAA = savedMsaa;
                if (cameraData != null) cameraData.renderPostProcessing = savedPostProcessing;
            }
            transparencyCamera = null; cameraData = null; Transparent = false;
        }
        public JObject NativeWindowState()
        {
            var state = new JObject { ["transparent"] = Transparent, ["requested"] = transparencyRequested, ["agentMode"] = transparencyAgentMode,
                ["status"] = Status, ["graphicsAPI"] = SystemInfo.graphicsDeviceType.ToString(), ["framebufferAlpha"] = Graphics.preserveFramebufferAlpha,
                ["dwmBuildProfile"] = BuiltForDwmAlpha,
                ["windowed"] = Screen.fullScreenMode == FullScreenMode.Windowed, ["editor"] = Application.isEditor };
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            state["dwmCompositionEnabled"] = DwmEnabled(); state["windowFound"] = IsPlayerWindow(handle);
            if (IsPlayerWindow(handle) && GetWindowRect(handle, out var bounds)) state["bounds"] = new JObject { ["left"] = bounds.left, ["top"] = bounds.top, ["width"] = bounds.right - bounds.left, ["height"] = bounds.bottom - bounds.top };
#endif
            return state;
        }
        void Update()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if ((!transparencyRequested && !Transparent) || Time.unscaledTime < nextTransparencyCheck) return;
            nextTransparencyCheck = Time.unscaledTime + .5f;
            if (!transparencyAgentMode && (Screen.fullScreenMode != lastFullScreenMode || !Transparent)) RefreshTransparency();
            if (Transparent && (transparentWidth != Screen.width || transparentHeight != Screen.height))
            {
                var margins = new Margins { left = -1, right = -1, top = -1, bottom = -1 };
                if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0) { DisableTransparency(); Status = "DWM alpha could not resume after the window resized."; Changed?.Invoke(); }
                transparentWidth = Screen.width; transparentHeight = Screen.height;
            }
#endif
        }
        void OnEnable()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (handle != IntPtr.Zero && transparencyRequested) RefreshTransparency();
#endif
        }
        void OnDisable() => DisableTransparency();
        void OnDestroy() => DisableTransparency();
    }
}
