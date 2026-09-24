<p align="center">
  <img src="src/SubMatcher.Gui/Assets/icon.png" width="96" alt="SubMatcher">
</p>

<h1 align="center">SubMatcher</h1>

<p align="center">用画面给字幕调轴，视频版的 <a href="https://github.com/tp7/Sushi">Sushi</a>。</p>

<p align="center">
  <a href="https://github.com/zzzwannasleep/submatcher/releases/latest"><img src="https://img.shields.io/github/v/release/zzzwannasleep/submatcher?style=flat-square" alt="release"></a>
  <a href="https://github.com/zzzwannasleep/submatcher/releases"><img src="https://img.shields.io/github/downloads/zzzwannasleep/submatcher/total?style=flat-square" alt="downloads"></a>
  <a href="https://github.com/zzzwannasleep/submatcher/actions/workflows/release.yml"><img src="https://img.shields.io/github/actions/workflow/status/zzzwannasleep/submatcher/release.yml?style=flat-square" alt="build"></a>
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue?style=flat-square" alt="platform">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" alt=".NET 10">
</p>

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/screenshot-dark.png">
    <img src="docs/screenshot-light.png" width="860" alt="screenshot">
  </picture>
</p>

TV / Web 的字幕搬到 BD 上，时间轴对不上。Sushi 靠音频对齐，SubMatcher 靠画面：把每行字幕出现时的画面拿到新片源里找，找到哪就挪到哪。台标、分辨率、调色不同都不影响，音轨重混或者干脆没有音轨也照样能用。

## 下载

去 [Releases](https://github.com/zzzwannasleep/submatcher/releases/latest) 下对应平台的 zip，解压就能用。带 `-ffmpeg` 的自带 ffmpeg，一般下这个。

macOS 第一次打开被拦的话，对解压出来的文件夹执行 `xattr -cr`。

## 用法

把源视频、源字幕、目标视频拖进窗口，点「开始调轴」。字幕输出到目标视频旁边，没把握的行会标出来，可以对着画面逐行微调。重新作画或者裁切过的镜头找不到对应画面，也会标出来。

整季批量处理、手动平移、转帧率、转编码在另外两个标签页里。

命令行：

```sh
submatcher-cli sync "[TV] 01.mkv" "[BD] 01.mkv" --sub "[TV] 01.ass"
submatcher-cli batch ./tv ./bd
```

全部参数见 `submatcher-cli --help`。

## 构建

```sh
dotnet test
./publish.sh win-x64    # 或 linux-x64 / osx-arm64 / osx-x64
```

## 致谢

[Sushi](https://github.com/tp7/Sushi) · [ACGRIP BBS](https://bbs.acgrip.com/thread-1936-1-1.html) · [Semi.Avalonia](https://github.com/irihitech/Semi.Avalonia) · [FFmpeg](https://ffmpeg.org)
