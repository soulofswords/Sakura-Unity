using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    [InitializeOnLoad]
    public static class SakuraBuildRequest
    {
        private const string Request = @"C:\AI\Unity-Verification\run-build.request";
        private const string Result = @"C:\AI\Unity-Verification\build-request-result.json";

        static SakuraBuildRequest() { EditorApplication.update += Tick; }

        private static void Tick()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling) return;
            string edition = File.ReadAllText(Request).Trim();
            File.Delete(Request);
            if (edition != "companion" && edition != "vtuber")
            {
                File.WriteAllText(Result, "{\"result\":\"failed\",\"error\":\"unknown edition\"}");
                return;
            }
            Build(edition);
        }

        private static void Build(string edition)
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                EditorApplication.delayCall += () => Build(edition);
                return;
            }
            try
            {
                string exe = SakuraBuild.BuildWindows(edition);
                File.WriteAllText(Result, "{\"edition\":\"" + edition + "\",\"result\":\"succeeded\",\"exe\":\"" + exe.Replace("\\", "\\\\") + "\"}");
                Debug.Log("Sakura build request finished: " + exe);
            }
            catch (Exception error)
            {
                File.WriteAllText(Result, "{\"edition\":\"" + edition + "\",\"result\":\"failed\",\"error\":\"" + error.Message.Replace("\\", "/").Replace("\"", "'") + "\"}");
                Debug.LogError("Sakura build request failed: " + error);
            }
        }
    }
}