using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraVoiceChecks
    {
        [MenuItem("Sakura/Validation/Check voice encoding and gating")]
        public static void Run()
        {
            Require(SakuraBackend.ResponseError(null, "Timed out") == "Timed out", "empty timeout response");
            Require(SakuraBackend.ResponseError("", "Timed out") == "Timed out", "blank timeout response");
            Require(SakuraBackend.ResponseError("null", "Timed out") == "Timed out", "null JSON response");
            Require(SakuraBackend.ResponseError("<html>Unavailable</html>", "503") == "503", "non-JSON failure response");
            Require(SakuraBackend.ResponseError("{\"error\":\"Quota exceeded\"}", "503") == "Quota exceeded", "provider error retained");
            Require(!SakuraBackend.ValidAudioPath("https://example.com/audio.wav") && !SakuraBackend.ValidAudioPath("/audio/../private.wav") && SakuraBackend.ValidAudioPath("/audio/speech-123.wav"), "audio route confinement");
            byte[] wav = SakuraVoice.EncodeWav(new[] { -2f, -1f, 0f, 1f, 2f }, 16000);
            using (var reader = new BinaryReader(new MemoryStream(wav)))
            {
                Require(new string(reader.ReadChars(4)) == "RIFF", "RIFF header");
                Require(reader.ReadInt32() == 46, "RIFF length"); reader.BaseStream.Position = 22;
                Require(reader.ReadInt16() == 1 && reader.ReadInt32() == 16000, "mono 16 kHz");
                reader.BaseStream.Position = 40; Require(reader.ReadInt32() == 10, "PCM byte count");
                Require(reader.ReadInt16() == -32768 && reader.ReadInt16() == -32768, "negative clipping");
                Require(reader.ReadInt16() == 0 && reader.ReadInt16() == 32767 && reader.ReadInt16() == 32767, "PCM silence and positive clipping");
            }
            Require(SakuraVoice.Rms(new[] { .5f, -.5f }) == .5f, "RMS uses both polarities");
            Require(SakuraVoice.Rms(Array.Empty<float>()) == 0, "empty PCM");
            Require(SakuraVoice.ExtractWakeCommand("Hey, Sakura! नमस्ते", "hey sakura") == "नमस्ते", "punctuation and Hindi preserved");
            Require(SakuraVoice.ExtractWakeCommand("they sakurana", "hey sakura") == null, "whole phrase boundaries");
            Require(SakuraVoice.ExtractWakeCommand("हे सकुरा, क्या हाल है?", "हे सकुरा") == "क्या हाल है?", "Unicode wake phrase");
            var gate = new SakuraVoice.SpeechGate(16000, .01f, .3f, .6f, 15f);
            Require(!gate.Push(new float[16000]), "silence does not send");
            var speech = new float[8000]; Array.Fill(speech, .05f);
            Require(!gate.Push(speech), "speech starts segment");
            Require(gate.Push(new float[9600]), "silence ends speech");
            Require(gate.Take().Length == 22400, "pre-roll, speech and trailing silence retained");
            var noise = new SakuraVoice.SpeechGate(16000, .01f, .3f, .6f, 15f);
            Require(!noise.Push(new[] { .9f }), "click starts candidate");
            Require(!noise.Push(new float[9600]), "short click never transcribes");
            bool invalid = false;
            try { SakuraVoice.EncodeWav(new[] { float.NaN }, 16000); } catch (ArgumentException) { invalid = true; }
            Require(invalid, "nonfinite PCM rejected");
            Debug.Log("PASS: voice PCM16/WAV, VAD segmentation, silence/click rejection, Unicode wake boundaries. No microphone opened.");
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Voice check failed: " + message); }
    }
}
