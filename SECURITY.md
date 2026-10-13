# Security policy

Applies to **OnvifSim** (GitHub Releases, the MSI, the Docker image, the snap and the winget package).

## Use on trusted networks

OnvifSim is a test bench. **The editor and its API have no authentication**: anyone who can reach
port 8080 can change the world, add cameras or turn them. The simulated cameras check the user
names and passwords set in the world, as real cameras do, but those are stored in plain text
([PRIVACY.md](PRIVACY.md)). Run it on a machine or network you control, and do not expose its ports
to the internet.

## Supported versions

Fixes go into the latest release only. Please check that a problem still reproduces on the newest
version before reporting.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately through GitHub:
[**Report a vulnerability**](https://github.com/treealarm/OnvifSim/security/advisories/new)
(repository → **Security** tab → **Report a vulnerability**). Only the maintainers can see the report.

Useful to include: the version and how it was installed, the operating system, steps to reproduce or
a proof of concept, and what an attacker could gain.

We will acknowledge the report, keep you updated in the advisory, and credit you in it when it is
published, unless you prefer to stay anonymous.

## Scope

In scope, for example:

- malformed SOAP, RTSP, WS-Discovery or editor requests causing code execution, reading or writing
  files outside the data directory, or a crash that corrupts `world.json`;
- the camera credentials check (WS-UsernameToken, HTTP Digest and Basic, RTSP) letting a client in
  without the right password;
- OnvifSim connecting anywhere other than described in [PRIVACY.md](PRIVACY.md#network-communication);
- the release workflow accepting an ffmpeg archive whose hash does not match.

Out of scope: the editor having no authentication (above), plain HTTP and RTSP (as on most cameras),
and vulnerabilities in ffmpeg itself, which are for [FFmpeg](https://ffmpeg.org/security.html).
