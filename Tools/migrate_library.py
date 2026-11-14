"""Copy a previous Sakura's user data without overwriting either installation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

USER_DATA = ("models", "voices", "desktop-profile", "llm-models", "settings.json")
# Browser cookies, sessions and caches are not user documents. They stay behind
# unless the owner explicitly opts in to carrying the signed-in browser profile.
BROWSER_DATA = {
    "Cookies", "Cookies-journal", "EBWebView", "DawnCache", "GPUCache", "Cache",
    "Code Cache", "Service Worker", "Local Storage", "Session Storage",
    "blob_storage", "Network", "Favicons", "History", "History-journal",
    "Login Data", "Login Data For Account", "Web Data", "Web Data-journal",
    "Preferences", "Local State", "TransportSecurity",
}


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def migrate(source, destination, dry_run=False, include_browser=False):
    source = Path(source).resolve(strict=True)
    destination = Path(destination).resolve(strict=True)
    if source == destination or source.is_relative_to(destination) or destination.is_relative_to(source):
        raise ValueError("Choose separate previous and new installation folders.")
    if not (source / "models").is_dir() or not (destination / "app.py").is_file():
        raise ValueError("Choose an existing Sakura library and a valid new Sakura installation.")
    plan = []
    # Validate the whole plan before writing any user data.
    for name in USER_DATA:
        base = source / name
        candidates = [base] if base.is_file() else base.rglob("*") if base.exists() else []
        for path in candidates:
            if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
                raise ValueError("Migration cannot follow links. Copy linked assets separately.")
            if not path.is_file():
                continue
            relative = path.relative_to(source)
            if not include_browser and any(part in BROWSER_DATA for part in relative.parts):
                continue
            if not path.resolve().is_relative_to(source):
                raise ValueError("Source file escapes the selected installation.")
            target = destination / relative
            if not target.resolve().is_relative_to(destination):
                raise ValueError("Destination file escapes the selected installation.")
            fingerprint = digest(path)
            if target.exists():
                if not target.is_file() or digest(target) != fingerprint:
                    raise FileExistsError(f"Existing data differs: {relative}. Migration stopped; neither copy was overwritten.")
                continue
            plan.append((path, target, fingerprint))
    copied = 0
    for path, target, fingerprint in plan:
        if dry_run:
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix=".sakura-migrate-", dir=target.parent)
        try:
            with os.fdopen(fd, "wb") as out, path.open("rb") as incoming:
                for block in iter(lambda: incoming.read(1024 * 1024), b""):
                    out.write(block)
                out.flush(); os.fsync(out.fileno())
            if digest(Path(temporary)) != fingerprint:
                raise RuntimeError(f"Source changed or checksum failed: {path.name}")
            # Windows rename is atomic and refuses an existing destination.
            if os.name == "nt":
                os.rename(temporary, target)
            else:
                os.link(temporary, target)
            copied += 1
        finally:
            Path(temporary).unlink(missing_ok=True)
    return {"planned": len(plan), "copied": copied, "dry_run": dry_run,
            "browser_profile_copied": bool(include_browser),
            "source_preserved": True, "existing_data_overwritten": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--destination", required=True)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--include-browser-profile", action="store_true", help="Also copy browser cookies and signed-in sessions.")
    args = parser.parse_args()
    try:
        print(json.dumps(migrate(args.source, args.destination, args.dry_run, args.include_browser_profile)))
    except (OSError, ValueError, RuntimeError) as error:
        parser.exit(2, str(error) + "\n")
