# Changelog

## Unreleased

- **Worlds you prepare and switch between.** The data directory now holds any number of named worlds
  (`worlds/<name>.json`), an `active-world.json` marker and a `trash/`. The editor's world manager
  creates an empty world, opens another one without a restart, moves worlds to the trash, restores
  them and empties the trash; an existing `world.json` becomes `worlds/default.json` on the first
  start. Switching clears the editor's undo history and asks whether to clear or keep the cameras'
  recordings; the server requires that choice when the outgoing world has recordings. The library is
  also available over HTTP (`/api/worlds`, `/api/trash`) and as `worlds` and `world-switched` events
  on `/api/events`.
- **A contents tree** on the left lists every camera, object, road, person and moving car and selects
  them with a click; objects are grouped by type, with their ids inside. A person now also has a
  larger invisible pick target in the 3D view, so they can be clicked there too.
- **A grouped toolbar**: Select, Add, Draw and Terrain choose which tools the second row offers; the
  active tool is marked with a dot, so changing the palette does not change what the viewport does.
- **Cars with licence plates**, for testing LPR clients: a new `car` object draws a low-poly car with
  real glyph geometry on its plates (5×7 bitmap font, no textures), and the inspector edits the plate
  text. The default village now includes a car in view of its fixed camera.
- **Cars that drive a route.** A `vehicle` is a moving car with a route, speed, loop-or-back
  behaviour, body size, paint and plate, drawn from the same mesh as a parked car. The editor's
  **Car route** tool draws its route point by point, and the car turns to follow it; its position
  follows from the clock like a walker's, so every stream, snapshot and replay agrees. Cameras'
  analytics box it with the class `Vehicle` in the ONVIF metadata track and raise motion while it is
  in view; `vehicles` is a list in the world file, with `/api/vehicles/{id}` and a ground-truth
  `/api/vehicles/{id}/pose`. The route rule is shared with walkers (`RouteMotion`). A parked car's
  panel has **Make it drive**, which turns it into a moving car on a straight route along its heading
  and keeps its paint, size and plate; the conversion is one Undo step.
- **Documentation**: the README is now the complete English documentation; the outdated
  `docs/PLAN.md` was removed (git history keeps it), and the README gained screenshots of the world
  manager, the contents tree, the car's plate and a moving car's route.
- **Streams are ONVIF profiles, and can be added and removed.** A camera starts with `Profile_1`
  (JPEG) and `Profile_2` (H.264); the camera panel adds and removes streams, names them and edits
  each one's RTSP path and encoder settings. Encoder tokens follow the profile tokens, deleted
  streams answer `NoProfile`, and H.264 streams are hidden when ffmpeg is absent. The RTSP paths are
  unique across the world.
- **Editable camera addresses.** A camera can bind its ONVIF listener and RTSP port to one address
  and use an RTSP port of its own (`rtspPort`, 0 keeps the global `--rtsp-port`); WS-Discovery
  advertises the bound address. Validation refuses an invalid address, a bound camera without an
  explicit RTSP port, and a wildcard and a specific listener on the same port.
- **Changed:** a new camera keeps an hour of recording by default (`recordingRetentionHours` was 30
  days); existing worlds keep the value they store.
- Profile G: every camera records on its own "SD card" and offers it through the Recording, Search
  and Replay services, with ONVIF replay over RTSP (`rtsp://<host>:8554/<camera id>/replay`: clock
  ranges, `Rate-Control: no`, the replay RTP header extension). The past is drawn again rather than
  stored, PTZ heads where they pointed then; a recording reaches back `recordingRetentionHours` (1
  hour by default). `PUT /api/cameras/{id}/network` cuts a camera off the network while it goes on
  recording, and `PUT /api/cameras/{id}/recording` stops and starts its recording, for testing how a
  VMS recovers what it missed.
- **Fixed:** the last frame an H.264 encoder produced was lost when it stopped.
- Replay starts at the key frame at or before the start asked for, as a real card does, not at a key
  frame made for exactly that moment: a client that stamps footage from where it asked for it rather
  than from the replay header's time is now off by up to a GOP, as it would be on a real camera.

- Ground truth for testing clients that calibrate cameras or aim one at a person:
  `GET /api/cameras/{id}/pose` (where a camera stands and looks, PTZ head included) and
  `GET /api/walkers/{id}/pose`; the boxes in `GET /api/cameras/{id}/analytics` say where on the
  ground each walker stands. The pose also gives the picture's own axes (`forward`, `right`, `up`),
  and `POST /api/cameras/{id}/ptz/aim` turns a head until a point of the world is in the middle of
  its picture.
- Cameras mounted any way up. A mount is a rigid body: a PTZ head pans about the mount's own up axis
  and tilts about its own right axis, so `roll: 180` hangs a camera upside down, `roll: 90` puts it
  on its side (pan sweeps up and down, tilt across) and `pitch` leans its pan axis. **Changed:** a PTZ
  camera's `pitch` used to be added to its tilt and its `roll` turned only the picture; a world with a
  PTZ camera whose pitch or roll is not 0 now looks elsewhere. Level mounts are unchanged.
- `video.rotate` (0, 90, 180, 270): the picture turned clockwise, as a camera rotates its image for
  an upside-down or corridor mount; both streams and the analytics boxes turn with it, and Media
  reports it as the video source's `Rotate`.
- `ptz.panReverse`, `ptz.tiltReverse`: ONVIF counts that axis the other way round (positions, steps
  and velocities), as cameras with a reversed control direction do; with both on, the PTZ
  configuration reports `PTControlDirection` `Reverse` ON.
- Cameras appear in each other's pictures: a pole, a bracket and the body turned the way the camera
  (or its head) looks, in the camera's own `color` or else orange for PTZ and blue for fixed
  (`environment.showCameras`, on by default).
  `environment.showFrustums` (off by default: no real camera sees them) also draws where each looks,
  as a wire frame in the same colour from the eye down to the ground it covers, ending at `environment.frustumLength`
  (40 m) where a ray meets no ground. Both cost about a millisecond of a 640×360 frame (`--bench`
  now prints them). The editor draws the same wire frame down to the ground instead of a 20 m
  pyramid, and the degree grid follows a mount that leans or lies on its side.

## 0.2.2 (2026-10-14)

- Windows: OnvifSim.exe and the MSI carry the camera icon (Installed apps, the Start menu entry, the
  installer's own dialogs).
- The RTSP server and the RTP payload formats are a library of their own in the solution,
  `src/OnvifSim.Rtsp`; nothing changes for clients except that the RTCP CNAME is now the session id.

## 0.2.1 (2026-10-13)

- Snap: an OnvifSim entry in the applications menu (and an icon in App Center) that opens the editor
  in the browser.

## 0.2.0 (2026-10-13)

- Analytics: every camera reports the people it sees in the ONVIF metadata track of its RTSP
  streams, boxes with object ids exact to the picture, people hidden behind houses and hills left out;
  optionally the PTZ position. Media and Media2 serve the metadata and analytics configurations, and
  the editor boxes the people over each camera's picture. Analytics can be turned off per camera,
  and a person can be left out of it.
- Events: pull point subscriptions with motion events, raised while a camera's analytics sees someone.

## 0.1.0 (2026-10-13)

First release: an ONVIF IP camera simulator.

- A low-poly world (terrain, trees, houses, rocks, roads, people walking their routes) drawn by a
  built-in software renderer; no GPU needed.
- Every camera is an ONVIF device on its own port: Device, Media, Media2, PTZ and Imaging, WS-Security
  and HTTP Digest, WS-Discovery.
- RTSP for all cameras: Motion JPEG always, H.264 through ffmpeg when available; TCP and UDP.
- Fixed and PTZ cameras; PTZ heads move at their set speeds (AbsoluteMove, RelativeMove,
  ContinuousMove, presets, home) and the picture follows; an optional pan/tilt degree grid.
- A browser editor: place and move objects and cameras, sculpt the terrain, draw roads and routes,
  see each camera's picture, drive PTZ cameras, undo and redo; every change is live.
- Docker image (amd64, arm64), single-file binaries for Windows and Linux, MSI, snap.
