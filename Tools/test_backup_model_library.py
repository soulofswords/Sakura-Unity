import base64
import hashlib
import importlib.util
import json
import tempfile
import unittest
import io
from unittest.mock import patch
from pathlib import Path

spec = importlib.util.spec_from_file_location("backup_model_library", Path(__file__).with_name("backup_model_library.py"))
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)


class BackupTests(unittest.TestCase):
    def test_cli_upload_handles_unicode_paths_on_a_windows_console(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp); root = base / 'library'; root.mkdir()
            (root / '使用规则.txt').write_text('model rules', encoding='utf-8')
            client_file = base / 'client.json'; client_file.write_text('{}')
            output = io.BytesIO(); console = io.TextIOWrapper(output, encoding='cp1252')
            with patch.object(backup, 'DriveClient') as factory, patch('sys.stdout', console):
                client = factory.return_value
                client.list_target.return_value = []
                client.check_target.return_value = {'name': 'Backup'}
                self.assertEqual(backup.main(['--source', 'library=' + str(root), '--oauth-client', str(client_file)]), 0)
                self.assertEqual(client.upload_resumable.call_count, 2)
                manifest = client.upload_resumable.call_args.args[1]
                self.assertRegex(manifest['md5'], r'^[A-Za-z0-9+/]{22}==$')
            console.flush()
            self.assertIn('使用规则.txt', output.getvalue().decode('utf-8'))
            console.detach()

    def test_upload_dry_run_never_constructs_client_or_writes_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'library'; root.mkdir()
            (root / 'model.pmx').write_bytes(b'owner model')
            before = {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}
            with patch.object(backup, 'DriveClient', side_effect=AssertionError('dry run connected to Drive')), patch('sys.stdout', new_callable=io.StringIO):
                self.assertEqual(backup.main(['--source', 'library=' + str(root), '--dry-run']), 0)
            self.assertEqual(before, {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()})

    def test_restore_dry_run_lists_but_never_downloads_models_or_creates_destination(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp); client_file = base / 'client.json'; client_file.write_text('{}')
            destination = base / 'not-created'
            manifest = {'files': [{'path': 'library/model.pmx', 'sha256': 'abc', 'size': 3}]}
            with patch.object(backup, 'DriveClient') as factory, patch('sys.stdout', new_callable=io.StringIO):
                client = factory.return_value
                client.list_target.return_value = [
                    {'id': 'manifest', 'appProperties': {'sakuraManifest': '1'}},
                    {'id': 'model', 'appProperties': {'sakuraPath': 'library/model.pmx', 'sakuraSha256': 'abc'}}]
                client.open.return_value.__enter__.return_value.read.return_value = json.dumps(manifest).encode()
                self.assertEqual(backup.main(['--restore', '--restore-to', str(destination), '--oauth-client', str(client_file), '--dry-run']), 0)
                client.open.assert_called_once_with(backup.API + '/files/manifest?alt=media')
                client.upload_resumable.assert_not_called()
                client.login.assert_not_called()
            self.assertFalse(destination.exists())

    def test_manifest_hashes_files_and_preserves_source_labels(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "source"
            (root / "cat").mkdir(parents=True)
            file = root / "cat" / "model.zip"
            file.write_bytes(b"model")
            entries = backup.build_manifest([("account-1", root)])
            self.assertEqual(len(entries), 1)
            self.assertEqual(entries[0]["path"], "account-1/cat/model.zip")
            self.assertEqual(entries[0]["sha256"], hashlib.sha256(b"model").hexdigest())
            self.assertEqual(entries[0]["md5"], base64.b64encode(hashlib.md5(b"model").digest()).decode())

    def test_resumable_upload_verifies_the_hex_checksum_returned_by_drive(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root / 'model.pmx').write_bytes(b'model')
            entry = backup.build_manifest([('library', root)])[0]
            client = object.__new__(backup.DriveClient)
            start = io.BytesIO(); start.headers = {'Location': 'https://www.googleapis.com/upload/session-fixture'}
            completed = io.BytesIO(json.dumps({'size': '5', 'md5Checksum': base64.b64encode(hashlib.md5(b'model').digest()).decode()}).encode())
            with patch.object(client, 'open', side_effect=[start, completed]) as request:
                self.assertEqual(client.upload_resumable('folder', entry)['size'], '5')
            self.assertEqual(request.call_args.args[1], b'model')

    def test_manifest_never_follows_symlink(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp); root = base / "source"; root.mkdir()
            target = base / "secret"; target.write_text("secret")
            link = root / "linked"
            try:
                link.symlink_to(target)
            except OSError:
                self.skipTest("symlinks unavailable")
            self.assertEqual(backup.build_manifest([("library", root)]), [])

    def test_drive_index_matches_path_and_digest_and_identifies_changed_files(self):
        local = [{"path": "account-a/a.zip", "sha256": "same", "size": 3}]
        remote = {("account-a/a.zip", "same"): {"sha256": "same", "size": 3}, ("old.zip", "old"): {"sha256": "old", "size": 2}}
        self.assertEqual(backup.plan_uploads(local, remote), [])
        local[0]["sha256"] = "new"
        self.assertEqual(backup.plan_uploads(local, remote), [local[0]])

    def test_restore_path_rejects_traversal_and_absolute_names(self):
        with tempfile.TemporaryDirectory() as temp:
            for name in ("../outside", "C:/outside", "/outside", "a/../../outside"):
                with self.assertRaises(ValueError):
                    backup.safe_restore_path(Path(temp), name)

    def test_restore_is_atomic_and_never_overwrites_without_flag(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); dest = root / "restore"; dest.mkdir()
            target = dest / "model.zip"; target.write_bytes(b"owner")
            with self.assertRaises(FileExistsError):
                backup.restore_bytes(dest, "model.zip", b"cloud")
            self.assertEqual(target.read_bytes(), b"owner")
            result = backup.restore_bytes(dest, "sub/model.zip", b"cloud")
            self.assertEqual(result.read_bytes(), b"cloud")
            self.assertFalse(list(dest.rglob("*.tmp")))

    def test_folder_id_parser_accepts_shared_drive_links_only(self):
        folder = "1-rY4kQdkQsfylSw9b_mfpKFiTWr_foe9"
        self.assertEqual(backup.folder_id(f"https://drive.google.com/drive/folders/{folder}?usp=sharing"), folder)
        with self.assertRaises(ValueError):
            backup.folder_id("https://example.com/drive/folders/abc")

    def test_restore_can_inspect_a_read_only_folder(self):
        client = object.__new__(backup.DriveClient)
        client.request = lambda *_: {"mimeType": "application/vnd.google-apps.folder", "name": "Shared", "capabilities": {"canAddChildren": False}}
        self.assertEqual(client.check_target("folder-id")["name"], "Shared")
        with self.assertRaisesRegex(RuntimeError, "No upload was attempted"):
            client.check_target("folder-id", require_write=True)

    def test_resumable_offset_handles_empty_range_and_rejects_invalid_range(self):
        self.assertEqual(backup.resumable_offset(None, 100), 0)
        self.assertEqual(backup.resumable_offset("bytes=0-49", 100), 50)
        for value in ("bytes=0--1", "bytes=0-100", "garbage"):
            with self.assertRaises(ValueError):
                backup.resumable_offset(value, 100)


if __name__ == "__main__":
    unittest.main()
