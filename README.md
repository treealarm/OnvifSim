# OnvifSim

A virtual world whose cameras are ONVIF devices. Build a low-poly landscape (terrain, trees,
houses) in the browser, put fixed and PTZ cameras into it, and every camera answers WS-Discovery,
serves RTSP and takes PTZ commands — including AbsoluteMove — like real hardware. Point ONVIF
Device Manager, an NVR or your own client at it; no cameras needed.

> **Status: early development.** Done so far: world model, software renderer, snapshots and MJPEG
> over HTTP. Coming next: RTSP, ONVIF Device/Media/PTZ, WS-Discovery, the browser editor, Docker
> image and installers (MSI, winget, snap).

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

`--bench` prints render and JPEG timings per camera and resolution.

## How it works

One .NET 10 process, no native dependencies: the world is rendered by a built-in CPU rasterizer
(a 640×360 frame takes about 5 ms), encoded to JPEG in-process, and a camera only renders while
someone is watching it.

## License

MIT
