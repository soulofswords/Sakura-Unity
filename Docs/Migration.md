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
| Local catalog | 544 real references discovered, three bridge unit tests passing |
| Authenticated backend | Existing API authorization probe passing, Unity chat transport remains unverified |
| Avatar rendering | VRM runtime import and rendering verified with ArisuMaid, PMX and Live2D pending |
| Tracking, expressions and physics | Pending |
| Voice and microphone | Pending |
| Agent and browser | Pending |
| Cloud library and mobile | Pending |
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
