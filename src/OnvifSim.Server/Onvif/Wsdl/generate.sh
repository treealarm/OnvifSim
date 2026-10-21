#!/usr/bin/env bash
# Regenerates OnvifContracts.cs from the official ONVIF WSDL (github.com/onvif/specs) with
# dotnet-svcutil: XmlSerializer types, every operation as a request/response message contract.
# Schema types (tt:) go to OnvifSim.Onvif.Wsdl, each service's contract and messages to a namespace of
# its own. Needs network: the ONVIF schemas import W3C and OASIS ones by URL.
set -euo pipefail

SPECS_TAG=26.06
SVCUTIL_VERSION=8.0.0

here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

curl -fsSL "https://github.com/onvif/specs/archive/refs/tags/$SPECS_TAG.tar.gz" | tar xz -C "$work"
wsdl="$work/specs-$SPECS_TAG/wsdl"
cp "$here/onvifsim.wsdl" "$wsdl/"

# `dotnet dnx` mangles svcutil's repeated -n options, so the tool is installed into the work folder.
# It targets an older runtime than the SDK's; roll forward to the installed one.
dotnet tool install dotnet-svcutil --version "$SVCUTIL_VERSION" --tool-path "$work/tools" >/dev/null
export DOTNET_ROLL_FORWARD=Major
# svcutil works inside a project and adds package references to it, so give it a throwaway one.
dotnet new classlib -o "$work/proj" >/dev/null
cd "$work/proj"
"$work/tools/dotnet-svcutil" "$wsdl/onvifsim.wsdl" \
    -ser XmlSerializer -mc -ct System.Array \
    -n "*,OnvifSim.Onvif.Wsdl" \
    -n "http://www.onvif.org/ver10/device/wsdl,OnvifSim.Onvif.Wsdl.Device" \
    -n "http://www.onvif.org/ver10/media/wsdl,OnvifSim.Onvif.Wsdl.Media" \
    -n "http://www.onvif.org/ver20/media/wsdl,OnvifSim.Onvif.Wsdl.Media2" \
    -n "http://www.onvif.org/ver20/ptz/wsdl,OnvifSim.Onvif.Wsdl.Ptz" \
    -n "http://www.onvif.org/ver20/imaging/wsdl,OnvifSim.Onvif.Wsdl.Imaging" \
    -n "http://www.onvif.org/ver10/events/wsdl,OnvifSim.Onvif.Wsdl.Events" \
    -n "http://www.onvif.org/ver10/recording/wsdl,OnvifSim.Onvif.Wsdl.Recording" \
    -n "http://www.onvif.org/ver10/search/wsdl,OnvifSim.Onvif.Wsdl.Search" \
    -n "http://www.onvif.org/ver10/replay/wsdl,OnvifSim.Onvif.Wsdl.Replay" \
    -n "http://docs.oasis-open.org/wsn/bw-2,OnvifSim.Onvif.Wsdl.Wsn" \
    -tf net10.0 -nl -d out -o OnvifContracts.cs >svcutil.log 2>&1 || true
# svcutil exits non-zero on schema warnings too (the ONVIF schemas give plenty); only a missing file is
# a failure.
[ -f out/OnvifContracts.cs ] || { cat svcutil.log; exit 1; }

cp out/OnvifContracts.cs "$here/OnvifContracts.cs"
echo "Wrote $here/OnvifContracts.cs (ONVIF specs $SPECS_TAG, dotnet-svcutil $SVCUTIL_VERSION)"
