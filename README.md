# OnvifSim

A virtual world whose cameras are ONVIF devices. Build a low-poly landscape (terrain, trees,
houses) in the browser, put fixed and PTZ cameras into it, and every camera answers WS-Discovery,
serves RTSP and takes PTZ commands — including AbsoluteMove — like real hardware. Point ONVIF
Device Manager, an NVR or your own client at it; no cameras needed.

> **Status: early development.** Done so far: world model, software renderer, snapshots and MJPEG
> over HTTP, RTSP with RTP/JPEG. Coming next: H.264, ONVIF Device/Media/PTZ, WS-Discovery, the
> browser editor, Docker image and installers (MSI, winget, snap).

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

| Port | |
|---|---|
| 8554/tcp | RTSP for all cameras (`--rtsp-port`, `ONVIFSIM_RTSP_PORT`) |
| 6970-6971/udp | RTP and RTCP of every UDP session (`--rtp-port`, `ONVIFSIM_RTP_PORT`) |

ffmpeg-based players need about 20 frames to start (they measure the frame rate first): ~2 s at the
default 10 fps, ~7 s at 3 fps. VLC and GStreamer show the first frame at once.

`--bench` prints render and JPEG timings per camera and resolution.

## How it works

One .NET 10 process, no native dependencies: the world is rendered by a built-in CPU rasterizer
(a 640×360 frame takes about 5 ms), encoded to JPEG in-process, and a camera only renders while
someone is watching it.

## License

MIT
