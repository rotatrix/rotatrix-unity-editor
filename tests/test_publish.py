"""Exercise publication against a local bare remote; no GitHub access required."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

import pytest


@pytest.fixture
def publisher(monkeypatch):
    directory = Path(__file__).resolve().parents[1]
    monkeypatch.syspath_prepend(str(directory))
    spec = importlib.util.spec_from_file_location("unity_publish", directory / "publish.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for role in ("AUTHOR", "COMMITTER"):
        monkeypatch.setenv(f"GIT_{role}_NAME", "Unity publication test")
        monkeypatch.setenv(f"GIT_{role}_EMAIL", "test@example.invalid")
    return module


def package_report(package, version="0.1.0"):
    (package / "Editor").mkdir(parents=True, exist_ok=True)
    (package / "Editor/Rotatrix.Unity.Editor.dll").write_bytes(b"test assembly fixture")
    manifest = {"name": "com.rotatrix.unity", "version": version}
    (package / "package.json").write_text(json.dumps(manifest))
    return {"package": manifest,
            "files": {p.relative_to(package).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                      for p in package.rglob("*") if p.is_file()},
            "source": {"revision": "a" * 40, "dirty": False},
            "openaxis": {"revision": "b" * 40, "dirty": False},
            "build_platform": "test", "target": "netstandard2.1/AnyCPU"}


def test_publish_and_immutable_tags(publisher, tmp_path):
    remote = tmp_path / "remote.git"
    subprocess.run(["git", "init", "--bare", str(remote)], check=True)
    source = tmp_path / "source"
    source.mkdir()
    publisher.git(source, "init", "--initial-branch=main")
    (source / "source.cs").write_text("// contributor source")
    publisher.git(source, "add", ".")
    publisher.git(source, "commit", "-m", "Source")
    publisher.git(source, "push", str(remote), "main")
    source_head = publisher.git(remote, "rev-parse", "main")
    package = tmp_path / "package"
    report = package_report(package)
    (package / "Editor/old.dll").write_bytes(b"old dependency")
    report = package_report(package)
    checkout, tag = publisher.prepare(package, report, str(remote), tmp_path / "prepared")
    assert not publisher.git(checkout, "ls-remote", "--heads", "origin", "refs/heads/upm")  # preparation never pushes
    publisher.git(checkout, "push", "--atomic", "origin", "HEAD:refs/heads/upm", "refs/tags/" + tag)
    assert publisher.git(remote, "rev-parse", "main") == source_head
    provenance_meta = publisher.git(remote, "show", tag + ":BUILD-INFO.json.meta")
    assert "guid: " in provenance_meta
    assert "DefaultImporter:" in provenance_meta
    original = publisher.git(checkout, "rev-parse", tag)
    with pytest.raises(ValueError, match="already exists"):
        publisher.prepare(package, report, str(remote), tmp_path / "prepared")
    (package / "Editor/old.dll").unlink()
    report = package_report(package, "0.2.0")
    checkout, tag = publisher.prepare(package, report, str(remote), tmp_path / "prepared")
    assert not (checkout / "Editor/old.dll").exists()
    assert (checkout / "BUILD-INFO.json").is_file()
    publisher.git(checkout, "push", "--atomic", "origin", "HEAD:refs/heads/upm", "refs/tags/" + tag)
    assert publisher.git(remote, "show", tag + ":BUILD-INFO.json.meta") == provenance_meta
    assert publisher.git(remote, "show", "main:source.cs") == "// contributor source"
    assert publisher.git(remote, "rev-parse", "main") == source_head
    assert publisher.git(remote, "rev-parse", "v0.1.0") == original
    assert publisher.git(remote, "show", "v0.1.0:Editor/old.dll") == "old dependency"


def test_reject_changed_or_source_package(publisher, tmp_path):
    report = package_report(tmp_path)
    (tmp_path / "Editor/Rotatrix.Unity.Editor.dll").write_bytes(b"tampered")
    with pytest.raises(ValueError, match="content changed"):
        publisher.verify_package(tmp_path, report)
    (tmp_path / "Editor/secret.cs").write_text("source must not ship")
    report = package_report(tmp_path)
    with pytest.raises(ValueError, match="Unexpected"):
        publisher.verify_package(tmp_path, report)
