#!/usr/bin/env sh
# Self-contained single-file builds of the GUI and CLI for one runtime.
#   ./publish.sh [rid=win-x64] [version]
set -e
rid="${1:-win-x64}"
for p in Gui Cli; do
  dotnet publish "src/SubMatcher.$p" -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none \
    ${2:+-p:Version=$2} -o "publish/$rid"
done
echo "→ publish/$rid  (portable: ffmpeg/ffprobe go next to the executables or on PATH)"
