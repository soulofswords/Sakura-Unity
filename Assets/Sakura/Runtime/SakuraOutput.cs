using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Newtonsoft.Json.Linq;

namespace Sakura.Unity
{
    public sealed class SakuraOutput : MonoBehaviour
    {
        public enum Quality { Low, Balanced, High }
        public event Action Changed;
        public SakuraBackend Backend { get; private set; }
        public SakuraAvatarViewport Viewport { get; private set; }
        public string Status { get; private set; } = "Outputs are off.";
        public Quality Preset { get; private set; } = Quality.Balanced;
        public int FrameRate { get; private set; } = 30;
        public bool Recording => recorder != null;
        public bool SavingRecording { get; private set; }
        public string EncoderPath { get; set; }
        public long CapturedFrames { get; private set; }
        public long SkippedFrames { get; private set; }
        public string MediaFolder => Path.Combine(Application.persistentDataPath, "Captures");
        public sealed class Channel
        {
            public readonly string Id, Header;
            public bool Enabled { get; internal set; }
            public bool Busy { get; internal set; }
            public long Frames { get; internal set; }
            internal string Token;
            internal int Generation;
            internal Channel(string id, string header) { Id = id; Header = header; }
        }
        public readonly Channel[] Channels = { new Channel("spout", "x-spout-session"), new Channel("ndi", "x-ndi-session"), new Channel("virtual-camera", "x-camera-session") };
        private RenderTexture capture;
        private Material backgroundComposite;
        private bool reading, screenshot, dead;
        private float nextFrame;
        private Recorder recorder;
        private readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        public void Initialize(SakuraBackend backend, SakuraAvatarViewport viewport)
        {
            Backend = backend; Viewport = viewport; EncoderPath = FindEncoder();
            SetQuality((Quality)Mathf.Clamp(PlayerPrefs.GetInt("sakura.output.quality", 1), 0, 2));
        }
        public static Vector2Int Dimensions(int width, int height, int maxSide)
        {
            if (width <= 0 || height <= 0 || maxSide < 2) throw new ArgumentException("Invalid output dimensions.");
            float scale = Mathf.Min(1, maxSide / (float)Mathf.Max(width, height));
            return new Vector2Int(Mathf.Max(2, ((int)(width * scale)) & ~1), Mathf.Max(2, ((int)(height * scale)) & ~1));
        }
        public void SetQuality(Quality quality)
        {
            if (!Enum.IsDefined(typeof(Quality), quality)) throw new ArgumentException("Invalid quality preset.");
            if (Recording || SavingRecording) { Report("Stop recording before changing quality."); return; }
            Preset = quality; PlayerPrefs.SetInt("sakura.output.quality", (int)quality);
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = quality == Quality.High ? 60 : 30;
            QualitySettings.shadows = quality == Quality.Low ? ShadowQuality.Disable : ShadowQuality.All;
            QualitySettings.shadowResolution = quality == Quality.High ? ShadowResolution.High : ShadowResolution.Low;
            QualitySettings.lodBias = quality == Quality.Low ? .7f : quality == Quality.High ? 1.5f : 1f;
            Changed?.Invoke();
        }
        public void SetFrameRate(int fps)
        {
            if (fps != 15 && fps != 30 && fps != 60) throw new ArgumentException("Choose 15, 30 or 60 FPS.");
            if (Recording || SavingRecording) { Report("Stop recording before changing the capture rate."); return; }
            foreach (var channel in Channels) if (channel.Enabled || channel.Busy) { Report("Stop streaming before changing the capture rate."); return; }
            FrameRate = fps; Changed?.Invoke();
        }
        public void Configure(string id, bool enabled, string sender = "Sakura")
        {
            var channel = Array.Find(Channels, value => value.Id == id);
            if (channel == null) throw new ArgumentException("Unknown output.");
            if (channel.Busy) { Report("Wait for the current output request."); return; }
            if (enabled && (Viewport == null || !Viewport.HasAvatar)) { Report("Load a character before starting output."); return; }
            if (enabled && id == "virtual-camera" && recorder != null && (recorder.Size.x > 1280 || recorder.Size.y > 1280)) { Report("Stop the high-resolution recording before enabling the virtual camera."); return; }
            if (enabled && !SystemInfo.supportsAsyncGPUReadback) { Report("This GPU does not support asynchronous capture."); return; }
            channel.Busy = true; int generation = ++channel.Generation;
            Backend.Api("/api/" + id + "/configure", new JObject { ["enabled"] = enabled, ["name"] = sender, ["fps"] = FrameRate }.ToString(), (json, error) =>
            {
                if (dead || generation != channel.Generation) return;
                channel.Busy = false; channel.Enabled = false; channel.Token = null;
                if (error != null) { Report(error); return; }
                try
                {
                    var reply = JObject.Parse(json); channel.Enabled = reply.Value<bool>("enabled"); channel.Token = reply.Value<string>("token");
                    if (channel.Enabled && string.IsNullOrEmpty(channel.Token)) { channel.Enabled = false; Report("Output returned no session; enable it again."); return; }
                    Report(channel.Enabled ? id + " sender started. Waiting for frames." : id + " stopped.");
                }
                catch (Exception) { channel.Enabled = false; Report("The output returned an invalid response."); }
            }); Changed?.Invoke();
        }
        public void Screenshot()
        {
            if (Viewport == null || !Viewport.HasAvatar) { Report("Load a character before taking a screenshot."); return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { Report("This GPU does not support asynchronous capture."); return; }
            screenshot = true; Report("Capturing transparent PNG…");
        }
        private void Report(string value) { Status = value; Changed?.Invoke(); }
        private void Update()
        {
            while (actions.TryDequeue(out var action)) action();
            if (recorder != null && recorder.Finished) StopRecording();
            if (Viewport == null || Viewport.OutputTexture == null || !Viewport.HasAvatar) return;
            bool stream = false; foreach (var channel in Channels) if (channel.Enabled && !channel.Busy) stream = true;
            if (!screenshot && !Recording && !stream) return;
            if (Time.unscaledTime < nextFrame) return;
            nextFrame = Time.unscaledTime + 1f / FrameRate;
            if (reading) { SkippedFrames++; return; }
            var source = Viewport.OutputTexture;
            int side = Preset == Quality.Low ? 640 : Preset == Quality.High ? 1920 : 1280;
            // The source virtual camera accepts dimensions no larger than 1280.
            foreach (var channel in Channels) if (channel.Id == "virtual-camera" && channel.Enabled) side = Mathf.Min(side, 1280);
            var size = recorder != null ? recorder.Size : Dimensions(source.width, source.height, side);
            if (capture == null || capture.width != size.x || capture.height != size.y)
            {
                if (capture != null) { capture.Release(); Destroy(capture); }
                capture = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGB32); capture.Create();
            }
            bool shot = screenshot; screenshot = false; reading = true;
            var backdrop = GetComponent<SakuraBackdrop>();
            if (backdrop != null && backdrop.SolidOutput)
            {
                if (backgroundComposite == null) backgroundComposite = new Material(Resources.Load<Shader>("Sakura/BackdropComposite"));
                backgroundComposite.SetColor("_Background", backdrop.Colour);
                Graphics.Blit(source, capture, backgroundComposite);
            }
            else Graphics.Blit(source, capture);
            AsyncGPUReadback.Request(capture, 0, TextureFormat.RGBA32, request =>
            {
                reading = false;
                if (dead) { if (capture != null) { capture.Release(); Destroy(capture); capture = null; } return; }
                if (request.hasError) { if (shot) Report("GPU screenshot readback failed."); else StopFailedOutputs("GPU output readback failed."); return; }
                var bytes = request.GetData<byte>().ToArray();
                // GPU readback is bottom-up on OpenGL, top-down on D3D/Metal.
                if (SystemInfo.graphicsUVStartsAtTop) FlipRows(bytes, size.x, size.y);
                CapturedFrames++;
                if (recorder != null && recorder.Size == size) recorder.Submit(bytes);
                if (shot) SavePng(bytes, size.x, size.y);
                foreach (var channel in Channels)
                {
                    if (!channel.Enabled || channel.Busy) continue;
                    channel.Busy = true; int generation = channel.Generation;
                    Backend.UploadOutput("/api/" + channel.Id + "/frame?width=" + size.x + "&height=" + size.y, bytes, "application/octet-stream", channel.Header, channel.Token, (json, error) =>
                    {
                        if (dead || generation != channel.Generation) return;
                        channel.Busy = false;
                        if (error != null) { channel.Enabled = false; channel.Token = null; Report(channel.Id + ": " + error); return; }
                        channel.Frames++; Changed?.Invoke();
                    });
                }
            });
        }
        public static void FlipRows(byte[] bytes, int width, int height)
        {
            int stride = checked(width * 4);
            if (width < 1 || height < 1 || bytes == null || bytes.Length != checked(stride * height)) throw new ArgumentException("Invalid RGBA frame.");
            for (int y = 0; y < height / 2; y++)
                for (int x = 0; x < stride; x++) { int a = y * stride + x, b = (height - 1 - y) * stride + x; byte swap = bytes[a]; bytes[a] = bytes[b]; bytes[b] = swap; }
        }
        private void SavePng(byte[] bytes, int width, int height)
        {
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false); texture.LoadRawTextureData(bytes); texture.Apply();
                // Native media also works without the Python backend, including on mobile.
                Directory.CreateDirectory(MediaFolder); string path = UniqueMediaPath(MediaFolder, ".png");
                File.WriteAllBytes(path, texture.EncodeToPNG()); Report("Screenshot saved: " + path);
            }
            catch (Exception error) { Report("Screenshot failed: " + error.Message); }
            finally { if (texture != null) Destroy(texture); }
        }
        public static string UniqueMediaPath(string folder, string extension) => Path.Combine(folder, "sakura-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + extension);
        public void StartRecording()
        {
            if (Recording || SavingRecording) return;
            if (Viewport == null || !Viewport.HasAvatar || Viewport.OutputTexture == null) { Report("Load a character before recording."); return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { Report("This GPU does not support asynchronous capture."); return; }
            if (Application.isMobilePlatform) { Report("Native video recording requires a desktop FFmpeg encoder. PNG capture is available on mobile."); return; }
            try
            {
                if (string.IsNullOrWhiteSpace(EncoderPath) || !File.Exists(EncoderPath)) throw new FileNotFoundException("Choose an installed FFmpeg executable to record WebM.");
                Directory.CreateDirectory(MediaFolder); var source = Viewport.OutputTexture;
                int side = Preset == Quality.Low ? 640 : Preset == Quality.High ? 1920 : 1280;
                foreach (var channel in Channels) if (channel.Id == "virtual-camera" && channel.Enabled) side = Mathf.Min(side, 1280);
                recorder = new Recorder(EncoderPath, UniqueMediaPath(MediaFolder, ".webm"), Dimensions(source.width, source.height, side), FrameRate);
                Report("Recording transparent WebM. Video only; frames are repeated if capture falls behind.");
            }
            catch (Exception error) { Report("Recording could not start: " + error.Message); }
        }
        public void StopRecording()
        {
            var active = recorder; if (active == null) return; recorder = null; SavingRecording = true; Report("Finishing WebM…");
            active.Stop((path, error) => actions.Enqueue(() => { SavingRecording = false; Report(error == null ? "Recording saved: " + path : "Recording failed: " + error); }));
        }
        private void StopFailedOutputs(string error)
        {
            foreach (var channel in Channels) if (channel.Enabled && !channel.Busy) Configure(channel.Id, false);
            StopRecording(); Report(error);
        }
        private void OnDestroy()
        {
            if (backgroundComposite != null) Destroy(backgroundComposite);
            dead = true; var active = recorder; recorder = null; active?.Stop((_, __) => { });
            foreach (var channel in Channels) if (channel.Enabled && Backend != null && Backend.Connected) Backend.Api("/api/" + channel.Id + "/configure", "{\"enabled\":false}", (_, __) => { });
            // A readback still owns its render texture until its callback finishes.
            if (!reading && capture != null) { capture.Release(); Destroy(capture); capture = null; }
        }
        public static string FindEncoder()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { string path = Path.Combine(folder.Trim('"'), Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer ? "ffmpeg.exe" : "ffmpeg"); if (File.Exists(path)) return path; } catch (ArgumentException) { }
            }
            return "";
        }
        private sealed class Recorder
        {
            public readonly Vector2Int Size;
            private readonly Process process;
            private readonly Thread worker;
            private readonly object gate = new object();
            private readonly string path;
            private readonly int fps;
            private byte[] latest;
            private volatile bool stopping;
            public bool Finished { get; private set; }
            private string failure;
            private Action<string, string> completed;
            public Recorder(string encoder, string output, Vector2Int size, int rate)
            {
                Size = size; path = output; fps = rate;
                if (path.Contains("\"")) throw new ArgumentException("The capture folder cannot contain quotes.");
                var info = new ProcessStartInfo(encoder) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
                // No shell is involved. Dimensions/rate are integers and the generated output path contains no quotes.
                info.Arguments = "-hide_banner -loglevel error -n -f rawvideo -pixel_format rgba -video_size " + size.x + "x" + size.y + " -framerate " + fps + " -i pipe:0 -vf vflip -an -c:v libvpx-vp9 -pix_fmt yuva420p -auto-alt-ref 0 -deadline realtime -cpu-used 8 -b:v 0 -crf 30 \"" + path + "\"";
                process = new Process { StartInfo = info };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) failure = e.Data.Length > 500 ? e.Data.Substring(0, 500) : e.Data; };
                process.Start(); process.BeginErrorReadLine();
                worker = new Thread(Write) { IsBackground = true, Name = "Sakura WebM encoder" }; worker.Start();
            }
            public void Submit(byte[] bytes) { lock (gate) { latest = bytes; Monitor.PulseAll(gate); } }
            public void Stop(Action<string, string> callback)
            {
                bool done; lock (gate) { completed = callback; stopping = true; done = Finished; Monitor.PulseAll(gate); }
                if (done) { callback(path, failure); return; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (worker.Join(20000)) return;
                    try { failure = "Encoder stopped responding; the WebM may be incomplete."; if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                });
            }
            private void Write()
            {
                try
                {
                    lock (gate) while (latest == null && !stopping) Monitor.Wait(gate, 100);
                    var clock = Stopwatch.StartNew(); long frame = 0;
                    while (!stopping)
                    {
                        byte[] bytes; lock (gate) bytes = latest;
                        if (bytes != null) process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        frame++;
                        int wait = (int)(frame * 1000L / fps - clock.ElapsedMilliseconds);
                        if (wait > 0) Thread.Sleep(wait);
                        // Discard scheduling debt if the encoder cannot sustain the target rate.
                        if (wait < -1000) frame = clock.ElapsedMilliseconds * fps / 1000;
                    }
                    process.StandardInput.Close();
                    if (!process.WaitForExit(15000)) { process.Kill(); failure = "Encoder did not finish in time; the WebM may be incomplete."; }
                    else { process.WaitForExit(); if (process.ExitCode != 0 && failure == null) failure = "Encoder exited with code " + process.ExitCode; }
                    if (!File.Exists(path) || new FileInfo(path).Length == 0) failure = failure ?? "No video frames were recorded.";
                }
                catch (Exception error) { failure = error.Message; try { if (!process.HasExited) process.Kill(); } catch (Exception) { } }
                finally { process.Dispose(); Action<string, string> callback; lock (gate) { Finished = true; callback = completed; } callback?.Invoke(path, failure); }
            }
        }
    }
}
