using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed partial class SakuraBackend
    {
        public UnityWebRequest CreateVoiceChangerConversion(byte[] wav, bool rvc = true)
        {
            if (!Connected || Mode != "VTuber") throw new InvalidOperationException("Microphone voice conversion requires connected VTuber mode.");
            if (wav == null || wav.Length < 44 || wav.Length > 12 * 1024 * 1024) throw new ArgumentException("Use a WAV segment under 12 MB.");
            var form = new WWWForm(); form.AddBinaryData("audio", wav, "microphone.wav", "audio/wav");
            var request = Request(rvc ? "/api/voice-changer/convert" : "/api/voice-changer/speak");
            request.method = "POST"; request.timeout = 120; request.uploadHandler = new UploadHandlerRaw(form.data);
            foreach (var header in form.headers) request.SetRequestHeader(header.Key, header.Value);
            request.downloadHandler.Dispose(); request.downloadHandler = new SakuraVoiceChanger.BoundedDownload();
            return request;
        }
    }

    /// <summary>Explicit VTuber microphone capture with one local RVC conversion in flight.</summary>
    public sealed class SakuraVoiceChanger : MonoBehaviour
    {
        public bool Active { get; private set; }
        public bool Starting { get; private set; }
        public bool Converting => conversion != null;
        public AudioSource PlaybackSource => playback;
        public string Status { get; private set; } = "Microphone voice changer off";
        public string InputDevice { get; set; } = "";
        public event Action Changed;
        private SakuraBackend backend;
        private SakuraVoice voice;
        private AudioClip microphone;
        private AudioSource playback;
        private SakuraVoice.SpeechGate gate;
        private UnityWebRequest conversion;
        private UnityWebRequest decoder;
        private string temporaryAudio;
        private bool customRvc;
        private string device;
        private int revision, readFrame;
        private float started, resumeAt;

        private void Awake()
        {
            backend = GetComponent<SakuraBackend>(); voice = GetComponent<SakuraVoice>();
            playback = gameObject.AddComponent<AudioSource>(); playback.playOnAwake = false; playback.spatialBlend = 0;
        }
        private bool Current(int token) => this != null && isActiveAndEnabled && token == revision && backend != null && backend.Mode == "VTuber";
        private void Report(string value) { Status = value; Changed?.Invoke(); }
        public void StartConversion()
        {
            if (Starting || Active) return;
            if (backend == null || backend.Mode != "VTuber") { Report("Return to VTuber mode before starting microphone conversion."); return; }
            if (voice?.Speaking == true) { Report("Wait for speech playback to finish before starting the voice changer."); return; }
            int token = ++revision; Starting = true; Report("Reading your saved voice selection…");
            backend.Api("/api/settings", null, (json, error) =>
            {
                if (!Current(token)) return;
                if (error != null) { Fail(error); return; }
                try
                {
                    var settings = JObject.Parse(json);
                    string selected = settings.Value<string>("selected_voice");
                    if (!ValidSavedVoice(selected, settings.Value<string>("rvc_model"), true))
                    { Fail("Select an installed voice and save settings before starting."); return; }
                    customRvc = selected.StartsWith("rvc_", StringComparison.Ordinal);
                }
                catch (Newtonsoft.Json.JsonException) { Fail("The saved voice settings were invalid."); return; }
                backend.Api("/api/unity/start-voice", "{}", (_, startupError) =>
                {
                    if (!Current(token)) return;
                    if (startupError != null) { Fail(startupError); return; }
                    if (!customRvc) { StartCoroutine(OpenMicrophone(token)); return; }
                    Report("Loading the saved RVC model…");
                    backend.Api("/api/voice-changer/prepare", "{}", (prepared, prepareError) =>
                    {
                        if (!Current(token)) { if (this != null && !Active && !Starting) StopEngine(); return; }
                        if (prepareError != null) { Fail(prepareError); return; }
                        StartCoroutine(OpenMicrophone(token));
                    });
                });
            });
        }
        public static bool ValidSavedVoice(string id, string model, bool allowBuiltIn = false) => id != null &&
            (id.StartsWith("rvc_", StringComparison.Ordinal) ? !string.IsNullOrWhiteSpace(model) : allowBuiltIn && (id.StartsWith("kokoro_", StringComparison.Ordinal) || id.StartsWith("system_", StringComparison.Ordinal) || id.StartsWith("gpt_sovits_", StringComparison.Ordinal)));
        private IEnumerator OpenMicrophone(int token)
        {
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (!Current(token)) yield break;
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { Fail("Microphone permission denied. Enable microphone access in Windows settings."); yield break; }
            string[] devices = Microphone.devices;
            device = string.IsNullOrEmpty(InputDevice) && devices.Length > 0 ? devices[0] : InputDevice;
            if (Array.IndexOf(devices, device) < 0) { Fail("The selected microphone is unavailable."); yield break; }
            string failure = null;
            try { microphone = Microphone.Start(device, true, 2, 16000); }
            catch (Exception error) { failure = error.Message; }
            if (microphone == null) { Fail("Could not open the microphone. " + failure); yield break; }
            started = Time.unscaledTime; readFrame = 0;
            gate = new SakuraVoice.SpeechGate(microphone.frequency, .012f, .12f, .72f, 9);
            Starting = false; Active = true; Report(customRvc ? "RVC microphone on · speak, then pause briefly" : "Built-in microphone voice on · speak, then pause briefly");
        }
        private void Update()
        {
            if ((!Active && !Starting) || backend == null) return;
            if (backend.Mode != "VTuber") { StopConversion(); return; }
            if (Starting || !Active) return;
            if (conversion != null || playback.isPlaying || voice?.Speaking == true || Time.unscaledTime < resumeAt)
            {
                CloseMicrophone(); return;
            }
            if (microphone == null) { Starting = true; StartCoroutine(OpenMicrophone(revision)); return; }
            int position = Microphone.GetPosition(device);
            if (position < 0 || !Microphone.IsRecording(device)) { Fail("Microphone disconnected. Reconnect it and retry."); return; }
            if (position == 0 && Time.unscaledTime - started > 3) { Fail("Microphone produced no audio. Check Windows microphone access."); return; }
            int available = (position - readFrame + microphone.samples) % microphone.samples;
            int block = Math.Max(1, microphone.frequency / 50);
            while (available >= block && microphone != null)
            {
                var interleaved = new float[block * microphone.channels];
                if (!microphone.GetData(interleaved, readFrame)) { Fail("Could not read microphone audio."); return; }
                var mono = new float[block];
                for (int frame = 0; frame < block; frame++) for (int channel = 0; channel < microphone.channels; channel++) mono[frame] += interleaved[frame * microphone.channels + channel] / microphone.channels;
                readFrame = (readFrame + block) % microphone.samples; available -= block;
                if (!gate.Push(mono)) continue;
                byte[] wav = SakuraVoice.EncodeWav(gate.Take(), microphone.frequency);
                CloseMicrophone(); StartCoroutine(Convert(wav, revision)); return;
            }
        }
        private IEnumerator Convert(byte[] wav, int token)
        {
            UnityWebRequest request = null;
            string failure = null;
            try { request = backend.CreateVoiceChangerConversion(wav, customRvc); conversion = request; }
            catch (Exception error) { failure = error.Message; }
            if (failure != null) { Fail(failure); yield break; }
            Report("Converting microphone segment locally…");
            try
            {
                yield return request.SendWebRequest();
                if (!Current(token) || !Active) yield break;
                if (request.result != UnityWebRequest.Result.Success)
                { Fail(SakuraBackend.ResponseError(Encoding.UTF8.GetString(request.downloadHandler.data ?? Array.Empty<byte>()), request.error)); yield break; }
                AudioClip clip = null;
                if ((request.GetResponseHeader("Content-Type") ?? "").StartsWith("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        temporaryAudio = Path.Combine(Path.GetTempPath(), "sakura-voice-" + Guid.NewGuid().ToString("N") + ".mp3");
                        File.WriteAllBytes(temporaryAudio, request.downloadHandler.data);
                        decoder = UnityWebRequestMultimedia.GetAudioClip(new Uri(temporaryAudio).AbsoluteUri, AudioType.MPEG);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException) { failure = error.Message; }
                    if (failure != null) { Fail(failure); yield break; }
                    yield return decoder.SendWebRequest();
                    if (!Current(token) || !Active) yield break;
                    if (decoder.result != UnityWebRequest.Result.Success) { Fail("Could not decode converted voice: " + decoder.error); yield break; }
                    clip = DownloadHandlerAudioClip.GetContent(decoder);
                    if (clip == null || clip.length > 30) { if (clip != null) Destroy(clip); Fail("Converted voice exceeds the supported 30-second segment."); yield break; }
                    decoder.Dispose(); decoder = null; DeleteTemporaryAudio();
                }
                else
                {
                    float[] samples = null; int rate = 0, channels = 0;
                    try { samples = DecodeWave(request.downloadHandler.data, out rate, out channels); }
                    catch (ArgumentException error) { failure = error.Message; }
                    if (failure != null) { Fail(failure); yield break; }
                    clip = AudioClip.Create("Microphone voice conversion", samples.Length / channels, channels, rate, false);
                    clip.SetData(samples, 0);
                }
                playback.clip = clip; playback.Play(); Report("Converted voice playing · microphone paused to prevent echo");
                while (Current(token) && Active && playback.isPlaying) yield return null;
                if (!Current(token) || !Active) yield break;
                ReleasePlayback(); resumeAt = Time.unscaledTime + .65f; Report(customRvc ? "RVC microphone on · listening" : "Built-in microphone voice on · listening");
            }
            finally
            {
                if (decoder != null) { decoder.Dispose(); decoder = null; } DeleteTemporaryAudio();
                if (conversion == request) { conversion = null; request.Dispose(); }
            }
        }
        public void StopConversion()
        {
            bool used = Active || Starting || conversion != null;
            revision++; Active = Starting = false;
            if (conversion != null) { conversion.Abort(); conversion.Dispose(); conversion = null; }
            if (decoder != null) { decoder.Abort(); decoder.Dispose(); decoder = null; }
            DeleteTemporaryAudio();
            StopAllCoroutines(); CloseMicrophone(); ReleasePlayback();
            if (used && customRvc) StopEngine();
            Report("Microphone voice changer off");
        }
        private void StopEngine() { if (backend != null && backend.Connected) backend.Api("/api/voice-changer/stop", "{}", (_, error) => { if (this != null && error != null && !Active && !Starting) Report(error); }); }
        private void Fail(string error) { StopConversion(); Report(error); }
        private void DeleteTemporaryAudio()
        {
            if (temporaryAudio == null) return;
            try { File.Delete(temporaryAudio); temporaryAudio = null; }
            catch (IOException error) { Debug.LogWarning("Could not remove temporary converted voice audio: " + error.Message); }
            catch (UnauthorizedAccessException error) { Debug.LogWarning("Could not remove temporary converted voice audio: " + error.Message); }
        }
        private void CloseMicrophone() { if (microphone == null) return; Microphone.End(device); Destroy(microphone); microphone = null; gate = null; }
        private void ReleasePlayback() { if (playback == null) return; playback.Stop(); var clip = playback.clip; playback.clip = null; if (clip != null) Destroy(clip); }
        private void OnDisable() { StopConversion(); }
        private void OnDestroy() { DeleteTemporaryAudio(); if (playback != null) Destroy(playback); }

        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            card.Add(new Label("Microphone voice changer"));
            var modes = new Label("Custom RVC voices convert your audio locally. Built-in voices transcribe locally and speak the recognized words through your saved voice."); modes.AddToClassList("description"); card.Add(modes);
            var devices = new System.Collections.Generic.List<string> { "System default" }; devices.AddRange(Microphone.devices);
            var input = new DropdownField("Microphone", devices, Math.Max(0, devices.IndexOf(InputDevice))); card.Add(input);
            input.RegisterValueChangedCallback(_ => InputDevice = input.index == 0 ? "" : input.value);
            var start = new Button(StartConversion) { text = "Start microphone voice changer" }; card.Add(start);
            var stop = new Button(StopConversion) { text = "Stop microphone voice changer" }; card.Add(stop);
            var status = new Label(Status); status.AddToClassList("description"); card.Add(status);
            var routing = new Label("Converted audio plays through Unity’s current Windows output device. To send it to OBS or a call app, select your virtual cable as Sakura’s output in Windows Volume mixer, then select the cable’s recording endpoint in that app. Use headphones to avoid speaker feedback."); routing.AddToClassList("description"); card.Add(routing);
            Action refresh = () => { status.text = Status; start.SetEnabled(!Active && !Starting && backend.Mode == "VTuber"); stop.SetEnabled(Active || Starting); input.SetEnabled(!Active && !Starting); };
            Changed += refresh; card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= refresh); refresh();
        }

        public sealed class BoundedDownload : DownloadHandlerScript
        {
            private readonly MemoryStream bytes = new MemoryStream();
            public BoundedDownload() : base(new byte[65536]) { }
            protected override bool ReceiveData(byte[] data, int count)
            { if (data == null || count < 0 || count > data.Length || bytes.Length + count > 12 * 1024 * 1024) return false; bytes.Write(data, 0, count); return true; }
            protected override byte[] GetData() => bytes.ToArray();
        }

        public static float[] DecodeWave(byte[] bytes, out int rate, out int channels)
        {
            rate = channels = 0;
            if (bytes == null || bytes.Length < 44 || bytes.Length > 12 * 1024 * 1024) throw new ArgumentException("Converted WAV must be under 12 MB.");
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new ArgumentException("Conversion returned an invalid WAV.");
                uint riffSize = reader.ReadUInt32();
                if (riffSize > bytes.Length - 8 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new ArgumentException("Conversion returned an invalid WAV.");
                int format = 0, bits = 0, offset = 0, length = 0;
                while (reader.BaseStream.Position + 8 <= bytes.Length)
                {
                    string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint size = reader.ReadUInt32(); long end = reader.BaseStream.Position + size;
                    if (end > bytes.Length) throw new ArgumentException("Converted WAV contains a truncated chunk.");
                    if (chunk == "fmt ")
                    {
                        if (size < 16) throw new ArgumentException("Converted WAV has an invalid format chunk.");
                        format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                    }
                    else if (chunk == "data") { offset = (int)reader.BaseStream.Position; length = (int)size; }
                    reader.BaseStream.Position = end + (size & 1);
                }
                if (rate < 8000 || rate > 192000 || channels < 1 || channels > 8 || !((format == 1 && (bits == 16 || bits == 24 || bits == 32)) || (format == 3 && bits == 32))) throw new ArgumentException("Converted WAV uses an unsupported audio format.");
                int sampleBytes = bits / 8;
                if (offset == 0 || length <= 0 || length % (sampleBytes * channels) != 0 || length / sampleBytes > rate * channels * 30) throw new ArgumentException("Converted WAV has an invalid or oversized audio segment.");
                var result = new float[length / sampleBytes]; reader.BaseStream.Position = offset;
                for (int index = 0; index < result.Length; index++)
                {
                    float sample;
                    if (format == 3) sample = reader.ReadSingle();
                    else if (bits == 16) sample = reader.ReadInt16() / 32768f;
                    else if (bits == 32) sample = reader.ReadInt32() / 2147483648f;
                    else { int value = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16; if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000); sample = value / 8388608f; }
                    if (float.IsNaN(sample) || float.IsInfinity(sample)) throw new ArgumentException("Converted WAV contains invalid samples.");
                    result[index] = Math.Max(-1, Math.Min(1, sample));
                }
                return result;
            }
        }
    }
}
