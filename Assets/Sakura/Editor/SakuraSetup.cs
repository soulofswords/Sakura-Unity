using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity.Editor
{
    [InitializeOnLoad]
    public static class SakuraSetup
    {
        private const string ScenePath = "Assets/Scenes/SakuraStudio.unity";

        static SakuraSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(ScenePath) && !EditorApplication.isPlayingOrWillChangePlaymode) CreateStudio();
            };
        }

        [MenuItem("Sakura/Open Studio")]
        public static void CreateStudio()
        {
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var cameraObject = new GameObject("Studio Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.125f, .106f, .145f);
                cameraObject.tag = "MainCamera";
                var studio = new GameObject("Sakura Studio");
                studio.AddComponent<UIDocument>();
                studio.AddComponent<SakuraStudio>();
                EditorSceneManager.SaveScene(scene, ScenePath);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                PlayerSettings.companyName = "Soul of Swords";
                PlayerSettings.productName = "Sakura";
                PlayerSettings.runInBackground = true;
            }
            else EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            UnityEngine.Debug.Log("Sakura native studio is ready. Press Play to preview. Avatar rendering parity is pending.");
        }

        [MenuItem("Sakura/Verify Studio")]
        public static void VerifyStudio()
        {
            if (!EditorApplication.isPlaying) { UnityEngine.Debug.LogError("Enter Play mode before verifying the Studio."); return; }
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            if (studio == null) throw new InvalidOperationException("Studio component missing");
            foreach (string mode in new[] { "Companion", "VTuber", "Agent" })
            {
                studio.SwitchMode(mode);
                if (studio.CurrentMode != mode) throw new InvalidOperationException("Mode switch failed");
            }
            studio.SwitchMode("Companion");
            studio.ShowPanel("Settings");
            if (studio.CurrentPanel != "Settings") throw new InvalidOperationException("Settings failed");
            studio.ShowPanel("Character");
            studio.ToggleTheme();
            studio.ToggleTheme();
            bool rejectedTraversal = false;
            try { SakuraAvatarViewport.ResolveCharacterPath(@"F:\AI\assistant\Sakura", "../../outside.vrm"); }
            catch (ArgumentException) { rejectedTraversal = true; }
            if (!rejectedTraversal) throw new InvalidOperationException("Model path traversal was accepted");
            var viewport = UnityEngine.Object.FindFirstObjectByType<SakuraAvatarViewport>();
            Directory.CreateDirectory(@"C:\AI\Unity-Verification");
            File.WriteAllText(@"C:\AI\Unity-Verification\studio-check.json",
                "{\"unity\":\"" + Application.unityVersion + "\",\"navigation\":true,\"pathTraversalRejected\":true,\"catalogCount\":" + studio.CharacterCount + ",\"avatarRendering\":" + (viewport != null && viewport.HasAvatar ? "true" : "false") + "}");
            ScreenCapture.CaptureScreenshot(@"C:\AI\Unity-Verification\studio.png");
            UnityEngine.Debug.Log("Sakura Studio navigation verified. Report saved to C:/AI/Unity-Verification.");
        }
    }
}
