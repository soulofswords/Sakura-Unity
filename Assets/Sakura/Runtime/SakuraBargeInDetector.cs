using System;

namespace Sakura.Unity
{
    public sealed partial class SakuraVoice
    {
        public enum BargeInState { Idle, Candidate, Confirmed }

        /// <summary>
        /// Double-talk detector for speaker playback. It learns the echo floor from the
        /// microphone while Sakura is speaking, then asks for sustained near-end speech
        /// above that floor. No Unity audio calls, so the editor checks can drive it.
        /// </summary>
        public sealed class BargeInDetector
        {
            private readonly float speechFloor, echoMargin, attackSeconds, releaseSeconds;
            private float voiced, quiet, echoFloor, priming;
            private bool wasPlaying, confirmed;

            public bool Armed { get; private set; }

            public BargeInDetector(float speechFloor = .012f, float echoMargin = 1.8f, float attackSeconds = .25f, float releaseSeconds = .2f)
            {
                this.speechFloor = Math.Max(.0001f, speechFloor);
                this.echoMargin = Math.Max(1f, echoMargin);
                this.attackSeconds = Math.Max(.05f, attackSeconds);
                this.releaseSeconds = Math.Max(.05f, releaseSeconds);
            }

            public BargeInState State { get; private set; }

            public void Reset()
            {
                voiced = quiet = echoFloor = priming = 0;
                wasPlaying = confirmed = false;
                Armed = false;
                State = BargeInState.Idle;
            }

            public BargeInState Push(float micRms, float playbackRms, float seconds)
            {
                if (seconds <= 0 || float.IsNaN(seconds) || float.IsInfinity(seconds)) return State;
                micRms = float.IsNaN(micRms) || float.IsInfinity(micRms) ? 0 : Math.Max(0, micRms);
                playbackRms = float.IsNaN(playbackRms) || float.IsInfinity(playbackRms) ? 0 : Math.Max(0, playbackRms);
                bool playing = playbackRms > .0001f;

                if (playing && !wasPlaying)
                {
                    voiced = quiet = priming = echoFloor = 0;
                    confirmed = false;
                    Armed = false;
                    State = BargeInState.Idle;
                }
                wasPlaying = playing;

                // Learn the room and speaker coupling before arming. A user who talks
                // during this window still has their audio buffered by the caller.
                if (playing)
                {
                    priming += seconds;
                    if (priming < .2f)
                    {
                        echoFloor = Blend(echoFloor, micRms, seconds / .2f);
                        State = BargeInState.Idle;
                        return State;
                    }
                    Armed = true;
                }

                float threshold = Math.Max(speechFloor * 1.2f, echoFloor * echoMargin);
                if (playing) threshold = Math.Max(threshold, playbackRms * .35f);

                if (State != BargeInState.Idle)
                {
                    if (micRms < threshold * .8f)
                    {
                        quiet += seconds;
                        if (quiet >= releaseSeconds)
                        {
                            State = BargeInState.Idle;
                            voiced = quiet = 0;
                            confirmed = false;
                        }
                    }
                    else quiet = 0;
                    if (State != BargeInState.Idle) return State;
                }

                if (micRms >= threshold)
                {
                    voiced += seconds;
                    quiet = 0;
                }
                else
                {
                    voiced = Math.Max(0, voiced - seconds * .5f);
                    quiet += seconds;
                    if (playing) echoFloor = Blend(echoFloor, micRms, seconds * 3f);
                    else echoFloor = Math.Max(0, echoFloor - seconds * .15f);
                }

                if (voiced >= attackSeconds)
                {
                    voiced = attackSeconds;
                    State = BargeInState.Candidate;
                    if (!confirmed)
                    {
                        confirmed = true;
                        return BargeInState.Confirmed;
                    }
                }
                return State;
            }

            private static float Blend(float current, float target, float amount) =>
                current + (target - current) * Math.Max(0, Math.Min(1, amount));
        }
    }
}
