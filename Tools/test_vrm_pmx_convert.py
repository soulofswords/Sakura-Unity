import importlib.util
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

spec = importlib.util.spec_from_file_location("vrm_pmx_convert", Path(__file__).with_name("vrm_pmx_convert.py"))
convert = importlib.util.module_from_spec(spec)
spec.loader.exec_module(convert)


class ConvertSafetyTests(unittest.TestCase):
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
