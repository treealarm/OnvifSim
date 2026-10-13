# Snap: onvifsim

[Snap Store](https://snapcraft.io/onvifsim) package of OnvifSim: `base: core24`, strict confinement,
a service (`daemon: simple`) that starts with the system, and an **OnvifSim** entry in the
applications menu that opens the service's editor in the browser. It repackages the **linux-x64
release zip** (the single-file server, the editor and the bundled GPL ffmpeg); nothing is compiled by
snapcraft.

```bash
sudo snap install onvifsim
# The editor: OnvifSim in the applications menu, or http://localhost:8080/
# Cameras: ONVIF on 8001 and up, RTSP on 8554
sudo snap logs onvifsim -f        # the server's log
sudo snap restart onvifsim
```

The world is kept in `/var/snap/onvifsim/common/world.json`; the service runs as root, so edit it
there with sudo, or (better) in the editor.

## Files

| File | Role |
|---|---|
| `snapcraft.yaml` | The recipe: `plugin: dump` of `payload/app`, the service, its plugs (`network`, `network-bind`). |
| `stage.sh` | Fills `payload/` (gitignored) from the release zip or the directory it unpacks to, and writes the version. |
| `launcher/open-editor` | The menu entry's command (`onvifsim.editor`): `xdg-open http://localhost:8080/`, which the base hands to snapd to open on the desktop. |
| `snap/gui/editor.desktop`, `icon.png` | The menu entry and its icon, which is also the Store's. |

## Release workflow

`.github/workflows/release.yml`, job **snap** (after **build**): downloads the linux-x64 zip
artifact, runs `stage.sh` with the version of the `vX.Y.Z` tag, builds with
[snapcore/action-build](https://github.com/snapcore/action-build) and, when the repository secret
`SNAPCRAFT_STORE_CREDENTIALS` is set, releases to the **stable** channel with
[snapcore/action-publish](https://github.com/snapcore/action-publish). A dry run (the workflow run by
hand without a tag) builds a `0.0.0+git…` snap as a workflow artifact and publishes nothing.

## Build and try locally

Requires snapcraft and LXD (`sudo snap install snapcraft --classic`, `sudo snap install lxd`,
`sudo lxd init --auto`, and membership of the `lxd` group). From the repository root, with a release
zip (or the dry run's artifact):

```bash
packaging/snap/stage.sh OnvifSim-linux-x64.zip 0.1.0+local
(cd packaging/snap && snapcraft)                 # -> packaging/snap/onvifsim_0.1.0+local_amd64.snap
sudo snap install --dangerous packaging/snap/onvifsim_*_amd64.snap
```

## Store setup (once)

```bash
snapcraft login
snapcraft register onvifsim
snapcraft export-login --snaps onvifsim --channels stable \
  --acls package_access,package_push,package_update,package_release snapcraft-store-credentials.txt
gh secret set SNAPCRAFT_STORE_CREDENTIALS --repo treealarm/OnvifSim < snapcraft-store-credentials.txt
rm snapcraft-store-credentials.txt
```
