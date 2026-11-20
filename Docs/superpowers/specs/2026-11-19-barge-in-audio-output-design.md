# Hands-free barge-in with speaker/headphone output — design

**Date:** 2026-11-19  
**Status:** Proposed  
**Target:** Sakura Unity 6.3 Companion voice loop with the existing Python STT/TTS backend  
**Decision:** Ship both listening profiles. Headphones get immediate voice barge-in. Speakers get echo-resistant barge-in using playback ducking, a double-talk detector, the existing echo guard, and optional saved-voice matching. No native audio plugin in this pass.

## 1. Intent

Sakura currently interrupts only on the words “stop” or “wait”. The parity gap with Open-LLM-VTuber, Warashi, N.E.K.O and rexclaw is natural barge-in: the user talks over her and she stops and answers.

The hard constraint is echo. Native Unity microphone capture has no acoustic echo cancellation, and the current Voice settings say so. Shipping a naive “stop on any speech” would make Sakura cut herself off on laptop speakers.

This design gives the user an explicit output profile and two behaviours:

- **Headphones** — nothing Sakura says reaches the microphone, so barge-in can be immediate.
- **Speakers** — Sakura ducks her own volume, checks whether the speech is the enrolled user, and only then stops and answers.

## 2. Goals

- Talking over Sakura stops her and submits the new utterance in both profiles.
- “Stop” and “wait” keep their current stop-only meaning.
- A persistent **How you listen** setting selects Headphones or Speakers.
- Speaker mode does not self-interrupt during normal TTS playback at normal volume.
- No regression to wake word, push-to-talk, voice enrolment, stop/wait, or VTuber playback.
- No native dependency, no Python route change, no credential or model change.

## 3. Non-goals

- True acoustic echo cancellation (AEC3/WebRTC APM). That needs a native audio capture/playback plugin and is a separate project.
- Changing the Windows default output device, or enumerating output devices. Unity has no built-in output-device API; the setting describes how the user listens, not which device Windows uses.
- Touch reactions, localization, or the plugin marketplace.
- Always-on barge-in while the microphone is off. Barge-in still requires the microphone switch.

## 4. Anti-goals

- Sakura interrupting herself on speakers.
- A barge-in path that sends “stop” to the LLM as a chat message.
- A speaker mode that only works if the user has enrolled a voice profile.
- Changing the audio output device behind the user’s back.

## 5. Constraints

- `SakuraVoice` captures 16 kHz mono through `UnityEngine.Microphone`; TTS plays through a single `AudioSource`.
- The backend already returns `speaker_rejected` and `speaker_message` from `/api/stt` when saved-voice matching is enabled.
- `PlaybackEchoGuard` already remembers recent spoken text and rejects matching transcripts.
- `SakuraVoice.SpokenInterruptionEnabled` is session-only and is surfaced in Settings → Voice.
- The existing `SpeechGate` starts on 0.12 s of speech and caps interrupt-only captures at 3 s. A real utterance needs the normal 15 s cap.
- No new Python dependency and no changes to the TTS audio format.

## 6. Approaches considered

**A. Headphones-only.** Keep the current gate, remove the stop/wait requirement, document “use headphones”. Smallest change, but does not meet the speaker requirement.

**B. Native AEC.** Add a native capture/playback plugin (WebRTC APM or Windows WASAPI) and run true echo cancellation before the gate. Best acoustic result. Too large for this pass: new native build, device plumbing, packaging, and a new failure surface.

**C. Hybrid (chosen).** One state machine with two profiles. Headphones use immediate VAD. Speakers use a double-talk detector against an adaptive playback baseline, duck the TTS volume instead of stopping it, transcribe the buffered utterance, and only stop when the transcript passes the echo guard and, when available, the saved-voice check. This ships both behaviours without native code; speaker mode is echo-resistant rather than studio-grade AEC.

## 7. Design

### 7.1 Setting and persistence

Add to `SakuraVoice`:

```csharp
public enum AudioOutput { Headphones, Speakers }
public AudioOutput Output { get; set; }   // PlayerPrefs: sakura_audio_output
public bool BargeInEnabled { get; set; }  // PlayerPrefs: sakura_barge_in, default true
```

Default output is **Speakers** (the common laptop/desktop case), with the conservative speaker detector. Headphones remains one click away. `BargeInEnabled` replaces the session-only `SpokenInterruptionEnabled`; the old property stays as an alias so existing panel code and tests keep compiling.

Settings → Voice gets:

- `How you listen` dropdown: `Speakers · echo-resistant barge-in` / `Headphones · immediate barge-in`.
- `Hands-free barge-in` toggle (default on).
- Note: “Speaker mode ducks Sakura’s voice, checks for your voice profile, and never uses native echo cancellation. Headphones are still the cleanest option.”
- If no voice profile is enrolled: “Speaker mode is more reliable with a saved voice in Recognised voices.”

Both settings persist in `PlayerPrefs`. A change applies on the next listening session or the next reply, never mid-reply: the current reply keeps the profile it started with, and switching does not restart playback. `OpenMicrophone` reads the current profile, so a switch forces the gate to be rebuilt on the next open.

One toggle controls both hands-free barge-in and stop/wait. With it off, the microphone is paused during playback, which is the current default behaviour. No backend schema change.

### 7.2 Voice state machine

States: `Idle`, `Listening`, `Speaking`, `BargeInCandidate`, `Verifying`.

**Not speaking:** unchanged. The normal `SpeechGate` records until silence and submits.

**Speaking, Headphones, barge-in on:**
1. Keep the microphone open with the normal gate (0.3 s start, 0.65 s end, 15 s max).
2. On the first confirmed speech block (before any transcript), call `StopSpeaking()` so the stop lands within the 400 ms target. The clip is destroyed, so a false trigger loses the rest of that reply; headphones make that rare and acceptable.
3. Keep buffering; on end of speech upload the utterance.
4. If the transcript is “stop”/“wait”, do not submit. If the echo guard matches or the transcript is empty, do not submit. Otherwise submit with the wake gate bypassed (see §7.3).

**Speaking, Speakers, barge-in on:**
1. Keep the microphone open with the normal gate.
2. While speaking, compute:
   - `micRms` from the captured block (already available as `InputLevel`).
   - `playbackRms` from the current TTS clip window, scaled by `speaker.volume * AudioListener.volume`.
   - `echoFloor`, a slow moving average of `micRms` while speaking and not in double-talk.
3. A new `BargeInDetector` (pure C#, no Unity audio calls) enters `Candidate` when `micRms > max(SpeechThreshold * 1.2, echoFloor * 1.8)` for 250 ms. It exits if the level falls for 200 ms.
4. On `Candidate`, duck the TTS to 10% volume (`speaker.volume = 0.1f`), keep recording, and keep the clip playing.
5. On end of speech, upload the buffered utterance.
6. If `/api/stt` returns `speaker_rejected`, or the echo guard matches, or the transcript is empty: restore volume (`Unduck()`), return to `Speaking`, and do not submit. The clip is still playing, so the reply continues.
7. If the transcript is “stop”/“wait”: `StopSpeaking()` and do not submit.
8. Otherwise: `StopSpeaking()`, restore the default volume, and submit the utterance with the wake gate bypassed.
9. If the TTS clip ends while verifying, ignore the late result and let the normal idle path take over.

**Speaking, barge-in off:** the microphone is paused during playback (current default), so neither stop/wait nor hands-free barge-in runs. Push-to-talk still works.

**Turn boundaries:** the barge-in window is `Speaking || Backend.Busy`, matching the existing stop/wait window. Talking while Sakura is thinking or downloading voice (no clip yet) cancels the in-flight turn with `InterruptChat()` and submits the new utterance; there is no playback to duck, so speaker mode behaves like headphones there.

**Wake gate:** barge-in is a continuation of an active turn, so it bypasses `ExtractWakeCommand` exactly as the existing interrupt-only path does. The user does not have to say “hey Sakura” while talking over her. Normal idle listening still requires the wake phrase when it is enabled.

**Push-to-talk / wake word / enrolment:** unchanged. Push-to-talk never auto-submits a barge-in; wake word still gates the first utterance. Enrolment pauses barge-in.

### 7.3 Components

- `SakuraVoice` — owns the state machine, duck/unduck, buffering, and the existing STT upload.
- `BargeInDetector` — new pure class in `SakuraVoice.cs` or its own file. `Push(micRms, playbackRms, dt)` returns `Idle | Candidate | Confirmed`; has `Reset()`. Tunables: 250 ms attack, 200 ms release, 1.8× echo margin, 1.2× speech floor.
- `PlaybackEchoGuard` — used as the transcript-level backstop. Extend it for barge-in: remember lines of two or more words, compare by normalised word-set overlap, and exact-match one-word lines. The current four-word minimum is too coarse for short replies.
- `SakuraSettingsPanel` — dropdown, toggle, note, PlayerPrefs writes.
- No Python changes. `/api/stt` already returns `speaker_rejected` when matching is enabled.

### 7.4 Playback level

`SakuraVoice` reads `speaker.clip` samples around `speaker.timeSamples` to estimate playback RMS. This avoids `OnAudioFilterRead` and works with the existing `AudioSource`. If the clip is unavailable, use `speaker.volume` as a constant estimate; the adaptive echo floor still learns the room.

### 7.5 Status text

- Headphones: `Sakura speaking · talk to interrupt`.
- Speakers: `Sakura speaking · say something to interrupt`.
- Candidate: `Listening to you…`.
- Rejected echo: `Speaker echo ignored · continuing`.
- No profile: `Speaker mode · add a saved voice for reliable interruption`.

## 8. Edge cases

- **User says “stop”** — stop only, never submit.
- **User says “wait” then a question** — stop, then submit the question (existing `ParseSpeechInterrupt` behaviour).
- **TTS finishes during verification** — discard the late transcript; do not talk over the next turn.
- **Mode switched mid-reply** — apply on the next listening session; do not restart the current playback.
- **Microphone muted/off** — no barge-in, unchanged.
- **No saved voice profile** — speaker mode still uses echo guard + double-talk; the UI warns.
- **Very loud room** — adaptive `echoFloor` rises; if it reaches the speech threshold, speaker barge-in becomes conservative rather than firing constantly.
- **Push-to-talk held during playback** — do not auto-submit; the user is explicitly holding the mic.

## 9. Verification

1. Compile in batchmode; build both Windows players.
2. Headphones: 10 replies, talk over each; confirm audio stops within ~400 ms and the new utterance is answered. Say “stop” once; confirm no LLM turn.
2b. Headphones: trigger a false barge-in (cough); confirm the reply stops and no turn is submitted. This is an accepted trade-off.
3. Speakers at 70% volume: 20 replies with no user speech; confirm zero self-interrupts. Include at least 10 short replies (“Okay.”, “Sure.”) because the echo guard treats those differently.
3b. Speakers, no saved voice profile: repeat the 20-reply test; confirm the level detector alone does not self-interrupt.
3c. Interrupt while Sakura is thinking or loading voice (no audio yet): confirm the in-flight turn cancels and the new utterance is answered.
4. Speakers: talk over 10 replies; confirm duck, verify, stop and answer; confirm a TTS-only echo is rejected and volume restores.
5. Speaker mode with matching on and a saved voice: confirm a different voice is rejected and Sakura resumes.
6. Toggle barge-in off; confirm the old paused-microphone behaviour returns.
7. Switch output profile mid-reply; confirm the current reply keeps its profile and the next reply uses the new one.
8. Confirm wake word, push-to-talk, enrolment, VTuber readout and “Stop speaking” still work.

## 10. Out of scope / follow-ups

- Native AEC for speaker mode.
- Output-device enumeration and routing.
- A “test my speakers” calibration tone.
- Touch reactions, localization, marketplace.

## 11. Open decision for the builder

Speaker mode is explicitly echo-resistant, not true AEC. If the acceptance test in §9.3 fails on the target hardware, the fallback is to keep speaker mode but require a saved voice profile, or to schedule the native AEC follow-up. No native plugin is added in this pass.
