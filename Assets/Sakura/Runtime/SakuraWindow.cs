using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sakura.Unity
{
    public sealed class SakuraWindow : MonoBehaviour
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
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr window,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr window,int index,IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
#endif
        private IEnumerator Start()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            for(int attempt = 0; attempt < 60 && handle == IntPtr.Zero; attempt++) { handle = GetActiveWindow(); yield return null; }
            if(handle != IntPtr.Zero)
            {
                SetStyle(handle,-16,new IntPtr(GetStyle(handle,-16).ToInt64() & ~0x00C00000L));
                SetWindowPos(handle,IntPtr.Zero,0,0,0,0,0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010);
            }
#else
            yield break;
#endif
        }
        public void Minimize()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if(handle != IntPtr.Zero) ShowWindow(handle,6);
#endif
        }
        public void Maximize()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if(handle != IntPtr.Zero) ShowWindow(handle,IsZoomed(handle) ? 9 : 3);
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
    }
}
