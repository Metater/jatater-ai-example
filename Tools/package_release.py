"""Package the SDK and optional UPM sources with their stable Unity GUIDs."""
import gzip
import io
from pathlib import Path
import re
import tarfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
SDK = ROOT / "Assets/JataterAI"
VERSION = "0.1.0"


def add(archive, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    archive.addfile(info, io.BytesIO(data))


def gzip_tar(writer):
    output = io.BytesIO()
    with gzip.GzipFile(fileobj=output, mode="wb", mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode="w") as archive:
            writer(archive)
    return output.getvalue()


def module(name):
    root = ROOT / "Integrations" / name

    def write(archive):
        for path in sorted(root.rglob("*")):
            if path.is_file():
                add(archive, "package/" + path.relative_to(root).as_posix(), path.read_bytes())
    return gzip_tar(write)


def asset(archive, path, meta, content):
    guid = re.search(rb"^guid: ([0-9a-f]{32})", meta, re.MULTILINE)
    if not guid:
        raise ValueError(f"Asset metadata missing a GUID: {path}")
    prefix = guid.group(1).decode() + "/"
    add(archive, prefix + "pathname", path.encode())
    add(archive, prefix + "asset.meta", meta)
    if content is not None:
        add(archive, prefix + "asset", content)


def write(archive):
    for path in [SDK, *sorted(SDK.rglob("*"))]:
        if path.suffix == ".meta" or path.name == "JataterSampleConnection.asset":
            continue
        meta = Path(str(path) + ".meta").read_bytes()
        asset(archive, path.relative_to(ROOT).as_posix(), meta, path.read_bytes() if path.is_file() else None)
    for name in ("Pocket", "Steam", "Whisper"):
        path = f"Assets/JataterAI/Editor/{name}.tgz.bytes"
        guid = uuid.uuid5(uuid.NAMESPACE_URL, "https://jatater.ai/unity/" + path).hex
        meta = f"fileFormatVersion: 2\nguid: {guid}\nTextScriptImporter:\n  externalObjects: {{}}\n".encode()
        asset(archive, path, meta, module(name))


if __name__ == "__main__":
    target = ROOT / f"Builds/JataterAI-{VERSION}.unitypackage"
    target.parent.mkdir(exist_ok=True)
    target.write_bytes(gzip_tar(write))
    print(f"{target} ({target.stat().st_size:,} bytes)")
