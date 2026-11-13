using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    public sealed class SakuraLibraryPreview : IDisposable
    {
        public const int MaximumBytes = 8 * 1024 * 1024;
        public Texture2D Texture { get; private set; }
        public int Frames { get; private set; }
        public int Columns { get; private set; }
        public static void ValidateMetadata(int frames, int columns, int width, int height)
        {
            if (frames < 1 || frames > 24 || columns < 1 || columns > 6 || width != columns * 160 || height != ((frames + columns - 1) / columns) * 160)
                throw new ArgumentException("Preview must contain 1–24 frames in 1–6 columns of 160-pixel cells.");
        }
        public static Rect FrameUv(int frame, int frames, int columns)
        {
            if (frames < 1 || frames > 24 || columns < 1 || columns > 6) throw new ArgumentException("Invalid preview frame metadata.");
            ValidateMetadata(frames, columns, columns * 160, ((frames + columns - 1) / columns) * 160);
            if (frame < 0 || frame >= frames) throw new ArgumentOutOfRangeException(nameof(frame));
            int rows = (frames + columns - 1) / columns;
            return new Rect((frame % columns) / (float)columns, 1 - (frame / columns + 1) / (float)rows, 1f / columns, 1f / rows);
        }
        public static SakuraLibraryPreview Decode(byte[] bytes, int frames, int columns)
        {
            if (bytes == null || bytes.Length < 33 || bytes.Length > MaximumBytes || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
                throw new ArgumentException("The protected library preview must be a PNG under 8 MB.");
            int Dimension(int offset)
            {
                uint value = (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3];
                if (value > 4096) throw new ArgumentException("Invalid preview image dimensions.");
                return (int)value;
            }
            ValidateMetadata(frames, columns, Dimension(16), Dimension(20));
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, true)) throw new ArgumentException("The library preview could not be decoded.");
                ValidateMetadata(frames, columns, texture.width, texture.height);
                return new SakuraLibraryPreview { Texture = texture, Frames = frames, Columns = columns };
            }
            catch { DestroyTexture(texture); throw; }
        }
        static void DestroyTexture(Texture2D texture)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture); else UnityEngine.Object.DestroyImmediate(texture);
        }
        public void Dispose() { if (Texture != null) DestroyTexture(Texture); Texture = null; }
    }

    public sealed partial class SakuraBackend
    {
        public IDisposable LoadLibraryPreview(string identifier, Action<SakuraLibraryPreview, string> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            if (!Connected || string.IsNullOrEmpty(identifier) || identifier.Length > 512 || identifier.Any(char.IsControl))
            { completed(null, "Connect and choose a protected library character first."); return null; }
            string route = "/api/unity/library-preview/" + Uri.EscapeDataString(identifier);
            var queue = GetComponent<SakuraLibraryPreviewQueue>();
            if (queue == null) queue = gameObject.AddComponent<SakuraLibraryPreviewQueue>();
            return queue.Enqueue(() => Request(route), completed);
        }
    }

    public sealed class SakuraLibraryPreviewQueue : MonoBehaviour
    {
        sealed class Job : IDisposable
        {
            public Func<UnityWebRequest> CreateRequest;
            public Action<SakuraLibraryPreview, string> Completed;
            public UnityWebRequest Request;
            public bool Cancelled;
            public void Dispose() { Cancelled = true; Request?.Abort(); }
        }
        sealed class BoundedDownload : DownloadHandlerScript
        {
            readonly MemoryStream bytes = new MemoryStream();
            public bool Rejected { get; private set; }
            public BoundedDownload() : base(new byte[64 * 1024]) { }
            protected override void ReceiveContentLengthHeader(ulong length) { if (length > SakuraLibraryPreview.MaximumBytes) Rejected = true; }
            protected override bool ReceiveData(byte[] data, int length)
            {
                if (Rejected || data == null || length < 0 || length > SakuraLibraryPreview.MaximumBytes - bytes.Length) { Rejected = true; return false; }
                bytes.Write(data, 0, length); return true;
            }
            protected override byte[] GetData() => bytes.ToArray();
            public byte[] Bytes => bytes.ToArray();
        }
        readonly Queue<Job> waiting = new Queue<Job>();
        readonly HashSet<Job> active = new HashSet<Job>();
        public IDisposable Enqueue(Func<UnityWebRequest> create, Action<SakuraLibraryPreview, string> completed)
        {
            var job = new Job { CreateRequest = create, Completed = completed }; waiting.Enqueue(job); Pump(); return job;
        }
        void Pump()
        {
            if (!isActiveAndEnabled) return;
            while (active.Count < 2 && waiting.Count > 0)
            {
                var job = waiting.Dequeue(); if (job.Cancelled) continue;
                active.Add(job); StartCoroutine(Download(job));
            }
        }
        IEnumerator Download(Job job)
        {
            string failure = null; UnityWebRequestAsyncOperation operation = null;
            var download = new BoundedDownload();
            try
            {
                job.Request = job.CreateRequest(); job.Request.timeout = 30;
                job.Request.downloadHandler?.Dispose(); job.Request.downloadHandler = download;
                operation = job.Request.SendWebRequest();
            }
            catch (Exception error) { failure = error.Message; }
            if (operation != null) yield return operation;
            SakuraLibraryPreview preview = null;
            try
            {
                if (!job.Cancelled && failure == null)
                {
                    if (download.Rejected) throw new InvalidDataException("Library preview exceeds 8 MB.");
                    if (job.Request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException("Library preview is unavailable.");
                    if (!int.TryParse(job.Request.GetResponseHeader("X-Sakura-Preview-Frames"), out int frames) || !int.TryParse(job.Request.GetResponseHeader("X-Sakura-Preview-Columns"), out int columns)) throw new InvalidDataException("Library preview metadata is missing.");
                    preview = SakuraLibraryPreview.Decode(download.Bytes, frames, columns);
                }
            }
            catch (Exception error) { failure = error.Message; }
            try
            {
                if (job.Cancelled) preview?.Dispose();
                else { job.Completed(preview, failure); preview = null; }
            }
            finally
            {
                preview?.Dispose(); job.Request?.Dispose(); job.Request = null; download.Dispose(); active.Remove(job); Pump();
            }
        }
        void OnDisable()
        {
            foreach (var job in waiting) job.Dispose(); waiting.Clear();
            foreach (var job in active.ToArray()) { job.Dispose(); job.Request?.Dispose(); job.Request = null; }
            StopAllCoroutines(); active.Clear();
        }
    }
}
