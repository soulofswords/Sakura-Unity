using System;
using System.Text;
using UnityEngine;

namespace Sakura.Unity
{
    internal static class SakuraEncoding
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch (Exception error) { Debug.LogWarning("Shift-JIS code page registration failed: " + error.Message); }
        }
    }
}
