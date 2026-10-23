# Rotatrix Scene Navigation for Unity

Editor-only orbit, pan, zoom, and free-camera navigation with a Rotatrix device.
Requires Unity **6000.6** and Rotatrix **1.6.0 or newer**. The package contains
managed AnyCPU DLLs. Windows is supported; macOS and Linux are experimental.

## Install

1. In Unity, open **Window > Package Management > Package Manager**.
2. Choose **Install package from Git URL** and paste the URL for a published tag:

   ```text
   https://github.com/rotatrix/rotatrix-unity-editor.git#v1.0.0-beta.1
   ```

3. After installation, start Rotatrix, connect your device, and open
   **Window > Rotatrix** in Unity to check the connection.

Check the repository's Tags list before installing: the URL only works once that
release has been published. Installation is per project. Commit
`Packages/manifest.json` and `Packages/packages-lock.json` so teammates use the same
dependency. Rotatrix's integration catalog links here; it does not install or
update project dependencies.

To update, install from a newer published Git tag through Package Manager and
commit the resulting manifest and lockfile changes. To uninstall, select
**Rotatrix Scene Navigation** in Package Manager and choose **Remove**.

### Test a local build

In Package Manager, choose **Install package from tarball** and select the built
`com.rotatrix.unity-1.0.0-beta.1.tgz`. Alternatively, choose **Install package from
disk** and select `package.json` inside the generated package folder. Some Unity
versions label these actions **Add package**. Do not select the source template.
Local file dependencies are machine-specific; replace them with a published Git
dependency before sharing the project.

## Use

Start Rotatrix, connect the device, and focus an unlocked 3D Scene view. Open
**Window > Rotatrix** for connection status, Connect/Disconnect, Orbit/Free Camera
preference, and navigation diagnostics. Disconnect lasts until you reconnect or
restart Unity; it survives script reloads. Navigation pauses in Play Mode.

Rotatrix's shared navigation profile owns bindings, speed, and pivot policy.
Select an object to supply a selection pivot. Unity's native Camera Speed controls
the free-camera translation scale. Normal mouse navigation remains available.
2D views, Game views, object manipulation, and gameplay input are outside this package.

## Troubleshooting

If installation fails, check that the selected Git tag exists and that Git is
available to Unity. For a local test, check that the package file still exists at
the recorded path.

If navigation is inactive, start Rotatrix with a connected device, check
**Window > Rotatrix**, and choose **Connect** if disconnected. Keep Unity in the
foreground with an unlocked 3D Scene view and exit Play Mode.

Use **Copy diagnostic log** for troubleshooting; the window also displays the
SDK session log path. Include Unity, Rotatrix, and package versions in reports.
The installed package contains editor-only binaries. Source and build tools are described in [development](DEVELOPMENT.md).
This repository is not directly installable as a UPM package; use a release tag
or a generated package.
Each compiled package includes **Third Party Notices.md** with dependency licenses.

## Development and contributions

See [development](DEVELOPMENT.md) for building from source and
[contributing](CONTRIBUTING.md) for changes and bug reports.

## Licensing

Source is available under ELv2. See [legal notices](LEGAL.md) for details.
