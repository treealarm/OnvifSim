# Privacy policy

Applies to **OnvifSim** in every form it is published in: the GitHub Releases (zips and the MSI),
the Docker image `ghcr.io/treealarm/onvifsim`, the snap `onvifsim` and the winget package
`treealarm.OnvifSim`.

Last updated: 2026-10-13.

## Summary

- OnvifSim has **no telemetry, no analytics, no crash reporting, no accounts and no update check**.
  The developers never receive any data from it.
- It **only answers**: it serves the editor, ONVIF, RTSP and WS-Discovery to whoever connects to it,
  and connects to nothing on the internet.
- Everything it keeps is the **world file on your own disk**.

## What it keeps

| Data | Where | Notes |
|---|---|---|
| The world: terrain, objects, roads, people, cameras and their settings | `world.json` in the data directory: `%LOCALAPPDATA%\OnvifSim` on Windows, `~/.local/share/OnvifSim` on Linux, `/data` in Docker, `/var/snap/onvifsim/common` in the snap, or wherever `--data` points | Written when you edit the world, and when an ONVIF client changes a camera (encoder settings, presets, where a PTZ camera stopped) |
| The simulated cameras' user names and passwords | The same `world.json`, **in plain text** | They protect simulated cameras only; do not reuse real passwords there |

Pictures are rendered on demand and streamed; nothing is recorded.

## Network communication

OnvifSim listens on its ports (editor 18180, ONVIF 8001 and up, RTSP 8554, RTP 6970-6971, WS-Discovery
3702; see the README) and answers what arrives there. The only messages it starts itself are
WS-Discovery Hello and Bye announcements, sent to the local multicast group 239.255.255.250 when
cameras appear and disappear. The browser editor is served by OnvifSim and loads nothing from
elsewhere.

The bundled ffmpeg runs as a local process that turns frames into H.264; it makes no network
connections.

## Contact

Questions: [GitHub issues](https://github.com/treealarm/OnvifSim/issues).
