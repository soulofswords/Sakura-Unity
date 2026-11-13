using System;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraVoiceParityChecks
    {
        [MenuItem("Sakura/Validation/Check speech interruption and sleeping wake")]
        public static void Run()
        {
            Require(SakuraVoice.ParseSpeechInterrupt("stop", "hey sakura") == "", "stop-only command");
            Require(SakuraVoice.ParseSpeechInterrupt("Hey Sakura, wait a moment!", "hey sakura") == "", "addressed pause command");
            Require(SakuraVoice.ParseSpeechInterrupt("Stop speaking, how does this work?", "hey sakura") == "how does this work?", "follow-up retained");
            Require(SakuraVoice.ParseSpeechInterrupt("रुको, क्या हुआ?", "hey sakura") == "क्या हुआ?", "Hindi interruption and question");
            Require(SakuraVoice.ParseSpeechInterrupt("stopwatch is useful", "hey sakura") == null, "command boundary enforced");
            Require(SakuraVoice.ParseSpeechInterrupt("stop talking about weather", "hey sakura") == null, "ordinary discussion excluded");
            Require(SakuraVoice.ParseSpeechInterrupt("pause the movie", "hey sakura") == null, "media request excluded");
            Require(SakuraVoice.ParseSpeechInterrupt("stop, stop, look at this", "hey sakura") == "look at this", "repeated control allows follow-up");
            Require(!SakuraVoice.WakeFromSleep("tell me the weather", "hey sakura", out _), "sleeping ordinary speech rejected");
            Require(SakuraVoice.WakeFromSleep("Wake up Sakura!", "hey sakura", out string command) && command == "", "wake-only command");
            Require(SakuraVoice.WakeFromSleep("Hey, Sakura! कैसे हो?", "hey sakura", out command) && command == "कैसे हो?", "punctuated custom wake command");
            Require(!SakuraVoice.WakeFromSleep("I said wake up Sakura yesterday", "hey sakura", out _), "wake prefix required");
            var echo = new SakuraVoice.PlaybackEchoGuard();
            echo.Remember("Let me explain how the microphone works.", 10);
            Require(echo.Matches("explain how the microphone works", 11), "spoken phrase echo recognized");
            Require(!echo.Matches("explain how the microphone works", 9), "unspoken future phrase excluded");
            Require(!echo.Matches("stop speaking", 11), "short real commands retained");
            Require(!echo.Matches("explain how a different microphone works", 11), "different utterance retained");
            echo.Finish(12);
            Require(echo.Matches("explain how the microphone works", 13) && !echo.Matches("explain how the microphone works", 14), "finished playback echo grace bounded");
            Debug.Log("PASS: spoken stop/wait parsing, follow-ups, sleeping wake boundaries and timestamped playback echo suppression. No microphone, speaker or settings changed.");
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Voice parity check failed: " + message); }
    }
}
