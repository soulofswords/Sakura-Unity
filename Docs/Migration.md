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

## Native PMX and idle checkpoint â€” 2026-11-12

PMX controls now keep independent per-model overrides under Application.persistentDataPath/pmx-overrides. They do not edit the PMX, original textures or original app's browser storage. Stored native settings use replacement writes. Original browser colour/texture/expression overrides are not yet migrated into the native profile.

The PMX Editor and MikuMikuDance buttons use the existing source application's validated launch helpers. Ringo was opened in PMX Editor with its full model filename on the process command line. MMD opened and accepted the selected model's file-drop import request. That is not a claim of testing every MMD operation.

Texture diagnostics reuse the original authenticated route and apply only confirmed package-relative paths to native diffuse/toon/sphere bindings. Missing or ambiguous images still require a replacement. Appearance matching reuses the real editor capture plugin, compares whole-frame and corresponding-material RGB statistics, preserves directional shading, and rejects regressions. It does not reconstruct artwork or guarantee identical rendering of unsupported editor effects.

Verification artifacts are in C:/AI/Unity-Verification. pmx-tools-check.json covers colour pixels, transparency independence, shader uniforms, frozen bones, reload persistence, snapshots and advancing breathing. model-import-check.json covers five real model packages and failed-load recovery. An intentionally darkened Ringo matched against the live PMX Editor capture with measured error reduced from 0.27653 to 0.09491; its original native settings were restored afterwards. Amika's ParamBreath changed while 48 real frames advanced, and the VRM blink weight reached 0.998.

No installer, APK or published update is produced by this checkpoint. Full native feature/UI parity is still incomplete. The original application remains intact.

The eight native appearance sliders use the same ranges as the reference app. Automatic matching searches the original .3/.12/.05 steps, retains directional shading and checks whole-model plus up to six measurable material references. After snapshots wait for a fresh frame, so they cannot accidentally show the final isolated reference material.

The native local model picker remembers its last folder separately for PMX, VRM and Live2D, including across reopening. A live Ringo-folder reopen check passed. SDK example scenes remain local owner-imported files and are excluded from this code checkpoint; SDK runtime, editor support, renderer assets and licenses are committed. The ownerâ€™s Unity cloud-connect preference is not changed or committed.

## Native voice and expressions checkpoint â€” 2026-11-13

The runtime assembly explicitly references Unity.InputSystem for focused expression shortcuts. A normal editor restart rebuilt the dependency graph and cleared the Safe Mode compilation errors. The obsolete duplicate mcs.rsp was removed; csc.rsp retains the unsafe setting required by the imported SDK. Voice settings open on the Voice tab, and the native microphone starts off.

Live tests exercised Ringo's 22 expressions plus a disposable synthetic mesh for strength, clearing, preview, rename and shortcut persistence. Voice checks cover PCM16/WAV clipping, RMS, VAD silence/click rejection, Unicode wake boundaries and Hindi transcript preservation without opening the microphone. These checks do not establish physical microphone quality or conversational parity.

The bridge captures the application's merged router lifespan rather than only the root lifespan, preserving Agent router lifecycle handlers when services start lazily. Native chat and speech retain mode-generation checks and desktop authentication; audio downloads accept only the owned /audio filename route. Tokens remain in memory and are not written into verification reports.

An early local-model greeting timed out after 120 seconds and exposed an empty-error-body NullReferenceException in the native transport. The transport now reports a visible failure and clears Busy; an isolated HTTP 503 regression test confirms it. Lazy optional audio imports subsequently allowed a real local greeting to return HTTP 200 in 10.1 seconds. Physical speech and sustained conversation remain unverified.


## PC parity correction checkpoint â€” 2026-11-13

PC parity takes priority over further mobile work, following the owner's correction. The reference is the current Sakura desktop source and its existing UI. The original source, SDK samples and personal assets remain in place.

The native shell now has separate Companion and VTuber toolbars using the reference artwork. VTuber has its position lock and hide-controls buttons, tracking footer, and the VTuber logo. Companion-only conversation/provider settings are excluded from the VTuber settings pages. A repeated toolbar click closes its pane and clears selection.

Model Settings uses the reference zoom row and five PMX/VRM tabs. Live2D retains eight tabs. Texture coloring includes an HSV wheel, RGB inputs and separate transparency editing. Texture replacement requires explicit selection followed by Apply. PMX appearance controls sit inside Texture coloring. Expression cards have centered expansion controls, editable names, preview, strength, activation and focused shortcut recording with Escape cancellation. Voice & speech opens directly with Friend/Teacher switches. Camera, phone, scenery and output controls use the reference card grouping.

Native UI checks exercise tab ordering, clipping, material picking, RGB controls, texture selection/apply, both navigation lists, position locking, expression expansion, Voice pane structure and VTuber settings isolation. Captures cover desktop, compact and light-theme layouts under C:/AI/Unity-Verification. A passed UI check covers those explicit assertions only.

### Outstanding parity work

- Full Live2D outline/authored-glow equivalence and broader PMX shader/reference comparisons across real models.
- Migration of original browser-stored model overrides. Native bone diagnosis is implemented; automatic repair is not.
- Authenticated live-account cloud-preview/download acceptance. Local rotating previews and published cloud sprite-sheet playback are implemented and checked without signing into a cloud account.
- Native collab is implemented and checked locally; multi-machine WebRTC and remote media remain unverified. Remaining VTS events are not emitted by the native bridge.
- External VMC/Cubism interoperability. Native VMC sending and the existing VBridger facial-input route exist. Export watching reloads changed Live2D exports; an Editor-to-Sakura parameter feed is absent from the backend.
- Physical sustained gameplay, microphone/wake interaction, voice enrollment, voice-changing capture, lip sync, output receivers and emergency-close hardware verification. Session-only headphone interruption has no acoustic echo cancellation.
- Photo-real visual equivalence for every shader and model. Exact ArtMesh/bone prop attachment, item ordering, pinning, throws and authored item motions are implemented.
- A fully interactive embedded Agent browser and independent installer/update packaging.
- Phone and physical receiver verification. Further mobile migration is paused until PC parity is accepted.

The Windows executables under C:/AI/unity exe remain development previews linked to the existing source environment. Full feature and visual parity is incomplete. No release is published from this checkpoint.

The PC review also closed minimized-chat height overriding its collapsed style, active wake settings becoming stale after Save, saved theme being ignored at startup, and shortcut recording continuing after a pane was hidden. Regression checks cover collapse/expand, explicit theme application and cancellation on both close and hide. Physical microphone behavior remains unverified.

## Integrated PC controls checkpoint â€” 2026-11-13

Agent mode now has the reference sidebar, lower composer, collapsed task settings, provider/access controls, recent tasks, persisted task conversation, searchable archived history and management-page navigation. A live disposable backend task passed create, inspect, pending approval, rejection, cancellation and deletion. No test action executed against owner files. The saved conversation preference was restored after the test.

VRM and Live2D now have native independent colour/transparency editing, atlas/texture replacement and persistent model profiles. VRM bone freezing excludes frozen joints from spring-chain simulation. Live2D has dedicated parameter output, smoothing, automatic blink/breath controls and per-model saturation, contrast, colour overlay and silhouette-contained glow. Shader fixture checks cover premultiplied RGB, unchanged alpha, in-place rendering and persistence. Five real imports passed shader support and nonempty-render checks. Hardware movement and receiver behavior remain unverified.

Voice & speech now includes grouped voice selection/preview, recognised-voice management for up to five profiles, rename/remove, matching toggle, consented recognition download with progress, and explicit six-second enrollment. Normal listening is paused during enrollment. RVC uploads use bounded file streaming into a temporary multipart body, then UploadHandlerFile, with teardown cleanup. Native file browsing and live microphone voice conversion remain pending. AI settings expose installed native/GGUF/Ollama choices, provider discovery and a saved-connection test.

Background controls support colour swatches, HSV/RGB editing, a solid OBS output background, normalized local images and muted repeating local video. Background media is saved independently of character assets. Images/videos appear in app-window capture. The dedicated output uses solid colour when opted in, while the avatar render texture keeps alpha for model-only hit testing. Synthetic GPU checks cover background placement, picking exclusion, transparent-pixel replacement and opaque-avatar preservation. A physical video-file playback test remains pending.

Seventeen bridge/tracking Python tests pass. Native checks cover Agent UI/lifecycle, speaker validation and small multipart fixtures, model tools/effects, backdrop compositing, original shell/layout, expressions, PMX editing, tracking kernels, stage transforms, output dimensions and double-click behavior. No physical microphone recording or cloud publication was performed. These checks are bounded acceptance evidence. Full 1:1 feature parity remains incomplete, including the outstanding items above.
## Screen, integration and guided-setup checkpoint â€” 2026-11-13

The native first-run guide follows the original Companion five-step and VTuber four-step sequences. Next opens and highlights its target. Skip/Finish and seen state persist independently, and Settings provides explicit replay. Navigation checks preserve the previous preferences.

Companion screen controls now use authenticated, explicit capture leases. Switching into Agent or VTuber clears those leases. Fresh bounded JPEG frames can accompany ordinary questions or occasional source-backed reactions. Real desktop capture returned a valid fresh frame, was stopped, and was not sent to an AI provider during the check. Game detection reuses the existing process/GPU sampler and model-switching policy. Successful model selection is retained, cloud fallback uses hysteresis, and stale in-flight game requests restore the universal model. The emergency guard matches the original sustained-pressure delay and cancellable countdown. Closing a player requires fresh backend confirmation; Editor checks use synthetic counters and never close the Editor.

Native VMC sends supported humanoid bones, root, trackers and expressions as bounded OSC/UDP bundles. The sender defaults off and has mirror, scale and recenter controls. Deterministic encoding checks passed. Receiver interoperability remains unverified. Cubism sends the loaded native Live2D parameter values to the existing Editor service at up to 10 Hz. Real SDK capture preserved final tracking/expression values in Amika; no external Editor connection was opened during this check. The existing backend does not provide an Editor-to-Sakura parameter feed.

Cloud preview requests retain existing signed-in account authorization, use a two-request queue and an 8 MB streaming limit. Published WebP previews are normalized into bounded PNG sprite sheets for native hover playback. Live2D uses a portrait frame. Unit checks cover animation preservation, authorization rejection, UVs and dimension bounds. A signed-in live download/preview test remains pending.

Render presets now bound the actual avatar texture resolution and MSAA while preserving viewport aspect ratio. Balanced and High target 60 FPS, Low targets 30 FPS. These are configured targets rather than measured guarantees on GTX/RTX hardware.

Twenty-one Python bridge/tracking tests pass. New native checks cover first-run navigation, guarded screen payloads, game pressure timing/cancellation/hysteresis, VMC packet limits, Cubism parameter schema/capture, preview decoding and graphics presets. Full 1:1 parity remains incomplete. The original project and personal assets remain intact.

## Companion reply and native integration checkpoint â€” 2026-11-13

Structured chat replies now load an installed model before presenting a successful outfit change. Failures retain a truthful error, library requests open the searchable picker, and expression commands resolve existing labels. Automatic reply emotions use supported facial expressions, fade in and preserve manual-expression priority. Costume/mouth controls are excluded. Source-backed Teacher tasks expose a route into their native Agent review page.

Room-description requests use a bounded 768-pixel JPEG from Unity's virtual stage and label it as a virtual room. Appearance questions can include the native avatar view when desktop screen sharing is already enabled. Ordinary greetings omit desktop vision. These captures do not include the app controls and do not require a physical camera. A real configured local-model request returned a short greeting successfully, with audio generation deferred.

Focused action shortcuts and `.vtube.json` expression shortcuts are imported with manual bindings preserved. Native Live2D right-drag/rotation now rolls in the screen plane while retaining its camera-facing orientation. Its per-model physics cadence, legacy setting, movement bounds, blink/breath setup and alpha-edge correction have SDK-backed checks. A live Amika rig retained its authored sub-rigs, wind and gravity through cadence changes; temporary settings and profile bytes were restored afterwards.

Agent uses the final reference blossom palette in both themes, outline navigation icons and a wrapping planning-model selector. Screenshot review caught icon/text overlap and added a layout assertion. The original backend suite passed 2,256 tests with two skips and 295 subtests. Twenty-one Unity bridge/tracking tests also passed. Native checks passed for Agent task create/propose/reject/cancel/delete, PMX editing, expression/emotion routing, window-slot limits, voice command parsing, stage isolation, output dimensions/compositing and first-run navigation.

Additional windows receive independent backend settings and a fresh stage; the primary window retains its selected character and background. Shared model assets and their saved per-model edits remain available. The current Windows builds remain source-dependent development previews. Full 1:1 parity, the outstanding features listed above and hardware acceptance remain unfinished.


## Built-player and native parity checkpoint â€” 2026-11-13

The native plugin importer previously enabled `mmd_runtime_ffi.dll` only for the Editor: its serialized Windows target was not recognized by Unity. A real Windows preview reproduced the missing-library import failure. The importer now explicitly enables Win64/x86_64, and the Windows builder refuses to build if that prerequisite is disabled. Built-player model loading is checked separately from Editor imports.

Material color and transparency presets are now reachable from both coloring panels and Expressions & hotkeys. They use stable model material identifiers, independent channels, finite bounds (including negative transparency), focused shortcuts and exact previous-value restoration. Real Ringo and Amika checks restored their original appearance/profile after temporary tests. VTube animation hotkeys now discover TriggerAnimation/StartAnimation as well as expression actions.

Additional native integrations include Live2D input mappings, parameter-only authored motion playback, bounded motion3 recording, export-change watching, local library hover previews, stage-item motions/throwing, voice changing, audio-envelope lip sync and Windows DWM transparency with compact/Agent mode restoration. Tests cover supported semantics rather than asserting equivalence for unimplemented features. The three-window limit and fourth-window refusal were exercised in built VTuber players. Screenshots/recordings unpremultiply color correctly when encoding straight-alpha output; streaming buffers retain their expected representation.

The original backend suite passed 2,256 tests (two skipped, 295 subtests), and the Unity bridge/tracking suite passed 22 tests. Physical microphone/phone/output receiver behavior and the outstanding features above remain open. This is a local development checkpoint, not a published release or a claim of complete parity.


Built Windows acceptance passed after the importer correction: both editions connected to the original backend, listed 544 local characters and imported Ringo with no native-library error. Companion retained DWM transparency, forced Agent opacity, restored its compact geometry/transparency on return and kept the microphone off. VTuber stayed isolated when asked to switch to Companion. A compact layout check now guards panel/footer, chat/footer and button/microphone overlap; the final previews include the corrected spacing.

Bug Hunter was installed into the local Codex skills directory. Its preflight and deterministic runtime-file triage ran; a complete canonical Bug Hunter audit has not been claimed or completed by this checkpoint.

## Native collab, pinning and plugin parity checkpoint — 2026-11-13

VTuber Collab now has a native dock pane backed by `SakuraNativeCollab`, WebRTC data channels and the existing authenticated loopback backend. The pane shares the selected model package, exchanges bounded poses at 20 Hz, imports remote VRM/PMX/Live2D avatars into per-peer viewports, and keeps room codes, WSS relay validation, optional TURN credentials and shared chat. A three-file package validator rejects path traversal, duplicate entries, links and executable files. Signaling and pose messages are bounded and validated before use.

Stage items now pin to a specific Live2D ArtMesh or 3D bone instead of only the whole avatar. Pin targets resolve once per frame, follow zoom/rotation, hide when the pinned character is unavailable, and persist per model. The VTS bridge now implements the item list/load/unload/move/sort/animation commands, ArtMesh selection, colour tint, local model URL loading and NDI configuration, using the same bounded native APIs as the UI.

PMX bone diagnosis reports missing standard humanoid bones, duplicate names and non-finite transforms on demand. It does not repair a rig or migrate browser-stored overrides.

The native VTS bridge now emits `ModelLoadedEvent`, `ItemEvent` and `TrackingStatusChangedEvent` lifecycle messages to subscribed plugin clients, and the global Hotkeys list matches the reference actions including show/hide stage items and model movement in four directions. Effects now include the reference 3D model outline through an inverted-hull URP shader; the Live2D authored-glow toggle remains unavailable because the native renderer has no authored glow pass.

`SakuraNativeCollabChecks` covers relay URL/room validation, package path rejection, pose bounds, pinned-attachment rotation/round-trip and pin-field validation. The Unity runtime compiles with the collab, stage-item, PMX and plugin changes. Existing stage-item, tracking and plugin bridge checks still pass. Physical multi-machine WebRTC, remote microphone/video, cloud-account previews and external receivers remain unverified; embedded Agent browsing and installer/update preservation remain open.
