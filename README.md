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
the browser (terrain, trees, houses, roads, cars with readable licence plates, people walking and
cars driving their routes), so a PTZ camera really turns towards what you asked for, and a person
walks out of one camera's view into the next. Prepare as many worlds as the tests need, switch between them from the
editor, and put away the ones you no longer use without losing them.

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
- RTSP for every camera: Motion JPEG always, H.264 through the bundled ffmpeg; RTP over UDP or
  interleaved in TCP.
- Snapshot URIs, and a live Motion JPEG page for the browser.
- One or more streams per camera, each an ONVIF profile with its own RTSP path, resolution, frame
  rate and quality. A camera starts with `Profile_1` (JPEG at `/<camera id>`) and `Profile_2` (H.264
  at `/<camera id>/h264`); add or remove streams in the camera panel.

**Analytics with exact answers**
- Every camera reports the people and cars it sees, as a camera's own analytics would: boxes with
  object ids in the ONVIF metadata track of its RTSP streams (`vnd.onvif.metadata`), five times a
  second. A person is classed `Human`, a moving car `Vehicle`.
- The boxes are exact, to the pixel of the picture, and objects hidden behind a house or a hill are
  not reported, so a VMS's overlay, tracking or rules can be checked against the truth.
- Optionally the PTZ position too, in the same track.
- Motion events through ONVIF pull points (`tns1:VideoSource/MotionAlarm`,
  `tns1:RuleEngine/CellMotionDetector/Motion`) while someone or a car is in view.

**PTZ you can see**
- AbsoluteMove, RelativeMove, ContinuousMove, Stop, GetStatus, presets and home.
- The head turns at the camera's set speeds and the stream shows it turning; AbsoluteMove ends exactly
  where asked, so a test can check the result on the picture.
- An optional pan/tilt degree grid over the picture shows where the camera is pointing.

**A world you edit in the browser**
- Place trees, houses, rocks, boxes, cars with editable licence plates and fixed or PTZ cameras;
  sculpt the terrain; draw roads and routes for people who walk them and cars that drive them over
  and over.
- Prepare any number of named worlds in the data directory and switch between them in the header,
  without restarting; move a world to the trash and restore it, or empty the trash for good.
- See each camera's picture beside the 3D view, with the people and cars its analytics reports
  boxed, and drive PTZ cameras with a joystick.
- Find every object, camera, road, person and moving car in the contents tree and select it there —
  the reliable way to reach a person only a few pixels tall in the 3D view.
- Every edit is live, on all streams within a moment; undo and redo; no Save button.
- Worlds are JSON files you can keep in git; an HTTP API changes them from scripts and tests.

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
| Setup | One program, one Docker image or one installer; worlds are JSON files you can keep in git, and the editor switches between them while the cameras keep running |

> Tested against real clients: OnvifLib and its Probe and GUI, onvif-zeep, ffmpeg, VLC, GStreamer
> and go2rtc. What changes from release to release is in the
> [changelog](CHANGELOG.md).

## Run

- **Windows**: the MSI from [Releases](https://github.com/treealarm/OnvifSim/releases) puts OnvifSim in
  the Start menu; starting it opens the editor in the browser.
- **Windows, Linux (x64, arm64)**: the portable zips from Releases; unzip and run `OnvifSim`.
- **Docker**: see [below](#docker).
- **Snap** (or Ubuntu's App Center): `sudo snap install onvifsim` runs it as a service, with the
  worlds in `/var/snap/onvifsim/common`; OnvifSim in the applications menu opens the editor.

Every package carries ffmpeg for the H.264 streams. Worlds are kept in `%LOCALAPPDATA%\OnvifSim`
or `~/.local/share/OnvifSim`; on first start the default village is written there, with a car in
view of the fixed camera so every feature is in sight. The editor is at http://localhost:18180/.

| Option | |
|---|---|
| `--data <dir>` | where the worlds, their trash and the recordings are kept |
| `--port <port>` | the editor and API (18180) |
| `--no-browser` | do not open the editor on start (it opens only where there is a desktop anyway) |
| `--ffmpeg <path\|none>` | the ffmpeg for H.264; by default the bundled one, then the one on PATH |

Ports are under [Ports](#ports). From source, with the .NET 10 SDK and Node.js 20.19 or later
(`dotnet build` runs `npm` for the editor when its sources change; without Node.js the server still
builds and serves everything except the editor):

```bash
dotnet run -c Release --project src/OnvifSim.Server -- --data ./data
```

## Worlds

One world is active; any number of others are kept beside it and opened from the editor without a
restart.

```text
<data>/
  active-world.json       the world a restart opens
  worlds/<name>.json      every world
  trash/<name>.json       worlds moved out of the list
  recordings.json         the cameras' own recordings (see Profile G below)
```

- **New world** in the header creates an empty one — flat ground, no cameras — and opens it.
- **Switch** by choosing a world in the list. Edits in the outgoing world are saved first, and the
  new world starts with an empty undo history.
- **Trash**, **Restore** and **Empty trash**: a trashed world keeps its file and can be brought back;
  emptying the trash deletes those files permanently. The active world may be trashed too — the next
  world opens, or a fresh empty `default` if none is left.
- Names may contain spaces and any letters, Cyrillic included. Path separators, control characters
  and Windows device names are refused; names are compared without regard to case.
- If the world being left has camera recordings, the editor asks whether to **clear the cameras' SD
  cards** or keep them. Keeping carries footage for the same camera ids into the new world; clearing
  starts every card again.
- An installation from before worlds had names keeps its `world.json`; it becomes
  `worlds/default.json` on the first start.

The library over HTTP, for scripts and tests:

| Method and path | |
|---|---|
| `GET /api/worlds` | the active world, all worlds, the trash, and whether the active world has recordings |
| `POST /api/worlds` `{ "name", "clearRecordings" }` | create an empty world and open it |
| `POST /api/worlds/{name}/activate` `{ "clearRecordings" }` | open a world |
| `POST /api/worlds/{name}/trash` `{ "clearRecordings" }` | move a world to the trash |
| `POST /api/trash/{name}/restore` `{ "name" }` | bring a trashed world back, optionally renamed |
| `DELETE /api/trash` | delete every trashed world permanently |

When a recordings choice is needed and the body leaves `clearRecordings` out, the server answers 409
with `"code": "recordings"`, so a client has to choose before a switch happens.

<p align="center">
  <img src="docs/images/worlds.png" alt="The world manager: the list of worlds, a field for a new world and the trash" width="90%">
</p>

## Docker

```bash
docker compose up -d                                # Linux: host networking, WS-Discovery works
docker compose -f docker-compose.ports.yml up -d    # Docker Desktop (Windows, macOS): mapped ports
```

The image `ghcr.io/treealarm/onvifsim` (amd64 and arm64) has ffmpeg, so H.264 is on, and keeps the
worlds in the `/data` volume. With host networking the cameras are on the LAN as they would be on real
hardware: discovery, ONVIF ports 8001…, RTSP 8554. Docker Desktop cannot put a container on the LAN,
so `docker-compose.ports.yml` maps the ports one by one (cameras 8001–8016) and turns discovery off;
connect to `http://<host>:8001/onvif/device_service` and so on. Behind NAT or in a VM, set
`ONVIFSIM_ADVERTISE_HOST` to the address clients should use.

## Editor

The browser editor shows the world in 3D and changes it live; there is no Save button, every edit
reaches the cameras and the active world's file within a moment. Undo and Redo (Ctrl+Z,
Ctrl+Shift+Z or Ctrl+Y) step back and forth through the last 100 edits; turning a PTZ camera is not
an edit. Switching worlds clears the history, and undo starts again in the new world.

The toolbar has two rows: **Select**, **Add**, **Draw** and **Terrain** choose which tools the second
row offers; Undo and Redo stay at its left. Choosing a tab only changes the palette — the active tool
is marked with a dot, so the viewport keeps doing exactly what it did until another tool is picked.

- **Select, Move, Rotate**: click anything in the viewport or in the contents tree to select it; drag
  the gizmo to move it (G) or turn it (R), or type exact values in the panel. Delete removes the
  selection, Escape drops it.
- **Add** trees, houses, rocks, boxes, cars and fixed or PTZ cameras: pick one and click the ground.
- **Draw** roads, people and car routes: click the ground point by point and finish with a
  double-click or Enter. A person walks their route, a car drives theirs, over and over, round in a
  loop or there and back, at the speed you set; a car turns to follow the route. Every camera sees
  them move: where they are follows from the clock, so all streams and snapshots agree. Drag the
  points of a selected road or route to reshape it.
- **Terrain** brushes: Raise, Lower, Smooth and Flatten, with radius and strength.
- **Contents** lists every camera, object, road, person and moving car in the world on the left.
  Objects are grouped by type, and opening a type shows its ids. Clicking a row selects it exactly as
  clicking it in 3D does — the reliable way to reach a person only a few pixels tall. The **World
  environment** row shows the sun, sky, fog and terrain settings.

<p align="center">
  <img src="docs/images/editor-tree.png" alt="The editor: the 3D view, the contents tree with cameras, objects, roads and people, and the inspector" width="90%">
</p>

- **Cameras** show what they see (the server's own picture, the same as RTSP), their ONVIF and RTSP
  addresses and every setting: mount, field of view, PTZ limits and speeds, their streams, image
  adjustments, users, the address and ports they listen on, and a colour of their own, which their
  body and the wire frame of where they look take in the editor and in the other cameras' pictures. A
  PTZ camera gets a joystick (drag it, or focus it and hold the arrow keys), zoom buttons and
  pan/tilt/zoom boxes that drive the same PTZ head as ONVIF clients.
  Every stream has a name, a path and its own encoder settings; **Add** makes a new profile, **Remove**
  takes it and its RTSP path away, and at least one stream stays. **Bind address** makes the camera's
  ONVIF listener and RTSP port listen on one interface; **RTSP port** 0 keeps the global one.

<p align="center">
  <img src="docs/images/camera-streams.png" alt="A camera panel: its ONVIF and RTSP addresses, and the list of its streams with each path and encoder settings" width="90%">
</p>

- **Cars** have a plate text field: Latin letters, digits, spaces and hyphens, up to 8 characters. The
  plate is part of the mesh, so it appears in every camera picture as well as in the editor. As with
  real cameras, the characters are readable at close range or through a narrow lens; a wide camera at
  village distance shows the car, not the text. **Car route** under Draw makes a car drive a route:
  set its speed, whether it loops or goes back, its body size, paint, plate background and plate, and
  whether cameras' analytics report it. A selected car shows its route, and the points drag like a
  person's.

<p align="center">
  <img src="docs/images/car-plate.png" alt="The editor with the car selected: its body and plate settings, including the plate text" width="90%">
  <br>
  <img src="docs/images/camera-car.jpg" alt="The village camera's picture: a red car in the foreground with its licence plate" width="70%">
  <br>
  <img src="docs/images/car-route.png" alt="A moving car selected: its route on the ground, speed, body and plate settings" width="90%">
</p>

- **Analytics**: the camera's picture boxes the people and cars its analytics reports, with their
  object ids, exactly as the metadata stream sends them; the panel turns analytics and the PTZ
  position in the metadata on and off, and a person's or car's own panel can leave it out of every
  camera's analytics ("Seen by analytics"), so it only appears in the picture.
- **World**: sun, sky colours, fog, terrain size, and what the camera pictures show of the other
  cameras: their bodies, and optionally the wire frame of where they look.
- **Worlds**: the header menu creates, opens, trashes and restores worlds; see [Worlds](#worlds).

ONVIF clients' changes (a new resolution, a preset, where a PTZ camera stopped) show up in the editor
too. `npm run dev` in `src/OnvifSim.Web` serves the editor with hot reload against a simulator on
port 18180; `npm run e2e` runs the browser test.

| Endpoint | |
|---|---|
| `GET /api/world`, `PUT /api/world` | the active world as JSON |
| `GET /api/worlds`, `POST /api/worlds`, `POST /api/worlds/{name}/activate`, `POST /api/worlds/{name}/trash`, `POST /api/trash/{name}/restore`, `DELETE /api/trash` | the world library; see [Worlds](#worlds) |
| `PUT`, `DELETE /api/objects/{id}`, `/api/roads/{id}`, `/api/walkers/{id}`, `/api/vehicles/{id}`, `/api/cameras/{id}` | add, replace or remove one object, road, walker, moving car or camera |
| `PUT /api/terrain`, `PUT /api/environment` | the terrain or the sun, sky and fog |
| `POST /api/cameras/{id}/ptz/move`, `goto`, `stop`, `home` | drive a PTZ head |
| `GET /api/events` | server-sent events: `world` (the active world), `worlds` (the library), `world-switched`, `status` (ONVIF and RTSP ports, H.264) and `ptz` (head positions) |
| `GET /api/cameras` | camera list |
| `GET /api/cameras/{id}/snapshot.jpg` | one rendered frame |
| `GET /api/cameras/{id}/mjpeg` | live Motion JPEG (open it in a browser) |
| `GET /api/cameras/{id}/analytics` | server-sent events: the people and cars the camera's analytics sees, five times a second, each with its `kind`, `objectId` and where it is on the ground (`x`, `z`, `heading`) |
| `GET /api/cameras/{id}/pose` | ground truth: where the camera stands (absolute, terrain included) and looks (heading and pitch with the PTZ head, the picture's own axes `forward`, `right`, `up`, field of view from the zoom) |
| `POST /api/cameras/{id}/ptz/aim` | turn a PTZ head until a point of the world (`x`, `y`, `z`, absolute) is in the middle of its picture; answers the pan and tilt, or 409 beyond the head's limits |
| `GET /api/walkers/{id}/pose`, `/api/vehicles/{id}/pose` | ground truth: where a walker or a car is at this moment |
| `GET /api/cameras/{id}/recording` | the camera's own recording: whether it records, whether it is online, its retention and the spans it holds |
| `PUT /api/cameras/{id}/recording` | `{"recording": false}` stops the camera's own recording, `true` starts it again |
| `PUT /api/cameras/{id}/network` | `{"online": false}` cuts the camera off the network (it goes on recording), `true` brings it back |

A change the simulator cannot run with (an unknown object type, a frame size that is not a
multiple of 8, two cameras on one port…) is refused with 400 and the reason.

## RTSP

Each stream is an ONVIF profile and an RTSP path. A new camera starts with `Profile_1` (JPEG) at
`/<camera id>` and `Profile_2` (H.264) at `/<camera id>/h264`; the camera panel adds and removes
streams and edits their paths. Paths are unique across the world, so `rtsp://<host>:<port>/<path>`
names exactly one stream, and a stream is served only on its camera's RTSP port (`0` keeps the global
`--rtsp-port`). A camera may bind its ONVIF listener and RTSP port to one address; a specific address
cannot share a port with a wildcard listener. RTP and RTCP stay on the one shared UDP pair.

Every camera streams Motion JPEG (RTP/JPEG, RFC 2435) at `rtsp://<host>:8554/<camera id>`:

```bash
ffplay rtsp://admin:admin@localhost:8554/cam1                       # RTP over UDP
ffplay -rtsp_transport tcp rtsp://admin:admin@localhost:8554/cam2   # RTP interleaved in RTSP/TCP
```

RTP goes over UDP or interleaved in the RTSP connection, whichever the client asks for. Credentials
are the camera's `users` in the active world file (Digest or Basic; the default world has `admin`/`admin`); a
camera with no users accepts anyone. Resolution, frame rate and JPEG quality are per camera
(`video` in the active world file, 640×360 at 10 fps by default).

ffmpeg-based players need about 20 frames to start (they measure the frame rate first): ~2 s at the
default 10 fps, ~7 s at 3 fps. VLC and GStreamer show the first frame at once.

### Metadata

Every stream of a camera carries a second track, the ONVIF metadata stream (`m=application`,
`vnd.onvif.metadata/90000`, `{stream}/track2`), as cameras with analytics do. Each document is a
`tt:MetadataStream` with a scene frame: every person and moving car the camera sees is a
`tt:Object` with an `ObjectId` that stays the same while it stays in view, a `BoundingBox` and
`CenterOfGravity` in ONVIF's normalized frame coordinates (-1..1, y up) and the class `Human` or
`Vehicle`. A document goes out with every look of the analytics (five a second) while something is
in view, one more when the last object leaves, and once a second otherwise. With `ptzStatus` on, a
PTZ camera adds its `tt:PTZStatus`.

The boxes are where the picture draws the objects, and one hidden behind a house, a tree or a hill is
not reported; a figure shorter than 2.5% of the frame is too small to detect. Clients that do not
want the track simply do not set it up; ffmpeg shows it as a data stream (`-map 0:d`).

### H.264

When an ffmpeg with libx264 is on `PATH` (or named with `--ffmpeg <path>`, `ONVIFSIM_FFMPEG`), every
H.264 stream is served (RTP, RFC 6184) at its path, by default
`rtsp://<host>:8554/<camera id>/h264`, with settings of its own (`h264` in the stream, 640×360, 10 fps,
1024 kbit/s, a key frame every 10 frames by default). One ffmpeg process per stream runs while someone
watches; a new viewer starts at the next key frame. Without ffmpeg (or with `--ffmpeg none`) H.264
streams stay in the world but are hidden from ONVIF profiles and RTSP: cameras stream JPEG only.

```bash
ffplay -rtsp_transport tcp rtsp://admin:admin@localhost:8554/cam1/h264
```

### Replay

`rtsp://<host>:8554/<camera id>/replay` is the camera's own recording (Profile G, see below), as
`GetReplayUri` hands it out: ONVIF replay (ONVIF Streaming Specification, 6), `Require: onvif-replay`,
`Range: clock=<start>-<end>` and `Rate-Control: no` for as fast as the client takes it. The first frame
is the key frame at or before the start asked for, as on a real card: the recording keys every GOV
length frames from where it began, so a start between two key frames gets up to a GOP of footage from
before it, and only the replay header says how much. RTP time runs with recording time and jumps across a
hole in the recording, with a new key frame after it; every packet carries the replay header extension
(0xABAC: the frame's NTP time and the C, E, D and T flags). When the range or the recording ends, the
server closes the connection. H.264 when ffmpeg is there, JPEG otherwise.

## ONVIF

Every camera is a device of its own, on its own port: `http://<host>:8001/onvif/device_service` for
the first camera, 8002 for the second and so on (`onvifPort` in the active world file pins one). Each answers
WS-Discovery, so ONVIF Device Manager, an NVR or `OnvifLib.Probe --discovery` lists them all.

- **Device**: device information, clock, `GetServices`/`GetCapabilities`, scopes, users, a read-only
  view of the network settings.
- **Media** (ver10) and **Media2** (ver20): every stream is a profile (`Profile_1`, `Profile_2`, …),
  with its own encoder token (`VideoEncoder_N`), RTSP path and encoder settings, and the camera's
  snapshot URI. `SetVideoEncoderConfiguration` changes resolution, frame rate, JPEG quality, H.264
  bitrate and GOV length within the offered options and is saved to that stream in the active world
  file. Streams are managed in the editor: ONVIF `CreateProfile` and `DeleteProfile` remain
  unsupported, and a deleted stream's token answers `NoProfile`. Without ffmpeg, H.264 streams are
  left out of the profiles. All profiles share the metadata configuration `Metadata_1`
  (`SetMetadataConfiguration` turns analytics and the PTZ status on and off, saved as `metadata` in the
  active world file) and the analytics configuration `VideoAnalytics_1`.
- A camera's `bindAddress`, `rtspPort` and `streams` in the active world file hold its listener address,
  its own RTSP port and its streams; `streams` absent means the legacy `Profile_1`/`Profile_2` pair.
  Each stream has a token, a name, a codec, an RTSP path and its codec's settings.
- **PTZ** (ver20, PTZ cameras only): AbsoluteMove, RelativeMove, ContinuousMove, Stop and
  GetStatus in the generic spaces, presets and a home position. The head moves at the camera's top
  speeds (`ptz` in the active world file), the stream shows it turning, and where it stops, its presets and
  its home are saved to the active world file.
- **Imaging** (ver20): brightness, contrast and colour saturation, applied to the picture and saved.
- **Events** (ver10): pull point subscriptions (`CreatePullPointSubscription`, `PullMessages`,
  `SetSynchronizationPoint`, `Renew`, `Unsubscribe`, topic filters in the ConcreteSet dialect). A
  camera raises motion on `tns1:VideoSource/MotionAlarm` and `tns1:RuleEngine/CellMotionDetector/Motion`
  as soon as its analytics sees someone or a car, and clears it a second after the last object is
  gone; a new subscription first gets the current state. A subscription lives for the time it asked for (a minute
  by default), renewed by `Renew` and by every pull; a camera keeps at most 10.

- **Recording, Search, Replay** (Profile G): the camera's own recording, as on a camera with an SD
  card. Every camera records from the moment it is in the world, with one recording (`Recording_1`,
  one video track) and one job (`RecordingJob_1`); `SetRecordingJobMode` Idle and Active stop and
  start it, and its `MaximumRetentionTime` (`recordingRetentionHours` in the active world file, 1 hour
  by default) is how far back the card reaches before it records over the oldest footage.
  `FindRecordings` (with the usual `TrackType`/`DataFrom`/`DataTo` filter) returns everything at
  once, `GetRecordingInformation`, `GetRecordingSummary` and `GetReplayUri` the rest; recordings and
  tracks cannot be created or deleted. No frame is stored: the world is a function of time, so replay
  draws the past again, PTZ heads where they pointed then. When the camera recorded is kept in
  `recordings.json` beside the worlds directory; while the simulator is not running the cameras do not
  record. Edits to the world are not historised: a replay shows the past in today's world.
  `PUT /api/cameras/{id}/network` cuts a camera off (its ONVIF port drops requests, its streams stop,
  discovery leaves it out) while it goes on recording: the case a VMS recovers footage for.

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
change either; they are the installer's, in the active world file.

The **degree grid** (`angleGrid` in the active world file, a checkbox in the editor) draws pan/tilt lines over
a camera's picture, relative to the mount (pan 0, tilt 0 is where the camera points at rest), with
labels every 15° that get finer as the camera zooms in. The default world turns it on for the PTZ
camera.

## Ports

| Port | |
|---|---|
| 18180/tcp | editor and API (`--port`, `ONVIFSIM_PORT`) |
| 8001…/tcp | ONVIF, one port per camera (`--onvif-base-port`, `ONVIFSIM_ONVIF_BASE_PORT`) |
| 8554/tcp | RTSP for cameras that keep the global port (`--rtsp-port`, `ONVIFSIM_RTSP_PORT`) |
| camera `rtspPort` | a camera's own RTSP port, when the inspector sets one |
| 6970-6971/udp | RTP and RTCP of every UDP session (`--rtp-port`, `ONVIFSIM_RTP_PORT`) |
| 3702/udp | WS-Discovery (`--discovery-port`, `ONVIFSIM_DISCOVERY_PORT`; 0 turns it off) |

`--bench` prints render and JPEG timings per camera and resolution, and what drawing the other
cameras and their fields of view adds.

A camera's `bindAddress` moves its ONVIF listener and RTSP port to that interface; WS-Discovery then
advertises the same address, IPv6 in brackets. A specific address needs an explicit `rtspPort`, and it
cannot share a port with a wildcard listener.

## How it works

One .NET 10 process, no native dependencies: the world is rendered by a built-in CPU rasterizer
(a 640×360 frame takes about 5 ms), encoded to JPEG in-process, and a camera only renders while
someone is watching it, and then only when its picture changes: a still camera with nobody walking
in view sends the same frame again. H.264 is the one optional extra: raw frames piped through an ffmpeg process.
Licence plates are geometry too: the characters come from a 5×7 bitmap font and become small flat
quads on the plate, which is why the CPU renderer and the browser editor show the same text without
textures.

The world library lives in `WorldStore`. It keeps `worlds/`, the active marker and the trash, and
switches all go through it. A switch fires `Switched` before `Changed`: the services that must not
carry state from one scene into another (PTZ head motion, the cameras' offline flags, the recording
log when the user chose to clear it) reset first, then the scene is rebuilt and the ONVIF listeners
follow the new cameras. The editor receives `world-switched` before the new `world`, so its undo
history and pending edits cannot leak into the scene that has just opened.

The RTSP server and the RTP payload formats (JPEG, H.264, ONVIF metadata) are a library of their
own, [src/OnvifSim.Rtsp](src/OnvifSim.Rtsp), which knows nothing of the simulator: the cameras reach
it through one interface. `RtspHost` keeps one listener per (address, port) a camera asks for, all
sharing the one RTP/RTCP pair; `RtspEndpoints` tells the streams and the editor which port each camera
is on.

ONVIF requests and responses are (de)serialized with types generated from the official ONVIF WSDL
([onvif/specs](https://github.com/onvif/specs)); `src/OnvifSim.Server/Onvif/Wsdl/generate.sh`
regenerates them.

## License

MIT
