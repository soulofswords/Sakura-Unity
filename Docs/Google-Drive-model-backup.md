# Owner model-library backup

`Tools/backup_model_library.py` is an owner CLI, separate from public model downloads. Target: https://drive.google.com/drive/folders/1-rY4kQdkQsfylSw9b_mfpKFiTWr_foe9 . A shared URL does not grant write access. Use a Google Desktop OAuth client JSON stored outside the repository and sign in as an account with write permission on that folder.

```powershell
$env:SAKURA_DRIVE_OAUTH_CLIENT = 'C:\private\google-desktop-client.json'
python Tools/backup_model_library.py --login
python Tools/backup_model_library.py --source 'account-1=C:\library-1' --source 'account-2=C:\library-2' --dry-run
python Tools/backup_model_library.py --source 'account-1=C:\library-1' --source 'account-2=C:\library-2'
python Tools/backup_model_library.py --restore --restore-to C:\restore-test --dry-run
python Tools/backup_model_library.py --restore --restore-to C:\restore-test
```

Only point source folders at model-library data. Session/token/credential names, links and common cache directories are excluded. Upload identities use logical path, size and SHA-256. Drive upload completion verifies MD5 and size; restore verifies SHA-256 and size. Existing files are skipped on restore unless `--overwrite` is explicitly supplied. Restore never deletes local files.

This version requests full Drive OAuth scope because it uses a pre-existing shared target without Google Picker. Its token is outside the repository under the current user's AppData by default; this is not folder-limited authorization. Restrict local access to that file. Scoped Picker authorization and encrypted token storage remain release hardening work. Local manifest/planning/restore safety tests are available in Tools/test_backup_model_library.py.

## Live owner connection — 2026-11-14

The owner approved Google OAuth. Write permission was verified on the existing `Sakura AI assistant.` folder; its `pmx` and `live2d` folders were preserved. Credentials and the refresh token are stored outside the repositories in `%APPDATA%/Sakura/DriveBackup`, with filesystem access restricted to the current user and SYSTEM. User environment variables point the tool at that configuration. The Unity General settings panel discovers this configuration and passes the selected OAuth client to its child process.

A separate `Sakura backup verification` subfolder received the complete local Ringo model: **7 files, 9,401,038 bytes**. Restore to `C:/AI/Unity-Verification/drive-restore-check` matched every source SHA-256. The live run exposed and fixed hexadecimal-vs-Base64 MD5 comparison and Windows console encoding for Chinese filenames. Retrying skipped the already uploaded files. Evidence: `C:/AI/Unity-Verification/drive-backup-live-check.json`.

Live OAuth, small resumable uploads, incremental skipping and subset restore are verified. Full-library backup and interrupted large-file/network recovery remain unverified; the existing manually uploaded folders have not been duplicated by this check. Updated tooling and Unity configuration discovery still need to be included in the next packaged release.
