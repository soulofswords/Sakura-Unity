using System;
using UnityEditor;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraLipSyncChecks
    {
        [MenuItem("Sakura/Validation/Check playback lip sync")]
        public static void Run()
        {
            Require(SakuraLipSync.Envelope(0, 0, 1, .01f, .08f, .016f) == 0, "silence leaves mouth closed");
            float open = SakuraLipSync.Envelope(0, .1f, 1, .01f, .08f, .016f);
            Require(open > 0 && open < 1, "playback level opens mouth smoothly");
            float closed = SakuraLipSync.Envelope(open, 0, 1, .01f, .08f, .016f);
            Require(closed >= 0 && closed < open, "playback end releases mouth");
            Require(SakuraLipSync.Envelope(0, .005f, 1, .01f, .08f, .016f) == 0, "noise below cutoff ignored");
            Require(SakuraLipSync.Envelope(0, .1f, 0, .01f, .08f, .016f) == 0, "zero strength disables movement");
            Require(float.IsFinite(SakuraLipSync.Envelope(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN)), "invalid parameters remain finite");
            foreach (string name in new[] { "あ", "aa", "A", "ParamMouthOpenY", "Parameter · ParamMouthOpenY", "jawOpen" })
                Require(SakuraLipSync.AutomaticMouthName(name), "supported mouth mapping " + name);
            Require(!SakuraLipSync.AutomaticMouthName("blink") && !SakuraLipSync.AutomaticMouthName("mouthSmile"), "automatic mapping avoids unrelated expressions");
            Require(SakuraLipSync.AutomaticAllowed(true, false, false), "playback can own mouth");
            Require(!SakuraLipSync.AutomaticAllowed(true, true, false) && !SakuraLipSync.AutomaticAllowed(true, false, true) && !SakuraLipSync.AutomaticAllowed(false, false, false), "camera manual and off priority gates");
            Debug.Log("PASS: playback envelope, cutoff, smoothing, finite values, PMX/VRM/Live2D mouth names and ownership gates. No audio device opened and no model preferences written.");
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Lip sync check failed: " + message); }
    }
}
