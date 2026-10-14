# winget

Manifests for [winget-pkgs](https://github.com/microsoft/winget-pkgs), installing the WiX MSI from
GitHub Releases (`InstallerType: wix`) that the **msi** job of `.github/workflows/release.yml` builds.

Current package: **treealarm.OnvifSim** `0.2.1` → tag `v0.2.1` → `OnvifSim-win-x64.msi`.

## Validate on Windows

```powershell
winget validate --manifest packaging\winget
winget install --manifest packaging\winget
```

## Submit

The three files go to `manifests/t/treealarm/OnvifSim/<version>/` of a winget-pkgs fork (one folder per
segment of the identifier), and a pull request from there. Title: `New version: treealarm.OnvifSim
version X.Y.Z` (`New package:` the first time).

## Bump for a new vX.Y.Z tag

1. Set `PackageVersion` to `X.Y.Z` in all three files.
2. Point `InstallerUrl` and `ReleaseNotesUrl` at the tag, and set `ReleaseDate`.
3. Set `InstallerSha256` (uppercase) from the release asset's `digest` in the GitHub API, or
   `Get-FileHash` of the downloaded MSI.
4. Keep `ManifestVersion` (and the `$schema` comments) at what recently merged pull requests in
   winget-pkgs use: their validation turns older schema versions away.
