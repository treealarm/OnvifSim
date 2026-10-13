# OnvifSim

A virtual world whose cameras are ONVIF devices. Build a low-poly landscape (terrain, trees,
houses) in the browser, put fixed and PTZ cameras into it, and every camera answers WS-Discovery,
serves RTSP and takes PTZ commands — including AbsoluteMove — like real hardware. Point ONVIF
Device Manager, an NVR or your own client at it; no cameras needed.

> **Status: early development.** Done so far: world model, software renderer, snapshots and MJPEG
> over HTTP, RTSP with RTP/JPEG and H.264, ONVIF Device + Media + Media2 + PTZ + Imaging and
> WS-Discovery. Coming next: the browser editor, Docker image and installers (MSI, winget, snap).

## Run

```bash
dotnet run -c Release --project src/OnvifSim.Server -- --data ./data --port 8080
```

On first start a default world is written to `./data/world.json`.

| Endpoint | |
|---|---|
| `GET /api/world`, `PUT /api/world` | the world as JSON |
| `GET /api/cameras` | camera list |
| `GET /api/cameras/{id}/snapshot.jpg` | one rendered frame |
| `GET /api/cameras/{id}/mjpeg` | live Motion JPEG (open it in a browser) |

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

`angleGrid: true` on a camera in `world.json` draws a pan/tilt degree grid over its picture, relative
to the mount (pan 0, tilt 0 is where the camera points at rest), with labels every 15° that get
finer as the camera zooms in. The default world turns it on for the PTZ camera.
- **Authentication** as on real cameras: WS-Security UsernameToken or HTTP Digest/Basic with the
  camera's `users`; `GetSystemDateAndTime`, `GetCapabilities`, `GetServices` and `GetHostname` answer
  without credentials.

XAddrs and stream URIs use the address the client connected to. WS-Discovery advertises the address
of the route towards the client, or `--advertise-host` (`ONVIFSIM_ADVERTISE_HOST`) when that is not
reachable from outside, e.g. in a container.

## Ports

| Port | |
|---|---|
| 8080/tcp | web UI and API (`--port`, `ONVIFSIM_PORT`) |
| 8001…/tcp | ONVIF, one port per camera (`--onvif-base-port`, `ONVIFSIM_ONVIF_BASE_PORT`) |
| 8554/tcp | RTSP for all cameras (`--rtsp-port`, `ONVIFSIM_RTSP_PORT`) |
| 6970-6971/udp | RTP and RTCP of every UDP session (`--rtp-port`, `ONVIFSIM_RTP_PORT`) |
| 3702/udp | WS-Discovery (`--discovery-port`, `ONVIFSIM_DISCOVERY_PORT`; 0 turns it off) |

`--bench` prints render and JPEG timings per camera and resolution.

## How it works

One .NET 10 process, no native dependencies: the world is rendered by a built-in CPU rasterizer
(a 640×360 frame takes about 5 ms), encoded to JPEG in-process, and a camera only renders while
someone is watching it. H.264 is the one optional extra: raw frames piped through an ffmpeg process.

ONVIF requests and responses are (de)serialized with types generated from the official ONVIF WSDL
([onvif/specs](https://github.com/onvif/specs)); `src/OnvifSim.Server/Onvif/Wsdl/generate.sh`
regenerates them.

## License

MIT
