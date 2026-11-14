using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using WebViewToolkit.UIToolkit;

namespace Sakura.Unity.Editor
{
    public static class SakuraBrowserLiveChecks
    {
        public static async Task Verify()
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first.");
            var document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            var view = new WebViewElement { InitialUrl = "about:blank" };
            view.style.position = Position.Absolute; view.style.left = 400; view.style.top = 120;
            view.style.width = 480; view.style.height = 320;
            string message = null;
            view.MessageReceived += value => message = value;
            document.rootVisualElement.Add(view);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!view.IsReady && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (!view.IsReady) throw new Exception("Native browser was not created within 20 seconds.");
                const string html = "<html><body style='margin:0;background:#ff0088'><button id='test' style='width:480px;height:320px' onclick=\"window.chrome.webview.postMessage('clicked')\">Sakura browser interaction check</button></body></html>";
                deadline = DateTime.UtcNow.AddSeconds(20);
                bool accepted = false;
                while (!accepted && DateTime.UtcNow < deadline)
                {
                    accepted = view.WebView.NavigateToString(html);
                    if (!accepted) await Task.Delay(200);
                }
                if (!accepted) throw new Exception("Native browser rejected the test document after its initialization grace period.");
                await Task.Delay(2500);
                if (view.WebView.Texture == null) throw new Exception("Native browser has no render texture.");
                if (!view.WebView.ExecuteScript("document.getElementById('test').click()")) throw new Exception("Native browser rejected script interaction.");
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (message == null && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (message == null || !message.Contains("clicked")) throw new Exception("Browser DOM interaction did not return its message.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\browser-live.png");
            }
            finally
            {
                var instance = view.WebView;
                view.RemoveFromHierarchy();
                if (instance != null && !instance.IsDestroyed) throw new Exception("Browser instance survived panel teardown.");
            }
            Debug.Log("PASS: embedded native browser creation, document load, render texture, DOM event message and detach cleanup. Physical mouse/keyboard and a task-owned loopback URL remain separate checks.");
        }
    }
}
