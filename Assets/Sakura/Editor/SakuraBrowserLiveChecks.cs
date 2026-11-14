using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
            using var server = new LocalPage();
            var view = new WebViewElement { InitialUrl = server.Url };
            using var preview = new SakuraBrowserPreview(view);
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
                deadline = DateTime.UtcNow.AddSeconds(20);
                while (server.Requests == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (server.Requests == 0) throw new Exception("Embedded browser did not load the loopback page.");
                await Task.Delay(2500);
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (message != "ready" && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (message != "ready")
                {
                    // A reused native browser can occasionally miss the first document's
                    // load message. Reload once before declaring the bridge unavailable.
                    view.WebView.Navigate(server.Url);
                    deadline = DateTime.UtcNow.AddSeconds(10);
                    while (message != "ready" && DateTime.UtcNow < deadline) await Task.Delay(100);
                }
                if (message != "ready") throw new Exception("Loopback document loaded but its ready/message handshake did not arrive.");
                deadline = DateTime.UtcNow.AddSeconds(15);
                while (preview.Texture == null && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (preview.Texture == null) throw new Exception("Browser returned no preview: " + preview.LastError);
                var texture = preview.Texture;
                var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
                var previous = RenderTexture.active;
                var pixels = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                try
                {
                    Graphics.Blit(texture, target); RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    int visible = 0;
                    foreach (var color in pixels.GetPixels32()) if (color.r > 80 || color.g > 80 || color.b > 80) visible++;
                    File.WriteAllBytes(@"C:\AI\Unity-Verification\browser-native-texture.png", pixels.EncodeToPNG());
                    if (visible < texture.width * texture.height / 4) throw new Exception("Browser texture is mostly blank despite successful DOM operations.");
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(pixels); }
                var native = view.WebView;
                message = null;
                if (!native.ExecuteScript("document.getElementById('test').click();"))
                    throw new Exception("Native browser rejected a DOM script before pointer testing.");
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (message == null && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (message != "clicked") throw new Exception("DOM script ran but its JavaScript message did not return.");
                message = null;
                if (!native.SendMouseEvent(WebViewToolkit.Native.MouseEventType.Down, WebViewToolkit.Native.MouseButton.Left, .5f, .5f)
                    || !native.SendMouseEvent(WebViewToolkit.Native.MouseEventType.Up, WebViewToolkit.Native.MouseButton.Left, .5f, .5f))
                    throw new Exception("Native browser rejected pointer interaction.");
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (message == null && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (message != "clicked") throw new Exception("DOM messaging passed, but injected pointer input did not activate the page control.");
                message = null;
                view.WebView.ExecuteScript("const field=document.createElement('input');field.id='typing';field.style='position:absolute;top:0;left:0';field.oninput=()=>window.chrome.webview.postMessage('typed:'+field.value);document.body.append(field);field.focus();");
                await Task.Delay(300);
                using (var keyboard = KeyDownEvent.GetPooled(new UnityEngine.Event { type = EventType.KeyDown, keyCode = KeyCode.A, character = 'a' }))
                { keyboard.target = view; view.SendEvent(keyboard); }
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (message == null && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (message == null || !message.Contains("typed:a")) throw new Exception("Typing did not update the embedded page input.");
                await SakuraSetup.CaptureReference(@"C:\AI\Unity-Verification\browser-live.png");
            }
            finally
            {
                var instance = view.WebView;
                view.RemoveFromHierarchy();
                if (instance != null && !instance.IsDestroyed) throw new Exception("Browser instance survived panel teardown.");
            }
            Debug.Log("PASS: embedded native browser creation, document load, render texture, DOM event message and detach cleanup. Loopback HTTP page, visible pixels and native pointer input verified. Physical keyboard and task-policy flow remain separate checks.");
        }
        sealed class LocalPage : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            bool stopped;
            public int Requests;
            public string Url { get; }
            public LocalPage()
            {
                listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!stopped)
                        {
                            using var client = await listener.AcceptTcpClientAsync();
                            using var stream = client.GetStream(); stream.ReadTimeout = 2000;
                            var buffer = new byte[4096]; int count = stream.Read(buffer, 0, buffer.Length);
                            if (count == 0) continue;
                            Interlocked.Increment(ref Requests);
                            byte[] body = Encoding.UTF8.GetBytes("<html><body style='margin:0;background:#ff0088'><button id='test' style='width:480px;height:320px' onclick=\"window.chrome.webview.postMessage('clicked')\">Sakura browser interaction check</button><script>window.addEventListener('load',()=>window.chrome.webview.postMessage('ready'))</script></body></html>");
                            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length);
                        }
                    }
                    catch (Exception) { if (!stopped) throw; }
                });
            }
            public void Dispose() { stopped = true; listener.Stop(); }
        }
    }
}
