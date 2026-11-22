#!/bin/sh
# Builds RefDump. Run it from an MSYS2 UCRT64 shell (the same tools as PianoBooster itself,
# see PianoBooster/BUILD.md). The executable is build/refdump.exe.
set -e
cd "$(dirname "$0")"
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
