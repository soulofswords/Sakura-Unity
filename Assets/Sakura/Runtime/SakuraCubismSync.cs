using System;
using System.Linq;
using Live2D.Cubism.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class SakuraCubismSync : MonoBehaviour
    {
        public event Action Changed;
        public bool Streaming { get; private set; }
        public bool Connected { get; private set; }
        public bool Approved { get; private set; }
        public string State { get; private set; } = "disconnected";
        public bool Busy { get; private set; }
        public string Status { get; private set; } = "Editor disconnected.";
        public int SentFrames { get; private set; }
        public int MatchedParameters { get; private set; }
        private SakuraBackend backend;
        private SakuraAvatarViewport viewport;
        private CharacterEntry[] models = Array.Empty<CharacterEntry>();
        private bool framePending, statusPending;
        private int generation;
        private float nextFrame, nextStatus;

        public void Initialize(SakuraBackend backend, SakuraAvatarViewport viewport)
        {
            if (this.backend == backend && this.viewport == viewport) return;
            Unbind(); this.backend = backend; this.viewport = viewport;
            if (backend != null) backend.CatalogReceived += ReceiveCatalog;
        }

        private void ReceiveCatalog(CharacterEntry[] entries)
        {
            models = (entries ?? Array.Empty<CharacterEntry>()).Where(entry => entry.format == "Live2D").ToArray();
            Changed?.Invoke();
        }

        public static JObject BuildParameterFrame(string[] identifiers, float[] values)
        {
            if (identifiers == null || values == null || identifiers.Length != values.Length) throw new ArgumentException("Parameter IDs and values must have the same length.");
            var parameters = new JArray();
            for (int index = 0; index < Math.Min(identifiers.Length, 2000); index++)
            {
                string id = identifiers[index]; float value = values[index];
                if (string.IsNullOrEmpty(id) || id.Length > 256 || float.IsNaN(value) || float.IsInfinity(value)) continue;
                parameters.Add(new JObject { ["Id"] = id, ["Value"] = value });
            }
            return new JObject { ["parameters"] = parameters };
        }

        public static JObject ReadParameters(CubismModel model)
        {
            var parameters = model == null ? Array.Empty<CubismParameter>() : model.Parameters.Take(2000).Where(parameter => parameter != null).ToArray();
            return BuildParameterFrame(parameters.Select(parameter => parameter.Id).ToArray(), parameters.Select(parameter => parameter.Value).ToArray());
        }

        public void Connect(int port)
        {
            if (port < 1 || port > 65535) { SetStatus("Enter a Cubism Editor port from 1 to 65535."); return; }
            if (Busy || backend == null) return;
            Streaming = false; int revision = ++generation; framePending = false; Busy = true;
            SetStatus("Connecting to Cubism Editor…");
            Request("connect", new JObject { ["port"] = port }, revision, result => { Busy = false; ReadState(result); });
        }

        public void Disconnect()
        {
            if (Busy || backend == null) return;
            Streaming = false; int revision = ++generation; framePending = false; Busy = true;
            Request("disconnect", new JObject(), revision, result => { Busy = false; ReadState(result); });
        }

        public void SetStreaming(bool enabled)
        {
            if (Busy || backend == null) return;
            if (enabled && (viewport == null || viewport.LoadedFormat != "Live2D" || viewport.AvatarRoot?.GetComponent<CubismModel>() == null))
            { SetStatus("Load a Live2D model before sending its parameters."); return; }
            Streaming = false; int revision = ++generation; framePending = false; Busy = true;
            Request("sync", new JObject { ["enabled"] = enabled }, revision, result =>
            {
                Busy = false; Streaming = enabled; ReadState(result);
            });
        }

        public void RefreshStatus()
        {
            if (Busy || statusPending || backend == null || !isActiveAndEnabled) return;
            statusPending = true; int revision = generation;
            backend.Api("/api/vtuber/cubism/status", null, (json, error) =>
            {
                if (this == null || !isActiveAndEnabled) return;
                statusPending = false;
                if (revision != generation) return;
                if (error != null) { SetStatus(error); return; }
                try { ReadState(JObject.Parse(json)); }
                catch (JsonException) { SetStatus("Cubism Editor returned an unreadable status."); }
            });
        }

        private void Request(string operation, JObject body, int revision, Action<JObject> complete)
        {
            backend.Api("/api/vtuber/cubism/" + operation, body.ToString(Formatting.None), (json, error) =>
            {
                if (this == null || !isActiveAndEnabled || revision != generation) return;
                if (error != null) { Busy = false; framePending = false; SetStatus(error); return; }
                try { complete(JObject.Parse(json)); }
                catch (JsonException) { Busy = false; framePending = false; SetStatus("Cubism Editor returned an unreadable response."); }
            });
        }

        private void ReadState(JObject state)
        {
            Connected = state.Value<bool?>("connected") == true;
            Approved = state.Value<bool?>("approved") == true;
            SentFrames = state.Value<int?>("sent_frames") ?? 0;
            MatchedParameters = state.Value<int?>("matched_parameters") ?? 0;
            State = (string)state["state"] ?? "disconnected";
            string error = (string)state["error"];
            if (!string.IsNullOrEmpty(error)) { SetStatus(error); return; }
            string message = State;
            switch (message)
            {
                case "disconnected": message = "Editor disconnected."; break;
                case "connecting": message = "Connecting to Cubism Editor…"; break;
                case "awaiting_approval": message = "Enable Allow for Sakura in Cubism Editor external application integration settings."; break;
                case "paused": message = "Connected. Parameter streaming paused."; break;
                case "ready": message = "Connected. Open the matching model in Cubism Editor and enable parameter streaming."; break;
                case "streaming": message = "Sending Live2D parameters to Cubism Editor · " + MatchedParameters + " parameters · " + SentFrames + " frames"; break;
                case "awaiting_model": message = "Select a model in Cubism Editor."; break;
                case "no_matching_parameters": message = "The selected Editor model has no matching Live2D parameter IDs."; break;
            }
            SetStatus(message ?? "Editor status unavailable.");
        }

        private void SetStatus(string message) { Status = message; Changed?.Invoke(); }

        private void LateUpdate()
        {
            if (backend == null || viewport == null) return;
            if (Time.unscaledTime >= nextStatus) { nextStatus = Time.unscaledTime + 1; RefreshStatus(); }
            if (!Streaming || Busy || framePending || !Connected || !Approved || viewport.Loading || viewport.LoadedFormat != "Live2D" || Time.unscaledTime < nextFrame) return;
            var model = viewport.AvatarRoot?.GetComponent<CubismModel>();
            if (model == null) return;
            var frame = ReadParameters(model);
            if (((JArray)frame["parameters"]).Count == 0) return;
            nextFrame = Time.unscaledTime + .1f; framePending = true;
            int revision = generation;
            Request("parameters", frame, revision, result => { framePending = false; ReadState(result); });
        }

        private void Unbind()
        {
            if (backend != null) backend.CatalogReceived -= ReceiveCatalog;
        }

        private void OnDisable()
        {
            generation++; Streaming = false; Busy = false; framePending = false; statusPending = false;
        }

        private void OnDestroy() { Unbind(); }

        public static SakuraCubismSync BuildPanel(SakuraBackend backend, SakuraAvatarViewport viewport, VisualElement parent)
        {
            var sync = backend.GetComponent<SakuraCubismSync>() ?? backend.gameObject.AddComponent<SakuraCubismSync>();
            sync.Initialize(backend, viewport);
            var owner = new VisualElement { name = "cubism-editor-panel" }; owner.AddToClassList("card"); parent.Add(owner);
            Text("Cubism Editor live parameters", owner, "card-title");
            var port = new IntegerField("Local editor port") { value = 22033, name = "cubism-port" }; owner.Add(port);
            var actions = new VisualElement(); actions.AddToClassList("row"); owner.Add(actions);
            var connect = new Button(() => sync.Connect(port.value)) { text = "Connect Cubism Editor", name = "cubism-connect" }; actions.Add(connect);
            var disconnect = new Button(sync.Disconnect) { text = "Disconnect editor", name = "cubism-disconnect" }; actions.Add(disconnect);
            var models = new DropdownField("Live2D model in Sakura", new System.Collections.Generic.List<string> { "Choose a model…" }, 0); owner.Add(models);
            CharacterEntry[] choices = Array.Empty<CharacterEntry>();
            models.RegisterValueChangedCallback(change =>
            {
                if (models.index > 0 && models.index <= choices.Length) _ = viewport.LoadModel(backend.SourceFolder, choices[models.index - 1].path);
            });
            var stream = new Toggle("Send current Live2D parameters to the editor") { name = "cubism-stream" }; owner.Add(stream);
            stream.RegisterValueChangedCallback(change => sync.SetStreaming(change.newValue));
            var status = Text(sync.Status, owner, "description"); status.name = "cubism-status";
            Text("Enable Cubism Editor’s external application integration server, allow Sakura, then select the matching editable model or animation timeline track in the Editor. Sakura sends its rendered parameter values at up to 10 frames per second.", owner, "description");
            void Refresh()
            {
                status.text = sync.Status; stream.SetValueWithoutNotify(sync.Streaming);
                stream.SetEnabled(!sync.Busy); connect.SetEnabled(!sync.Busy && !sync.Connected && sync.State != "connecting"); disconnect.SetEnabled(!sync.Busy && (sync.Connected || sync.State == "connecting"));
                if (!choices.SequenceEqual(sync.models))
                {
                    choices = sync.models;
                    models.choices = new[] { "Choose a model…" }.Concat(choices.Select(entry => entry.name)).ToList();
                }
                models.SetValueWithoutNotify(models.choices[Math.Max(0, Array.FindIndex(choices, entry => entry.path == viewport.LoadedModelPath) + 1)]);
            }
            sync.Changed += Refresh;
            owner.RegisterCallback<DetachFromPanelEvent>(_ => sync.Changed -= Refresh);
            Refresh(); sync.RefreshStatus(); backend.RefreshCatalog();
            return sync;
        }

        private static Label Text(string text, VisualElement parent, string style)
        {
            var label = new Label(text); label.AddToClassList(style); parent.Add(label); return label;
        }
    }
}
