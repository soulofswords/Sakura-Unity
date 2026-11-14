# Sakura 2.2 — Unity desktop release

## What changed

- Native Unity 6.3 desktop client for Companion and VTuber, using the existing authenticated Python services.
- PMX, VRM and Live2D rendering, expressions, hotkeys, tracking, physics, stage items, backgrounds and outputs.
- Native Agent workspace, embedded local-app browser, VTuber chat/co-host, Twitch emotes, channel points and Twitch device sign-in.
- Native VTuber Collab, VTS-compatible plugin API, VMC send, Cubism Editor parameter feed and Drive backup tooling.
- Phone model handoff supports local PC library and cloud library selection; phone local import is available in the mobile source.
- VRM ↔ PMX converter tab backed by the separately installed pinned Model-To-PMX engine. Conversion is best-effort; rig, material, axis and physics fidelity are not 1:1.

## Updating from 2.1

1. Close Sakura.
2. Run `Sakura-Updater-2.2.exe --package Sakura-Companion-2.2-Update.zip` --install "<existing install folder>" --edition companion` (or `vtuber`).
3. The updater backs up user data, replaces only application files, preserves `Sakura/models`, `Sakura/voices`, `Sakura/llm-models`, `Sakura/desktop-profile` and `Sakura/settings.json`, removes stale 2.1 application files, updates shortcuts/registry and restarts.
4. `--dry-run` lists files without changing anything. `--no-restart` skips launch.

If the install folder contains a v2.1 library, the first Unity launch also offers **Bring my existing Sakura library** in Models; that path copies and verifies the same user data without touching the originals.

## Verification performed

- Unity compile: 0 errors.
- Desktop parity sweep: 11/11 passed (shell, compact layout, model settings, Agent, guide, rig monitor, phone transfer, collab boundaries, stage profile, VMC packets, embedded browser).
- Packaged Companion and VTuber launch outside Unity, connect to the bundled backend and load a migrated PMX model.
- In-place update tested from a v2.1-style folder: settings, custom keys, models and voices preserved; `Desktop Companion.exe` removed; `Sakura Companion.exe`/`Sakura VTuber.exe` installed; registry set to 2.2.0.
- Updater rejects zip path traversal and cleans only files listed by the previous installation manifest.

## Known limits

- No code-signed installer yet; the updater/setup executables are unsigned.
- Phone direct cloud download is not implemented; cloud models reach the phone through the paired PC picker. The phone local-import source is present but needs an Android rebuild and device test.
- Converter rig fidelity, live Cubism Editor round trip, real multi-machine collab, cloud-account download and physical microphone/receiver tests remain open.
- Drive backup still uses full Drive OAuth scope; Picker-scoped authorization and encrypted token storage are release hardening work.
- Unity may re-enable the Unity Connect analytics flag in ProjectSettings; it is committed disabled.
