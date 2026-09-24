#!/usr/bin/env sh
# Native AOT builds of the GUI and CLI for one runtime (must run on that OS; macOS can cross x64/arm64).
#   ./publish.sh [rid=win-x64] [version]
set -e
rid="${1:-win-x64}"
for p in Gui Cli; do
  dotnet publish "src/SubMatcher.$p" -c Release -r "$rid" -p:DebugType=none ${2:+-p:Version=$2} -o "publish/$rid"
done
rm -rf "publish/$rid"/*.pdb "publish/$rid"/*.dbg "publish/$rid"/*.dSYM
echo "→ publish/$rid  (portable: ffmpeg/ffprobe go next to the executables or on PATH)"
