import importlib.util
import tempfile
import subprocess
import json
import struct
import unittest
from unittest.mock import patch
from pathlib import Path

spec = importlib.util.spec_from_file_location("vrm_pmx_convert", Path(__file__).with_name("vrm_pmx_convert.py"))
convert = importlib.util.module_from_spec(spec)
spec.loader.exec_module(convert)


class ConvertSafetyTests(unittest.TestCase):
    def test_vrm_omits_null_optional_thumbnail_without_changing_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "output.vrm"
            data = {"asset": {"version": "2.0"}, "extensions": {"VRMC_vrm": {"meta": {"name": "Fixture", "thumbnailImage": None}}}}
            encoded = json.dumps(data).encode(); encoded += b" " * (-len(encoded) % 4)
            binary = struct.pack("<II", 4, 0x004E4942) + b"DATA"
            path.write_bytes(struct.pack("<III", 0x46546C67, 2, 20 + len(encoded) + len(binary)) + struct.pack("<II", len(encoded), 0x4E4F534A) + encoded + binary)
            self.assertTrue(convert.normalize_vrm(path))
            result = path.read_bytes(); length = struct.unpack_from("<I", result, 12)[0]
            parsed = json.loads(result[20:20 + length])
            self.assertNotIn("thumbnailImage", parsed["extensions"]["VRMC_vrm"]["meta"])
            self.assertEqual(result[20 + length:], binary)
            self.assertEqual(struct.unpack_from("<I", result, 8)[0], len(result))
            self.assertFalse(convert.normalize_vrm(path))
            self.assertEqual(path.read_bytes(), result)

    def test_modified_converter_is_rejected_even_when_head_is_pinned(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "convert").mkdir()
            (root / "formats").mkdir()
            script = root / "convert" / "vrm2pmx.py"
            script.write_text("# original", encoding="utf-8")
            (root / "formats" / "pmxio.py").write_text("# original", encoding="utf-8")
            def git(*args):
                return subprocess.run(["git", "-C", str(root), *args], check=True,
                                      capture_output=True, text=True).stdout.strip()
            git("init", "-q")
            git("add", ".")
            git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture")
            with patch.object(convert, "PINNED_COMMIT", git("rev-parse", "HEAD")):
                self.assertEqual(convert.converter_script(root, ".vrm"), script.resolve())
                script.write_text("raise RuntimeError('unreviewed edit')", encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "modified"):
                    convert.converter_script(root, ".vrm")

    def test_only_cross_format_conversion_is_allowed(self):
        self.assertEqual(convert.target_extension(".vrm"), ".pmx")
        self.assertEqual(convert.target_extension(".PMX"), ".vrm")
        for ext in (".zip", ".fbx"):
            with self.assertRaises(ValueError):
                convert.target_extension(ext)

    def test_converter_scripts_are_selected_from_validated_external_install(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "convert").mkdir()
            (root / "formats").mkdir()
            for name in ("vrm2pmx.py", "pmx2vrm.py"):
                (root / "convert" / name).write_text("# pinned converter", encoding="utf-8")
            with patch.object(convert.subprocess, "run") as git:
                git.return_value.stdout = convert.PINNED_COMMIT
                git.side_effect = lambda command, **kwargs: subprocess.CompletedProcess(
                    command, 0, stdout=git.return_value.stdout if "rev-parse" in command else "")
                self.assertEqual(convert.converter_script(root, ".vrm").name, "vrm2pmx.py")
                self.assertEqual(convert.converter_script(root, ".pmx").name, "pmx2vrm.py")
                git.return_value.stdout = "wrong revision"
                with self.assertRaises(ValueError):
                    convert.converter_script(root, ".pmx")
            (root / "convert" / "pmx2vrm.py").unlink()
            with self.assertRaises(FileNotFoundError):
                convert.converter_script(root, ".pmx")

    def test_source_is_inside_models_root_and_limited(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp); root = base / "models"; root.mkdir()
            source = root / "avatar.vrm"; source.write_bytes(b"fixture")
            self.assertEqual(convert.validate_source(root, source), source.resolve())
            outside = base / "outside.vrm"; outside.write_bytes(b"fixture")
            with self.assertRaises(ValueError):
                convert.validate_source(root, outside)
            too_big = root / "large.vrm"; too_big.write_bytes(b"12345")
            with self.assertRaises(ValueError):
                convert.validate_source(root, too_big, max_bytes=4)

    def test_safe_output_never_overwrites_source_or_existing_output(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); models = root / "models"; models.mkdir()
            source = models / "hero.vrm"; source.write_bytes(b"source")
            out = convert.output_path(models, "Hero Converted", ".pmx", source)
            self.assertEqual(out.parent, (models / "converted").resolve())
            out.parent.mkdir()
            out.mkdir()
            with self.assertRaises(FileExistsError):
                convert.output_path(models, "Hero Converted", ".pmx", source)
            with self.assertRaises(ValueError):
                convert.safe_name("../escape")

    def test_staging_id_is_validated_and_contained(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "converted"
            one = convert.staging_path(root, "12345678-1234-1234-1234-123456789abc")
            self.assertTrue(one.is_relative_to(root.resolve()))
            with self.assertRaises(ValueError):
                convert.staging_path(root, "../../outside")

    def test_mapping_report_marks_format_losses(self):
        report = convert.mapping_report("hero.vrm", "hero.pmx", ".vrm", 12)
        self.assertEqual(report["status"], "needs-review")
        self.assertTrue(any("spring" in item.lower() for item in report["limitations"]))
        self.assertTrue(any("rigid" in item.lower() for item in report["limitations"]))


if __name__ == "__main__":
    unittest.main()
