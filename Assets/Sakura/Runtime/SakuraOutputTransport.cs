using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    public sealed partial class SakuraBackend
    {
        private readonly HashSet<string> outputUploads = new HashSet<string>();
        public static bool ValidOutputUpload(string route, int count, string contentType, string header, string token)
        {
            if (route == "/api/media/save") return count > 0 && count <= 64 * 1024 * 1024 && contentType == "image/png" && string.IsNullOrEmpty(header) && string.IsNullOrEmpty(token);
            var match = Regex.Match(route ?? "", @"\A/api/(spout|ndi|virtual-camera)/frame\?width=([0-9]{1,4})&height=([0-9]{1,4})\z");
            if (!match.Success || contentType != "application/octet-stream" || string.IsNullOrEmpty(token) || token.Length > 256 || token.Contains("\r") || token.Contains("\n")) return false;
            string kind = match.Groups[1].Value;
            string expectedHeader = kind == "virtual-camera" ? "x-camera-session" : "x-" + kind + "-session";
            int width = int.Parse(match.Groups[2].Value), height = int.Parse(match.Groups[3].Value);
            int limit = kind == "virtual-camera" ? 1280 : 2560;
            return header == expectedHeader && width >= 1 && height >= 1 && width <= limit && height <= limit && (kind != "ndi" || width % 2 == 0) && count == width * height * 4;
        }
        public void UploadOutput(string route, byte[] bytes, string contentType, string sessionHeader, string sessionToken, Action<string, string> completed)
        {
            if (bytes == null || !ValidOutputUpload(route, bytes.Length, contentType, sessionHeader, sessionToken)) { completed(null, "Invalid capture upload."); return; }
            if (!Connected) { completed(null, "Connect the backend before streaming output."); return; }
            string key = route.Split('?')[0];
            if (!outputUploads.Add(key)) { completed(null, "A frame is still uploading."); return; }
            StartCoroutine(UploadOutputFrame(route, key, bytes, contentType, sessionHeader, sessionToken, completed));
        }
        private IEnumerator UploadOutputFrame(string route, string key, byte[] bytes, string contentType, string header, string token, Action<string, string> completed)
        {
            UnityWebRequest request;
            try
            {
                request = Request(route); request.method = "POST"; request.timeout = 5;
                request.uploadHandler = new UploadHandlerRaw(bytes); request.SetRequestHeader("Content-Type", contentType);
                if (!string.IsNullOrEmpty(header)) request.SetRequestHeader(header, token);
            }
            catch (Exception error) { outputUploads.Remove(key); completed(null, error.Message); yield break; }
            using (request)
            {
                yield return request.SendWebRequest();
                outputUploads.Remove(key);
                if (request.result == UnityWebRequest.Result.Success) completed(request.downloadHandler.text, null);
                else completed(null, ResponseError(request.downloadHandler?.text, request.error));
            }
        }
    }
}
