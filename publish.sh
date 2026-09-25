#!/usr/bin/env sh
# Native AOT builds of the GUI and CLI for one runtime (must run on that OS; macOS can cross x64/arm64).
# TG_API_ID / TG_API_HASH in the environment are baked in for the Telegram subtitle download (CI: repo secrets).
#   ./publish.sh [rid=win-x64] [version]
set -e
rid="${1:-win-x64}"
for p in Gui Cli; do
  dotnet publish "src/SubMatcher.$p" -c Release -r "$rid" -p:DebugType=none ${2:+-p:Version=$2} \
    ${TG_API_ID:+-p:TgApiId=$TG_API_ID -p:TgApiHash=$TG_API_HASH} -o "publish/$rid"
done
rm -rf "publish/$rid"/*.pdb "publish/$rid"/*.dbg "publish/$rid"/*.dSYM
echo "→ publish/$rid  (portable: ffmpeg/ffprobe go next to the executables or on PATH)"
