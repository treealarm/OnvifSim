# OnvifSim

<p align="center">
  <img src="docs/images/banner.jpg" alt="OnvifSim, an ONVIF IP camera simulator: four simulated cameras, one of them PTZ, showing a village with a person walking down the road" width="100%">
</p>

<p align="center">
  <a href="https://github.com/treealarm/OnvifSim/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/treealarm/OnvifSim/ci.yml?branch=main&style=flat-square&label=CI" alt="CI"></a>
  <a href="https://github.com/treealarm/OnvifSim/releases"><img src="https://img.shields.io/github/v/release/treealarm/OnvifSim?filter=v*&style=flat-square" alt="Release"></a>
  <a href="https://github.com/treealarm/OnvifSim/pkgs/container/onvifsim"><img src="https://img.shields.io/badge/docker-ghcr.io%2Ftreealarm%2Fonvifsim-2496ED?style=flat-square&logo=docker&logoColor=white" alt="Docker"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square" alt="MIT"></a>
  <a href="https://github.com/sponsors/treealarm"><img src="https://img.shields.io/badge/sponsor-%E2%99%A5-ea4aaa?style=flat-square&logo=githubsponsors&logoColor=white" alt="Sponsor"></a>
</p>

**An ONVIF IP camera simulator.** Run one program and get as many virtual network cameras as you
like: each one is found by WS-Discovery, answers ONVIF on its own port, streams RTSP and turns when
it gets PTZ commands, just like real hardware. Point ONVIF Device Manager, an NVR or VMS, or your own
ONVIF client at it; no cameras needed.

What the cameras show is not a test pattern or a looped video: they look at a 3D world you build in
the browser (terrain, trees, houses, roads, people walking their routes), so a PTZ camera really
turns towards what you asked for, and a person walks out of one camera's view into the next.

<p align="center">
  <img src="docs/images/editor.gif" alt="The editor: a PTZ camera turned with the joystick and zoomed in, its picture beside the 3D view" width="90%">
</p>

## Features

**Cameras that behave like real ONVIF devices**
- Fixed and PTZ cameras, each an ONVIF device on its own port
  (`http://<host>:8001/onvif/device_service`, 8002, …).
- WS-Discovery: clients that search the network find them by themselves.
- ONVIF Device, Media, Media2, PTZ, Imaging and Events services; the request and response types are
  generated from the official ONVIF WSDL.
- Authentication as on real cameras: WS-Security UsernameToken, HTTP Digest and Basic, per-camera users.
- Settings changed over ONVIF (resolution, frame rate, bitrate, image settings, presets, home position)
  are kept in the world file and survive a restart.

**Video**
- RTSP for every camera at `rtsp://<host>:8554/<camera id>`: Motion JPEG always, H.264 through the
  bundled ffmpeg; RTP over UDP or interleaved in TCP.
- Snapshot URIs, and a live Motion JPEG page for the browser.
- Two streams per camera with their own resolution, frame rate and quality, as ONVIF profiles.

**Analytics with exact answers**
- Every camera reports the people it sees, as a camera's own analytics would: boxes with object ids
  in the ONVIF metadata track of its RTSP streams (`vnd.onvif.metadata`), five times a second.
- The boxes are exact, to the pixel of the picture, and people hidden behind a house or a hill are
  not reported, so a VMS's overlay, tracking or rules can be checked against the truth.
- Optionally the PTZ position too, in the same track.
- Motion events through ONVIF pull points (`tns1:VideoSource/MotionAlarm`,
  `tns1:RuleEngine/CellMotionDetector/Motion`) while someone is in view.

**PTZ you can see**
- AbsoluteMove, RelativeMove, ContinuousMove, Stop, GetStatus, presets and home.
- The head turns at the camera's set speeds and the stream shows it turning; AbsoluteMove ends exactly
  where asked, so a test can check the result on the picture.
- An optional pan/tilt degree grid over the picture shows where the camera is pointing.

**A world you edit in the browser**
- Place trees, houses, rocks, cameras; sculpt the terrain; draw roads and routes for people who walk
  them over and over.
- See each camera's picture beside the 3D view, with the people its analytics reports boxed, and drive
  PTZ cameras with a joystick.
- Every edit is live, on all streams within a moment; undo and redo; no Save button.
- The world is one JSON file you can keep in git; an HTTP API changes it from scripts and tests.

**Runs anywhere**
- Windows installer (MSI), portable zips for Windows and Linux (x64, arm64), a Docker image (amd64,
  arm64), a snap.
- One self-contained program, no GPU: a built-in software renderer draws the pictures.

**Use it for**
- Developing and testing an ONVIF client, NVR or VMS without a shelf of cameras.
- Automated tests in CI: the picture depends only on the world and the clock, so runs are repeatable.
- Trying out PTZ control, presets and following a person from one camera to the next.
- Demos and training: a whole site of cameras on a laptop.

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

> Tested against real clients: OnvifLib and its Probe and GUI, onvif-zeep, ffmpeg, VLC, GStreamer
> and go2rtc. What changes from release to release is in the
> [changelog](CHANGELOG.md).

## Run

- **Windows**: the MSI from [Releases](https://github.com/treealarm/OnvifSim/releases) puts OnvifSim in
  the Start menu; starting it opens the editor in the browser.
- **Windows, Linux (x64, arm64)**: the portable zips from Releases; unzip and run `OnvifSim`.
- **Docker**: see [below](#docker).
- **Snap** (or Ubuntu's App Center): `sudo snap install onvifsim` runs it as a service, with the world
  in `/var/snap/onvifsim/common`; OnvifSim in the applications menu opens the editor.

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
- **Analytics**: the camera's picture boxes the people its analytics reports, with their object ids,
  exactly as the metadata stream sends them; the panel turns people detection and the PTZ position in
  the metadata on and off, and a person's own panel can leave them out of every camera's analytics
  ("Seen by analytics"), so they only appear in the picture.
- **World**: sun, sky colours, fog, terrain size, and what the camera pictures show of the other
  cameras: their bodies, and optionally the wire frame of where they look.

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
| `GET /api/cameras/{id}/analytics` | server-sent events: the people the camera's analytics sees, five times a second, each with where they stand on the ground (`x`, `z`, `heading`) |
| `GET /api/cameras/{id}/pose` | ground truth: where the camera stands (absolute, terrain included) and looks (heading and pitch with the PTZ head, the picture's own axes `forward`, `right`, `up`, field of view from the zoom) |
| `POST /api/cameras/{id}/ptz/aim` | turn a PTZ head until a point of the world (`x`, `y`, `z`, absolute) is in the middle of its picture; answers the pan and tilt, or 409 beyond the head's limits |
| `GET /api/walkers/{id}/pose` | ground truth: where a walker is at this moment |

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

### Metadata

Both streams of a camera carry a second track, the ONVIF metadata stream (`m=application`,
`vnd.onvif.metadata/90000`, `{stream}/track2`), as cameras with analytics do. Each document is a
`tt:MetadataStream` with a scene frame: every person the camera sees is a `tt:Object` with an
`ObjectId` that stays the same while they stay in view, a `BoundingBox` and `CenterOfGravity` in
ONVIF's normalized frame coordinates (-1..1, y up) and the class `Human`. A document goes out with
every look of the analytics (five a second) while someone is in view, one more when the last person
leaves, and once a second otherwise. With `ptzStatus` on, a PTZ camera adds its `tt:PTZStatus`.

The boxes are where the picture draws the people, and someone hidden behind a house, a tree or a hill
is not reported; a person shorter than 2.5% of the frame is too small to detect. Clients that do not
want the track simply do not set it up; ffmpeg shows it as a data stream (`-map 0:d`).

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
  length within the offered options and is saved to `world.json`. Both profiles share the metadata
  configuration `Metadata_1` (`SetMetadataConfiguration` turns analytics and the PTZ status on and
  off, saved as `metadata` in `world.json`) and the analytics configuration `VideoAnalytics_1`.
- **PTZ** (ver20, PTZ cameras only): AbsoluteMove, RelativeMove, ContinuousMove, Stop and
  GetStatus in the generic spaces, presets and a home position. The head moves at the camera's top
  speeds (`ptz` in `world.json`), the stream shows it turning, and where it stops, its presets and
  its home are saved to `world.json`.
- **Imaging** (ver20): brightness, contrast and colour saturation, applied to the picture and saved.
- **Events** (ver10): pull point subscriptions (`CreatePullPointSubscription`, `PullMessages`,
  `SetSynchronizationPoint`, `Renew`, `Unsubscribe`, topic filters in the ConcreteSet dialect). A
  camera raises motion on `tns1:VideoSource/MotionAlarm` and `tns1:RuleEngine/CellMotionDetector/Motion`
  as soon as its analytics sees someone, and clears it a second after the last person is gone; a new
  subscription first gets the current state. A subscription lives for the time it asked for (a minute
  by default), renewed by `Renew` and by every pull; a camera keeps at most 10.

- **Authentication** as on real cameras: WS-Security UsernameToken or HTTP Digest/Basic with the
  camera's `users`; `GetSystemDateAndTime`, `GetCapabilities`, `GetServices` and `GetHostname` answer
  without credentials.

XAddrs and stream URIs use the address the client connected to. WS-Discovery advertises the address
of the route towards the client, or `--advertise-host` (`ONVIFSIM_ADVERTISE_HOST`) when that is not
reachable from outside, e.g. in a container.

**Mounts.** `heading`, `pitch` and `roll` place a camera any way up; a PTZ head turns with its mount
(pan about the mount's up axis, tilt about its right axis), so a camera hung upside down or on its
side behaves as one does. `video.rotate` turns the picture (reported as the video source's
`Rotate`), and `ptz.panReverse` / `ptz.tiltReverse` make ONVIF count an axis the other way round
(`PTControlDirection` `Reverse`), as installers set cameras that hang upside down. ONVIF cannot
change either; they are the installer's, in `world.json`.

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

`--bench` prints render and JPEG timings per camera and resolution, and what drawing the other
cameras and their fields of view adds.

## How it works

One .NET 10 process, no native dependencies: the world is rendered by a built-in CPU rasterizer
(a 640×360 frame takes about 5 ms), encoded to JPEG in-process, and a camera only renders while
someone is watching it, and then only when its picture changes: a still camera with nobody walking
in view sends the same frame again. H.264 is the one optional extra: raw frames piped through an ffmpeg process.

The RTSP server and the RTP payload formats (JPEG, H.264, ONVIF metadata) are a library of their
own, [src/OnvifSim.Rtsp](src/OnvifSim.Rtsp), which knows nothing of the simulator: the cameras reach
it through one interface.

ONVIF requests and responses are (de)serialized with types generated from the official ONVIF WSDL
([onvif/specs](https://github.com/onvif/specs)); `src/OnvifSim.Server/Onvif/Wsdl/generate.sh`
regenerates them.

## License

MIT
