using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    public sealed partial class SakuraBackend
    {
        private string agentToken;

        public static bool ValidAgentRoute(string route) => route != null &&
            Regex.IsMatch(route, @"\A/api/agent/[A-Za-z0-9/_-]+(?:\?[A-Za-z0-9_%=&.+-]*)?\z") && !route.Contains("//");

        public static string ReadAgentToken(string html)
        {
            var match = Regex.Match(html ?? "", @"const token='([A-Za-z0-9_-]{40,100})';");
            if (!match.Success) throw new InvalidOperationException("The local Agent page did not provide its session authorization.");
            return match.Groups[1].Value;
        }

        public void AgentApi(string route, string body, Action<string, string> completed, string method = null)
        {
            if (!ValidAgentRoute(route) || (method != null && method != "GET" && method != "POST" && method != "PUT" && method != "DELETE"))
            { completed(null, "Invalid Agent request."); return; }
            StartCoroutine(CallAgentApi(route, body, completed, method ?? (body == null ? "GET" : "POST")));
        }

        private IEnumerator AuthorizeAgent(Action<string> completed)
        {
            if (Mode == "VTuber") { completed("Agent tools stay off in VTuber mode."); yield break; }
            string failure = null;
            yield return CallApi("/api/unity/start-services", "{}", (_, error) => failure = error);
            if (failure != null) { completed(failure); yield break; }
            if (!string.IsNullOrEmpty(agentToken)) { completed(null); yield break; }
            using (var request = Request("/agent"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) { completed(ResponseError(request.downloadHandler?.text, request.error)); yield break; }
                try { agentToken = ReadAgentToken(request.downloadHandler.text); }
                catch (InvalidOperationException error) { failure = error.Message; }
            }
            completed(failure);
        }

        private IEnumerator CallAgentApi(string route, string body, Action<string, string> completed, string method)
        {
            string failure = null;
            yield return AuthorizeAgent(error => failure = error);
            if (failure != null) { completed(null, failure); yield break; }
            if (Mode == "VTuber") { completed(null, "Agent tools stay off in VTuber mode."); yield break; }
            using (var request = Request(route))
            {
                request.method = method;
                request.SetRequestHeader("X-Sakura-Agent", agentToken);
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success) completed(request.downloadHandler.text, null);
                else completed(null, ResponseError(request.downloadHandler?.text, request.error));
            }
        }

        public void AgentPreview(string identifier, bool local, Action<Texture2D, string> completed)
        {
            if (!Regex.IsMatch(identifier ?? "", @"\A[a-zA-Z0-9_-]{1,80}\z")) { completed(null, "Choose a valid task."); return; }
            StartCoroutine(ReadAgentPreview(identifier, local, completed));
        }

        private IEnumerator ReadAgentPreview(string identifier, bool local, Action<Texture2D, string> completed)
        {
            string failure = null;
            yield return AuthorizeAgent(error => failure = error);
            if (failure != null) { completed(null, failure); yield break; }
            using (var request = Request("/api/agent/" + (local ? "local-browser/" : "browser/") + identifier + "/preview"))
            {
                request.SetRequestHeader("X-Sakura-Agent", agentToken);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) { completed(null, ResponseError(request.downloadHandler?.text, request.error)); yield break; }
                byte[] bytes = request.downloadHandler.data;
                if (bytes == null || bytes.Length > 16 * 1024 * 1024) { completed(null, "Browser preview is too large."); yield break; }
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes) || texture.width > 4096 || texture.height > 4096)
                { Destroy(texture); completed(null, "Invalid browser preview."); yield break; }
                completed(texture, null);
            }
        }
    }
}
