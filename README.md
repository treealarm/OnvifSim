# OnvifSim

<p align="center">
  <img src="docs/images/banner.jpg" alt="OnvifSim: a village with roads and people walking, rendered by the simulator" width="100%">
</p>

<p align="center">
  <a href="https://github.com/treealarm/OnvifSim/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/treealarm/OnvifSim/ci.yml?branch=main&style=flat-square&label=CI" alt="CI"></a>
  <a href="https://github.com/treealarm/OnvifSim/releases"><img src="https://img.shields.io/github/v/release/treealarm/OnvifSim?filter=v*&style=flat-square" alt="Release"></a>
  <a href="https://github.com/treealarm/OnvifSim/pkgs/container/onvifsim"><img src="https://img.shields.io/badge/docker-ghcr.io%2Ftreealarm%2Fonvifsim-2496ED?style=flat-square&logo=docker&logoColor=white" alt="Docker"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square" alt="MIT"></a>
</p>

A virtual world whose cameras are ONVIF devices. Build a low-poly landscape (terrain, trees,
houses, roads, people walking their routes) in the browser, put fixed and PTZ cameras into it, and
every camera answers WS-Discovery, serves RTSP and takes PTZ commands — including AbsoluteMove —
like real hardware. Point ONVIF Device Manager, an NVR or your own client at it; no cameras needed.

<p align="center">
  <img src="docs/images/editor.gif" alt="The editor: a PTZ camera turned with the joystick and zoomed in, its picture beside the 3D view" width="90%">
</p>

## Why a simulator

There are good ONVIF servers already; they turn a real camera, a video file or an RTSP stream into
an ONVIF device, which is what they are for. OnvifSim has no video source: it renders a world, so
what the cameras show depends on where they are and where they point.

| | OnvifSim |
|---|---|
| Video | Rendered from the world, per camera; no camera, file or stream needed |
| PTZ | Moves the picture: the head turns at its set speed and the stream shows it turning; AbsoluteMove ends exactly where asked, which a test can check on the picture |
| Several cameras | One world seen from different places: overlapping views, a person walking from one camera's view into the next |
| Repeatable | The picture is a function of the world and the clock: the same world file gives the same frames, in CI too |
| Setup | One program, one Docker image or one installer; the world is a JSON file you can keep in git |

> **Status: first release on the way.** Everything below works and is tested against real clients
> (OnvifLib and its Probe and GUI, onvif-zeep, ffmpeg, VLC, GStreamer, go2rtc); what changes is
> listed in the [changelog](CHANGELOG.md).

## Run

- **Windows**: the MSI from [Releases](https://github.com/treealarm/OnvifSim/releases) puts OnvifSim in
  the Start menu; starting it opens the editor in the browser.
- **Windows, Linux (x64, arm64)**: the portable zips from Releases; unzip and run `OnvifSim`.
- **Docker**: see [below](#docker).
- **Snap**: `sudo snap install onvifsim` runs it as a service, with the world in
  `/var/snap/onvifsim/common`.

Every package carries ffmpeg for the H.264 streams. The world is kept in `%LOCALAPPDATA%\OnvifSim`
or `~/.local/share/OnvifSim`; on first start a default world is written there. The editor is at
http://localhost:8080/.

| Option | |
|---|---|
| `--data <dir>` | where `world.json` is kept |
| `--port <port>` | the editor and API (8080) |
| `--no-browser` | do not open the editor on start (it opens only where there is a desktop anyway) |
| `--ffmpeg <path\|none>` | the ffmpeg for H.264; by default the bundled one, then the one on PATH |

Ports are under [Ports](#ports). From source, with the .NET 10 SDK and Node.js 20.19 or later
(`dotnet build` runs `npm` for the editor when its sources change; without Node.js the server still
builds and serves everything except the editor):

```bash
dotnet run -c Release --project src/OnvifSim.Server -- --data ./data
```

## Docker

```bash
docker compose up -d                                # Linux: host networking, WS-Discovery works
docker compose -f docker-compose.ports.yml up -d    # Docker Desktop (Windows, macOS): mapped ports
```

The image `ghcr.io/treealarm/onvifsim` (amd64 and arm64) has ffmpeg, so H.264 is on, and keeps the
world in the `/data` volume. With host networking the cameras are on the LAN as they would be on real
hardware: discovery, ONVIF ports 8001…, RTSP 8554. Docker Desktop cannot put a container on the LAN,
so `docker-compose.ports.yml` maps the ports one by one (cameras 8001–8016) and turns discovery off;
connect to `http://<host>:8001/onvif/device_service` and so on. Behind NAT or in a VM, set
`ONVIFSIM_ADVERTISE_HOST` to the address clients should use.

## Editor

The browser editor shows the world in 3D and changes it live; there is no Save button, every edit
reaches the cameras and `world.json` within a moment. Undo and Redo (Ctrl+Z, Ctrl+Shift+Z or
Ctrl+Y) step back and forth through the last 100 edits; turning a PTZ camera is not an edit.

- **Place** trees, houses, rocks, boxes and fixed or PTZ cameras: pick one on the toolbar and click
  the ground. Click anything to select it; drag the gizmo to move it (G) or turn it (R), or type
  exact values in the panel. Delete removes the selection, Escape drops it.
- **Sculpt** the terrain with the Raise, Lower, Smooth and Flatten brushes.
- **Roads and people**: pick Road or Person, click the ground point by point and finish with a
  double-click or Enter. A person walks their route over and over, round in a loop or there and
  back, at the speed you set, and every camera sees them walking: where they are follows from the
  clock, so all streams and snapshots agree. Drag the points of a selected road or route to reshape it.
- **Cameras** show what they see (the server's own picture, the same as RTSP), their ONVIF and RTSP
  addresses and every setting: mount, field of view, PTZ limits and speeds, both streams, image
  adjustments, users and port. A PTZ camera gets a joystick (drag it, or focus it and hold the arrow
  keys), zoom buttons and pan/tilt/zoom boxes that drive the same PTZ head as ONVIF clients.
- **World**: sun, sky colours, fog and terrain size.

ONVIF clients' changes (a new resolution, a preset, where a PTZ camera stopped) show up in the editor
too. `npm run dev` in `src/OnvifSim.Web` serves the editor with hot reload against a simulator on
port 8080; `npm run e2e` runs the browser test.

| Endpoint | |
|---|---|
| `GET /api/world`, `PUT /api/world` | the world as JSON |
| `PUT`, `DELETE /api/objects/{id}`, `/api/roads/{id}`, `/api/walkers/{id}`, `/api/cameras/{id}` | add, replace or remove one object, road, walker or camera |
| `PUT /api/terrain`, `PUT /api/environment` | the terrain or the sun, sky and fog |
| `POST /api/cameras/{id}/ptz/move`, `goto`, `stop`, `home` | drive a PTZ head |
| `GET /api/events` | server-sent events: the world, ports and PTZ positions as they change |
| `GET /api/cameras` | camera list |
| `GET /api/cameras/{id}/snapshot.jpg` | one rendered frame |
| `GET /api/cameras/{id}/mjpeg` | live Motion JPEG (open it in a browser) |

A change the simulator cannot run with (an unknown object type, a frame size that is not a
multiple of 8, two cameras on one port…) is refused with 400 and the reason.

## RTSP

Every camera streams Motion JPEG (RTP/JPEG, RFC 2435) at `rtsp://<host>:8554/<camera id>`:

```bash
ffplay rtsp://admin:admin@localhost:8554/cam1                       # RTP over UDP
ffplay -rtsp_transport tcp rtsp://admin:admin@localhost:8554/cam2   # RTP interleaved in RTSP/TCP
```

RTP goes over UDP or interleaved in the RTSP connection, whichever the client asks for. Credentials
are the camera's `users` in `world.json` (Digest or Basic; the default world has `admin`/`admin`); a
camera with no users accepts anyone. Resolution, frame rate and JPEG quality are per camera
(`video` in `world.json`, 640×360 at 10 fps by default).

ffmpeg-based players need about 20 frames to start (they measure the frame rate first): ~2 s at the
default 10 fps, ~7 s at 3 fps. VLC and GStreamer show the first frame at once.

### H.264

When an ffmpeg with libx264 is on `PATH` (or named with `--ffmpeg <path>`, `ONVIFSIM_FFMPEG`), every
camera also streams H.264 (RTP, RFC 6184) at `rtsp://<host>:8554/<camera id>/h264`, with settings of
its own (`h264` in `world.json`: 640×360, 10 fps, 1024 kbit/s, a key frame every 10 frames by
default). One ffmpeg process per camera runs while someone watches; a new viewer starts at the next
key frame. Without ffmpeg (or with `--ffmpeg none`) cameras stream JPEG only.

```bash
ffplay -rtsp_transport tcp rtsp://admin:admin@localhost:8554/cam1/h264
```

## ONVIF

Every camera is a device of its own, on its own port: `http://<host>:8001/onvif/device_service` for
the first camera, 8002 for the second and so on (`onvifPort` in `world.json` pins one). Each answers
WS-Discovery, so ONVIF Device Manager, an NVR or `OnvifLib.Probe --discovery` lists them all.

- **Device**: device information, clock, `GetServices`/`GetCapabilities`, scopes, users, a read-only
  view of the network settings.
- **Media** (ver10) and **Media2** (ver20): `Profile_1` with the JPEG stream and, when H.264 is on,
  `Profile_2` with the H.264 one, each with its RTSP and snapshot URIs.
  `SetVideoEncoderConfiguration` changes resolution, frame rate, JPEG quality, H.264 bitrate and GOV
  length within the offered options and is saved to `world.json`.
- **PTZ** (ver20, PTZ cameras only): AbsoluteMove, RelativeMove, ContinuousMove, Stop and
  GetStatus in the generic spaces, presets and a home position. The head moves at the camera's top
  speeds (`ptz` in `world.json`), the stream shows it turning, and where it stops, its presets and
  its home are saved to `world.json`.
- **Imaging** (ver20): brightness, contrast and colour saturation, applied to the picture and saved.

- **Authentication** as on real cameras: WS-Security UsernameToken or HTTP Digest/Basic with the
  camera's `users`; `GetSystemDateAndTime`, `GetCapabilities`, `GetServices` and `GetHostname` answer
  without credentials.

XAddrs and stream URIs use the address the client connected to. WS-Discovery advertises the address
of the route towards the client, or `--advertise-host` (`ONVIFSIM_ADVERTISE_HOST`) when that is not
reachable from outside, e.g. in a container.

The **degree grid** (`angleGrid` in `world.json`, a checkbox in the editor) draws pan/tilt lines over
a camera's picture, relative to the mount (pan 0, tilt 0 is where the camera points at rest), with
labels every 15° that get finer as the camera zooms in. The default world turns it on for the PTZ
camera.

## Ports

| Port | |
|---|---|
| 8080/tcp | editor and API (`--port`, `ONVIFSIM_PORT`) |
| 8001…/tcp | ONVIF, one port per camera (`--onvif-base-port`, `ONVIFSIM_ONVIF_BASE_PORT`) |
| 8554/tcp | RTSP for all cameras (`--rtsp-port`, `ONVIFSIM_RTSP_PORT`) |
| 6970-6971/udp | RTP and RTCP of every UDP session (`--rtp-port`, `ONVIFSIM_RTP_PORT`) |
| 3702/udp | WS-Discovery (`--discovery-port`, `ONVIFSIM_DISCOVERY_PORT`; 0 turns it off) |

`--bench` prints render and JPEG timings per camera and resolution.

## How it works

One .NET 10 process, no native dependencies: the world is rendered by a built-in CPU rasterizer
(a 640×360 frame takes about 5 ms), encoded to JPEG in-process, and a camera only renders while
someone is watching it, and then only when its picture changes: a still camera with nobody walking
in view sends the same frame again. H.264 is the one optional extra: raw frames piped through an ffmpeg process.

ONVIF requests and responses are (de)serialized with types generated from the official ONVIF WSDL
([onvif/specs](https://github.com/onvif/specs)); `src/OnvifSim.Server/Onvif/Wsdl/generate.sh`
regenerates them.

## License

MIT
