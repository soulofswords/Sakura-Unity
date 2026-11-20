using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Sakura.Unity
{
    /// <summary>Explicitly enabled native microphone capture. Recognition and language stay in the existing backend.</summary>
    [DisallowMultipleComponent]
    public sealed partial class SakuraVoice : MonoBehaviour
    {
        public bool SpeechInputBusy => recognizing || enrolling;
        public enum AudioOutput { Headphones, Speakers }
        private const string OutputPreference = "sakura_audio_output", BargePreference = "sakura_barge_in";
        private AudioOutput output = AudioOutput.Speakers;
        private bool bargeInEnabled = true;
        public AudioOutput Output
        {
            get => output;
            set { if (output == value) return; output = value; PlayerPrefs.SetInt(OutputPreference, (int)value); PlayerPrefs.Save(); }
        }
        public bool BargeInEnabled
        {
            get => bargeInEnabled;
            set { if (bargeInEnabled == value) return; bargeInEnabled = value; PlayerPrefs.SetInt(BargePreference, value ? 1 : 0); PlayerPrefs.Save(); }
        }
        public bool SpokenInterruptionEnabled { get => BargeInEnabled; set => BargeInEnabled = value; }
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
        public AudioSource SpeechSource => speaker;
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
        private bool microphoneHasSamples;
        private bool enrolling, resumeAfterEnrolment;
        private bool captureInterruptOnly;
        private SakuraAvatarActions sleepActions;
        private string pendingSpeechText;
        private readonly PlaybackEchoGuard echoGuard = new PlaybackEchoGuard();
        private readonly BargeInDetector bargeIn = new BargeInDetector();
        private bool bargeInTriggered, ducked, bargeInHadPlayback;
        private float bargeInLatchAt;
        private AudioOutput captureOutput = AudioOutput.Speakers;
        private float savedVolume = 1f;
        private float[] playbackSamples;
        private bool playbackRmsUnavailable;
        private string wakePhrase = "hey sakura";
        public string WakePhrase => wakePhrase;
        private string playbackMode;
        private float resumeAt, microphoneStartedAt, wakeDeadline;

        private bool Companion => Backend != null && Backend.Mode == "Companion";

        private void OnEnable()
        {
            if (Backend == null) Backend = GetComponent<SakuraBackend>();
            output = PlayerPrefs.GetInt(OutputPreference, (int)AudioOutput.Speakers) == (int)AudioOutput.Headphones ? AudioOutput.Headphones : AudioOutput.Speakers;
            bargeInEnabled = PlayerPrefs.GetInt(BargePreference, 1) != 0;
            boundBackend = Backend;
            if (boundBackend != null) { boundBackend.AudioReplyReceived += PlayReply; boundBackend.ReplyDataReceived += ReadSpeechText; }
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

        public bool TryPauseForEnrolment()
        {
            if (!Companion || !isActiveAndEnabled || enrolling || recognizing || Speaking || Backend.Busy) return false;
            bool wasListening = Listening;
            StopListening();
            enrolling = true; resumeAfterEnrolment = wasListening;
            SetStatus("Voice enrolment · conversation microphone paused");
            return true;
        }

        public void ResumeAfterEnrolment()
        {
            if (!enrolling) return;
            bool resume = resumeAfterEnrolment;
            enrolling = false; resumeAfterEnrolment = false;
            if (resume && Companion && isActiveAndEnabled) StartListening();
            else SetStatus("Microphone OFF");
        }

        private void StartInput(bool held)
        {
            if (!isActiveAndEnabled) return;
            if (enrolling) { SetStatus("Finish voice enrolment before starting conversation listening."); return; }
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
                microphoneStartedAt = Time.realtimeSinceStartup; readFrame = 0; microphoneHasSamples = false;
                captureInterruptOnly = BargeInEnabled && (Speaking || Backend.Busy);
                captureOutput = Output; bargeInHadPlayback = Speaking;
                gate = new SpeechGate(microphone.frequency, SpeechThreshold, .3f, .65f, 15f);
                if (!captureInterruptOnly) { bargeIn.Reset(); bargeInTriggered = false; Unduck(); }
                heldSpeech.Clear();
                SetStatus(pushToTalk ? "Microphone ON · speak, then release" : ListeningText());
            }
            finally { starting = false; }
        }

        private bool CanCapture(int captureGeneration) => Listening && captureGeneration == generation && isActiveAndEnabled && Companion;
        private string ListeningText()
        {
            if (Speaking) return BargeInEnabled
                ? (Output == AudioOutput.Speakers ? "Sakura speaking · say something to interrupt" : "Sakura speaking · talk to interrupt")
                : "Sakura speaking · microphone paused to prevent echo";
            if (Backend.Busy) return BargeInEnabled ? "Working · talk to interrupt" : "Working · microphone paused";
            return wakeEnabled ? "Microphone ON · say " + wakePhrase : "Microphone ON · listening";
        }

        private void Update()
        {
            var actions = GetComponent<SakuraAvatarActions>();
            if (actions != sleepActions)
            {
                if (sleepActions != null) sleepActions.SleepChanged -= SleepChanged;
                sleepActions = actions;
                if (sleepActions != null) sleepActions.SleepChanged += SleepChanged;
            }
            if (enrolling) return;
            if (!Companion)
            {
                if (Listening || recognizing) StopListening();
                if (Speaking && Backend.Mode != playbackMode) StopSpeaking();
                return;
            }
            if (!Listening) return;
            if (bargeInTriggered && !Speaking && !recognizing && (gate == null || !gate.Active) && Time.realtimeSinceStartup - bargeInLatchAt > .8f)
            { bargeInTriggered = false; bargeIn.Reset(); Unduck(); }
            if (recognizing || ((Speaking || Backend.Busy) && !BargeInEnabled) || Time.realtimeSinceStartup < resumeAt)
            {
                if (microphone != null) CloseMicrophone();
                return;
            }
            if (microphone == null)
            {
                if (!starting) StartCoroutine(OpenMicrophone(generation));
                return;
            }
            if (!pushToTalk && captureInterruptOnly != (BargeInEnabled && (Speaking || Backend.Busy || bargeInTriggered))) { CloseMicrophone(); return; }
            int position = Microphone.GetPosition(activeDevice);
            if (position < 0 || !Microphone.IsRecording(activeDevice)) { FailInput("Microphone disconnected or stopped. Check the selected device and retry."); return; }
            if (position > 0) microphoneHasSamples = true;
            if (!microphoneHasSamples && Time.realtimeSinceStartup - microphoneStartedAt > 3)
            { FailInput("Microphone produced no audio. Check Windows microphone access and the selected device."); return; }
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
                if (BargeInEnabled && !pushToTalk && captureInterruptOnly)
                {
                    if (captureOutput == AudioOutput.Headphones && Speaking && !bargeInTriggered && InputLevel >= SpeechThreshold)
                    {
                        StopPlayback(false, true); bargeInTriggered = true; bargeInLatchAt = Time.realtimeSinceStartup; SetStatus("Listening to you…");
                    }
                    else if (captureOutput == AudioOutput.Speakers && Speaking)
                    {
                        var state = bargeIn.Push(InputLevel, PlaybackRms(), block / (float)microphone.frequency);
                        if (state == BargeInState.Confirmed) { bargeInTriggered = true; bargeInLatchAt = Time.realtimeSinceStartup; Duck(); SetStatus("Listening to you…"); }
                        else if (state == BargeInState.Candidate) { bargeInTriggered = true; bargeInLatchAt = Time.realtimeSinceStartup; Duck(); }
                        else if (state == BargeInState.Idle) Unduck();
                    }
                }
                if (pushToTalk)
                {
                    heldSpeech.AddRange(mono);
                    if (heldSpeech.Count >= microphone.frequency * 45) { EndPushToTalk(); return; }
                }
                else if (gate.Push(mono))
                {
                    var pcm = gate.Take();
                    bool barge = captureInterruptOnly;
                    if (barge && captureOutput == AudioOutput.Speakers && bargeInHadPlayback && bargeIn.Armed && !bargeInTriggered)
                    { bargeIn.Reset(); Unduck(); bargeInTriggered = false; continue; }
                    bargeInTriggered = false;
                    Submit(pcm, microphone.frequency, barge);
                    return;
                }
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

        private void Submit(float[] pcm, int sampleRate, bool barge = false)
        {
            int captureGeneration = generation;
            float capturedAt = Time.realtimeSinceStartup;
            bool wasHeld = pushToTalk;
            CloseMicrophone(); recognizing = true; SetStatus("Recognising speech…");
            byte[] wav = EncodeWav(pcm, sampleRate);
            bool offlineApproved = false;
            void Reject(string message)
            {
                if (barge) { Unduck(); bargeIn.Reset(); bargeInTriggered = false; }
                SetStatus(message);
            }
            void Recognize() => Backend.UploadAudio(wav, (json, error) =>
            {
                if (this == null || !isActiveAndEnabled || captureGeneration != generation || !Companion) return;
                recognizing = false; resumeAt = Time.realtimeSinceStartup + .25f;
                if (error != null) { Reject("Speech recognition failed: " + error + ". Check the microphone and retry."); return; }
                Recognition result;
                try { result = JsonUtility.FromJson<Recognition>(json); }
                catch (Exception) { Reject("Speech recognition returned an invalid response. Retry."); return; }
                if (result == null) { Reject("Speech recognition returned no response. Retry."); return; }
                if (result.speaker_rejected) { Reject(string.IsNullOrEmpty(result.speaker_message) ? "Voice profile did not match. Re-record your recognised voice in Voice & speech." : result.speaker_message); return; }
                if (!string.IsNullOrEmpty(result.error)) { Reject(result.error); return; }
                string text = (result.text ?? "").Trim();
                if (text.Length == 0) { Reject("No clear speech recognised. Check the selected microphone and try again."); return; }
                if (!wasHeld && echoGuard.Matches(text, capturedAt, barge) && (barge || ExtractWakeCommand(text, wakePhrase) == null))
                { Reject(barge ? "Speaker echo ignored · continuing" : "Speaker echo ignored · listening"); return; }
                bool woke = false;
                if (sleepActions != null && sleepActions.Sleeping)
                {
                    if (!WakeFromSleep(text, wakePhrase, out string command)) { Reject("Sleeping · say wake up Sakura or your wake phrase"); return; }
                    if (!sleepActions.Act("home")) sleepActions.Stop();
                    text = command; woke = true;
                    if (text.Length == 0) { Reject("Awake · ready to listen"); return; }
                }
                if (barge)
                {
                    if (Speaking) StopSpeaking();
                    if (Backend.Busy) Backend.InterruptChat();
                    string followup = ParseSpeechInterrupt(text, wakePhrase);
                    if (followup != null)
                    {
                        SetStatus("Paused. Ask your next question.");
                        if (followup.Length == 0) { bargeIn.Reset(); bargeInTriggered = false; Unduck(); return; }
                        text = followup;
                    }
                }
                if (wakeEnabled && !wasHeld && !woke && !barge)
                {
                    string command = ExtractWakeCommand(text, wakePhrase);
                    if (command != null)
                    {
                        if (command.Length == 0) { wakeDeadline = Time.realtimeSinceStartup + 15; SetStatus("Ready · say your question within 15 seconds"); return; }
                        text = command;
                    }
                    else if (!offlineApproved && Time.realtimeSinceStartup >= wakeDeadline) { SetStatus("Say " + wakePhrase + " first"); return; }
                    wakeDeadline = 0;
                }
                bargeIn.Reset(); bargeInTriggered = false; Unduck();
                TranscriptReceived?.Invoke(text);
                Backend.SendChat(text);
                SetStatus(Listening ? ListeningText() : "Speech sent · microphone OFF");
            });
            if (wakeEnabled && !wasHeld && !barge && Time.realtimeSinceStartup >= wakeDeadline)
            {
                SetStatus("Checking wake phrase offline…");
                var payload = new Newtonsoft.Json.Linq.JObject { ["audio"] = Convert.ToBase64String(EncodeWakeWav(pcm, sampleRate)) };
                Backend.Api("/api/unity/wake/check", payload.ToString(Newtonsoft.Json.Formatting.None), (json, error) =>
                {
                    if (this == null || !isActiveAndEnabled || captureGeneration != generation || !Companion) return;
                    if (error != null) { recognizing = false; SetStatus("Offline wake failed: " + error); return; }
                    try
                    {
                        var result = Newtonsoft.Json.Linq.JObject.Parse(json);
                        if (result.Value<bool>("available") && !result.Value<bool>("detected"))
                        { recognizing = false; SetStatus("Listening offline · say " + wakePhrase); return; }
                        offlineApproved = result.Value<bool>("available") && result.Value<bool>("detected");
                        Recognize();
                    }
                    catch (Exception failure) { recognizing = false; SetStatus("Wake check failed: " + failure.Message); }
                });
            }
            else Recognize();
        }

        public static byte[] EncodeWakeWav(float[] pcm, int rate)
        {
            if (pcm == null || rate < 8000 || rate > 192000 || pcm.Length > (long)rate * 16)
                throw new ArgumentException("Wake audio must be at most 16 seconds at a supported sample rate.");
            if (rate == 16000) return EncodeWav(pcm, rate);
            var mono = new float[(int)((long)pcm.Length * 16000 / rate)];
            for (int index = 0; index < mono.Length; index++)
            {
                double position = (double)index * rate / 16000;
                int first = (int)position, second = Math.Min(first + 1, pcm.Length - 1);
                mono[index] = Mathf.Lerp(pcm[first], pcm[second], (float)(position - first));
            }
            return EncodeWav(mono, 16000);
        }

        private void PlayReply(string relativeAudio)
        {
            if (!Companion || !isActiveAndEnabled || string.IsNullOrEmpty(relativeAudio))
            {
                Debug.LogWarning("[Sakura] speech ignored: companion=" + Companion + " active=" + isActiveAndEnabled + " audio=" + relativeAudio);
                Backend?.ReleasePendingReply();
                return;
            }
            string text = pendingSpeechText; pendingSpeechText = null;
            PlayAudio(relativeAudio, text);
        }

        public void PlayReadout(string relativeAudio)
        {
            if (!isActiveAndEnabled || Backend == null || Backend.Mode != "VTuber") return;
            PlayAudio(relativeAudio);
        }

        public void PlayCohost(string relativeAudio) => PlayReply(relativeAudio);

        private void ReadSpeechText(Newtonsoft.Json.Linq.JObject reply) { pendingSpeechText = (string)reply["speech_reply"] ?? (string)reply["reply"]; }
        private void SleepChanged(bool sleeping) { if (sleeping) { StopSpeaking(); wakeDeadline = 0; SetStatus("Sleeping · say wake up Sakura or your wake phrase"); } }

        private void PlayAudio(string relativeAudio, string text = null)
        {
            StopSpeaking(); CloseMicrophone();
            int playback = playbackGeneration;
            string mode = Backend.Mode;
            playbackMode = mode;
            SetStatus("Loading Sakura’s voice…");
            Backend.DownloadAudio(relativeAudio, (clip, error) =>
            {
                if (this == null || !isActiveAndEnabled || playback != playbackGeneration || Backend.Mode != mode)
                { if (clip != null) Destroy(clip); return; }
                if (error != null || clip == null)
                {
                    Debug.LogWarning("[Sakura] speech download failed: " + (error ?? "empty audio"));
                    SetStatus("Voice playback failed: " + (error ?? "empty audio")); Backend?.ReleasePendingReply(); resumeAt = Time.realtimeSinceStartup + .65f; return;
                }
                if (speaker == null) { Debug.LogWarning("[Sakura] speech audio source is missing."); Destroy(clip); return; }
                Debug.Log("[Sakura] speech playing " + clip.length.ToString("0.00") + "s " + clip.frequency + "Hz, listener volume " + AudioListener.volume + " mute " + AudioListener.pause);
                speaker.clip = clip; Backend?.ReleasePendingReply(); speaker.Play(); echoGuard.Remember(text, Time.realtimeSinceStartup);
                if (BargeInEnabled && Companion) resumeAt = 0;
                playbackRmsUnavailable = false;
                bargeIn.Reset(); bargeInTriggered = false;
                StartCoroutine(CompletePlayback(playback));
            });
        }

        private IEnumerator CompletePlayback(int playback)
        {
            SetStatus(Listening ? ListeningText() : "Sakura speaking · microphone OFF");
            while (speaker != null && speaker.isPlaying && playback == playbackGeneration) yield return null;
            if (playback != playbackGeneration) yield break;
            echoGuard.Finish(Time.realtimeSinceStartup); ReleasePlayback(); Unduck();
            if (!Speaking && !recognizing && (gate == null || !gate.Active)) { bargeInTriggered = false; bargeIn.Reset(); }
            if (!bargeInTriggered) resumeAt = Time.realtimeSinceStartup + .65f;
            SetStatus(Listening ? ListeningText() : "Microphone OFF");
        }

        public void StopSpeaking() => StopPlayback(true, false);

        private void StopPlayback(bool resume, bool keepBargeIn)
        {
            playbackGeneration++; echoGuard.Finish(Time.realtimeSinceStartup); ReleasePlayback(); Unduck();
            if (!keepBargeIn) { bargeIn.Reset(); bargeInTriggered = false; }
            resumeAt = resume ? Time.realtimeSinceStartup + .65f : 0;
        }

        private void Duck()
        {
            if (speaker == null || ducked) return;
            savedVolume = speaker.volume; speaker.volume = Mathf.Min(speaker.volume, .1f); ducked = true;
        }

        private void Unduck()
        {
            if (speaker == null || !ducked) return;
            speaker.volume = savedVolume; ducked = false;
        }

        private float PlaybackRms()
        {
            var clip = speaker == null ? null : speaker.clip;
            if (clip == null || !speaker.isPlaying) return 0f;
            if (playbackRmsUnavailable) return speaker.volume * .15f;
            int channels = Mathf.Max(1, clip.channels);
            int window = Mathf.Min(clip.samples, Mathf.Max(256, clip.frequency / 20));
            int start = Mathf.Clamp(speaker.timeSamples, 0, Mathf.Max(0, clip.samples - window));
            int length = window * channels;
            if (playbackSamples == null || playbackSamples.Length != length) playbackSamples = new float[length];
            if (!clip.GetData(playbackSamples, start)) { playbackRmsUnavailable = true; return speaker.volume * .15f; }
            double sum = 0;
            foreach (float sample in playbackSamples) sum += sample * sample;
            return (float)Math.Sqrt(sum / length) * speaker.volume;
        }

        private void ReleasePlayback()
        {
            if (speaker == null) return;
            speaker.Stop(); var clip = speaker.clip; speaker.clip = null;
            if (clip != null) Destroy(clip);
        }

        public void StopListening()
        {
            if (enrolling) resumeAfterEnrolment = false;
            generation++; recognizing = false; wakeDeadline = 0;
            bool wasListening = Listening; Listening = false; CloseMicrophone(); heldSpeech.Clear();
            Unduck(); bargeIn.Reset(); bargeInTriggered = false; captureInterruptOnly = false;
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
            if (boundBackend != null) { boundBackend.AudioReplyReceived -= PlayReply; boundBackend.ReplyDataReceived -= ReadSpeechText; }
            if (sleepActions != null) sleepActions.SleepChanged -= SleepChanged;
            sleepActions = null; pendingSpeechText = null;
            StopAllCoroutines(); starting = false; loadingSettings = false;
            StopListening(); StopSpeaking();
            enrolling = false; resumeAfterEnrolment = false;
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

        public static bool WakeFromSleep(string text, string phrase, out string command)
        {
            string raw = (text ?? "").Trim();
            foreach (string prefix in new[] { "wake up sakura", "sakura wake up", "wake up", phrase })
            {
                if (string.IsNullOrWhiteSpace(prefix)) continue;
                string[] words = Regex.Split(prefix.Trim(), @"[\s,.!?।]+").Where(word => word.Length > 0).ToArray();
                if (words.Length == 0) continue;
                string pattern = @"\A" + string.Join(@"[\s,.!?।]+", words.Select(Regex.Escape)) + @"(?:[\s,.!?।]+|$)";
                var match = Regex.Match(raw, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success) { command = raw.Substring(match.Length).Trim(); return true; }
            }
            command = ""; return false;
        }

        public static string ParseSpeechInterrupt(string text, string phrase)
        {
            const string control = @"(?:please\s+)?(?:stop(?:\s+(?:speaking|talking))?|wait(?:\s+(?:a second|a moment))?|hold on|pause|(?:ज़रा\s+|जरा\s+)?रुको(?:\s+(?:ज़रा|जरा))?|रुक जाओ|बस(?: करो)?|ruko(?: zara)?|ruk jao)(?:\s+please)?(?![\p{L}\p{M}\p{N}])";
            const string separators = @"[\s,.!?।:;—–-]*";
            string value = (text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                var prefix = Regex.Match(value, @"\A" + Regex.Escape(phrase.Trim()) + @"[\s,.!?।:;-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (prefix.Success) value = value.Substring(prefix.Length);
            }
            var match = Regex.Match(value, @"\A(?:" + control + ")" + separators + "(?:(?:" + control + ")" + separators + "){0,5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            string followup = Regex.Replace(value.Substring(match.Length), @"\A(?:and\s+)?(?:please\s+)?", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (followup.Length == 0) return "";
            bool repeated = Regex.IsMatch(value, @"\A(?:(?:" + control + @")[\s,.!?।:;—–-]+){2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool question = Regex.IsMatch(followup, @"\A(?:what|why|how|when|where|which|who|tell me|explain|can you|could you|do you|did you|is it|are you|मुझे|बताओ|क्या|क्यों|कैसे|कब|कहाँ|kya|kyun|kaise|batao)(?![\p{L}\p{M}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return repeated || question ? followup : null;
        }

        public sealed class PlaybackEchoGuard
        {
            private sealed class Phrase { public string Text; public float Started, Until; }
            private readonly List<Phrase> phrases = new List<Phrase>();
            private static string[] Words(string text) => Regex.Matches((text ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\p{L}\p{M}\p{N}]+").Cast<Match>().Select(match => match.Value).ToArray();
            public void Remember(string text, float now)
            {
                string[] words = Words(text);
                if (words.Length < 1) return;
                phrases.Add(new Phrase { Text = string.Join(" ", words), Started = now, Until = now + 60 });
                if (phrases.Count > 8) phrases.RemoveAt(0);
            }
            public void Finish(float now)
            { if (phrases.Count > 0) phrases[phrases.Count - 1].Until = Math.Min(phrases[phrases.Count - 1].Until, now + 1.8f); }
            public bool Matches(string text, float capturedAt, bool relaxed = false)
            {
                string[] words = Words(text);
                if (words.Length < (relaxed ? 1 : 4)) return false;
                string candidate = string.Join(" ", words);
                return phrases.Any(phrase =>
                {
                    if (capturedAt < phrase.Started || capturedAt > phrase.Until) return false;
                    if (words.Length == 1) return candidate == phrase.Text;
                    var phraseWords = new HashSet<string>(phrase.Text.Split(' '));
                    return words.All(phraseWords.Contains);
                });
            }
        }

        public sealed class SpeechGate
        {
            private readonly int preRoll, minimum, silence, maximum;
            private readonly float threshold;
            private readonly List<float> segment = new List<float>();
            private int voiced, quiet;
            private bool active;
            public bool Active => active;
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
