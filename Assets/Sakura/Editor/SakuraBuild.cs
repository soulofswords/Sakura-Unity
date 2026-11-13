using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraBuild
    {
        public static string BuildAndroid()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before building.");
            const string output = @"C:\AI\unity exe\Sakura Mobile\Sakura-Mobile-Unity-preview.apk";
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            string previousName = PlayerSettings.productName;
            string previousId = PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
            var previousBackend = PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android);
            var previousArchitectures = PlayerSettings.Android.targetArchitectures;
            var previousMin = PlayerSettings.Android.minSdkVersion;
            var previousTarget = PlayerSettings.Android.targetSdkVersion;
            bool previousBundle = EditorUserBuildSettings.buildAppBundle;
            try
            {
                PlayerSettings.productName = "Sakura Mobile Unity";
                PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android,"app.sakura.unitypreview");
                PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android,ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel28;
                PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel35;
                EditorUserBuildSettings.buildAppBundle = false;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/SakuraStudio.unity" }, target = BuildTarget.Android,
                    locationPathName = output, options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Android build failed with " + report.summary.totalErrors + " errors.");
                return output;
            }
            finally
            {
                PlayerSettings.productName = previousName;
                PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android,previousId);
                PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android,previousBackend);
                PlayerSettings.Android.targetArchitectures = previousArchitectures;
                PlayerSettings.Android.minSdkVersion = previousMin;
                PlayerSettings.Android.targetSdkVersion = previousTarget;
                EditorUserBuildSettings.buildAppBundle = previousBundle;
            }
        }

        public static string BuildWindows(string edition)
        {
            if(edition != "companion" && edition != "vtuber") throw new ArgumentException("Unknown edition");
            if(EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before building.");
            string destination = Path.Combine(@"C:\AI\unity exe", edition == "vtuber" ? "Sakura VTuber" : "Sakura Companion");
            Directory.CreateDirectory(destination);
            string editionFile = "Assets/Resources/Sakura/Edition.txt";
            byte[] previousEdition = File.Exists(editionFile) ? File.ReadAllBytes(editionFile) : null;
            string previousName = PlayerSettings.productName;
            try
            {
                File.WriteAllText(editionFile, edition);
                AssetDatabase.ImportAsset(editionFile, ImportAssetOptions.ForceSynchronousImport);
                PlayerSettings.productName = edition == "vtuber" ? "Sakura VTuber" : "Sakura Companion";
                string executable = Path.Combine(destination, PlayerSettings.productName + ".exe");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/SakuraStudio.unity" },
                    target = BuildTarget.StandaloneWindows64,
                    locationPathName = executable,
                    options = BuildOptions.None
                });
                if(report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Build failed with " + report.summary.totalErrors + " errors.");
                string tools = Path.Combine(destination,"Tools"); Directory.CreateDirectory(tools);
                foreach(string filename in new[] { "backend_bridge.py", "unity_tracking_api.py", "unity_tracking_worker.py" })
                    File.Copy(Path.Combine("Tools", filename), Path.Combine(tools, filename), true);
                File.WriteAllText(Path.Combine(destination,"TEST-BUILD.txt"),
                    "Unity migration test build. Uses the existing backend and models at F:\\AI\\assistant\\Sakura on this PC.\nKeep the executable with its Data folder, UnityPlayer.dll and Tools folder.\nFull feature parity is still under verification.\n");
                return executable;
            }
            finally
            {
                PlayerSettings.productName = previousName;
                if(previousEdition == null) AssetDatabase.DeleteAsset(editionFile);
                else {File.WriteAllBytes(editionFile,previousEdition);AssetDatabase.ImportAsset(editionFile);}
            }
        }
    }
}
