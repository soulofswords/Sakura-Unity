using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using System.Threading.Tasks;

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

        public static void VerifyReferenceTypography()
        {
            var document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            var root = document.rootVisualElement;
            float titleHeight = root.Q<Label>(className: "card-title").layout.height;
            if (Math.Abs(titleHeight - 19.6f) > 2)
                throw new InvalidOperationException("Card title differs from the current app. Native height " + titleHeight + ", reference 19.6px.");
            if (SakuraCloudLibrary.ValidateAccount("bad", "", false) == null)
                throw new InvalidOperationException("Invalid account input was accepted.");
            if (SakuraCloudLibrary.ValidateAccount("owner@example.com", "long-password", false) != null)
                throw new InvalidOperationException("Valid account input was rejected.");
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            studio.SwitchMode("Companion");
            studio.ToggleChrome();
            if (!studio.ControlsHidden) throw new InvalidOperationException("Double-click controls mode failed.");
            studio.ToggleChrome();
            studio.SwitchMode("VTuber"); studio.ToggleChrome();
            if (!root.ClassListContains("controls-hidden") || !root.ClassListContains("vtuber"))
                throw new InvalidOperationException("Avatar-only VTuber state failed.");
            studio.ToggleChrome(); studio.SwitchMode("Companion");
        }

        public static async Task CaptureReference(string outputPath, int width = 1440, int height = 900)
        {
            var document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            var settings = document.panelSettings;
            var previous = settings.targetTexture;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            target.Create(); settings.targetTexture = target;
            try
            {
                await Task.Delay(500);
                // Rebind after the target change and viewport resize have completed.
                document.rootVisualElement.Q("avatar-stage")?.Q<Image>()?.MarkDirtyRepaint();
                await Task.Delay(150);
                var active = RenderTexture.active;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(outputPath, image.EncodeToPNG());
                }
                finally { RenderTexture.active = active; UnityEngine.Object.Destroy(image); }
            }
            finally { settings.targetTexture = previous; target.Release(); UnityEngine.Object.Destroy(target); }
        }

        public static async Task VerifyDoubleClick()
        {
            var studio = UnityEngine.Object.FindFirstObjectByType<SakuraStudio>();
            var root = studio.GetComponent<UIDocument>().rootVisualElement;
            var stage = root.Q("avatar-stage");
            var avatar = stage.Q<Image>();
            var chat = root.Q("chat-panel");
            var panel = root.Q("companion-panel");
            string previousMode = studio.CurrentMode;
            string previousPanel = studio.CurrentPanel;
            bool previousHidden = studio.ControlsHidden;
            try
            {
                studio.SwitchMode("Companion");
                SendStageClick(stage, 1);
                if (studio.ControlsHidden) throw new InvalidOperationException("Single click hid the interface.");
                SendStageClick(stage, 2);
                await Task.Delay(150);
                AssertDisplay(root.Q("top-bar"), false);
                AssertDisplay(root.Q("companion-dock"), false);
                AssertDisplay(panel, false);
                AssertDisplay(root.Q("companion-statusbar"), false);
                AssertDisplay(chat, true);
                AssertDisplay(stage, true);
                AssertDisplay(avatar, true);
                SendStageClick(avatar, 2);
                await Task.Delay(150);
                AssertDisplay(panel, true);
                AssertDisplay(root.Q("companion-dock"), true);
                panel.AddToClassList("panel-closed");
                SendStageClick(stage, 2);
                SendStageClick(stage, 2);
                await Task.Delay(150);
                AssertDisplay(panel, false);
                studio.SwitchMode("VTuber");
                SendStageClick(avatar, 2);
                await Task.Delay(150);
                AssertDisplay(chat, false);
                AssertDisplay(panel, false);
                AssertDisplay(root.Q("companion-dock"), false);
                AssertDisplay(avatar, true);
                SendStageClick(stage, 2);
                await Task.Delay(150);
                AssertDisplay(panel, true);
                studio.SwitchMode("Agent");
                SendStageClick(stage, 2);
                if (studio.ControlsHidden) throw new InvalidOperationException("Agent controls were hidden.");
                File.WriteAllText(@"C:\AI\Unity-Verification\double-click-check.json",
                    "{\"companionChatRetained\":true,\"vtuberAvatarOnly\":true,\"backgroundAndAvatarToggle\":true,\"restoreControls\":true,\"singleClickUnchanged\":true,\"closedPanelPreserved\":true,\"agentUnaffected\":true}");
            }
            finally
            {
                studio.SwitchMode(previousMode);
                studio.ShowPanel(previousPanel);
                if (previousHidden) studio.ToggleChrome();
            }
        }

        private static void SendStageClick(VisualElement target, int count)
        {
            var input = new UnityEngine.Event { type = EventType.MouseUp, button = 0, clickCount = count };
            using (var click = ClickEvent.GetPooled(input))
            {
                click.target = target;
                target.SendEvent(click);
            }
        }

        private static void AssertDisplay(VisualElement element, bool visible)
        {
            if (element == null || (element.resolvedStyle.display != DisplayStyle.None) != visible)
                throw new InvalidOperationException("Unexpected visibility for " + element?.name);
        }
    }
}
