# Core Unity Parity Implementation Plan

**Goal:** Preserve the original app's core behavior in the Unity client.
**Architecture:** Reuse existing authenticated Python services and native SDK data; port the missing client behavior rather than duplicate AI services.
**Spec:** Docs/Migration.md and original Sakura source.

- [x] Offline wake implementation: reuse the installed Vosk pack before STT, explicit install control, bounded audio, Companion-only routes, lifecycle cleanup and tests. Physical wake accuracy/latency remains an acceptance check.
- [x] Bone monitor: sustained sampling is limited to physics-driven PMX bones and VRM spring joints; saved freeze state is keyed to the model fingerprint. A runnable Unity validation covers stable authored poses, persistent jitter, stretch, angle limits and manual-repair thresholds. Physical long-run rig behavior remains an acceptance check.
- [x] Independent packaging: standalone Windows staging bundles the backend/runtime without importing settings, memories or models, rejects unsafe paths/links, and preserves user data across updates. Four packaging tests pass. A real installer and upgrade still need acceptance testing.
- [ ] Exercise complete JSON appearance import, embedded local browser, cloud and phone paths in live clients.
- [ ] Run remaining native checks and compare both editions with the reference. Record hardware-dependent acceptance separately.

Preserve the original project and assets. Large verification/build outputs belong on C:. The model picker will host a **Converter** tab for VRM ↔ PMX; conversion quality and format losses must be stated rather than treated as 1:1.
