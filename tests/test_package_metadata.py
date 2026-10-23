"""Unity metadata sidecars must not become assets on subsequent builds."""
import importlib.util
from pathlib import Path


def test_metadata_generation_handles_existing_sidecars(tmp_path):
    script = Path(__file__).resolve().parents[1] / "build.py"
    spec = importlib.util.spec_from_file_location("unity_build", script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    asset = tmp_path / "package.json"
    asset.write_text("{}", encoding="utf-8")
    module.meta(asset, tmp_path)
    original = (tmp_path / "package.json.meta").read_bytes()
    # Reproduce a source tree already imported by Unity, then enumerate it again.
    for path in sorted(tmp_path.rglob("*")):
        module.meta(path, tmp_path)
    assert (tmp_path / "package.json.meta").read_bytes() == original
    assert not list(tmp_path.rglob("*.meta.meta"))


def test_builtin_module_versions_are_independent_of_plugin_version():
    import json
    manifest = json.loads((Path(__file__).resolve().parents[1] / "package/package.json").read_text())
    modules = {name: version for name, version in manifest["dependencies"].items()
               if name.startswith("com.unity.modules.")}
    assert modules
    assert set(modules.values()) == {"1.0.0"}
