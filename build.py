"""Build a local, compiled UPM folder and tarball. Never installs or publishes."""
from __future__ import annotations

import argparse
import sys
import hashlib
import json
import os
import platform
import re
from pathlib import Path
import shutil
import subprocess
import tarfile
import uuid

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "src/Rotatrix.Unity.Editor"


def managed_directory(editor: Path) -> Path:
    """Accept a Hub version directory, Unity.app, executable, or Managed folder."""
    editor = editor.resolve()
    if editor.is_file():
        editor = editor.parent
    for candidate in (editor, editor / "Data/Managed", editor / "Editor/Data/Managed",
                      editor / "Contents/Managed", editor / "../Managed",
                      editor / "Unity.app/Contents/Managed"):
        if (candidate / "UnityEditor.dll").is_file():
            return candidate.resolve()
    raise ValueError(f"Cannot find Unity managed assemblies under {editor}")


def meta(path: Path, package: Path) -> None:
    # Unity may add metadata to the source package after a local UPM import.
    # Metadata is not itself an asset and must never receive another sidecar.
    if path.suffix.lower() == ".meta":
        return
    identity = path.relative_to(package).as_posix()
    guid = uuid.uuid5(uuid.NAMESPACE_URL, "com.rotatrix.unity/" + identity).hex
    header = f"fileFormatVersion: 2\nguid: {guid}\n"
    if path.is_dir():
        body = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
    elif path.suffix == ".dll":
        body = """PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
        OS: AnyOS
  userData:
  assetBundleName:
  assetBundleVariant:
"""
    else:
        body = "DefaultImporter:\n  externalObjects: {}\n"
    path.with_name(path.name + ".meta").write_text(header + body, encoding="utf-8")


def build(editor: Path) -> Path:
    managed = managed_directory(editor)
    manifest = json.loads((ROOT / "package/package.json").read_text(encoding="utf-8"))
    version = manifest["version"]
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("package.json version must be a release or prerelease SemVer")
    subprocess.run(["dotnet", "build", str(PROJECT / "Rotatrix.Unity.Editor.csproj"),
                    "-c", "Release", f"-p:UnityManagedPath={managed}", f"-p:UnityPluginVersion={version}", "--nologo"], check=True)
    output = PROJECT / "bin/Release/netstandard2.1"
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    package = artifacts / "com.rotatrix.unity"
    # Only replace our fixed generated directory; never delete a user-supplied path.
    if package.exists():
        if package.is_symlink() or package.resolve().parent != artifacts.resolve():
            raise ValueError("Refusing to replace redirected package output")
        shutil.rmtree(package)
    # The binary package owns deterministic GUIDs; don't copy locally generated
    # source metadata (including any stale nested .meta sidecars).
    shutil.copytree(ROOT / "package", package, ignore=shutil.ignore_patterns("*.meta"))
    shutil.copy2(ROOT / "LICENSE.md", package / "LICENSE.md")
    shutil.copy2(ROOT / "LEGAL.md", package / "LEGAL.md")
    plugins = package / "Editor"
    plugins.mkdir()
    # netstandard2.1 already supplies these APIs. Recent Unity versions also
    # provide BCL extension assemblies; shipping duplicates breaks import.
    provided = {"System.Memory.dll", "System.Buffers.dll", "System.Numerics.Vectors.dll",
                "System.Threading.Tasks.Extensions.dll", "Microsoft.Bcl.AsyncInterfaces.dll"}
    bcl = managed.parent / "BCLExtensions/TargetingPacks/netstandard2.1/ref"
    provided.update(p.name for p in bcl.glob("*.dll"))
    for dll in output.glob("*.dll"):
        if dll.name not in provided:
            if dll.name.startswith("Unity"):
                raise ValueError(f"Unity-owned assembly unexpectedly in build output: {dll}")
            shutil.copy2(dll, plugins / dll.name)
    notices = package / "Third Party Notices.md"
    notices.write_text(third_party_notices({p.name for p in plugins.glob('*.dll')}), encoding="utf-8")
    for path in sorted(package.rglob("*")):
        meta(path, package)
    manifest = json.loads((package / "package.json").read_text(encoding="utf-8"))
    archive = artifacts / f"{manifest['name']}-{manifest['version']}.tgz"
    with tarfile.open(archive, "w:gz") as tar:
        tar.add(package, arcname="package")
    report = {
        "unity_managed_path": str(managed),
        "package": manifest,
        "assemblies": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in plugins.glob("*.dll")},
        "files": {p.relative_to(package).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                  for p in sorted(package.rglob("*")) if p.is_file()},
        "build_platform": platform.system(),
        "target": "netstandard2.1/AnyCPU; Unity Editor Windows, macOS, Linux",
        "source": source_info(ROOT),
        "openaxis": next({"package": name.split("/")[0], "version": name.split("/")[1]}
                         for name in json.loads((PROJECT / "obj/project.assets.json").read_text())["libraries"]
                         if name.startswith("OpenAxis/")),
    }
    (artifacts / "build-info.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"\nLocal UPM package: {package / 'package.json'}\nTarball: {archive}")
    return package


def source_info(directory: Path) -> dict:
    def git(*args):
        return subprocess.check_output(["git", "-C", str(directory), *args], text=True).strip()
    return {"revision": git("rev-parse", "HEAD"),
            "dirty": bool(git("status", "--porcelain", "--ignore-submodules=none"))}


def third_party_notices(shipped: set[str]) -> str:
    assets = json.loads((PROJECT / "obj/project.assets.json").read_text(encoding="utf-8"))
    sections = ["# Third-party notices\n"]
    for name, library in assets["libraries"].items():
        if library["type"] != "package":
            continue
        if not any(Path(p).name in shipped for p in library.get("files", [])):
            continue
        package_name = name.split("/")[0]
        bundled = ROOT / "licenses" / ("MessagePack.txt" if package_name.startswith("MessagePack") else package_name + ".txt")
        if bundled.is_file():
            sections.append(f"## {name}\n\n" + bundled.read_text(encoding="utf-8"))
            continue
        for cache in assets["packageFolders"]:
            folder = Path(cache) / library["path"]
            if not folder.is_dir():
                continue
            licenses = [p for p in folder.rglob("*") if p.is_file()
                        and (p.name.lower().startswith("license") or p.name.lower() == "copyright.txt")]
            text = "\n\n".join(p.read_text(encoding="utf-8-sig") for p in licenses)
            if not text:
                raise ValueError(f"Add the upstream license for {name} under licenses/")
            sections.append(f"## {name}\n\n{text}")
            break
        else:
            raise ValueError(f"Missing package metadata for {name}")
    sections.append("## Apache License 2.0 (MessagePack third-party code)\n\n" +
                    (ROOT / "licenses/Apache-2.0.txt").read_text(encoding="utf-8"))
    return "\n\n".join(sections) + "\n"


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == 'test':
        subprocess.run(["uv", "run", "--project", str(ROOT), "--locked", "--python", "3.13", "--group", "test",
                        "python", "-m", "pytest", "-c", str(ROOT / "pytest.ini"),
                        str(ROOT / "tests"), "-q"], check=True)
        subprocess.run(["dotnet", "run", "--project", str(ROOT / "tests/Rotatrix.Unity.Tests.csproj"),
                        "-c", "Release"], check=True)
        raise SystemExit(0)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", type=Path, default=os.environ.get("UNITY_EDITOR"),
                        help="Unity 6.6 installation/executable (or set UNITY_EDITOR)")
    args = parser.parse_args()
    if not args.unity:
        parser.error("Provide --unity or set UNITY_EDITOR")
    build(args.unity)
