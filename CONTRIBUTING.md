# Contributing

Issues and pull requests are welcome. For anything bigger than a fix, open an issue first so we can
agree on the approach.

## Build and test

You need the .NET 10 SDK and Node.js 20.19 or later (for the editor); ffmpeg with libx264 on PATH
turns on the H.264 tests, which are skipped without it.

```bash
dotnet build OnvifSim.slnx          # also builds the editor through npm
dotnet test tests/OnvifSim.Tests    # server: ONVIF, RTSP, PTZ, rendering, the editor API
cd src/OnvifSim.Web
npx playwright install chromium     # once
npm run e2e                         # the editor in a real browser against a real simulator
npm run dev                         # the editor with hot reload, against a simulator on port 8080
```

Check a change against real clients too when it touches what they see: ONVIF Device Manager or
[OnvifLib.Probe](https://github.com/treealarm/OnvifLib/tree/main/samples/OnvifLib.Probe) for ONVIF,
ffplay, VLC or GStreamer for RTSP.

## Code

- Match the code around you: plain, readable code with names that say what things are, comments that
  explain why rather than what, no abstractions without a present need.
- Tests describe behaviour (`Scenario_Outcome`), are deterministic (inject `TimeProvider`, use port 0),
  and come with a fix: reproduce the bug first.
- Commit messages in the imperative, the first line under about 70 characters, the body saying why.
- ONVIF types come from the official WSDL: run `src/OnvifSim.Server/Onvif/Wsdl/generate.sh` rather
  than editing `OnvifContracts.cs`.

By contributing you agree that your work is released under the [MIT license](LICENSE).
