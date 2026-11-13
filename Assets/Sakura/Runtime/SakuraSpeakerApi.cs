using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    public sealed partial class SakuraBackend
    {
        public static bool ValidRvcName(string name) => System.Text.RegularExpressions.Regex.IsMatch((name ?? "").Trim().ToLowerInvariant().Replace(' ', '_'), @"\A[a-z0-9_]{1,48}\z");
        public static bool ValidRvcFileLength(long length) => length > 0 && length <= 512L * 1024 * 1024;

        public void UploadRvcVoice(string name, string modelPath, string indexPath, Action<string, string> completed)
        {
            if (!Connected) { completed(null, "Connect the backend before importing a voice."); return; }
            if (!ValidRvcName(name)) { completed(null, "Use up to 48 letters, numbers, underscores or spaces for the voice name."); return; }
            string temporary = null;
            string boundary = "SakuraVoice" + Guid.NewGuid().ToString("N");
            string error = null;
            try
            {
                temporary = CreateRvcMultipartFile(name, modelPath, indexPath, boundary);
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is ArgumentException || failure is NotSupportedException)
            { error = failure.Message; }
            if (error != null) { completed(null, error); return; }
            StartCoroutine(SendRvcVoice(temporary, boundary, completed));
        }

        public static string CreateRvcMultipartFile(string name, string modelPath, string indexPath, string boundary)
        {
            if (!ValidRvcName(name)) throw new ArgumentException("Choose a valid voice name.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(boundary ?? "", @"\A[A-Za-z0-9_-]{1,70}\z")) throw new ArgumentException("Invalid multipart boundary.");
            var model = ValidateVoiceFile(modelPath, ".pth");
            var index = string.IsNullOrWhiteSpace(indexPath) ? null : ValidateVoiceFile(indexPath, ".index");
            string temporary = Path.Combine(Path.GetTempPath(), "sakura-rvc-" + Guid.NewGuid().ToString("N") + ".multipart");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                {
                    WriteMultipartText(output, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"character_name\"\r\n\r\n" + name.Trim() + "\r\n");
                    CopyVoicePart(output, model, "pth_file", boundary);
                    if (index != null) CopyVoicePart(output, index, "index_file", boundary);
                    WriteMultipartText(output, "--" + boundary + "--\r\n");
                }
                return temporary;
            }
            catch { File.Delete(temporary); throw; }
        }

        private static void WriteMultipartText(Stream output, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
        }

        private static void CopyVoicePart(Stream output, FileInfo file, string field, string boundary)
        {
            string filename = file.Name.Replace('"', '_').Replace('\\', '_').Replace('\r', '_').Replace('\n', '_');
            WriteMultipartText(output, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + field + "\"; filename=\"" + filename + "\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                if (!ValidRvcFileLength(input.Length)) throw new ArgumentException("Voice files must be nonempty and under 512 MB.");
                var buffer = new byte[65536];
                long copied = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += count;
                    if (copied > 512L * 1024 * 1024) throw new ArgumentException("Voice files must be under 512 MB.");
                    output.Write(buffer, 0, count);
                }
            }
            WriteMultipartText(output, "\r\n");
        }

        private static FileInfo ValidateVoiceFile(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose an existing " + extension + " file.");
            var file = new FileInfo(path.Trim());
            if (!file.Exists || !file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an existing " + extension + " file.");
            if (!ValidRvcFileLength(file.Length)) throw new ArgumentException("Voice files must be nonempty and under 512 MB.");
            return file;
        }

        private IEnumerator SendRvcVoice(string temporary, string boundary, Action<string, string> completed)
        {
            var lifetime = GetComponent<SakuraRvcUploadLifetime>() ?? gameObject.AddComponent<SakuraRvcUploadLifetime>();
            UnityWebRequest request = null;
            string setupError = null;
            try
            {
                request = Request("/api/voices/upload");
                request.method = "POST";
                request.uploadHandler = new UploadHandlerFile(temporary);
                request.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);
                lifetime.Track(request, temporary);
            }
            catch (Exception error) { setupError = error.Message; }
            if (setupError != null)
            {
                request?.Dispose(); File.Delete(temporary); completed(null, setupError); yield break;
            }
            try
            {
                yield return request.SendWebRequest();
                completed(request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : null,
                    request.result == UnityWebRequest.Result.Success ? null : ResponseError(request.downloadHandler?.text, request.error));
            }
            finally { lifetime.Release(request, temporary); }
        }

        public static bool ValidSpeakerName(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 40;

        public void RemoveSpeaker(string name, Action<string, string> completed)
        {
            if (!ValidSpeakerName(name)) { completed(null, "Use a speaker name of up to 40 characters."); return; }
            if (!Connected || Mode != "Companion") { completed(null, "Connect in Companion mode to manage recognised voices."); return; }
            StartCoroutine(SpeakerRequest("/api/companion/speakers/" + Uri.EscapeDataString(name), "DELETE", null, completed));
        }

        public void EnrolSpeaker(string name, byte[] wav, Action<string, string> completed)
        {
            if (!ValidSpeakerName(name) || wav == null || wav.Length < 44 || wav.Length > 5000000 ||
                wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            { completed(null, "Use a speaker name of up to 40 characters and a WAV recording under 5 MB."); return; }
            if (!Connected || Mode != "Companion") { completed(null, "Connect in Companion mode to save a recognised voice."); return; }
            StartCoroutine(SpeakerRequest("/api/companion/speakers/enroll?name=" + Uri.EscapeDataString(name.Trim()), "POST", wav, completed));
        }

        private IEnumerator SpeakerRequest(string route, string method, byte[] wav, Action<string, string> completed)
        {
            using (var request = Request(route))
            {
                request.method = method;
                if (wav != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(wav);
                    request.SetRequestHeader("Content-Type", "audio/wav");
                }
                yield return request.SendWebRequest();
                completed(request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : null,
                    request.result == UnityWebRequest.Result.Success ? null : ResponseError(request.downloadHandler?.text, request.error));
            }
        }
    }

    // Explicit lifetime ownership also covers Unity stopping coroutines during scene teardown.
    public sealed class SakuraRvcUploadLifetime : MonoBehaviour
    {
        private readonly Dictionary<UnityWebRequest, string> uploads = new Dictionary<UnityWebRequest, string>();
        public void Track(UnityWebRequest request, string path) { uploads.Add(request, path); }
        public void Release(UnityWebRequest request, string path)
        {
            if (uploads.Remove(request)) request.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
        private void OnDisable()
        {
            foreach (var upload in uploads)
            {
                upload.Key.Abort(); upload.Key.Dispose();
                if (File.Exists(upload.Value)) File.Delete(upload.Value);
            }
            uploads.Clear();
        }
    }
}
