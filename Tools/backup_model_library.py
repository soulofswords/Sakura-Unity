#!/usr/bin/env python3
"""Incremental, verified backup of model-library folders to Google Drive.

OAuth client and token files stay outside the repository. The shared Drive target
must be writable by the signed-in account. Restore is additive and never removes
or overwrites local files unless --overwrite is explicitly supplied.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import json
import os
import re
import secrets
import shutil
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_FOLDER = "1-rY4kQdkQsfylSw9b_mfpKFiTWr_foe9"
API = "https://www.googleapis.com/drive/v3"
UPLOAD_API = "https://www.googleapis.com/upload/drive/v3"
TOKEN_URL = "https://oauth2.googleapis.com/token"
AUTH_URL = "https://accounts.google.com/o/oauth2/v2/auth"
SCOPE = "https://www.googleapis.com/auth/drive"
MAX_FILE_BYTES = 100 * 1024**3
CHUNK_BYTES = 8 * 1024 * 1024
EXCLUDED_PARTS = {".git", "node_modules", "__pycache__", ".cache", "logs"}
SECRET_PARTS = {".jio-owner-session.bin", ".owner-session.bin"}


def resumable_offset(range_header, total: int) -> int:
    """Return the next byte to send from Drive's 308 Range response."""
    if not isinstance(total, int) or total < 0:
        raise ValueError("Invalid resumable-upload size.")
    if not range_header:
        return 0
    match = re.fullmatch(r"bytes=0-(\d+)", str(range_header).strip())
    if not match:
        raise ValueError("Google returned an invalid resumable-upload Range header.")
    offset = int(match.group(1)) + 1
    if offset <= 0 or offset >= total:
        raise ValueError("Google returned an out-of-range resumable-upload offset.")
    return offset


def folder_id(value: str) -> str:
    value = (value or "").strip()
    match = re.fullmatch(r"[A-Za-z0-9_-]{10,200}", value)
    if match:
        return value
    parsed = urllib.parse.urlparse(value)
    if parsed.scheme != "https" or parsed.netloc.lower() not in {"drive.google.com", "www.drive.google.com"}:
        raise ValueError("Provide a Google Drive folder URL or folder ID.")
    parts = parsed.path.split("/")
    try:
        candidate = parts[parts.index("folders") + 1]
    except (ValueError, IndexError):
        raise ValueError("The URL does not contain a Drive folder ID.")
    if not re.fullmatch(r"[A-Za-z0-9_-]{10,200}", candidate):
        raise ValueError("The Drive folder ID is malformed.")
    return candidate


def safe_relative(value: str) -> str:
    value = (value or "").replace("\\", "/")
    parts = value.split("/")
    if not value or value.startswith("/") or ":" in value or any(p in {"", ".", ".."} or p.endswith((".", " ")) for p in parts):
        raise ValueError("Unsafe relative library path.")
    return value


def build_manifest(roots):
    def linked(path):
        return path.is_symlink() or getattr(path, "is_junction", lambda: False)()

    entries = []
    for label, root_value in roots:
        label = safe_relative(label)
        root = Path(root_value).expanduser().resolve(strict=True)
        if not root.is_dir():
            raise ValueError(f"Not a library folder: {root}")
        for parent, dirs, files in os.walk(root, followlinks=False):
            parent_path = Path(parent)
            dirs[:] = sorted(d for d in dirs if d.lower() not in EXCLUDED_PARTS and not linked(parent_path / d))
            for name in sorted(files):
                path = parent_path / name
                if linked(path) or name.lower() in SECRET_PARTS or any(word in name.lower() for word in ("owner-session", "credential", "token")):
                    continue
                stat = path.stat()
                if not path.is_file() or stat.st_size > MAX_FILE_BYTES:
                    continue
                relative = safe_relative(f"{label}/{path.relative_to(root).as_posix()}")
                digest = hashlib.sha256(); md5 = hashlib.md5()
                with path.open("rb") as stream:
                    for block in iter(lambda: stream.read(1024 * 1024), b""):
                        digest.update(block); md5.update(block)
                after = path.stat()
                if (after.st_size, after.st_mtime_ns) != (stat.st_size, stat.st_mtime_ns):
                    raise RuntimeError(f"Source changed while hashing; retry the backup: {relative}")
                entries.append({"path": relative, "size": stat.st_size, "mtime": stat.st_mtime, "sha256": digest.hexdigest(), "md5": base64.b64encode(md5.digest()).decode(), "local_path": str(path)})
    entries.sort(key=lambda item: item["path"].casefold())
    return entries


def plan_uploads(local, remote):
    pending = []
    for item in local:
        # Accept either a set of exact (path, digest, size) identities or the
        # Drive listing map returned by index_target().
        identity = (item["path"], item["sha256"], int(item["size"]))
        if isinstance(remote, set):
            present = identity in remote
        else:
            found = remote.get((item["path"], item["sha256"]))
            present = found is not None and int(found.get("size", -1)) == item["size"]
        if not present:
            pending.append(item)
    return pending


def safe_restore_path(root: Path, relative: str) -> Path:
    relative = safe_relative(relative)
    root = root.resolve()
    candidate = (root / Path(*relative.split("/"))).resolve()
    if candidate == root or not candidate.is_relative_to(root):
        raise ValueError("Restore path escapes the destination.")
    return candidate


def publish_restore(temporary, target, overwrite):
    if overwrite:
        os.replace(temporary, target)
    elif os.name == "nt":
        os.rename(temporary, target)
    else:
        os.link(temporary, target)


def restore_bytes(root: Path, relative: str, data: bytes, overwrite=False) -> Path:
    target = safe_restore_path(root, relative)
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists() and not overwrite:
        raise FileExistsError(str(target))
    fd, temp_name = tempfile.mkstemp(prefix=".sakura-restore-", dir=str(target.parent))
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        publish_restore(temp_name, target, overwrite)
        return target
    finally:
        if os.path.exists(temp_name): os.unlink(temp_name)


class DriveClient:
    def __init__(self, client_file: Path, token_file: Path):
        self.client_file = Path(client_file)
        self.token_file = Path(token_file)
        self.client = json.loads(self.client_file.read_text(encoding="utf-8"))
        self.config = self.client.get("installed") or self.client.get("web")
        if not self.config or not self.config.get("client_id"):
            raise ValueError("OAuth file must be a Google Desktop OAuth client JSON.")
        self.token = json.loads(self.token_file.read_text(encoding="utf-8")) if self.token_file.exists() else {}

    def login(self):
        state, verifier = secrets.token_urlsafe(24), secrets.token_urlsafe(48)
        challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
        result = {}

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(inner):
                query = urllib.parse.parse_qs(urllib.parse.urlparse(inner.path).query)
                if query.get("state", [""])[0] != state:
                    inner.send_error(400, "OAuth state mismatch"); result["error"] = "OAuth state mismatch"; return
                result["code"] = query.get("code", [""])[0]
                inner.send_response(200); inner.send_header("Content-Type", "text/plain; charset=utf-8"); inner.end_headers()
                inner.wfile.write(b"Sakura Drive authorization received. You can close this tab.")
            def log_message(inner, *_): pass

        server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        server.allow_reuse_address = False
        server.timeout = 180
        redirect = f"http://127.0.0.1:{server.server_port}/"
        params = {"client_id": self.config["client_id"], "redirect_uri": redirect, "response_type": "code", "scope": SCOPE,
                  "state": state, "code_challenge": challenge, "code_challenge_method": "S256", "access_type": "offline", "prompt": "consent"}
        url = AUTH_URL + "?" + urllib.parse.urlencode(params)
        print("Opening Google sign-in. Approve Sakura Drive backup access in the browser.")
        webbrowser.open(url)
        server.handle_request(); server.server_close()
        if not result.get("code"):
            raise RuntimeError(result.get("error", "Google sign-in timed out or was cancelled."))
        payload = {"client_id": self.config["client_id"], "code": result["code"], "code_verifier": verifier,
                   "grant_type": "authorization_code", "redirect_uri": redirect}
        if self.config.get("client_secret"):
            payload["client_secret"] = self.config["client_secret"]
        self.token = self._request(TOKEN_URL, data=urllib.parse.urlencode(payload).encode())
        self.token["expires_at"] = time.time() + int(self.token.get("expires_in", 3600))
        self._save_token()

    def _save_token(self):
        self.token_file.parent.mkdir(parents=True, exist_ok=True)
        temp = self.token_file.with_suffix(self.token_file.suffix + ".tmp")
        temp.write_text(json.dumps(self.token), encoding="utf-8")
        os.replace(temp, self.token_file)

    def _refresh(self):
        refresh_token = self.token.get("refresh_token")
        if not refresh_token:
            raise RuntimeError("Drive sign-in is missing. Run this tool with --login first.")
        payload = {"client_id": self.config["client_id"], "refresh_token": refresh_token, "grant_type": "refresh_token"}
        if self.config.get("client_secret"): payload["client_secret"] = self.config["client_secret"]
        updated = self._request(TOKEN_URL, data=urllib.parse.urlencode(payload).encode())
        updated["refresh_token"] = refresh_token
        updated["expires_at"] = time.time() + int(updated.get("expires_in", 3600))
        self.token = updated; self._save_token()

    @staticmethod
    def _request(url, data=None, headers=None, method=None):
        req = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
        try:
            with urllib.request.urlopen(req, timeout=90) as response:
                body = response.read()
                return json.loads(body) if body and response.headers.get("Content-Type", "").startswith("application/json") else body
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", "replace")[:2000]
            raise RuntimeError(f"Google Drive API returned HTTP {error.code}: {detail}") from error

    @staticmethod
    def _open(url, data=None, headers=None, method=None):
        return urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers or {}, method=method), timeout=180)

    def request(self, url, data=None, headers=None, method=None):
        headers = dict(headers or {})
        if not self.token.get("access_token") or self.token.get("expires_at", 0) < time.time() + 60:
            self._refresh()
        headers["Authorization"] = "Bearer " + self.token["access_token"]
        try:
            return self._request(url, data, headers, method)
        except RuntimeError as error:
            if "HTTP 401" not in str(error): raise
            self._refresh(); headers["Authorization"] = "Bearer " + self.token["access_token"]
            return self._request(url, data, headers, method)

    def open(self, url, data=None, headers=None, method=None):
        headers = dict(headers or {})
        if not self.token.get("access_token") or self.token.get("expires_at", 0) < time.time() + 60: self._refresh()
        headers["Authorization"] = "Bearer " + self.token["access_token"]
        try: return self._open(url, data, headers, method)
        except urllib.error.HTTPError as error:
            if error.code != 401: raise
            self._refresh(); headers["Authorization"] = "Bearer " + self.token["access_token"]
            return self._open(url, data, headers, method)

    def check_target(self, target, require_write=False):
        fields = urllib.parse.urlencode({"fields": "id,name,mimeType,capabilities(canAddChildren)", "supportsAllDrives": "true"})
        info = self.request(f"{API}/files/{urllib.parse.quote(target, safe='')}?{fields}")
        if info.get("mimeType") != "application/vnd.google-apps.folder":
            raise RuntimeError("The selected Google Drive target is not a folder.")
        if require_write and not (info.get("capabilities") or {}).get("canAddChildren"):
            raise RuntimeError(f"Google account cannot add files to Drive folder '{info.get('name', target)}'. No upload was attempted.")
        return info

    def list_target(self, target):
        fields = "nextPageToken,files(id,name,size,md5Checksum,appProperties,mimeType,modifiedTime)"
        q = urllib.parse.urlencode({"q": f"'{target}' in parents and trashed=false", "pageSize": 1000, "fields": fields, "supportsAllDrives": "true", "includeItemsFromAllDrives": "true"})
        items = []
        while True:
            page = self.request(f"{API}/files?{q}")
            items.extend(page.get("files", []))
            token = page.get("nextPageToken")
            if not token: break
            q = urllib.parse.urlencode({"q": f"'{target}' in parents and trashed=false", "pageSize": 1000, "fields": fields, "supportsAllDrives": "true", "includeItemsFromAllDrives": "true", "pageToken": token})
        return items

    def upload_resumable(self, target, entry):
        path = Path(entry["local_path"]); name = entry["path"].replace("/", " ⧸ ")
        app_properties = {"sakuraPath": entry["path"], "sakuraSha256": entry["sha256"]}
        if entry.get("manifest"): app_properties["sakuraManifest"] = "1"
        metadata = {"name": name, "parents": [target], "appProperties": app_properties}
        start_url = UPLOAD_API + "/files?uploadType=resumable&fields=id,name,size,md5Checksum,appProperties"
        response = self.open(start_url, json.dumps(metadata).encode(), {"Content-Type": "application/json; charset=UTF-8", "X-Upload-Content-Type": "application/octet-stream", "X-Upload-Content-Length": str(entry["size"])}, "POST")
        session_url = response.headers.get("Location"); response.close()
        parsed_session = urllib.parse.urlparse(session_url or "")
        if parsed_session.scheme != "https" or parsed_session.hostname not in {"www.googleapis.com", "content.googleapis.com"}:
            raise RuntimeError("Google returned an invalid resumable upload URL.")
        offset = 0
        with path.open("rb") as stream:
            while offset < entry["size"]:
                stream.seek(offset); chunk = stream.read(min(CHUNK_BYTES, entry["size"] - offset))
                headers = {"Content-Type": "application/octet-stream", "Content-Length": str(len(chunk)), "Content-Range": f"bytes {offset}-{offset+len(chunk)-1}/{entry['size']}"}
                for attempt in range(5):
                    try:
                        result = self.open(session_url, chunk, headers, "PUT")
                        metadata_result = json.loads(result.read()); result.close()
                        if int(metadata_result.get("size", -1)) != entry["size"] or metadata_result.get("md5Checksum") != entry["md5"]:
                            raise RuntimeError("Drive post-upload size or MD5 verification failed.")
                        return metadata_result
                    except urllib.error.HTTPError as error:
                        if error.code == 308:
                            received = error.headers.get("Range")
                            offset = resumable_offset(received, entry["size"])
                            break
                        if error.code in {408, 429} or error.code >= 500:
                            if attempt >= 4: raise RuntimeError(f"Resumable upload failed after retries (HTTP {error.code}).") from error
                            # Query session status before retrying a chunk: the server
                            # may have accepted it before the connection was lost.
                            try:
                                status = self.open(session_url, headers={"Content-Range": f"bytes */{entry['size']}"}, method="PUT")
                                meta = json.loads(status.read()); status.close()
                                if int(meta.get("size", -1)) == entry["size"] and meta.get("md5Checksum") == entry["md5"]:
                                    return meta
                            except urllib.error.HTTPError as probe:
                                if probe.code == 308:
                                    received = probe.headers.get("Range")
                                    offset = resumable_offset(received, entry["size"])
                                elif probe.code not in {404, 410}:
                                    raise
                            time.sleep(min(2 ** attempt, 16)); continue
                        raise RuntimeError(f"Resumable upload failed (HTTP {error.code}).") from error
                    except (OSError, TimeoutError):
                        if attempt >= 4: raise
                        # The server may have committed this chunk before the socket failed.
                        try:
                            status = self.open(session_url, headers={"Content-Range": f"bytes */{entry['size']}"}, method="PUT")
                            meta = json.loads(status.read()); status.close()
                            if int(meta.get("size", -1)) == entry["size"] and meta.get("md5Checksum") == entry["md5"]:
                                return meta
                        except urllib.error.HTTPError as probe:
                            if probe.code == 308:
                                offset = resumable_offset(probe.headers.get("Range"), entry["size"])
                            elif probe.code not in {404, 410}:
                                raise
                        time.sleep(min(2 ** attempt, 16))
        if entry["size"] == 0:
            result = self.request(session_url, b"", {"Content-Length": "0", "Content-Range": "bytes */0"}, "PUT")
            if int(result.get("size", 0)) != 0: raise RuntimeError("Drive post-upload size verification failed.")
            return result
        raise RuntimeError("Drive upload ended without a verified completion response.")


def plan_cli(args):
    roots = []
    for value in args.source:
        if "=" not in value: raise ValueError("Each --source must be LABEL=FOLDER.")
        label, path = value.split("=", 1); roots.append((label, path))
    if not roots: raise ValueError("Provide at least one --source LABEL=FOLDER.")
    entries = build_manifest(roots)
    return entries


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", action="append", default=[], help="Library root as label=folder; repeat for Jio account roots.")
    parser.add_argument("--folder", default=DEFAULT_FOLDER, help="Google Drive folder ID or shared-folder URL.")
    parser.add_argument("--oauth-client", default=os.environ.get("SAKURA_DRIVE_OAUTH_CLIENT"), help="Google Desktop OAuth client JSON path (or env SAKURA_DRIVE_OAUTH_CLIENT).")
    parser.add_argument("--token-file", default=os.environ.get("SAKURA_DRIVE_TOKEN_FILE", str(Path(os.environ.get("APPDATA", Path.home())) / "Sakura" / "drive-backup-token.json")))
    parser.add_argument("--login", action="store_true", help="Sign in interactively and save refresh token outside the repo.")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--restore", action="store_true")
    parser.add_argument("--restore-to", type=Path)
    parser.add_argument("--overwrite", action="store_true", help="Explicitly allow restoring over local files.")
    args = parser.parse_args(argv)
    try:
        target = folder_id(args.folder)
        client_path = Path(args.oauth_client).expanduser() if args.oauth_client else None
        if args.login:
            if not client_path or not client_path.is_file():
                raise RuntimeError("Set SAKURA_DRIVE_OAUTH_CLIENT to a Google Desktop OAuth client JSON file. Credentials remain outside the repository.")
            client = DriveClient(client_path, Path(args.token_file).expanduser())
            client.login(); print("Google Drive sign-in saved outside the repository.")
            return 0
        entries = plan_cli(args) if not args.restore else None
        if args.dry_run and not args.restore:
            print(json.dumps({"files": len(entries), "bytes": sum(x["size"] for x in entries), "folder_id": target, "dry_run": True}, indent=2))
            return 0
        if not client_path or not client_path.is_file():
            raise RuntimeError("Google Drive write access is not connected. Set SAKURA_DRIVE_OAUTH_CLIENT to a Google Desktop OAuth client JSON file, then run --login. Credentials remain outside the repository.")
        client = DriveClient(client_path, Path(args.token_file).expanduser())
        if args.restore:
            if not args.restore_to: raise ValueError("--restore requires --restore-to.")
            client.check_target(target)
            files = client.list_target(target)
            manifests = sorted((f for f in files if (f.get("appProperties") or {}).get("sakuraManifest") == "1"), key=lambda f: f.get("modifiedTime", ""), reverse=True)
            if not manifests: raise RuntimeError("No Sakura backup manifest is present in the target folder.")
            with client.open(f"{API}/files/{manifests[0]['id']}?alt=media") as response:
                manifest = json.loads(response.read())
            if args.dry_run:
                available = {( (f.get("appProperties") or {}).get("sakuraPath"),
                               (f.get("appProperties") or {}).get("sakuraSha256")) for f in files}
                planned = [item for item in manifest.get("files", [])
                           if (item.get("path"), item.get("sha256")) in available
                           and (args.overwrite or not safe_restore_path(args.restore_to, item["path"]).exists())]
                print(json.dumps({"restore_files": len(planned), "restore_bytes": sum(int(x["size"]) for x in planned),
                                  "destination": str(args.restore_to), "dry_run": True,
                                  "local_files_deleted": 0, "folder_id": target}, indent=2)); return 0
            restored = skipped = 0
            for item in manifest["files"]:
                match = next((f for f in files if (f.get("appProperties") or {}).get("sakuraPath") == item["path"] and (f.get("appProperties") or {}).get("sakuraSha256") == item["sha256"]), None)
                if not match: continue
                dest = safe_restore_path(args.restore_to, item["path"])
                if dest.exists() and not args.overwrite: skipped += 1; continue
                target_path = safe_restore_path(args.restore_to, item["path"]); target_path.parent.mkdir(parents=True, exist_ok=True)
                if target_path.exists() and not args.overwrite: skipped += 1; continue
                fd, tmp_name = tempfile.mkstemp(prefix=".sakura-restore-", dir=str(target_path.parent)); digest = hashlib.sha256(); size = 0
                try:
                    with os.fdopen(fd, "wb") as out, client.open(f"{API}/files/{match['id']}?alt=media") as response:
                        while True:
                            block = response.read(1024 * 1024)
                            if not block: break
                            out.write(block); digest.update(block); size += len(block)
                        out.flush(); os.fsync(out.fileno())
                    if digest.hexdigest() != item["sha256"] or size != int(item["size"]): raise RuntimeError(f"Checksum/size mismatch while restoring {item['path']}.")
                    try:
                        publish_restore(tmp_name, target_path, args.overwrite)
                    except FileExistsError:
                        if args.overwrite:
                            raise
                        skipped += 1
                        continue
                    restored += 1
                finally:
                    if os.path.exists(tmp_name): os.unlink(tmp_name)
            print(f"Restore complete: {restored} restored, {skipped} already existed; no local files were deleted.")
            return 0
        target_info = client.check_target(target, require_write=not args.dry_run)
        remote = {}
        for file in client.list_target(target):
            props = file.get("appProperties") or {}
            if props.get("sakuraPath") and props.get("sakuraSha256"):
                remote[(props["sakuraPath"], props["sakuraSha256"])] = file
        pending = plan_uploads(entries, remote)
        print(f"Drive backup: {len(pending)} new/changed of {len(entries)} files ({sum(x['size'] for x in pending):,} bytes).")
        if args.dry_run:
            print(json.dumps({"dry_run": True, "folder_id": target, "folder": target_info.get("name", target),
                              "pending_files": len(pending), "pending_bytes": sum(x["size"] for x in pending),
                              "files": [x["path"] for x in pending]}, indent=2))
            return 0
        for index, entry in enumerate(pending, 1):
            print(f"[{index}/{len(pending)}] {entry['path']}")
            client.upload_resumable(target, entry)
        manifest_fd, manifest_name = tempfile.mkstemp(prefix="sakura-drive-manifest-", suffix=".json")
        os.close(manifest_fd)
        manifest_path = Path(manifest_name)
        try:
            safe_entries = [{k: v for k, v in item.items() if k != "local_path"} for item in entries]
            manifest_path.write_text(json.dumps({"schema": 1, "created_at": datetime.now(timezone.utc).isoformat(), "files": safe_entries}, indent=2), encoding="utf-8")
            manifest_entry = {"path": "sakura-library-manifest.json", "size": manifest_path.stat().st_size,
                "md5": base64.b64encode(hashlib.md5(manifest_path.read_bytes()).digest()).decode(),
                "sha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest(), "local_path": str(manifest_path), "manifest": True}
            manifest_entry["name"] = "sakura-library-manifest.json"
            result = client.upload_resumable(target, manifest_entry)
            print(f"Backup verified: {len(pending)} uploaded; {len(entries)-len(pending)} already present; manifest file {result.get('id')} in {target_info.get('name', target)}.")
        finally:
            manifest_path.unlink(missing_ok=True)
        return 0
    except (ValueError, OSError, RuntimeError, json.JSONDecodeError) as error:
        print(f"backup_model_library: {error}", file=sys.stderr); return 2


if __name__ == "__main__":
    raise SystemExit(main())
