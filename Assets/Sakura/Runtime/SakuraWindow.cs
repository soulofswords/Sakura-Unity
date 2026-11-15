using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sakura.Unity
{
    public sealed partial class SakuraWindow : MonoBehaviour
    {
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
        IntPtr styledHandle;
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr window,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr window,int index,IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
#endif
        private IEnumerator Start()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            for(int attempt = 0; attempt < 60 && handle == IntPtr.Zero; attempt++) { handle = FindPlayerWindow(); yield return null; }
            if(handle != IntPtr.Zero)
            {
                ApplyBorderless();
                RefreshTransparency();
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
        }
#endif
        public void Minimize()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if(handle != IntPtr.Zero) ShowWindow(handle,6);
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
