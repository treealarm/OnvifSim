# Changelog

## Unreleased

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
