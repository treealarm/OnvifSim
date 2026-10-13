#!/usr/bin/env bash
# Fills payload/ for snapcraft.yaml from the linux-x64 release zip or the directory it unpacks to.
#
#   ./stage.sh <OnvifSim-linux-x64.zip | directory> <version>
#
# Result (gitignored): payload/app (the server, wwwroot/ and ffmpeg/) and payload/version.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
src=${1:?usage: stage.sh <zip-or-directory> <version>}
version=${2:?usage: stage.sh <zip-or-directory> <version>}
payload=$here/payload

# Snap versions: at most 32 letters, digits and . + ~ : -, starting and ending with a letter or digit.
if ! [[ $version =~ ^[A-Za-z0-9][A-Za-z0-9.+~:-]{0,30}[A-Za-z0-9+~]?$ ]]; then
  echo "stage.sh: '$version' is not a valid snap version" >&2
  exit 1
fi

rm -rf "$payload"
mkdir -p "$payload"
if [ -d "$src" ]; then
  cp -a "$src/." "$payload/app"
else
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  unzip -q "$src" -d "$tmp"
  # The zip holds one top-level directory (OnvifSim-linux-x64); take the one with the server.
  app=$(find "$tmp" -maxdepth 2 -type f -name OnvifSim -printf '%h\n' | head -n1)
  test -n "$app" || { echo "stage.sh: no OnvifSim executable inside $src" >&2; exit 1; }
  mv "$app" "$payload/app"
fi

test -f "$payload/app/wwwroot/index.html" || { echo "stage.sh: $payload/app has no editor (wwwroot)" >&2; exit 1; }
test -f "$payload/app/ffmpeg/ffmpeg" || { echo "stage.sh: ffmpeg/ffmpeg is missing; the snap must bundle it" >&2; exit 1; }
chmod +x "$payload/app/OnvifSim" "$payload/app/ffmpeg/ffmpeg"
printf '%s\n' "$version" > "$payload/version"
echo "staged $(du -sh "$payload/app" | cut -f1) into $payload (version $version)"
