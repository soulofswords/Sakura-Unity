#!/usr/bin/env python3
"""Safe process bridge for the optional, separately installed Model-To-PMX tool.

The GPL-3.0 converter is not bundled. This bridge accepts only its reviewed pinned
commit, writes into Sakura's converted-model area, never edits the source, and
uses stdin line `CANCEL` to stop the child process and discard its private stage.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

PINNED_COMMIT = "5b00e66ac5585e0c6402ea30ccab5c05fad6db2e"
MAX_INPUT_BYTES = 1024 * 1024 * 1024
MAX_OUTPUT_BYTES = 4 * 1024 * 1024 * 1024


def target_extension(source_ext: str) -> str:
    ext = (source_ext or "").lower()
    if ext == ".vrm": return ".pmx"
    if ext == ".pmx": return ".vrm"
    raise ValueError("Only VRM ↔ PMX conversion is supported.")


def validate_source(model_root: Path, source: Path, max_bytes: int = MAX_INPUT_BYTES) -> Path:
    root = Path(model_root).resolve(strict=True)
    candidate = Path(source).resolve(strict=True)
    if not candidate.is_file() or not candidate.is_relative_to(root):
        raise ValueError("Select a model file inside Sakura's local models folder.")
    target_extension(candidate.suffix)
    size = candidate.stat().st_size
    if size <= 0 or size > max_bytes:
        raise ValueError(f"The selected model must be between 1 byte and {max_bytes // (1024**2)} MiB.")
    return candidate


def safe_name(value: str) -> str:
    value = (value or "").strip()
    if (not value or len(value) > 80 or value in {".", ".."}
            or not re.fullmatch(r"[\w -]+", value, flags=re.UNICODE)):
        raise ValueError("Use a name with letters, numbers, spaces, '_' or '-'.")
    return re.sub(r"\s+", "_", value)


def output_path(model_root: Path, name: str, extension: str, source: Path) -> Path:
    root = (Path(model_root) / "converted").resolve()
    output = (root / safe_name(name)).resolve()
    if output == Path(source).resolve() or not output.is_relative_to(root):
        raise ValueError("Unsafe output path; conversion cannot overwrite its source.")
    if output.exists():
        raise FileExistsError(f"Output already exists: {output}")
    return output


def staging_path(converted_root: Path, job_id: str) -> Path:
    if not re.fullmatch(r"[0-9a-fA-F-]{36}", job_id or ""):
        raise ValueError("Invalid conversion job ID.")
    root = Path(converted_root).resolve()
    stage = (root / (".sakura-convert-" + job_id.lower())).resolve()
    if not stage.is_relative_to(root):
        raise ValueError("Unsafe temporary output path.")
    return stage


def converter_script(converter_root: Path, source_ext: str) -> Path:
    root = Path(converter_root).resolve(strict=True)
    script_name = "vrm2pmx.py" if source_ext.lower() == ".vrm" else "pmx2vrm.py" if source_ext.lower() == ".pmx" else None
    if not script_name:
        target_extension(source_ext)
    script = (root / "convert" / script_name).resolve(strict=True)
    formats = (root / "formats").resolve(strict=True)
    if not script.is_file() or not script.is_relative_to(root) or not formats.is_dir() or not formats.is_relative_to(root):
        raise ValueError("Select the full pinned Model-To-PMX source folder (it must contain convert/ and formats/).")
    try:
        result = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"], check=True,
                                capture_output=True, text=True, timeout=10)
        commit = result.stdout.strip().lower()
    except (OSError, subprocess.SubprocessError) as exc:
        raise ValueError("Git is required to verify the optional converter's pinned revision.") from exc
    if commit != PINNED_COMMIT:
        raise ValueError(f"Unsupported converter revision. Sakura requires pinned commit {PINNED_COMMIT}.")
    return script


def mapping_report(source_name: str, output_name: str, source_ext: str, output_bytes: int) -> dict:
    direction = "VRM to PMX" if source_ext.lower() == ".vrm" else "PMX to VRM"
    return {
        "schema": 1, "status": "needs-review", "direction": direction,
        "source": Path(source_name).name, "output": Path(output_name).name,
        "output_bytes": output_bytes, "converter_commit": PINNED_COMMIT,
        "limitations": [
            "Humanoid bones are mapped heuristically; inspect the generated rig and adjust bone names/axes if needed.",
            "Morph/expression support depends on source data and is not guaranteed to map one-to-one.",
            "Materials and textures are converted best-effort; alpha, toon, sphere and MToon properties can change.",
            "VRM spring bones and PMX rigid bodies/joints are not 1:1 and must be rebuilt in the target format.",
            "Scale and forward-axis conventions can differ; verify stance, handedness and camera framing.",
        ],
    }


def emit(progress: int, message: str) -> None:
    print("SAKURA_PROGRESS " + json.dumps({"progress": max(0, min(100, progress)), "message": message}, ensure_ascii=False), flush=True)


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True)
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--converter-root", required=True)
    parser.add_argument("--python", required=True, dest="python_exe")
    parser.add_argument("--output-name", required=True)
    parser.add_argument("--job-id", required=True)
    return parser.parse_args(argv)


def run(args) -> int:
    source = validate_source(Path(args.model_root) / "models", Path(args.input))
    output_ext = target_extension(source.suffix)
    name = safe_name(args.output_name)
    converter = Path(args.converter_root).resolve(strict=True)
    script = converter_script(converter, source.suffix)
    python_exe = Path(args.python_exe).resolve(strict=True)
    if not python_exe.is_file():
        raise ValueError("Select an installed Python executable.")
    model_root = (Path(args.model_root).resolve(strict=True) / "models").resolve(strict=True)
    final = output_path(model_root, name, output_ext, source)
    converted_root = final.parent
    converted_root.mkdir(parents=True, exist_ok=True)
    stage = staging_path(converted_root, args.job_id)
    stage.mkdir()
    staged_model = stage / (name + output_ext)
    report_path = stage / "mapping-report.json"
    child = None
    cancelled = threading.Event()

    def read_cancel():
        try:
            if sys.stdin.readline().strip().upper() == "CANCEL":
                cancelled.set()
                if child and child.poll() is None:
                    child.terminate()
        except OSError:
            pass

    threading.Thread(target=read_cancel, daemon=True).start()
    try:
        if source.suffix.lower() == ".vrm":
            command = [str(python_exe), str(script), str(source), "--out", str(staged_model), "--name", name]
        else:
            command = [str(python_exe), str(script), str(source), "--out", str(staged_model), "--title", name, "--author", "Sakura"]
        emit(4, "Verifying pinned converter and preparing a private output folder")
        environment = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
        child = subprocess.Popen(command, cwd=str(converter), stdin=subprocess.DEVNULL, env=environment,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                 encoding="utf-8", errors="replace", bufsize=1)
        emit(10, "Running optional converter; model inspection is still required")
        last_emit = time.monotonic()
        converter_log = []
        if child.stdout:
            for line in child.stdout:
                line = line.rstrip()
                if line and len(converter_log) < 300:
                    converter_log.append(line[:2000])
                if line:
                    print("SAKURA_CONVERTER_LOG " + line[:2000], flush=True)
                if time.monotonic() - last_emit >= 2:
                    emit(12, "Converter is processing the model; this tool does not expose a precise percent")
                    last_emit = time.monotonic()
        code = child.wait()
        if cancelled.is_set():
            emit(0, "Conversion cancelled")
            return 130
        if code != 0:
            raise RuntimeError(f"External converter exited with code {code}.")
        if not staged_model.is_file() or staged_model.stat().st_size <= 0:
            raise RuntimeError("Converter exited successfully but did not produce the requested model file.")
        files = [p for p in stage.rglob("*") if p.is_file()]
        total = sum(p.stat().st_size for p in files)
        if total > MAX_OUTPUT_BYTES:
            raise RuntimeError("Converted package exceeds Sakura's 4 GiB output limit.")
        report = mapping_report(source.name, staged_model.name, source.suffix, total)
        report["converter_log"] = converter_log
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        # Windows rename fails if a destination appeared while the tool ran. It never replaces.
        os.rename(stage, final)
        final_model = final / staged_model.name
        emit(100, "Conversion complete; inspect the model and mapping report")
        print("SAKURA_RESULT " + json.dumps({"output": str(final_model), "folder": str(final),
                                             "report": str(final / report_path.name)}, ensure_ascii=False), flush=True)
        return 0
    finally:
        if child is not None and child.poll() is None:
            child.terminate()
            try: child.wait(timeout=5)
            except subprocess.TimeoutExpired: child.kill(); child.wait(timeout=5)
        if stage.exists():
            shutil.rmtree(stage, ignore_errors=True)


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    try:
        raise SystemExit(run(parse_args()))
    except Exception as error:
        print("SAKURA_CONVERTER_ERROR " + str(error), file=sys.stderr, flush=True)
        raise SystemExit(2)
