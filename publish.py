"""Prepare a binary UPM release; --push atomically publishes its commit and tag.

The destination repository must already exist. No source or Unity-owned DLLs
are uploaded. Run build.py after committing the source changes first.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from build import ROOT, meta, source_info


def git(directory: Path, *args: str) -> str:
    return subprocess.check_output(["git", "-C", str(directory), *args], text=True).strip()


def verify_package(package: Path, report: dict) -> str:
    manifest = json.loads((package / "package.json").read_text(encoding="utf-8"))
    if manifest != report["package"] or manifest["name"] != "com.rotatrix.unity":
        raise ValueError("Package manifest differs from the build report")
    version = manifest["version"]
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("Invalid release version")
    files = {}
    for path in package.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Package contains a symlink: {path}")
        if path.is_file():
            if path.suffix.lower() not in {".dll", ".json", ".meta", ".md", ".txt"}:
                raise ValueError(f"Unexpected binary-package file: {path}")
            if path.name.startswith("Unity") and path.suffix.lower() == ".dll":
                raise ValueError(f"Unity-owned assembly in package: {path}")
            files[path.relative_to(package).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    if files != report["files"]:
        raise ValueError("Package content changed after build; rebuild before publishing")
    if "Editor/Rotatrix.Unity.Editor.dll" not in files:
        raise ValueError("Missing editor assembly")
    return "v" + version


def prepare(package: Path, report: dict, remote: str, output: Path) -> tuple[Path, str]:
    tag = verify_package(package, report)
    output.mkdir(parents=True, exist_ok=True)
    checkout = Path(tempfile.mkdtemp(prefix=tag + "-", dir=output))
    git(checkout, "init", "--initial-branch=upm")
    git(checkout, "config", "core.autocrlf", "false")
    git(checkout, "remote", "add", "origin", remote)
    if git(checkout, "ls-remote", "--tags", "origin", "refs/tags/" + tag):
        raise ValueError(f"{tag} already exists; bump package/package.json and rebuild. Tags are immutable.")
    if git(checkout, "ls-remote", "--heads", "origin", "refs/heads/upm"):
        git(checkout, "fetch", "--depth=1", "origin", "upm")
        git(checkout, "checkout", "-B", "upm", "FETCH_HEAD")
        # Only the freshly created publication checkout is modified.
        git(checkout, "rm", "-r", "--ignore-unmatch", ".")
    shutil.copytree(package, checkout, dirs_exist_ok=True)
    # Provenance has no machine paths or source contents.
    provenance = {key: report[key] for key in ("source", "openaxis", "build_platform", "target", "files")}
    (checkout / "BUILD-INFO.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    meta(checkout / "BUILD-INFO.json", checkout)
    git(checkout, "add", "--all")
    git(checkout, "commit", "-m", f"Release Unity editor plugin {tag}")
    git(checkout, "tag", "-a", tag, "-m", f"Unity editor plugin {tag}; source {report['source']['revision']}")
    return checkout, tag


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="rotatrix/rotatrix-unity-editor", help="Existing GitHub owner/repository")
    parser.add_argument("--push", action="store_true", help="Push upm and the immutable version tag atomically")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo):
        parser.error("--repo must be a GitHub owner/repository")
    report = json.loads((ROOT / "artifacts/build-info.json").read_text(encoding="utf-8"))
    for key, directory in (("source", ROOT),):
        current = source_info(directory)
        if current["dirty"] or report[key]["dirty"] or current != report[key]:
            raise ValueError(f"{key} must be clean and match the built revision. Commit changes, then rebuild.")
    remote = f"https://github.com/{args.repo}.git"
    checkout, tag = prepare(ROOT / "artifacts/com.rotatrix.unity", report, remote, ROOT / "artifacts/publish")
    print(f"Prepared release: {checkout}\nTag: {tag}")
    if args.push:
        git(checkout, "push", "--atomic", "origin", "HEAD:refs/heads/upm", f"refs/tags/{tag}")
        print(f"Published. Unity Package Manager Git URL: {remote}#{tag}")
    else:
        print("Nothing pushed. Review the prepared checkout, then rerun with --push to publish.")


if __name__ == "__main__":
    main()
