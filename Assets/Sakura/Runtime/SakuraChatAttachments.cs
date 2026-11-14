using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Sakura.Unity
{
    public sealed partial class SakuraBackend
    {
        public readonly List<JObject> ChatAttachments = new List<JObject>();
        public bool AttachmentBusy { get; private set; }
        public event Action AttachmentsChanged;

        public void PickChatAttachment()
        {
            if (VtuberEdition || Mode == "VTuber" || Busy || AttachmentBusy) return;
            if (ChatAttachments.Count >= 3) { StatusChanged?.Invoke("Attach up to three files at a time."); return; }
            AttachmentBusy = true; AttachmentsChanged?.Invoke();
            RunPmxTool("chat-attachment", "", (json, error) =>
            {
                if (error != null) { FinishAttachment(error); return; }
                try
                {
                    var result = JObject.Parse(json);
                    if (result.Value<bool?>("cancelled") == true) { FinishAttachment(null); return; }
                    string path = result.Value<string>("path");
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length < 1 || file.Length > 10 * 1024 * 1024 || path.StartsWith(@"\\"))
                        throw new IOException("Choose a non-empty local file of up to 10 MB.");
                    // Cap the actual read too, in case the selected file grows after selection.
                    byte[] bytes;
                    using (var input = file.OpenRead())
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[65536]; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        { if (output.Length + count > 10 * 1024 * 1024) throw new IOException("Attachment exceeds 10 MB."); output.Write(buffer, 0, count); }
                        bytes = output.ToArray();
                    }
                    Api("/api/unity/start-services", "{}", (_, failure) =>
                    {
                        if (failure != null) FinishAttachment(failure);
                        else StartCoroutine(UploadChatAttachment(bytes, file.Name));
                    });
                }
                catch (Exception failure) { FinishAttachment(failure.Message); }
            });
        }

        IEnumerator UploadChatAttachment(byte[] bytes, string name)
        {
            if (Mode == "VTuber" || VtuberEdition) { FinishAttachment("Attachments are available in AI modes."); yield break; }
            var form = new WWWForm(); form.AddBinaryData("file", bytes, name, "application/octet-stream");
            using (var request = Request("/api/chat/attachments"))
            {
                request.method = "POST"; request.uploadHandler = new UploadHandlerRaw(form.data);
                foreach (var header in form.headers) request.SetRequestHeader(header.Key, header.Value);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { FinishAttachment(ResponseError(request.downloadHandler?.text, request.error)); yield break; }
                string error = null;
                try
                {
                    var receipt = JObject.Parse(request.downloadHandler.text);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(receipt.Value<string>("id") ?? "", @"\A[a-f0-9]{32}\z"))
                        throw new IOException("Attachment upload returned an invalid receipt.");
                    ChatAttachments.Add(receipt);
                }
                catch (Exception failure) { error = failure.Message; }
                FinishAttachment(error);
            }
        }

        void FinishAttachment(string error)
        {
            AttachmentBusy = false; AttachmentsChanged?.Invoke();
            if (error != null) StatusChanged?.Invoke(error);
        }

        public void RemoveChatAttachment(JObject item)
        {
            if (Busy || AttachmentBusy || !ChatAttachments.Remove(item)) return;
            AttachmentsChanged?.Invoke();
            StartCoroutine(DeleteChatAttachment((string)item["id"]));
        }

        IEnumerator DeleteChatAttachment(string id)
        {
            if (!Connected) yield break;
            using (var request = Request("/api/chat/attachments/" + Uri.EscapeDataString(id)))
            {
                request.method = "DELETE"; yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) StatusChanged?.Invoke("Attachment removed from this message; backend cleanup will expire automatically.");
            }
        }
    }
}
