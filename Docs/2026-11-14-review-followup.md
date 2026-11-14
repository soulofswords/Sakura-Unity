# Follow-up verification of the DeepAI/Qoder checkpoint

Reviewed baseline: Unity `9122c79`. The original Sakura backend also has owner changes that are not committed. Imported Cubism Samples were preserved.

## Confirmed

- `a11756e` commits the browser native source, DLL, build script and provenance together. Current DLL SHA-256: `f6f2077de1302f5cd5ac56b78bc34bf43b1d4e7e5ebc6f2456120787d8426cc5`.
- Migration excludes browser sessions by default. Analytics/diagnostic flags and `submitAnalytics` are disabled in project settings. No player network capture was performed.
- `0e8f831` restores the microphone voice-changer panel in VTuber Streaming & voice. Its controls are included in the shell check; physical microphone conversion remains separate acceptance.
- Unity's phone-transfer picker already includes Cloud library and stages downloaded models using the original request ID. The original web desktop's native picker only supports local PMX/VRM/Live2D packages; the missing Cloud option finding applies to that client.
- Both 2.2 update archives contain the Drive backup tool, converter bridge and encrypted-backup module. Entries: Companion 21,941; VTuber 21,639. These archives predate the fixes below and must be refreshed before publication.
- External converter checkout is pinned at `5b00e66ac5585e0c6402ea30ccab5c05fad6db2e`. This check does not establish rig fidelity.

## Fixed in this follow-up

- The updater now selects `SakuraVTuber` or `SakuraCompanion` uninstall keys by edition. Compiled reflection verification checks both selectors without writing the registry. The fixed binary replaces `C:/AI/Installer ready/Sakura-Updater-2.2.exe`; its previous binary is kept under `C:/AI/Unity-Verification/`.
- Original mobile transfer instructions now describe local selection and downloading cloud models onto the PC before sending them. No APK rebuild was performed.
- Backup routes now have tests for the local-only gate, invalid paths/suffix, refusal to overwrite, dispatch and error responses.
- SQLite backup connections explicitly close. A transaction context alone does not close a Python sqlite3 connection; on Windows this could leave snapshot handles open and scratch files behind. Two database round trips now verify both data and cleanup. Free-space checks include retained snapshots.
- Drive upload dry run is verified not to construct a client or write local files. Restore dry run reads the manifest without downloading models or creating the destination.
- The shell check waits for an ongoing startup import before loading its Ringo fixture explicitly. Without that wait, the viewport's concurrent-import guard correctly declined the fixture load and the check failed prematurely. The desktop sweep uses 1440 x 900 regardless of the Editor's docked Game-view size; compact checks still exercise their separate 460 x 740 target. A 549 x 195 docked view was the cause of the previous composer-bound failure. Bounds remain in failure messages.

## Verification

- Focused Unity tooling tests: 41 passed, 1 skipped (migration, Drive backup, converter, packaging, backend bridge).
- Original backend backup and route tests: 15 passed, including temporary SQLite create/restore checks.
- Compiled updater check: passed for both edition keys. No real installation or registry was changed by the check.
- Mobile transfer JavaScript syntax: passed.
- Fresh Editor sweep: **11/11 passed**, with no C# compile errors in `C:/AI/Unity-Verification/codex-review-accepted-2026-11-14.log`. The result is `C:/AI/Unity-Verification/parity-sweep.json`. Checks cover desktop shell, compact layout, model settings, Agent shell, guide navigation, rig monitor, phone-transfer validation, collab boundaries, stage profile, VMC packets and embedded browser. Earlier viewport-dependent results are retained as `parity-sweep-small-editor-view.json`.

## Still requires acceptance

Full 1:1 parity is not established by these tests. Real installer upgrades, signed-in cloud transfer, Drive OAuth/upload/restore, physical microphone/wake/RVC and phone behavior, external video receivers, Cubism live round trip, cross-machine collab and broad model/shader comparisons remain distinct checks. Converter round-trip humanoid fidelity remains experimental. Drive currently uses broad scope and an AppData token file; restricted-scope Picker access and encrypted token storage are unresolved hardening work.

No push, release publication, source deletion or user-library cleanup was performed.
