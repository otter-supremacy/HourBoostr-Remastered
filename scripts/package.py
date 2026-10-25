"""Package only an explicit publish directory, never runtime account data."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import re
from pathlib import Path
import stat
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--input", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--runtime", choices=["win-x64", "linux-x64", "linux-arm64"], required=True)
parser.add_argument("--version", required=True)
args = parser.parse_args()
if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", args.version):
    raise SystemExit("Version must be a semantic version such as 1.0.0-preview.1")
source = args.input.resolve()
if not (source / "HourBoostr.Remastered.dll").is_file():
    raise SystemExit("Input must be a publish directory containing the remaster assembly")
for path in source.rglob("*"):
    if path.is_symlink():
        raise SystemExit("Publish directory contains a symbolic link")
    if path.name.startswith(("session-", "statistics-")) or path.suffix in (".secret", ".pfx", ".pem", ".key", ".log") or path.name in ("settings.json", "events.json", "desktop.json", "webview", "instance.lock"):
        raise SystemExit("Private runtime data must never be packaged")
manifest = {"product": "HourBoostr.Remastered", "version": args.version, "runtime": args.runtime, "builtUtc": datetime.now(timezone.utc).isoformat()}
(source / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
args.output.mkdir(parents=True, exist_ok=True)
archive = args.output / f"HourBoostr-Remastered-{args.version}-{args.runtime}.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as release:
    for path in sorted(source.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(source).as_posix()
        info = zipfile.ZipInfo(relative)
        info.create_system = 3
        mode = 0o755 if args.runtime.startswith("linux") and (path.name == "HourBoostr.Remastered" or path.suffix == ".sh") else 0o644
        info.external_attr = (stat.S_IFREG | mode) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        release.writestr(info, path.read_bytes())
with archive.open("rb") as stream:
    checksum = hashlib.file_digest(stream, "sha256").hexdigest()
archive.with_suffix(archive.suffix + ".sha256").write_text(f"{checksum}  {archive.name}\n", encoding="ascii")
print(archive)
print("SHA256 " + checksum)
