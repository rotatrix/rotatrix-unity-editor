# Developing Rotatrix for Unity Editor

The repository contains source and build tooling. It is not itself a UPM package.

- `src/Rotatrix.Unity.Editor/`: editor adapter source and C# project.
- `tests/`: standalone and native Unity checks.
- `package/`: UPM manifest and user documentation used as build inputs.
- `artifacts/com.rotatrix.unity/`: generated installable UPM package.
- `artifacts/*.tgz`: generated release tarballs.

## Build and test

Requires Python 3.13, uv, .NET SDK 9.0.314, Git, and Unity 6000.6. The root
`mise.toml` pins uv and .NET; use `mise install` and `mise exec --` if you use mise.
Python environments are managed by uv. No Rotatrix checkout or submodule is needed.
Run these commands from the repository root:

```sh
uv run --no-project python build.py --unity "/path/to/Unity/6000.6.1f1"
uv run --no-project python build.py test
uv run --no-project python tests/run_editor.py --unity "/path/to/Unity/6000.6.1f1/Editor/Unity.exe"
```

Use your native editor path: a Hub installation on Windows/Linux or Unity.app on
macOS for the build; the executable itself for native tests. `UNITY_EDITOR` can
replace the build's `--unity` argument. PowerShell also accepts these commands.

OpenAxis is restored from NuGet at exactly `1.0.0-rc.1`. MessagePack is pinned to
`2.5.303` to retain the advisory fix. Committed NuGet and uv locks keep dependency
resolution fixed. When deliberately updating a NuGet dependency, run
`dotnet restore <project.csproj> -p:RestoreLockedMode=false` for the plugin and
standalone test projects, review both locks, then rebuild and test.

Install the generated tarball through Unity Package Manager, or select
`artifacts/com.rotatrix.unity/package.json` using Install package from
disk. The build ships editor-only DLLs; it excludes Unity assemblies and plugin
source. Do not install the `package/` source template.

The native runner creates its own TestProject and requires an activated Unity
license. It does not test authenticated device input. Run physical-device checks
separately. Run platform-specific tests on their actual operating systems.

## Releases

Development happens on `main`. `publish.py` prepares compiled package commits on
`upm` and immutable `v<version>` tags, leaving `main` unchanged. Existing tags,
including `v1.0.0-beta.1`, must not be replaced. Use a new version for the next release.

1. Set the next version in `package/package.json`, update installation examples,
   and commit source changes on main.
2. Build and test the generated package from that clean revision. Push the source
   commit so release provenance points to publicly available source.
3. Prepare the package and review the printed checkout:

   ```sh
   uv run --no-project python publish.py
   ```

4. Publish the reviewed package with `publish.py --push` using the
   same Python command. It atomically pushes `upm` and the new annotated tag.
5. Create a GitHub release for that tag and attach the exact tested `.tgz` from
   `artifacts/`. Mark prereleases as prereleases.

Git URL installs use release tags pointing to compiled packages on `upm`, not
`main`. Existing release tags retain their root package layout, so installed Git
URLs continue working. The release README links back to source on main. Generated
packages and binaries are not checked into the source branch.

## Rotatrix catalog listing

The repository root's [listing.toml](listing.toml) owns the catalog description,
requirements, setup link, and recommended version. Keep it with the integration's
release changes; publish the binary release before advertising its version.

Catalog publication uses the Rotatrix release tooling and operator credentials.
From a sibling Rotatrix checkout, validate and publish to staging:

```sh
uv run python release/integrations/cli.py validate-listing ../rotatrix-unity-editor/listing.toml
uv run python release/integrations/cli.py --staging publish-listing ../rotatrix-unity-editor/listing.toml
```

Adjust the listing path for other checkout layouts. Refresh the staging catalog
to review changes; omit `--staging` only when publishing to production. Metadata
publication does not rebuild the plugin or create a GitHub release. The plugin
remains an external listing; Unity manages project installation and updates.

## Use

1. Start Rotatrix and connect a device.
2. Open **Window > Rotatrix** to see connection status and Connect/Disconnect.
   Choose **Camera preference > Orbit / Free Camera** there. The preference is
   saved in personal Editor preferences; switching cancels the current gesture
   and refreshes the navigation hint used by Rotatrix's shared profiles.
3. Focus a **3D Scene view**. Select an object (or hierarchy) for a selection-based
   orbit pivot. Without selection the server can use the view target or its other
   fallback policy. Use the normal Unity Frame Selected action to frame an object.
4. Rotatrix routes Unity through its shared navigation profile (**nav_default**
   by default). Hold Win
   (Windows), Ctrl (Mac), or Super (Linux) to orbit; hold btn4 to pan. Shift swaps
   orbit/pan. Btn1 snaps the view; btn2 applies a turntable lock. A green marker
   shows the authoritative pivot returned by the server.

The package announces `app.unity`, `workspace.scene`, and
either `navigation.hint.orbit` or `navigation.hint.free_camera`. The preference is advisory; host policy decides
the effective mode. There is no separate Unity free-camera binding implementation.
The shared profiles match the server-added `capability.navigation` tag and use
`navigation.hint.free_camera` for free-camera bindings. Customize the shared
profile in Rotatrix's profile editor. No host configuration is rewritten by this
integration.

The Scene view under the cursor receives control without requiring a click.
Moving over editor panels (including Inspector, Hierarchy, Project, and Rotatrix)
retains the last targeted Scene view without stealing keyboard focus. Hovering a
Game view, 2D view, or rotation-locked view pauses navigation. Switching targets
cancels the old gesture; Unity must remain the foreground application. When no
window is under the cursor, the focused window supplies the same targeting rules.
One connection is shared by all viewports in each Unity editor process; separate
project processes have separate connections.
Game views, Play Mode, 2D views,
and rotation-locked views are inactive. Switching views, selection, scenes, stages,
or projection cancels the old gesture. Normal mouse camera changes are reconciled
through the SDK's navigation session. No scene objects or saved cameras are moved.

Connection absence is shown in the status window without repeated Console warnings.
Unity's orthographic Scene view places its eye at twice its view size from the
native view pivot; the adapter preserves this convention and Unity's clipping
settings. Geometry can still cross the near plane when navigating through it.
Frame Selected or adjust Unity's Scene camera clipping settings when needed.
Enable **Navigation diagnostics** in **Window > Rotatrix** to show the OpenAxis
SDK's query facts, camera write verdicts, correction state, and geometry in the
Scene view. **Copy diagnostic log** copies the last 200 lifecycle/navigation log
entries, including when the overlay is disabled. The same SDK-formatted entries
are flushed to `logs/unity-YYYYMMDDTHHMMSSZ.log` inside the user's Rotatrix data directory:
`%LOCALAPPDATA%/Rotatrix` on Windows, `~/Library/Application Support/Rotatrix` on
macOS, and `${XDG_DATA_HOME:-~/.local/share}/Rotatrix` on Linux. Rotatrix's log
viewer discovers each editor's log without installing the integration through
the app. `ROTATRIX_LOG_DIR` overrides the log directory. Each file rotates at
5 MiB with one backup; the SDK retains ten Unity sessions at startup. Filename
collision suffixes keep simultaneous editor sessions separate. Headers include
the SDK and plugin versions.
The Unity window shows the path and any write failure; a disk failure leaves the
memory copy available. There is no per-frame Console output. The diagnostics
toggle controls the overlay and is a personal Editor preference.
The client retries automatically. Disconnect is session-only: it survives script
reloads but resets when Unity restarts. Reload and Play Mode transitions stop the
connection, including when domain reload is disabled.

## Current scope

- Perspective and 3D orthographic Scene view camera poses.
- Left-handed Unity world conversion and aspect-correct vertical projection.
- Selection bounds (including descendants), visible scene bounds, and Scene view target facts.
- Cursor and viewport-center surface picks, including selection-restricted picks.
- Meshes without colliders, non-readable imported meshes, baked skinned meshes,
  and terrains (including holes and terrain without a collider).
- Viewport cursor NDC, native Unity camera-speed translation scale, and personal free-camera
  upright/ground-plane preferences in Window > Rotatrix. The host owns camera
  movement in both orbit and free-camera modes; the shared profile follows the
  camera preference (Orbit by default).
- Translation scale is `18 Ã— cameraSettings.speed`: one quarter-turn at unit
  gain represents two seconds of native, unaccelerated Scene-view travel.
  Unity's native fly-speed factor is 9 world units/second; the integration
  multiplies it by the two-second trial default. The installed editor's native
  factor is verified by the smoke test.
  Adjust Unity's native Camera Speed setting; there is no separate Rotatrix speed
  control. Zoom does not change this base scale. Native acceleration and the
  Shift speed modifier are not copied; Rotatrix applies its own input mapping.
  Native speed changes invalidate the gesture so the next query uses the new value.
- Server pivot feedback, focus routing, retry, and native camera correction.
- Pivot feedback follows [OpenAxis pivot appearance](https://openaxis.rotatrix.com/recommendations/pivot-appearance/):
  a pure-green disc (4-logical-pixel radius) and black annulus (5.5-pixel outer
  radius), opaque where visible and 20% opacity on occluded fragments. The unlit
  marker tests scene depth without writing it. DPI is applied once; points outside
  the viewport or camera clipping range are hidden.
- Existing shared Rotatrix device verification and C# OpenAxis SDK.
- SDK-owned navigation diagnostics and palette, with Unity-only presentation.

Geometry queries respect the current editing stage, hidden/inactive objects,
camera layers, and editor layer visibility. Picking also respects disabled picking,
camera clipping, and foreground occlusion within the applicable candidate set.
Selection-only picks exclude unselected geometry before nearest-hit ordering.
A missing cursor or an empty scene
produces an unavailable fact, rather than inventing a pivot. Mesh intersections
use Unity's internal `HandleUtility.IntersectRayMesh`, checked against the supported
editor version; newer editor versions require revalidation. These are geometric
surface picks, not shader/alpha-tested pixel picks. Procedural GPU-only surfaces
without a mesh or terrain representation cannot supply a surface hit.

2D navigation, object transforms, camera recording/piloting, specialized asset
preview windows, and gameplay input are outside this Scene view navigation package.
Optional facts without a Unity equivalent (such as Blender's 3D cursor or a CAD
sketch plane) remain unavailable. Prefab Mode uses stage-local geometry/context;
it still needs interactive QA.

