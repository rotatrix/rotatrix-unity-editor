"""Test the binary UPM package native APIs in an isolated Unity project.

The old OpenAxis 1.0 protocol fixture is disabled pending a design for testing
without a licensed device. This runner opens no fake server. Production
verification is unchanged.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
from pathlib import Path
import shutil
import subprocess

TESTS = Path(__file__).resolve().parent
ROOT = TESTS.parent


async def run(editor: Path):
    package = ROOT / "artifacts/com.rotatrix.unity"
    if not (package / "Editor/Rotatrix.Unity.Editor.dll").is_file():
        raise RuntimeError("Run build.py first")
    project = ROOT / "TestProject"
    (project / "Assets/Editor").mkdir(parents=True, exist_ok=True)
    (project / "Packages").mkdir(exist_ok=True)
    (project / "ProjectSettings").mkdir(exist_ok=True)
    version = next((part for part in editor.parts if re.fullmatch(r"6000\.\d+\.\d+[abfp]\d+", part)), None)
    if version is None:
        raise ValueError("Use an executable inside a versioned Unity Hub installation")
    (project / "ProjectSettings/ProjectVersion.txt").write_text(f"m_EditorVersion: {version}\n", encoding="utf-8")
    # A versioned project avoids the first-run migration that injects Unity's
    # entire default package set, most of which is irrelevant to this fixture.
    shutil.copytree(TESTS / "Editor", project / "Assets/Editor", dirs_exist_ok=True)
    (project / "Packages/manifest.json").write_text(json.dumps({"dependencies": {
        "com.rotatrix.unity": "file:" + package.as_posix(),
        "com.unity.modules.imgui": "1.0.0",
        "com.unity.modules.physics": "1.0.0",
        "com.unity.modules.terrain": "1.0.0",
        "com.unity.modules.terrainphysics": "1.0.0"}}), encoding="utf-8")
    result = ROOT / "artifacts/editor-test-result.txt"
    result.unlink(missing_ok=True)
    log = ROOT / "artifacts/editor-test.log"
    env = dict(os.environ, ROTATRIX_UNITY_TEST_RESULT=str(result), ROTATRIX_UNITY_NATIVE_ONLY="1")
    env.pop("ROTATRIX_UNITY_TEST_URL", None)
    print("Protocol fixture disabled; running native package/editor checks only.", flush=True)
    process = subprocess.Popen([str(editor.resolve()), "-batchmode", "-nographics",
        "-projectPath", str(project), "-executeMethod", "Rotatrix.Unity.Tests.SmokeTests.Run",
        "-logFile", str(log)], env=env,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    try:
        for _ in range(240):
            if process.poll() is not None:
                break
            await asyncio.sleep(1)
        else:
            raise TimeoutError(f"Unity did not exit; inspect {log}")
        if process.returncode != 0 or not result.exists():
            raise RuntimeError(f"Unity tests failed; inspect {log}\n" +
                               (result.read_text() if result.exists() else "No result file"))
        print(result.read_text())
    finally:
        if process.poll() is None:
            process.terminate()
            await asyncio.to_thread(process.wait, 10)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path, help="Unity executable")
    parser.add_argument("--native-only", action="store_true",
                        help="Compatibility flag; native-only is now the default and sole supported mode")
    args = parser.parse_args()
    asyncio.run(run(args.unity))
