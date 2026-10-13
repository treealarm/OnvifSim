# Changelog

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
