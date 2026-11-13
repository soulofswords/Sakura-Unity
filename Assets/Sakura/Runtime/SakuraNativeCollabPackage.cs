using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    public static class SakuraNativeCollabPackage
    {
        public const long MaximumBytes = 300L * 1024 * 1024;
        public const long MaximumExpandedBytes = 600L * 1024 * 1024;
        public static string SessionFolder(string token)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(token ?? "", @"\A[a-f0-9]{32}\z")) throw new ArgumentException("Invalid collab session token.");
            return Path.Combine(Application.temporaryCachePath, "native-collab", token);
        }
        public static string SafeEntry(string name)
        {
            name = (name ?? "").Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.Length > 512 || name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(part => string.IsNullOrEmpty(part) || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" "))) throw new InvalidDataException("Unsafe path in collab package.");
            return name;
        }
        public static string Extract(string archivePath, string entry, string sessionFolder, CancellationToken cancellation)
        {
            entry = SafeEntry(entry);
            if (!entry.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a VRM, PMX or Live2D collab model.");
            if (new FileInfo(archivePath).Length > MaximumBytes) throw new InvalidDataException("Collab package exceeds 300 MB.");
            string root = Path.Combine(sessionFolder, "models");
            try
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    var files = archive.Entries.Where(file => !file.FullName.EndsWith("/", StringComparison.Ordinal)).ToArray();
                    if (files.Length == 0 || files.Length > 150 || files.Sum(file => file.Length) > MaximumExpandedBytes) throw new InvalidDataException("Collab package exceeds 150 files or 600 MB expanded.");
                    var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        string name = SafeEntry(file.FullName);
                        if (!names.Add(name) || ((file.ExternalAttributes >> 16) & 0xf000) == 0xa000 || new[] { ".html", ".htm", ".xhtml", ".js", ".mjs", ".exe", ".dll", ".bat", ".cmd", ".ps1" }.Contains(Path.GetExtension(name).ToLowerInvariant())) throw new InvalidDataException("Collab package has duplicate paths, links or executable files.");
                    }
                    if (!names.Contains(entry)) throw new InvalidDataException("The model entry is missing from the collab package.");
                    Directory.CreateDirectory(root); long written = 0; var buffer = new byte[64 * 1024];
                    foreach (var file in files)
                    {
                        cancellation.ThrowIfCancellationRequested(); string target = SakuraModelImport.ResolveFile(root, SafeEntry(file.FullName)); Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = file.Open()) using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        {
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellation.ThrowIfCancellationRequested(); written += count;
                                if (written > MaximumExpandedBytes || output.Length + count > file.Length) throw new InvalidDataException("Collab package expanded beyond its declared size.");
                                output.Write(buffer, 0, count);
                            }
                            if (output.Length != file.Length) throw new InvalidDataException("Incomplete collab package entry.");
                        }
                    }
                }
                return SakuraModelImport.ResolveFile(root, entry);
            }
            catch { if (Directory.Exists(root)) Directory.Delete(root, true); throw; }
        }
        public static void DeleteSession(string token)
        {
            string folder = SessionFolder(token); if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    public sealed partial class SakuraBackend
    {
        internal Task<(string file, string entry, string name)> DownloadNativeCollabPackage(string relative, string destination, CancellationToken cancellation)
        {
            SakuraAvatarViewport.ResolveCharacterPath(SourceFolder, relative);
            var answer = new TaskCompletionSource<(string, string, string)>();
            StartCoroutine(NativeCollabDownload(relative, destination, cancellation, answer)); return answer.Task;
        }
        IEnumerator NativeCollabDownload(string relative, string destination, CancellationToken cancellation, TaskCompletionSource<(string, string, string)> answer)
        {
            UnityWebRequest request = null; UnityWebRequestAsyncOperation operation = null; Exception failure = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                request = Request("/api/collab/package"); request.timeout = 180; request.method = "POST";
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(new JObject { ["path"] = relative }.ToString())); request.SetRequestHeader("Content-Type", "application/json");
                request.downloadHandler.Dispose(); request.downloadHandler = new DownloadHandlerFile(destination) { removeFileOnAbort = true };
                operation = request.SendWebRequest();
            }
            catch (Exception error) { failure = error; }
            if (operation != null)
                while (!operation.isDone && !cancellation.IsCancellationRequested && request.downloadedBytes <= (ulong)SakuraNativeCollabPackage.MaximumBytes) yield return null;
            try
            {
                if (failure != null) throw failure; cancellation.ThrowIfCancellationRequested();
                if (request.downloadedBytes > (ulong)SakuraNativeCollabPackage.MaximumBytes) throw new InvalidDataException("Collab package exceeds 300 MB.");
                if (request.result != UnityWebRequest.Result.Success) throw new IOException("The selected model could not be packaged by Sakura.");
                string entry = Uri.UnescapeDataString(request.GetResponseHeader("X-Sakura-Collab-Entry") ?? "");
                string name = Uri.UnescapeDataString(request.GetResponseHeader("X-Sakura-Collab-Name") ?? Path.GetFileName(relative));
                answer.TrySetResult((destination, SakuraNativeCollabPackage.SafeEntry(entry), name));
            }
            catch (OperationCanceledException) { answer.TrySetCanceled(); }
            catch (Exception error) { answer.TrySetException(error); }
            finally { request?.Abort(); request?.Dispose(); if (!answer.Task.IsCompletedSuccessfully && File.Exists(destination)) File.Delete(destination); }
        }
    }
}
