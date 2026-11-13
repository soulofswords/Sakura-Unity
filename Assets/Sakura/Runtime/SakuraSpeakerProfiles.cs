using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>Owner-operated profile management. Capture starts only from the Record button.</summary>
    public sealed class SakuraSpeakerProfiles : IDisposable
    {
        private readonly SakuraBackend backend;
        private readonly VisualElement card, profiles, actions;
        private readonly Label status, matchingNote;
        private readonly TextField name;
        private readonly Toggle matching, consent;
        private readonly Button record, install, refresh, cancel;
        private readonly List<string> savedNames = new List<string>();
        private readonly IVisualElementScheduledItem poll;
        private bool alive = true, busy, ready, installing, paused, matchingEnabled = true;
        private AudioClip clip;
        private string device;
        private Coroutine capture;

        public SakuraSpeakerProfiles(SakuraBackend backend, VisualElement host)
        {
            this.backend = backend;
            card = new VisualElement(); card.AddToClassList("card"); host.Add(card);
            Text("Recognised voices", "card-title");
            Text("Save up to five voices. Say “Hello Sakura, this is my voice. Please listen when I speak.”", "description");
            matching = new Toggle("Voice matching"); matching.name = "speaker-matching"; card.Add(matching);
            matchingNote = Text("Loading saved-voice matching…", "description");
            matching.RegisterValueChangedCallback(evt =>
            {
                if (busy) return;
                RunJson("/matching", new JObject { ["enabled"] = evt.newValue });
            });
            name = new TextField("Your name") { maxLength = 40 }; name.name = "speaker-name"; card.Add(name);
            name.RegisterValueChangedCallback(_ => UpdateControls());
            consent = new Toggle("Download the optional CPU voice recognition runtime (large download)"); card.Add(consent);
            consent.RegisterValueChangedCallback(_ => UpdateControls());
            actions = new VisualElement(); actions.AddToClassList("row"); card.Add(actions);
            install = Button("Download voice recognition", () => RunJson("/install", new JObject { ["consent"] = true }), actions);
            record = Button("Record 6 seconds", BeginCapture, actions); record.name = "speaker-record";
            refresh = Button("Refresh voices", Refresh, actions);
            cancel = Button("Cancel recording", () => { CancelCapture(); busy = false; status.text = "Recording cancelled."; UpdateControls(); }, actions);
            status = Text("Loading recognised voices…", "description");
            profiles = new VisualElement(); card.Add(profiles);
            Text("Voice matching is experimental and cannot prove identity. Profiles are encrypted locally. Recordings are discarded after processing.", "description");
            card.RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
            poll = card.schedule.Execute(() => { if (backend.Mode != "Companion") { Dispose(); return; } if (installing && !busy) Refresh(); }).Every(1000);
            UpdateControls(); Refresh();
        }

        private Label Text(string text, string css)
        { var label = new Label(text); label.AddToClassList(css); card.Add(label); return label; }
        private static Button Button(string text, Action action, VisualElement parent)
        { var button = new Button(action) { text = text }; parent.Add(button); return button; }

        private void UpdateControls()
        {
            bool available = alive && !busy && backend.Mode == "Companion";
            matching.SetEnabled(available);
            name.SetEnabled(available);
            refresh.SetEnabled(available);
            profiles.SetEnabled(available);
            consent.style.display = ready ? DisplayStyle.None : DisplayStyle.Flex;
            install.style.display = ready ? DisplayStyle.None : DisplayStyle.Flex;
            install.text = installing ? "Installing…" : "Download voice recognition";
            install.SetEnabled(available && !installing && consent.value);
            record.SetEnabled(available && CanEnrol(name.value, savedNames, ready, installing));
            cancel.style.display = capture != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public static bool CanEnrol(string name, IList<string> profiles, bool ready, bool installing) =>
            ready && !installing && SakuraBackend.ValidSpeakerName(name) && (profiles.Count < 5 || profiles.Contains(name.Trim()));

        private void Refresh()
        {
            if (!alive || busy) return;
            busy = true; UpdateControls();
            backend.Api("/api/companion/speakers", null, (json, error) =>
            {
                if (!alive) return;
                busy = false;
                if (error != null) { status.text = error; UpdateControls(); return; }
                try
                {
                    var data = JObject.Parse(json);
                    ready = data.Value<bool?>("ready") ?? false;
                    installing = data.Value<bool?>("busy") ?? false;
                    matchingEnabled = data.Value<bool?>("matching_enabled") ?? true;
                    matching.SetValueWithoutNotify(matchingEnabled);
                    matchingNote.text = matching.value
                        ? "Only saved voices pass the voice filter when profiles are enrolled. The microphone has its own switch."
                        : "Microphone input passes without saved-voice filtering. Your profiles are kept.";
                    var names = (data["profiles"] as JArray ?? new JArray()).Values<string>().Where(value => value != null).ToList();
                    if (!savedNames.SequenceEqual(names)) { savedNames.Clear(); savedNames.AddRange(names); RenderProfiles(); }
                    string errorText = data.Value<string>("error");
                    status.text = string.IsNullOrEmpty(errorText) ? names.Count + " / 5 voices saved" + InstallProgress(data) : errorText;
                }
                catch (Exception errorReply) when (errorReply is Newtonsoft.Json.JsonException || errorReply is FormatException || errorReply is InvalidCastException)
                { ready = false; status.text = "Voice recognition returned an invalid status. Refresh to retry."; }
                UpdateControls();
            });
        }

        public static string InstallProgress(JObject data)
        {
            if (data.Value<bool?>("busy") != true) return "";
            var progress = data["progress"] as JObject;
            if (progress == null) return " · Installing…";
            double total = progress.Value<double?>("total_bytes") ?? 0;
            double downloaded = progress.Value<double?>(total > 0 ? "file_downloaded_bytes" : "downloaded_bytes") ?? 0;
            string transfer = (Math.Max(0, downloaded) / 1000000).ToString("F1") + " MB";
            if (total > 0) transfer += " / " + (total / 1000000).ToString("F1") + " MB";
            double speed = progress.Value<double?>("bytes_per_second") ?? 0;
            return " · " + (progress.Value<string>("phase") ?? "Installing") + " · " + transfer + " · " + (Math.Max(0, speed) / 1000000).ToString("F1") + " MB/s";
        }

        private void RenderProfiles()
        {
            profiles.Clear();
            foreach (string savedName in savedNames)
            {
                string original = savedName;
                var row = new VisualElement(); row.AddToClassList("row"); profiles.Add(row);
                var label = new TextField("Speaker name") { value = original, maxLength = 40 }; row.Add(label);
                Button("Rename", () =>
                {
                    if (!SakuraBackend.ValidSpeakerName(label.value)) { status.text = "Enter a name of up to 40 characters."; return; }
                    RunJson("/rename", new JObject { ["name"] = original, ["new_name"] = label.value.Trim() });
                }, row);
                Button("Remove", () =>
                {
                    if (!alive || busy) return;
                    busy = true; UpdateControls();
                    backend.RemoveSpeaker(original, (json, error) => CompleteMutation(error));
                }, row);
            }
        }

        private void RunJson(string route, JObject payload)
        {
            if (!alive || busy) return;
            busy = true; status.text = "Updating recognised voices…"; UpdateControls();
            backend.Api("/api/companion/speakers" + route, payload.ToString(Newtonsoft.Json.Formatting.None), (_, error) => CompleteMutation(error));
        }

        private void CompleteMutation(string error)
        {
            if (!alive) return;
            busy = false; UpdateControls();
            if (error != null) { matching.SetValueWithoutNotify(matchingEnabled); status.text = error; return; }
            Refresh();
        }

        private void BeginCapture()
        {
            if (!alive || busy || !CanEnrol(name.value, savedNames, ready, installing)) return;
            var voice = backend.GetComponent<SakuraVoice>();
            if (voice == null || !voice.TryPauseForEnrolment()) { status.text = "Wait for the current reply or speech recognition to finish before recording."; return; }
            paused = true; busy = true;
            capture = backend.StartCoroutine(Capture(name.value.Trim(), voice.MicrophoneDevice));
            UpdateControls();
        }

        private IEnumerator Capture(string speakerName, string selectedDevice)
        {
            // Delay one frame so the coroutine handle exists before any synchronous failure cleans up.
            yield return null;
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (!alive || backend.Mode != "Companion") { FinishCapture("Recording cancelled."); yield break; }
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { FinishCapture("Microphone permission denied. Enable microphone access in Windows settings."); yield break; }
            string[] devices = Microphone.devices;
            device = string.IsNullOrEmpty(selectedDevice) ? devices.FirstOrDefault() : selectedDevice;
            if (device == null || Array.IndexOf(devices, device) < 0) { FinishCapture("The selected microphone is unavailable."); yield break; }
            string failure = null;
            try { clip = Microphone.Start(device, false, 7, 16000); }
            catch (Exception error) { failure = error.Message; }
            if (clip == null) { FinishCapture("Microphone could not start. " + failure); yield break; }
            float started = Time.realtimeSinceStartup;
            int frames = 0;
            while (frames < clip.frequency * 6)
            {
                if (!alive || backend.Mode != "Companion") { FinishCapture("Recording cancelled."); yield break; }
                frames = Microphone.GetPosition(device);
                if (frames < 0 || !Microphone.IsRecording(device)) { FinishCapture("Microphone disconnected. Reconnect it and retry."); yield break; }
                if (Time.realtimeSinceStartup - started > 10) { FinishCapture("Microphone timed out. Check Windows microphone access and retry."); yield break; }
                status.text = "Recording… " + Math.Min(6, frames / clip.frequency + 1) + " / 6 seconds. Say the sentence above.";
                yield return null;
            }
            byte[] wav = null;
            try
            {
                int length = clip.frequency * 6;
                var interleaved = new float[length * clip.channels];
                if (!clip.GetData(interleaved, 0)) throw new InvalidOperationException("Could not read the microphone audio.");
                var mono = new float[length];
                for (int frame = 0; frame < length; frame++)
                    for (int channel = 0; channel < clip.channels; channel++) mono[frame] += interleaved[frame * clip.channels + channel] / clip.channels;
                wav = SakuraVoice.EncodeWav(mono, clip.frequency);
            }
            catch (Exception error) { failure = error.Message; }
            ReleaseMicrophone(); capture = null; UpdateControls();
            if (failure != null) { FinishCapture(failure); yield break; }
            status.text = "Recognising and saving your voice…";
            backend.EnrolSpeaker(speakerName, wav, (_, error) =>
            {
                ResumeVoice();
                CompleteMutation(error);
            });
        }

        private void FinishCapture(string message)
        {
            ReleaseMicrophone(); capture = null; ResumeVoice(); busy = false;
            if (alive) { status.text = message; UpdateControls(); }
        }
        private void ReleaseMicrophone()
        {
            if (clip == null) return;
            Microphone.End(device); UnityEngine.Object.Destroy(clip); clip = null;
        }
        private void ResumeVoice()
        {
            if (!paused) return;
            paused = false;
            if (backend != null) backend.GetComponent<SakuraVoice>()?.ResumeAfterEnrolment();
        }
        private void CancelCapture()
        {
            if (capture != null && backend != null) backend.StopCoroutine(capture);
            capture = null; ReleaseMicrophone(); ResumeVoice();
        }
        public void Dispose()
        {
            if (!alive) return;
            alive = false; poll?.Pause(); CancelCapture();
        }
    }
}
