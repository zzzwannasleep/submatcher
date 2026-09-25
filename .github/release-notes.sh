#!/usr/bin/env sh
# Release notes = every commit since the previous version tag.
#   sh .github/release-notes.sh <tag> [commit=tag]
set -e
tag=$1; ref=${2:-$1}
repo=${GITHUB_REPOSITORY:-zzzwannasleep/submatcher}
prev=$( (git tag -l 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$'; echo "$tag") | sort -Vu | sed -n "/^$tag\$/{x;p;q};h")

echo "## 更新内容"
echo
log=$(git log --no-merges --format='### %s%n%n%b' ${prev:+$prev..}$ref | grep -v '^Co-Authored-By:')
if [ -n "$log" ]; then echo "$log"; else echo "无代码改动（重新构建）。"; fi
echo
echo "## 下载"
echo
echo '解压即用。带 `-ffmpeg` 的自带 ffmpeg，一般下这个；已经在用的话，在「设置 → 检查更新」里也能直接更新。'
echo
echo '`win-x64` · `linux-x64` · `osx-arm64`（M 系列 Mac）· `osx-x64`（Intel Mac）。macOS 第一次打开被拦的话，对解压出来的文件夹执行 `xattr -cr`。'
[ -n "$prev" ] && { echo; echo "**完整对比**: https://github.com/$repo/compare/$prev...$tag"; }
exit 0
