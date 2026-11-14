using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sakura.Unity
{
    // In Editor Play mode the taskbar button belongs to Unity.exe, and Explorer
    // caches button icons per AppUserModelID, so the logo needs both WM_SETICON
    // and a Sakura window AUMID. A built player gets its icon from exe resources.
    public sealed class SakuraWindowIcon : MonoBehaviour
    {
#if UNITY_EDITOR_WIN
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach() => new GameObject("Sakura Window Icon").AddComponent<SakuraWindowIcon>();

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr CreateIcon(IntPtr instance, int width, int height, byte planes, byte bitsPerPixel, byte[] andMask, byte[] xorMask);
        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, [In, Out] PropVariant value);
            void SetValue(ref PropertyKey key, [In, Out] PropVariant value);
            void Commit();
        }
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid format; public uint id; }
        [StructLayout(LayoutKind.Sequential)] private class PropVariant { public ushort type; public ushort reserved1; public ushort reserved2; public ushort reserved3; public IntPtr pointer; }

        private static readonly PropertyKey AppUserModelId = new PropertyKey { format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), id = 5 };

        private IntPtr icon;
        private IntPtr window;
        private IntPtr root;
        private IntPtr oldWindowSmall, oldWindowBig, oldRootSmall, oldRootBig;

        private IEnumerator Start()
        {
            for (int attempt = 0; attempt < 60 && window == IntPtr.Zero; attempt++) { window = GetActiveWindow(); yield return null; }
            if (window == IntPtr.Zero) yield break;
            var logo = new Texture2D(2, 2);
            if (!logo.LoadImage(File.ReadAllBytes(Path.Combine(Application.dataPath, "Resources/Sakura/CompanionLogo.png")))) yield break;
            var pixels = logo.GetPixels32();
            int width = logo.width, height = logo.height;
            var colour = new byte[width * height * 4];
            var mask = new byte[((width + 15) / 16) * 2 * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var pixel = pixels[(height - 1 - y) * width + x];
                    int index = (y * width + x) * 4;
                    colour[index] = pixel.b; colour[index + 1] = pixel.g; colour[index + 2] = pixel.r; colour[index + 3] = pixel.a;
                    if (pixel.a == 0) mask[y * ((width + 15) / 16 * 2) + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            icon = CreateIcon(IntPtr.Zero, width, height, 1, 32, mask, colour);
            UnityEngine.Object.Destroy(logo);
            if (icon == IntPtr.Zero) yield break;
            root = GetAncestor(window, 2);
            if (root == IntPtr.Zero) root = window;
            oldWindowSmall = SendMessage(window, 0x007F, new IntPtr(0), IntPtr.Zero);
            oldWindowBig = SendMessage(window, 0x007F, new IntPtr(1), IntPtr.Zero);
            oldRootSmall = SendMessage(root, 0x007F, new IntPtr(0), IntPtr.Zero);
            oldRootBig = SendMessage(root, 0x007F, new IntPtr(1), IntPtr.Zero);
            ApplyIdentity(window);
            ApplyIdentity(root);
            Debug.Log("Sakura logo applied to the Play-mode window icon.");
        }

        private void ApplyIdentity(IntPtr target)
        {
            SendMessage(target, 0x0080, new IntPtr(0), icon);
            SendMessage(target, 0x0080, new IntPtr(1), icon);
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(target, ref iid, out var store) != 0) return;
            var value = new PropVariant { type = 31, pointer = Marshal.StringToCoTaskMemUni("SoulOfSwords.Sakura.Unity") };
            var key = AppUserModelId;
            store.SetValue(ref key, value);
            store.Commit();
            Marshal.FreeCoTaskMem(value.pointer);
            Marshal.ReleaseComObject(store);
        }

        private void OnDestroy()
        {
            if (window == IntPtr.Zero) return;
            RestoreIdentity(window, oldWindowSmall, oldWindowBig);
            if (root != IntPtr.Zero && root != window) RestoreIdentity(root, oldRootSmall, oldRootBig);
        }

        private void RestoreIdentity(IntPtr target, IntPtr small, IntPtr big)
        {
            SendMessage(target, 0x0080, new IntPtr(0), small);
            SendMessage(target, 0x0080, new IntPtr(1), big);
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(target, ref iid, out var store) != 0) return;
            var empty = new PropVariant();
            var key = AppUserModelId;
            store.SetValue(ref key, empty);
            store.Commit();
            Marshal.ReleaseComObject(store);
        }
#endif
    }
}