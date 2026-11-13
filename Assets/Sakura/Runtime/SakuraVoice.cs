using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Sakura.Unity
{
    /// <summary>Explicitly enabled native microphone capture. Recognition and language stay in the existing backend.</summary>
    [DisallowMultipleComponent]
    public sealed class SakuraVoice : MonoBehaviour
    {
        [Serializable] private sealed class Settings { public bool wake_word_enabled; public string wake_word; }
        [Serializable] private sealed class Recognition { public string text; public string error; public bool speaker_rejected; public string speaker_message; }

        public SakuraBackend Backend;
        public string MicrophoneDevice = "";
        [Range(.001f, .1f)] public float SpeechThreshold = .012f;
        public event Action<string> TranscriptReceived;
        public event Action<string> StatusChanged;
        public event Action<bool> ListeningChanged;
        public bool Listening { get; private set; }
        public bool Recording => microphone != null;
        public bool Speaking => speaker != null && speaker.isPlaying;
        public string Status { get; private set; } = "Microphone OFF";
        public float InputLevel { get; private set; }

        private SakuraBackend boundBackend;
        private AudioClip microphone;
        private AudioSource speaker;
        private AudioListener ownedListener;
        private SpeechGate gate;
        private readonly List<float> heldSpeech = new List<float>();
        private string activeDevice;
        private int readFrame, generation, playbackGeneration;
        private bool pushToTalk, recognizing, starting, loadingSettings, settingsReady, wakeEnabled;
        private string wakePhrase = "hey sakura";
        private float resumeAt, microphoneStartedAt, wakeDeadline;

        private bool Companion => Backend != null && Backend.Mode == "Companion";

        private void OnEnable()
        {
            if (Backend == null) Backend = GetComponent<SakuraBackend>();
            boundBackend = Backend;
            if (boundBackend != null) boundBackend.AudioReplyReceived += PlayReply;
            speaker = gameObject.AddComponent<AudioSource>();
            speaker.playOnAwake = false; speaker.spatialBlend = 0;
            if (FindFirstObjectByType<AudioListener>() == null) ownedListener = gameObject.AddComponent<AudioListener>();
        }

        public void RefreshSettings()
        {
            if (Backend == null || loadingSettings) return;
            loadingSettings = true; settingsReady = false;
            Backend.Api("/api/settings", null, (json, error) =>
            {
                loadingSettings = false;
                if (this == null || !isActiveAndEnabled) return;
                if (error != null) { StopListening(); SetStatus("Cannot read voice settings: " + error); return; }
                try
                {
                    var settings = JsonUtility.FromJson<Settings>(json);
                    wakeEnabled = settings.wake_word_enabled;
                    wakePhrase = string.IsNullOrWhiteSpace(settings.wake_word) ? "hey sakura" : settings.wake_word.Trim();
                    settingsReady = true; wakeDeadline = 0;
                }
                catch (Exception) { StopListening(); SetStatus("Voice settings were invalid. Reconnect and retry."); }
            });
        }

        public void StartListening() { StartInput(false); }
        public void BeginPushToTalk() { StartInput(true); }

        private void StartInput(bool held)
        {
            if (!isActiveAndEnabled) return;
            if (!Companion) { SetStatus("Microphone OFF · available in Companion mode"); return; }
            StopListening(); StopSpeaking();
            pushToTalk = held; Listening = true;
            ListeningChanged?.Invoke(true);
            RefreshSettings();
            StartCoroutine(OpenMicrophone(generation));
        }

        private IEnumerator OpenMicrophone(int captureGeneration)
        {
            if (starting) yield break;
            starting = true;
            try
            {
                SetStatus("Reading voice settings…");
                float deadline = Time.realtimeSinceStartup + 15;
                while (!settingsReady && Listening && captureGeneration == generation && Time.realtimeSinceStartup < deadline) yield return null;
                if (!CanCapture(captureGeneration)) yield break;
                if (!settingsReady) { FailInput("Could not load voice settings. Reconnect and retry."); yield break; }
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                    yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
                if (!CanCapture(captureGeneration)) yield break;
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { FailInput("Microphone permission denied. Enable microphone access in Windows settings."); yield break; }
                string[] devices = Microphone.devices;
                if (devices.Length == 0) { FailInput("No microphone found. Connect a microphone and retry."); yield break; }
                activeDevice = string.IsNullOrEmpty(MicrophoneDevice) ? devices[0] : MicrophoneDevice;
                if (Array.IndexOf(devices, activeDevice) < 0) { FailInput("Selected microphone is unavailable. Choose a connected device."); yield break; }
                string startError = null;
                try { microphone = Microphone.Start(activeDevice, true, 2, 16000); }
                catch (Exception error) { startError = error.Message; }
                if (microphone == null) { FailInput("Microphone could not start. " + startError); yield break; }
                microphoneStartedAt = Time.realtimeSinceStartup; readFrame = 0;
                gate = new SpeechGate(microphone.frequency, SpeechThreshold, .3f, .65f, 15f);
                heldSpeech.Clear();
                SetStatus(pushToTalk ? "Microphone ON · speak, then release" : ListeningText());
            }
            finally { starting = false; }
        }

        private bool CanCapture(int captureGeneration) => Listening && captureGeneration == generation && isActiveAndEnabled && Companion;
        private string ListeningText() => wakeEnabled ? "Microphone ON · transcript wake gate: say " + wakePhrase : "Microphone ON · listening";

        private void Update()
        {
            if (!Companion)
            {
                if (Listening || recognizing) StopListening();
                if (Speaking) StopSpeaking();
                return;
            }
            if (!Listening) return;
            if (Speaking || Backend.Busy || recognizing || Time.realtimeSinceStartup < resumeAt)
            {
                if (microphone != null) CloseMicrophone();
                return;
            }
            if (microphone == null)
            {
                if (!starting) StartCoroutine(OpenMicrophone(generation));
                return;
            }
            int position = Microphone.GetPosition(activeDevice);
            if (position < 0 || !Microphone.IsRecording(activeDevice)) { FailInput("Microphone disconnected or stopped. Check the selected device and retry."); return; }
            if (position == 0 && Time.realtimeSinceStartup - microphoneStartedAt > 3) { FailInput("Microphone produced no audio. Check Windows microphone access and the selected device."); return; }
            int available = (position - readFrame + microphone.samples) % microphone.samples;
            int block = Mathf.Max(1, microphone.frequency / 50);
            while (available >= block && microphone != null)
            {
                var interleaved = new float[block * microphone.channels];
                if (!microphone.GetData(interleaved, readFrame)) { FailInput("Could not read microphone audio. Select another microphone and retry."); return; }
                var mono = new float[block];
                for (int frame = 0; frame < block; frame++)
                    for (int channel = 0; channel < microphone.channels; channel++) mono[frame] += interleaved[frame * microphone.channels + channel] / microphone.channels;
                readFrame = (readFrame + block) % microphone.samples; available -= block;
                InputLevel = Rms(mono);
                if (pushToTalk)
                {
                    heldSpeech.AddRange(mono);
                    if (heldSpeech.Count >= microphone.frequency * 45) { EndPushToTalk(); return; }
                }
                else if (gate.Push(mono)) { Submit(gate.Take(), microphone.frequency); return; }
            }
        }

        public void EndPushToTalk()
        {
            if (!Listening || !pushToTalk) return;
            int rate = microphone != null ? microphone.frequency : 16000;
            float[] pcm = heldSpeech.ToArray();
            CloseMicrophone(); Listening = false; ListeningChanged?.Invoke(false);
            if (pcm.Length < rate * .25f || Rms(pcm) < SpeechThreshold) { SetStatus("Microphone OFF · no clear speech captured. Hold the microphone while speaking."); return; }
            Submit(pcm, rate);
        }

        private void Submit(float[] pcm, int sampleRate)
        {
            int captureGeneration = generation;
            bool wasHeld = pushToTalk;
            CloseMicrophone(); recognizing = true; SetStatus("Recognising speech…");
            Backend.UploadAudio(EncodeWav(pcm, sampleRate), (json, error) =>
            {
                if (this == null || !isActiveAndEnabled || captureGeneration != generation || !Companion) return;
                recognizing = false; resumeAt = Time.realtimeSinceStartup + .25f;
                if (error != null) { SetStatus("Speech recognition failed: " + error + ". Check the microphone and retry."); return; }
                Recognition result;
                try { result = JsonUtility.FromJson<Recognition>(json); }
                catch (Exception) { SetStatus("Speech recognition returned an invalid response. Retry."); return; }
                if (result == null) { SetStatus("Speech recognition returned no response. Retry."); return; }
                if (result.speaker_rejected) { SetStatus(string.IsNullOrEmpty(result.speaker_message) ? "Voice profile did not match. Re-record your recognised voice in Voice & speech." : result.speaker_message); return; }
                if (!string.IsNullOrEmpty(result.error)) { SetStatus(result.error); return; }
                string text = (result.text ?? "").Trim();
                if (text.Length == 0) { SetStatus("No clear speech recognised. Check the selected microphone and try again."); return; }
                if (wakeEnabled && !wasHeld)
                {
                    // shortcut: transcript gating requires STT; it is not the original offline wake detector.
                    string command = ExtractWakeCommand(text, wakePhrase);
                    if (command != null)
                    {
                        if (command.Length == 0) { wakeDeadline = Time.realtimeSinceStartup + 15; SetStatus("Ready · say your question within 15 seconds"); return; }
                        text = command;
                    }
                    else if (Time.realtimeSinceStartup >= wakeDeadline) { SetStatus("Say " + wakePhrase + " first · transcript wake gate"); return; }
                    wakeDeadline = 0;
                }
                TranscriptReceived?.Invoke(text);
                Backend.SendChat(text);
                SetStatus("Speech sent · microphone paused during Sakura’s reply");
            });
        }

        private void PlayReply(string relativeAudio)
        {
            if (!Companion || !isActiveAndEnabled || string.IsNullOrEmpty(relativeAudio)) return;
            StopSpeaking(); CloseMicrophone();
            int playback = playbackGeneration;
            SetStatus("Loading Sakura’s voice…");
            Backend.DownloadAudio(relativeAudio, (clip, error) =>
            {
                if (this == null || !isActiveAndEnabled || playback != playbackGeneration || !Companion)
                { if (clip != null) Destroy(clip); return; }
                if (error != null || clip == null) { SetStatus("Voice playback failed: " + (error ?? "empty audio")); resumeAt = Time.realtimeSinceStartup + .65f; return; }
                speaker.clip = clip; speaker.Play();
                StartCoroutine(CompletePlayback(playback));
            });
        }

        private IEnumerator CompletePlayback(int playback)
        {
            SetStatus("Sakura speaking · microphone paused to prevent echo");
            while (speaker != null && speaker.isPlaying && playback == playbackGeneration) yield return null;
            if (playback != playbackGeneration) yield break;
            ReleasePlayback(); resumeAt = Time.realtimeSinceStartup + .65f;
            SetStatus(Listening ? ListeningText() : "Microphone OFF");
        }

        public void StopSpeaking()
        {
            playbackGeneration++; ReleasePlayback(); resumeAt = Time.realtimeSinceStartup + .65f;
        }

        private void ReleasePlayback()
        {
            if (speaker == null) return;
            speaker.Stop(); var clip = speaker.clip; speaker.clip = null;
            if (clip != null) Destroy(clip);
        }

        public void StopListening()
        {
            generation++; recognizing = false; wakeDeadline = 0;
            bool wasListening = Listening; Listening = false; CloseMicrophone(); heldSpeech.Clear();
            if (wasListening) ListeningChanged?.Invoke(false);
            SetStatus("Microphone OFF");
        }

        private void CloseMicrophone()
        {
            if (microphone != null)
            {
                Microphone.End(activeDevice); Destroy(microphone); microphone = null;
            }
            InputLevel = 0; readFrame = 0; gate = null;
        }

        private void FailInput(string message) { StopListening(); SetStatus(message); }
        private void SetStatus(string message) { if (Status == message) return; Status = message; StatusChanged?.Invoke(message); }
        private void OnDisable()
        {
            if (boundBackend != null) boundBackend.AudioReplyReceived -= PlayReply;
            StopAllCoroutines(); starting = false; loadingSettings = false;
            StopListening(); StopSpeaking();
            if (speaker != null) { Destroy(speaker); speaker = null; }
            if (ownedListener != null) { Destroy(ownedListener); ownedListener = null; }
        }

        public static byte[] EncodeWav(float[] samples, int sampleRate)
        {
            if (samples == null || sampleRate < 8000 || sampleRate > 192000) throw new ArgumentException("Invalid PCM audio.");
            using (var stream = new MemoryStream(44 + samples.Length * 2))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
                foreach (float sample in samples)
                {
                    if (float.IsNaN(sample) || float.IsInfinity(sample)) throw new ArgumentException("PCM contains nonfinite values.");
                    writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, sample)) * (sample < 0 ? 32768 : 32767)));
                }
                return stream.ToArray();
            }
        }

        public static float Rms(float[] samples)
        {
            if (samples.Length == 0) return 0;
            double sum = 0; foreach (float sample in samples) sum += sample * sample;
            return (float)Math.Sqrt(sum / samples.Length);
        }

        public static string ExtractWakeCommand(string text, string phrase)
        {
            string[] words = Regex.Split((phrase ?? "").Trim(), @"[^\p{L}\p{M}\p{N}]+");
            var escaped = new List<string>();
            foreach (string word in words) if (word.Length > 0) escaped.Add(Regex.Escape(word));
            if (escaped.Count == 0 || text == null) return null;
            var match = Regex.Match(text, @"(?<![\p{L}\p{M}\p{N}])" + string.Join(@"[^\p{L}\p{M}\p{N}]+", escaped) + @"(?![\p{L}\p{M}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? Regex.Replace(text.Substring(match.Index + match.Length), @"^[\s,\.!?।]+", "").Trim() : null;
        }

        public sealed class SpeechGate
        {
            private readonly int preRoll, minimum, silence, maximum;
            private readonly float threshold;
            private readonly List<float> segment = new List<float>();
            private int voiced, quiet;
            private bool active;
            public SpeechGate(int rate, float threshold, float minimumSeconds, float silenceSeconds, float maximumSeconds)
            {
                this.threshold = threshold; preRoll = rate * 3 / 10;
                minimum = (int)(rate * minimumSeconds); silence = (int)(rate * silenceSeconds); maximum = (int)(rate * maximumSeconds);
            }
            public bool Push(float[] block)
            {
                bool speech = Rms(block) >= threshold;
                if (!active && !speech)
                {
                    segment.AddRange(block);
                    if (segment.Count > preRoll) segment.RemoveRange(0, segment.Count - preRoll);
                    return false;
                }
                active = true; segment.AddRange(block);
                if (speech) { voiced += block.Length; quiet = 0; } else quiet += block.Length;
                if (quiet < silence && segment.Count < maximum) return false;
                if (voiced >= minimum) return true;
                segment.Clear(); voiced = 0; quiet = 0; active = false; return false;
            }
            public float[] Take()
            {
                var result = segment.ToArray(); segment.Clear(); voiced = 0; quiet = 0; active = false; return result;
            }
        }
    }
}
