using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sakura.Unity
{
    public sealed partial class SakuraWindow : MonoBehaviour
    {
        private bool startupChooser;
        public event Action<bool> VisibilityShortcut;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
        private static extern int ReadRegistryValue(IntPtr key, string path, string name, uint flags, IntPtr type, out uint value, ref uint bytes);
#endif
        public static bool SystemUsesLightTheme()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            uint bytes = 4;
            return ReadRegistryValue(new IntPtr(unchecked((int)0x80000001)), @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0x10, IntPtr.Zero, out uint value, ref bytes) == 0 && value != 0;
#else
            return false;
#endif
        }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] struct Point { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct Rectangle { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct Placement
        { public int length, flags, show; public Point minimum, maximum; public Rectangle normal; }
        Placement companionPlacement;
        bool restoreCompanion;
        Placement startupPlacement;
        bool restoreStartup;
        IntPtr styledHandle;
        IntPtr shortcutWindow;
        delegate IntPtr WindowSubclass(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
        WindowSubclass shortcutCallback;
        [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr window, WindowSubclass callback, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr window, WindowSubclass callback, UIntPtr id);
        [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetKeyState(int virtualKey);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
        private static extern uint RegisterWindowMessage(string name);
        static readonly uint ForwardedTransparencyMessage = RegisterWindowMessage("SoulOfSwords.Sakura.ToggleTransparency");
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr window,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr window,int index,IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
        [StructLayout(LayoutKind.Sequential)]
        struct MonitorInfo { public int size; public Rectangle monitor, work; public uint flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        bool TryWorkArea(out Rectangle area)
        {
            var info = new MonitorInfo { size = Marshal.SizeOf<MonitorInfo>() };
            bool found = GetMonitorInfo(MonitorFromWindow(handle, 2), ref info);
            area = info.work;
            return found;
        }
#endif
        internal void FocusAgentWindow(IntPtr target)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // Let Windows negotiate focus from the active Sakura window. If the
            // person has switched apps during planning, leave their focus alone.
            if (IsPlayerWindow(GetForegroundWindow()) && IsWindow(target))
                SetForegroundWindow(target);
#endif
        }

        private IEnumerator Start()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            for(int attempt = 0; attempt < 60 && handle == IntPtr.Zero; attempt++) { handle = FindPlayerWindow(); yield return null; }
            if(handle != IntPtr.Zero)
            {
                ApplyBorderless();
                RefreshTransparency();
                if (startupChooser) SetStartupChooser(true);
            }
#else
            yield break;
#endif
        }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // Unity can replace its splash window with the player window after the
        // first frames, and maximizing can restore the native caption. Keep the
        // native frame removed so the in-app header is the only title bar.
        void MaintainWindowFrame()
        {
            if (handle == IntPtr.Zero || !IsPlayerWindow(handle)) handle = FindPlayerWindow();
            if (handle == IntPtr.Zero) return;
            long style = GetStyle(handle, -16).ToInt64();
            if (handle != styledHandle || (style & 0x00C00000L) != 0) ApplyBorderless();
        }
        void ApplyBorderless()
        {
            if (handle == IntPtr.Zero) return;
            SetStyle(handle, -16, new IntPtr(GetStyle(handle, -16).ToInt64() & ~0x00C00000L));
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010);
            styledHandle = handle;
            if (shortcutWindow != handle)
            {
                RemoveVisibilityShortcuts();
                shortcutCallback ??= VisibilityKeyMessage;
                if (SetWindowSubclass(handle, shortcutCallback, new UIntPtr(1), UIntPtr.Zero)) shortcutWindow = handle;
            }
        }

        IntPtr VisibilityKeyMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            long key = wParam.ToInt64();
            if (!startupChooser && ForwardedTransparencyMessage != 0 && message == ForwardedTransparencyMessage)
                visibilityRequest = 2;
            else if (message == 0x0100 && !startupChooser && (key == 'H' || key == 'T') &&
                (lParam.ToInt64() & (1L << 30)) == 0 && GetKeyState(0x11) < 0 && GetKeyState(0x10) < 0 && GetKeyState(0x12) >= 0)
                visibilityRequest = key == 'T' ? 2 : 1;
            return DefSubclassProc(window, message, wParam, lParam);
        }

        int visibilityRequest;
        void RemoveVisibilityShortcuts()
        {
            if (shortcutWindow != IntPtr.Zero) RemoveWindowSubclass(shortcutWindow, shortcutCallback, new UIntPtr(1));
            shortcutWindow = IntPtr.Zero;
        }
#endif
        public void Minimize(bool compactCompanion = false)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (handle == IntPtr.Zero) return;
            if (!compactCompanion) { ShowWindow(handle, 6); return; }
            if (!TryWorkArea(out var work)) return;
            int width = Math.Min(460, work.right - work.left);
            int height = Math.Min(760, work.bottom - work.top);
            if (IsZoomed(handle)) ShowWindow(handle, 9);
            SetWindowPos(handle, IntPtr.Zero, Math.Max(work.left, work.right - width - 12),
                Math.Max(work.top, work.bottom - height - 12), width, height, 0x0004 | 0x0010);
            ApplyBorderless();
#endif
        }
        public void SetStartupChooser(bool visible)
        {
            startupChooser = visible;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (handle == IntPtr.Zero) return;
            if (visible && !restoreStartup)
            {
                var placement = new Placement { length = Marshal.SizeOf<Placement>() };
                if (!GetWindowPlacement(handle, ref placement)) return;
                startupPlacement = placement; restoreStartup = true;
                if (!TryWorkArea(out var work)) { restoreStartup = false; return; }
                int width = Math.Min(620, work.right - work.left);
                int height = Math.Min(695, work.bottom - work.top);
                int x = work.left + (work.right - work.left - width) / 2;
                int y = work.top + (work.bottom - work.top - height) / 2;
                ShowWindow(handle, 9);
                SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010);
            }
            else if (!visible && restoreStartup)
            {
                SetWindowPlacement(handle, ref startupPlacement); restoreStartup = false;
            }
#endif
        }
        public void Maximize()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if(handle != IntPtr.Zero) { ShowWindow(handle,IsZoomed(handle) ? 9 : 3); ApplyBorderless(); }
#else
            if(!Application.isEditor) Screen.fullScreen = !Screen.fullScreen;
#endif
        }
        public void Drag()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if(handle == IntPtr.Zero) return;
            ReleaseCapture(); SendMessage(handle,0x00A1,new IntPtr(2),IntPtr.Zero);
#endif
        }
        public void SetAgentMode(bool agent)
        {
            transparencyAgentMode = agent;
            RefreshTransparency();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (handle == IntPtr.Zero) return;
            if (agent && !restoreCompanion && !IsZoomed(handle))
            {
                var placement = new Placement { length = Marshal.SizeOf<Placement>() };
                if (GetWindowPlacement(handle, ref placement) && placement.normal.right - placement.normal.left < 900)
                {
                    companionPlacement = placement; restoreCompanion = true;
                    ShowWindow(handle, 3);
                }
            }
            else if (!agent && restoreCompanion)
            {
                SetWindowPlacement(handle, ref companionPlacement); restoreCompanion = false;
            }
#endif
        }
    }
}
