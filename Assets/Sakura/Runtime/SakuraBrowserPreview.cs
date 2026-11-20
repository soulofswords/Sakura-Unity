#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UIElements;
using WebViewToolkit.UIToolkit;

namespace Sakura.Unity
{
    public sealed class SakuraBrowserPreview : IDisposable
    {
        [DllImport("WebViewToolkit", CallingConvention = CallingConvention.Cdecl)] static extern int WebViewToolkit_RequestPreview(uint handle);
        [DllImport("WebViewToolkit", CallingConvention = CallingConvention.Cdecl)] static extern int WebViewToolkit_ReadPreview(uint handle, [Out] byte[] destination, int capacity);
        static readonly List<SakuraBrowserPreview> active = new List<SakuraBrowserPreview>();
        readonly WebViewElement view;
        readonly Image image;
        readonly Action<string> reportError;
        Texture2D texture;
        uint handle;
        bool pending, disposed;
        double requested, nextUpdate;
        public Texture2D Texture => texture;
        public string LastError { get; private set; }
        public SakuraBrowserPreview(WebViewElement view, Action<string> reportError = null)
        {
            this.view = view; this.reportError = reportError;
            image = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.StretchToFill };
            image.style.position = Position.Absolute; image.style.left = image.style.right = image.style.top = image.style.bottom = 0;
            view.Add(image); active.Add(this);
            view.focusable = true;
            view.RegisterCallback<MouseDownEvent>(_ => view.Focus());
            view.RegisterCallback<KeyDownEvent>(KeyDown);
            view.RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
        }
        void KeyDown(KeyDownEvent key)
        {
            if (disposed || !view.IsReady) return;
            string text = null, command = "";
            if ((key.ctrlKey || key.commandKey) && key.keyCode == KeyCode.V) text = GUIUtility.systemCopyBuffer;
            else if ((key.ctrlKey || key.commandKey) && key.keyCode == KeyCode.A) command = "select";
            else if (key.ctrlKey || key.commandKey || key.altKey) return;
            else if (key.keyCode == KeyCode.Backspace) command = "backspace";
            else if (key.keyCode == KeyCode.Delete) command = "delete";
            else if (key.keyCode == KeyCode.Return || key.keyCode == KeyCode.KeypadEnter) command = "enter";
            else if (key.character >= ' ') text = key.character.ToString();
            else return;
            if (text != null && text.Length > 10000) text = text.Substring(0, 10000);
            string script = "(()=>{const e=document.activeElement;if(!e||e.disabled||e.readOnly)return;const t=" + JsonConvert.SerializeObject(text) + ",c=" + JsonConvert.SerializeObject(command)
                + ";if(!['INPUT','TEXTAREA'].includes(e.tagName))return;if(c==='select'){e.select();return;}if(c==='enter'&&e.tagName!=='TEXTAREA'){e.form?.requestSubmit();return;}"
                + "let a=e.selectionStart??e.value.length,b=e.selectionEnd??a,v=e.value;if(c==='backspace'&&a===b)a=Math.max(0,a-1);if(c==='delete'&&a===b)b=Math.min(v.length,b+1);"
                + "const add=c==='enter'?'\\n':(t??'');const setter=Object.getOwnPropertyDescriptor(e.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype,'value').set;"
                + "setter.call(e,v.slice(0,a)+add+v.slice(b));try{e.setSelectionRange(a+add.length,a+add.length);}catch(_){}e.dispatchEvent(new Event('input',{bubbles:true}));})()";
            view.WebView.ExecuteScript(script); key.StopPropagation(); key.PreventDefault();
        }
        public static void UpdateAll()
        {
            for (int index = active.Count - 1; index >= 0; index--) active[index].Tick();
        }
        void Tick()
        {
            if (disposed || view.panel == null || !view.IsReady || Time.realtimeSinceStartupAsDouble < nextUpdate) return;
            nextUpdate = Time.realtimeSinceStartupAsDouble + .1;
            try
            {
                handle = view.WebView.Handle;
                if (!pending)
                {
                    pending = WebViewToolkit_RequestPreview(handle) == 1;
                    requested = Time.realtimeSinceStartupAsDouble;
                    if (!pending) WebViewToolkit_ReadPreview(handle, null, -1);
                    return;
                }
                int count = WebViewToolkit_ReadPreview(handle, null, 0);
                if (count == 0 && Time.realtimeSinceStartupAsDouble - requested < 10) return;
                if (count <= 0 || count > 16 * 1024 * 1024)
                {
                    WebViewToolkit_ReadPreview(handle, null, -1); pending = false;
                    LastError = "Browser page preview is unavailable. Check that the local server is running.";
                    reportError?.Invoke(LastError); return;
                }
                var bytes = new byte[count];
                int received = WebViewToolkit_ReadPreview(handle, bytes, count); pending = false;
                if (received != count) return;
                var next = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(next, bytes) || next.width > 4096 || next.height > 4096)
                { UnityEngine.Object.Destroy(next); LastError = "Browser preview exceeds the supported size."; reportError?.Invoke(LastError); return; }
                var previous = texture; texture = next; image.image = next;
                if (previous != null) UnityEngine.Object.Destroy(previous);
            }
            catch (Exception error) { LastError = error.ToString(); reportError?.Invoke("Browser preview failed: " + error.Message); Dispose(); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; active.Remove(this);
            view.UnregisterCallback<KeyDownEvent>(KeyDown);
            if (handle != 0) try { WebViewToolkit_ReadPreview(handle, null, -1); } catch (Exception) { }
            image.RemoveFromHierarchy();
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
#endif
