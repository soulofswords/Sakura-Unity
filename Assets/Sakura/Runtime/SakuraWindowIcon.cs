using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sakura.Unity
{
    // In Editor Play mode the taskbar button belongs to Unity.exe, so the Sakura
    // logo has to be pushed onto the window with WM_SETICON; a built player gets
    // its icon from the executable resources instead.
    public sealed class SakuraWindowIcon : MonoBehaviour
    {
#if UNITY_EDITOR_WIN
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach() => new GameObject("Sakura Window Icon").AddComponent<SakuraWindowIcon>();

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr CreateIcon(IntPtr instance, int width, int height, byte planes, byte bitsPerPixel, byte[] andMask, byte[] xorMask);

        private IntPtr icon;

        private IEnumerator Start()
        {
            IntPtr window = IntPtr.Zero;
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
            SendMessage(window, 0x0080, new IntPtr(1), icon);
            SendMessage(window, 0x0080, new IntPtr(0), icon);
            var root = GetAncestor(window, 2);
            if (root != IntPtr.Zero && root != window)
            {
                SendMessage(root, 0x0080, new IntPtr(1), icon);
                SendMessage(root, 0x0080, new IntPtr(0), icon);
            }
            Debug.Log("Sakura logo applied to the Play-mode window icon.");
        }
#endif
    }
}