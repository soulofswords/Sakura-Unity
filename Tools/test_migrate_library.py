import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("migration", Path(__file__).with_name("migrate_library.py"))
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


class MigrationTests(unittest.TestCase):
    def test_copy_is_verified_repeatable_and_preserves_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); old = root / "old"; new = root / "new"
            (old / "models").mkdir(parents=True); new.mkdir()
            (new / "app.py").write_text("# backend")
            (old / "models" / "avatar.pmx").write_bytes(b"avatar")
            (old / "owner-token.bin").write_bytes(b"private")
            self.assertEqual(migration.migrate(old, new, True)["planned"], 1)
            self.assertFalse((new / "models").exists())
            self.assertEqual(migration.migrate(old, new)["copied"], 1)
            self.assertEqual(migration.migrate(old, new)["copied"], 0)
            self.assertEqual((old / "models/avatar.pmx").read_bytes(), b"avatar")
            self.assertFalse((new / "owner-token.bin").exists())
            (new / "models/avatar.pmx").write_bytes(b"user edits")
            with self.assertRaises(FileExistsError):
                migration.migrate(old, new)
            self.assertEqual((new / "models/avatar.pmx").read_bytes(), b"user edits")
