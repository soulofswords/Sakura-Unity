# Model selection → Converter

The tab accepts installed local PMX/VRM models and writes a new package under `models/converted/<output-name>`. Existing output names are refused. Source models are never overwritten. The status percentage covers preparation and completion; the external engine has no precise progress percentage. Cancel signals the bridge, terminates its child and cleans its own staging directory.

Install the optional external engine separately:

```powershell
git clone https://github.com/SaraKale/Model-To-PMX.git C:\AI\Model-To-PMX
git -C C:\AI\Model-To-PMX checkout 5b00e66ac5585e0c6402ea30ccab5c05fad6db2e
```

Select that folder in the Converter tab. Git verifies the selected revision. The engine is GPL-3.0 and is not bundled in Sakura. It runs using Sakura's Python as a separate process. Its source and license remain at https://github.com/SaraKale/Model-To-PMX . Review the license before distributing it yourself.

Outputs contain `mapping-report.json` with format limitations and the converter's bounded log. The real Irida smoke check completed both directions but the return pass synthesized humanoid bones. Geometry/morph export success does not demonstrate a usable humanoid rig. Toon/sphere/MToon appearance, transparency, scale/axes and physics can change. VRM spring bones and PMX rigid bodies are not interchangeable. This converter remains experimental until rig-fidelity acceptance is complete.

The preferred Blender pipeline was not selected: a generic imported VRM is not an MMD export root, so simply invoking an exporter does not establish a working pipeline. Alternative engine evaluation and comprehensive fixture round trips remain open.
