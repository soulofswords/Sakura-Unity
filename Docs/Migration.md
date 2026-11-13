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
| Local catalog | 544 real references discovered; ten bridge unit tests passing |
| Authenticated backend | Loopback bridge and authorization probe verified. AI services start lazily. A real local greeting returned HTTP 200 in 10.1 seconds after lazy audio imports. Sustained conversation remains under verification |
| Avatar rendering | Native PMX (Ringo, Killer Luka), Live2D (DeepSeek, Amika) and VRM (ArisuMaid) import/render/switching verified |
| Idle motion | Native breathing and blinking verified. Six original 3D idle poses ported; authored Live2D Idle groups load when supplied. Exact motion parity and live tracking priority remain under verification |
| PMX editing | Native independent colour/transparency, diffuse replacement, texture diagnostics/confirmed path repair, indexed bone freezing and eight appearance controls implemented. Editor reference matching tested. Native material and strongest-bone picking pass a real Ringo hit test. Automatic rig diagnosis, material-expression presets and migration of old browser overrides remain pending. Existing package textures.json overrides are read at import |
| Tracking, expressions and physics | Native expressions and expandable UI implemented for PMX, VRM and Live2D. Strength, preview, toggles, rename and focused shortcut persistence pass native tests. Tracking, global/action hotkeys and complete physics parity remain pending |
| Voice and microphone | Native microphone/VAD, source-language STT, recognised-voice rejection, wake transcript gate and local TTS playback wired. PCM, VAD and wake-boundary checks pass. Physical conversation, lip-sync, interruption and offline wake parity remain pending |
| Backend mode gating | Live cold-start VTuber rejection and return to Companion verified. Ten bridge tests cover lifecycle, included-router startup/shutdown, stale revisions and import boundaries. Warmed-service transitions and edition packaging remain pending |
| Agent and browser | Native tasks, planning/review, management panes and bounded browser preview wired. A harmless create/propose/reject/cancel/delete lifecycle was verified. Interactive embedded-browser parity remains pending |
| Cloud library and mobile | Native library sign-in, registration, recovery and download controls wired to existing routes. Live account/download verification and mobile integration pending |
| Double-click clean stage | Unity event tests verify Companion chat remains visible, VTuber hides controls, repeated double-click restores the previous panel state |
| Streaming outputs | Native Spout/NDI/virtual-camera transports, PNG and FFmpeg WebM implemented. Capture/validation checks pass. Physical receiver checks remain pending |
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
- Original Sakura source retains its user probes. Commit 25b5e0d separately makes optional audio imports lazy for plain-chat startup.

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

## Native voice and expressions checkpoint — 2026-11-13

The runtime assembly explicitly references Unity.InputSystem for focused expression shortcuts. A normal editor restart rebuilt the dependency graph and cleared the Safe Mode compilation errors. The obsolete duplicate mcs.rsp was removed; csc.rsp retains the unsafe setting required by the imported SDK. Voice settings open on the Voice tab, and the native microphone starts off.

Live tests exercised Ringo's 22 expressions plus a disposable synthetic mesh for strength, clearing, preview, rename and shortcut persistence. Voice checks cover PCM16/WAV clipping, RMS, VAD silence/click rejection, Unicode wake boundaries and Hindi transcript preservation without opening the microphone. These checks do not establish physical microphone quality or conversational parity.

The bridge captures the application's merged router lifespan rather than only the root lifespan, preserving Agent router lifecycle handlers when services start lazily. Native chat and speech retain mode-generation checks and desktop authentication; audio downloads accept only the owned /audio filename route. Tokens remain in memory and are not written into verification reports.

A real local-model greeting attempt timed out after 120 seconds. That exposed an empty-error-body NullReferenceException in the native chat transport. The transport now reports a visible failure and clears Busy; a live isolated HTTP 503 regression test confirms it. Successful local generation and physical speech remain unverified. No model/provider setting was changed to conceal the failure.


## PC parity correction checkpoint — 2026-11-13

PC parity takes priority over further mobile work, following the owner's correction. The reference is the current Sakura desktop source and its existing UI. The original source, SDK samples and personal assets remain in place.

The native shell now has separate Companion and VTuber toolbars using the reference artwork. VTuber has its position lock and hide-controls buttons, tracking footer, and the VTuber logo. Companion-only conversation/provider settings are excluded from the VTuber settings pages. A repeated toolbar click closes its pane and clears selection.

Model Settings uses the reference zoom row and five PMX/VRM tabs. Live2D retains eight tabs. Texture coloring includes an HSV wheel, RGB inputs and separate transparency editing. Texture replacement requires explicit selection followed by Apply. PMX appearance controls sit inside Texture coloring. Expression cards have centered expansion controls, editable names, preview, strength, activation and focused shortcut recording with Escape cancellation. Voice & speech opens directly with Friend/Teacher switches. Camera, phone, scenery and output controls use the reference card grouping.

Native UI checks exercise tab ordering, clipping, material picking, RGB controls, texture selection/apply, both navigation lists, position locking, expression expansion, Voice pane structure and VTuber settings isolation. Captures cover desktop, compact and light-theme layouts under C:/AI/Unity-Verification. A passed UI check covers those explicit assertions only.

### Outstanding parity work

- Full Live2D physics controls, custom parameter/VTS mapping, outline and authored-glow behavior. Native material editing, bone freezing and bounded postprocessing are now implemented.
- Automatic bone diagnosis, PMX material-expression presets, migration of original browser-stored overrides and broader shader/reference comparisons.
- Rotating/local-library preview cards and full native cloud-preview behavior.
- Native VTS plugin actions, Cubism synchronization, collab, VMC transmission and fresh isolated multi-instance behavior.
- Screen capture, reactions, game-mode resource safeguards and their complete native controls.
- Physical voice-enrollment verification, offline wake recognition, interruption, live voice-changing capture and lip-sync verification.
- Live2D stage-item editing, full prop attachment/animation controls and native window transparency behavior. Native stage images, looping video and solid output backgrounds are implemented.
- A fully interactive embedded Agent browser, first-run walkthrough and independent installer/update packaging.
- Phone and physical receiver verification. Further mobile migration is paused until PC parity is accepted.

The Windows executables under C:/AI/unity exe remain development previews linked to the existing source environment. Full feature and visual parity is incomplete. No release is published from this checkpoint.

The PC review also closed minimized-chat height overriding its collapsed style, active wake settings becoming stale after Save, saved theme being ignored at startup, and shortcut recording continuing after a pane was hidden. Regression checks cover collapse/expand, explicit theme application and cancellation on both close and hide. Physical microphone behavior remains unverified.

## Integrated PC controls checkpoint — 2026-11-13

Agent mode now has the reference sidebar, lower composer, collapsed task settings, provider/access controls, recent tasks, persisted task conversation, searchable archived history and management-page navigation. A live disposable backend task passed create, inspect, pending approval, rejection, cancellation and deletion. No test action executed against owner files. The saved conversation preference was restored after the test.

VRM and Live2D now have native independent colour/transparency editing, atlas/texture replacement and persistent model profiles. VRM bone freezing excludes frozen joints from spring-chain simulation. Live2D has dedicated parameter output, smoothing, automatic blink/breath controls and per-model saturation, contrast, colour overlay and silhouette-contained glow. Shader fixture checks cover premultiplied RGB, unchanged alpha, in-place rendering and persistence. Five real imports passed shader support and nonempty-render checks. Hardware movement and receiver behavior remain unverified.

Voice & speech now includes grouped voice selection/preview, recognised-voice management for up to five profiles, rename/remove, matching toggle, consented recognition download with progress, and explicit six-second enrollment. Normal listening is paused during enrollment. RVC uploads use bounded file streaming into a temporary multipart body, then UploadHandlerFile, with teardown cleanup. Native file browsing and live microphone voice conversion remain pending. AI settings expose installed native/GGUF/Ollama choices, provider discovery and a saved-connection test.

Background controls support colour swatches, HSV/RGB editing, a solid OBS output background, normalized local images and muted repeating local video. Background media is saved independently of character assets. Images/videos appear in app-window capture. The dedicated output uses solid colour when opted in, while the avatar render texture keeps alpha for model-only hit testing. Synthetic GPU checks cover background placement, picking exclusion, transparent-pixel replacement and opaque-avatar preservation. A physical video-file playback test remains pending.

Seventeen bridge/tracking Python tests pass. Native checks cover Agent UI/lifecycle, speaker validation and small multipart fixtures, model tools/effects, backdrop compositing, original shell/layout, expressions, PMX editing, tracking kernels, stage transforms, output dimensions and double-click behavior. No physical microphone recording or cloud publication was performed. These checks are bounded acceptance evidence. Full 1:1 feature parity remains incomplete, including the outstanding items above.