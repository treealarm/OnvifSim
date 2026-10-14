# Changelog

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
