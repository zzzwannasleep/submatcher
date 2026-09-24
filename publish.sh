#!/usr/bin/env sh
# Self-contained single-file builds of the GUI and CLI for one runtime (default win-x64).
set -e
rid="${1:-win-x64}"
for p in Gui Cli; do
  dotnet publish "src/SubMatcher.$p" -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "publish/$rid"
done
echo "→ publish/$rid  (put ffmpeg/ffprobe next to the executables or on PATH)"
