#!/bin/sh
# Builds fbxprobe against the Autodesk FBX SDK.
#
# Point FBXSDK at an unpacked SDK; the default is where the Linux download puts
# itself. Linked statically on purpose: the shared object asks for versioned
# libxml2 symbols that a current system libxml2 does not carry, and the static
# archive references them unversioned.
set -e

FBXSDK="${FBXSDK:-$HOME/Dev/fbx202039_fbxsdk_linux}"
OUT="${1:-$(dirname "$0")/fbxprobe}"

test -f "$FBXSDK/lib/release/libfbxsdk.a" || {
    echo "no FBX SDK at $FBXSDK -- set FBXSDK to an unpacked one" >&2
    exit 1
}

g++ -std=c++17 -O1 -o "$OUT" "$(dirname "$0")/fbxprobe.cpp" \
    -I"$FBXSDK/include" "$FBXSDK/lib/release/libfbxsdk.a" \
    -lxml2 -lz -ldl -lpthread 2>&1 | grep -v 'tempnam\|warning:' || true

test -x "$OUT" && echo "built $OUT"
