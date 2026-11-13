using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraVoiceChangerChecks
    {
        [MenuItem("Sakura/Validation/Check native RVC microphone conversion")]
        public static void Run()
        {
            Require(SakuraVoiceChanger.ValidSavedVoice("rvc_my_voice", "voices/my_voice/model.pth"), "saved RVC selection accepted");
            Require(!SakuraVoiceChanger.ValidSavedVoice("kokoro_af_heart", "model.pth") && !SakuraVoiceChanger.ValidSavedVoice("rvc_my_voice", ""), "built-in or missing model rejected");
            Require(SakuraVoiceChanger.ValidSavedVoice("kokoro_hf_beta_hinglish", "", true) && SakuraVoiceChanger.ValidSavedVoice("system_hi_female", "", true), "saved built-in voice mode accepted explicitly");
            Require(!SakuraVoiceChanger.ValidSavedVoice("arbitrary_unknown_voice", "", true) && !SakuraVoiceChanger.ValidSavedVoice("rvc_missing", "", true), "unknown and incomplete modes rejected");
            byte[] pcm = SakuraVoice.EncodeWav(new[] { -1f, 0f, .5f, 1f }, 16000);
            float[] decoded = SakuraVoiceChanger.DecodeWave(pcm, out int rate, out int channels);
            Require(rate == 16000 && channels == 1 && decoded.Length == 4 && decoded[0] == -1 && Math.Abs(decoded[2] - .5f) < .0001f, "PCM16 conversion response decoded");
            Require(Rejects(new byte[44]), "non-WAV response rejected");
            byte[] truncated = (byte[])pcm.Clone(); truncated[40] = 255;
            Require(Rejects(truncated), "truncated data chunk rejected");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(40); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)3); writer.Write((short)1); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)32);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(4); writer.Write(.25f);
                float[] floating = SakuraVoiceChanger.DecodeWave(stream.ToArray(), out rate, out channels);
                Require(floating.Length == 1 && floating[0] == .25f && rate == 48000, "float32 WAV response decoded");
                stream.Position = 44; writer.Write(float.NaN);
                Require(Rejects(stream.ToArray()), "nonfinite audio rejected");
            }
            var gate = new SakuraVoice.SpeechGate(16000, .012f, .12f, .72f, 9);
            Require(!gate.Push(new float[16000]), "silence never queues conversion");
            var speech = new float[3200]; Array.Fill(speech, .05f);
            Require(!gate.Push(speech) && gate.Push(new float[11520]), "utterance pause queues one bounded segment");
            Require(gate.Take().Length < 16000 * 10, "utterance remains bounded");
            Debug.Log("PASS: native RVC saved-selection validation, PCM16/float32 decoding, malformed/nonfinite audio rejection and bounded VAD. No microphone, playback, service startup or settings changed.");
        }
        private static bool Rejects(byte[] bytes)
        { try { SakuraVoiceChanger.DecodeWave(bytes, out _, out _); return false; } catch (ArgumentException) { return true; } }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Voice changer check failed: " + message); }
    }
}
