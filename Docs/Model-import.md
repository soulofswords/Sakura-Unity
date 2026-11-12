# Native model import verification

The Unity client loads existing model packages directly from Sakura's models directory. PMX uses the MIT-licensed unity-mmd-loader runtime and its URP toon shader. Live2D uses the owner-imported official Cubism Unity URP SDK and its masking/blend shaders. VRM retains UniVRM's URP MToon renderer.

Implementation sequence

1. Add format-specific loaders with confined file access and owned-resource cleanup.
2. Route both model searches and format pickers through the shared viewport load method.
3. Register Cubism's URP render feature and preserve shader references for builds.
4. Test PMX texture decoding, toon/sphere bindings and shader support with Ringo.
5. Test an existing Live2D model and DeepSeek with masks and transparent edges.
6. Verify format switching and failed-load recovery preserve a working viewport.

Runtime model loading and the Upload file-picker workflow are separate checks. This pass must report their actual verification state independently.

## Dependency provenance

MMD Loader is vendored from yohawing/unity-mmd-loader commit 8928de9076c79df6dc1263f990725785c245d581. Its license is preserved inside the embedded package. A compatibility patch removes an assignment to HumanDescription.rootMotionBoneName, which is unavailable in Unity 6000.3.28f1. Native MMD physics and animation still require separate runtime verification.

Cubism was imported by the owner from Live2D's SDK download. Its proprietary Core and open components retain their supplied licenses. Publication requires reviewing the SDK's applicable distribution terms. This development pass does not publish SDK binaries or model files.

Additional local MMD patches preserve duplicate display names (bones remain indexed), expose the bounded texture decoder for confirmed remaps, and add the four native appearance contribution uniforms to the URP toon shader. The original MIT license remains included. Cubism uses its dedicated official URP renderer at index 1; the original 3D renderer remains index 0. Runtime mask-atlas count grows for models such as DeepSeek with 49 clipping groups.

New import packages use the source application's existing package validation/deduplication. Duplicate checks for Ringo, Amika and ArisuMaid returned already_installed without creating duplicate folders. File-picker cancellation and backend-process failures surface as status messages. Manual selection in the actual native file picker still needs an owner interaction check.
