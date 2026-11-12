# Sakura Unity migration

## Approved direction

Recreate the current Sakura Companion, Agent and VTuber experience in Unity 6.3 LTS, using the existing app as the functional and visual reference. Keep the existing project and personal assets until the owner approves removal. The native Unity client uses URP and reuses the existing Python services through an authenticated loopback connection.

## Constraints

- Reference checkpoint is Sakura commit 0c7d061.
- F: initially has 36.2 GB free. Keep large build outputs and temporary verification artifacts on C:.
- Target scalable graphics for GTX 10-series and older devices, with a high preset for RTX 3060 and newer. These remain performance targets pending hardware benchmarks.
- Preserve imported models, expressions, tracking profiles, voices, credentials and update behavior.
- Keep Companion AI capabilities separate from the standalone VTuber edition.
- Tokens remain in process memory. Never put credentials in scenes, assets, source control or logs.

## Architecture decision

The Unity client owns the interface, avatar rendering, animation and eventually tracking/output integration. Existing Python services retain AI, memory, MCP, Agent workflows and model-library behavior. This avoids a simultaneous rewrite of all service logic. A browser wrapper alone would retain the existing rendering limitations. A full C# backend rewrite would add migration risk and duplicate working behavior.

## Implementation order

1. Native studio shell, branding, themes, mode navigation and real local catalog browsing. Verify compilation and rendered layout.
2. Authenticated Python process lifecycle and chat transport. Verify loopback restrictions, failure handling and shutdown.
3. Runtime VRM, Live2D and PMX loading. Verify one representative model of each format, then complex models and imported assets.
4. Expressions, hotkeys, framing, tracking, physics, rooms, items and appearance controls.
5. Voice, mic/wake behavior, Agent tools, embedded browser, cloud library and phone integration.
6. Streaming outputs, multi-instance, installer/update preservation and platform-specific builds.
7. Run the full parity inventory, screenshot comparisons, security tests and hardware benchmarks. Obtain owner sign-off.

## Acceptance ledger

| Area | State |
|---|---|
| Existing app checkpoint | Committed locally |
| Native Unity shell | Running in Editor Play mode, navigation and theme switching verified |
| Local catalog | 544 real references discovered; eight bridge unit tests passing |
| Authenticated backend | Loopback bridge and authorization probe verified. AI services start lazily. Chat generation remains unverified |
| Avatar rendering | Native PMX (Ringo, Killer Luka), Live2D (DeepSeek, Amika) and VRM (ArisuMaid) import/render/switching verified |
| Idle motion | Native breathing and blinking verified. Six original 3D idle poses ported; authored Live2D Idle groups load when supplied. Exact motion parity and live tracking priority remain under verification |
| PMX editing | Native independent colour/transparency, diffuse replacement, texture diagnostics/confirmed path repair, indexed bone freezing and eight appearance controls implemented. Editor reference matching tested. Pick-on-model, automatic rig diagnosis, material-expression presets and migration of old browser overrides remain pending. Existing package textures.json overrides are read at import |
| Tracking, expressions and physics | Tracking, expression UI/hotkeys and physics parity pending |
| Voice and microphone | Pending |
| Backend mode gating | Native mode navigation is present; source service pausing/edition packaging still needs native integration verification |
| Agent and browser | Pending |
| Cloud library and mobile | Native library sign-in, registration, recovery and download controls wired to existing routes. Live account/download verification and mobile integration pending |
| Double-click clean stage | Unity event tests verify Companion chat remains visible, VTuber hides controls, repeated double-click restores the previous panel state |
| Streaming outputs | Pending |
| Install/update preservation | Pending |
| Full feature and visual parity | Pending |

Each capability needs a callable backend or native implementation and an actual verification result. A displayed control alone does not count as migrated.

## First milestone verification

- Unity 6000.3.28f1 compiled the native code with zero reported compilation errors or warnings.
- Imported official UniVRM 0.131.4 with its embedded UniGLTF and VRM packages. SDK license is included at Packages/com.vrmc.gltf/LICENSE.md.
- An existing VRM loaded directly from the original models folder with three renderers. The model was never copied into Unity assets.
- Native Model Settings has working VRM zoom and rotation controls. Pose and appearance matching remain pending.
- Verify Studio checks all three mode selections, Settings navigation, theme toggling and model path traversal rejection.
- Authenticated source API accepts model-catalog and identity requests and denies anonymous model-catalog access. The probe runs without starting LLMs, microphones, automations or other lifespan services.
- Screenshots and the verification JSON are saved outside F: at C:/AI/Unity-Verification.
- Old installer build snapshots total 4.59 GB. Automatic approval review rejected deletion. Those files remain in place.
- Original Sakura source status remains unchanged from its checkpoint apart from the five previously uncommitted temporary probes.

## Development setup

Open Assets/Scenes/SakuraStudio.unity and press Play. Sakura/Open Studio opens the scene. Sakura/Verify Studio writes the current smoke-test result and screenshot while Play mode is running.

Settings can launch the existing Python backend using its development virtual environment. This is development-only packaging. Full chat, voice and backend shutdown behavior still need live integration tests before release. The installer will eventually bundle and launch the backend without requiring a source checkout.

## Current UI parity checks

The native shell now uses the original app's DM Sans and Bricolage Grotesque fonts, toolbar icon artwork, theme colors and measured title bar, dock, panel and chat geometry. Font licenses accompany the assets. The right panel supports resizing from each edge.

Double-click the background or avatar to hide controls. Companion retains its chat window. VTuber retains its avatar stage. Double-click again restores controls, including the prior closed-panel state. Agent mode ignores this gesture. These checks use dispatched Unity UI events and resolved display styles, with screenshots in C:/AI/Unity-Verification.

The native Settings tabs reuse the existing settings API. Camera, microphone, renderer-specific physics and other partially migrated preferences still require their corresponding native integrations. Visible controls and saved preferences do not establish full feature parity.

## Native PMX and idle checkpoint — 2026-11-12

PMX controls now keep independent per-model overrides under Application.persistentDataPath/pmx-overrides. They do not edit the PMX, original textures or original app's browser storage. Stored native settings use replacement writes. Original browser colour/texture/expression overrides are not yet migrated into the native profile.

The PMX Editor and MikuMikuDance buttons use the existing source application's validated launch helpers. Ringo was opened in PMX Editor with its full model filename on the process command line. MMD opened and accepted the selected model's file-drop import request. That is not a claim of testing every MMD operation.

Texture diagnostics reuse the original authenticated route and apply only confirmed package-relative paths to native diffuse/toon/sphere bindings. Missing or ambiguous images still require a replacement. Appearance matching reuses the real editor capture plugin, compares whole-frame and corresponding-material RGB statistics, preserves directional shading, and rejects regressions. It does not reconstruct artwork or guarantee identical rendering of unsupported editor effects.

Verification artifacts are in C:/AI/Unity-Verification. pmx-tools-check.json covers colour pixels, transparency independence, shader uniforms, frozen bones, reload persistence, snapshots and advancing breathing. model-import-check.json covers five real model packages and failed-load recovery. An intentionally darkened Ringo matched against the live PMX Editor capture with measured error reduced from 0.27653 to 0.09491; its original native settings were restored afterwards. Amika's ParamBreath changed while 48 real frames advanced, and the VRM blink weight reached 0.998.

No installer, APK or published update is produced by this checkpoint. Full native feature/UI parity is still incomplete. The original application remains intact.

The eight native appearance sliders use the same ranges as the reference app. Automatic matching searches the original .3/.12/.05 steps, retains directional shading and checks whole-model plus up to six measurable material references. After snapshots wait for a fresh frame, so they cannot accidentally show the final isolated reference material.

The native local model picker remembers its last folder separately for PMX, VRM and Live2D, including across reopening. A live Ringo-folder reopen check passed. SDK example scenes remain local owner-imported files and are excluded from this code checkpoint; SDK runtime, editor support, renderer assets and licenses are committed. The owner’s Unity cloud-connect preference is not changed or committed.
