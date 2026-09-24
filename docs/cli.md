# 命令行

`submatcher-cli` 和桌面版在同一个压缩包里，功能一样，适合批处理和写脚本。

```sh
submatcher-cli <命令> [参数]
submatcher-cli --help
```

| 命令 | 作用 |
|---|---|
| [`sync`](#sync) | 按画面调轴 |
| [`batch`](#batch) | 两个文件夹整季调轴 |
| [`shift`](#shift) | 手动平移时间轴 |
| [`fps`](#fps) | 帧率转换 |
| [`encode`](#encode) | 字幕转 UTF-8 |
| [`subset`](#subset) | 字体子集化 |
| [`clear-cache`](#clear-cache) | 清空画面指纹缓存 |

## sync

```sh
submatcher-cli sync <源视频> <目标视频> [--sub 源字幕] [-o 输出] [选项]
```

源视频是字幕原本对应的那个（TV / Web），目标视频是要对上的那个（BD）。

```sh
# 最常用
submatcher-cli sync "[TV] 01.mkv" "[BD] 01.mkv" --sub "[TV] 01.sc.ass"

# 不给 --sub 就用源视频里的内封字幕，--stream 选第几条（从 0 数）
submatcher-cli sync "[TV] 01.mkv" "[BD] 01.mkv" --stream 1

# 指定输出位置
submatcher-cli sync "[TV] 01.mkv" "[BD] 01.mkv" --sub "[TV] 01.sc.ass" -o out/01.ass
```

不给 `-o` 时，输出放在目标视频旁边，文件名是目标视频名加上源字幕的后缀：`[TV] 01.sc.ass` 调完是 `[BD] 01.sc.ass`。同目录还会生成一个 `.check.log`：偏移分布（按整帧）、偏移变化的位置、没把握的行，以及一份屏幕字清单（逐帧动画合成一条，标出行数），调完先看它，再按清单在目标视频里逐帧抽查屏幕字。

所有时间都按整帧平移，并写在目标视频对应帧的中间，ASS 的 10ms 精度不会让屏幕字跨到相邻帧。原本卡在切镜头上的时间点，调完也卡在目标视频对应的切点上；对不上会标「屏幕字未落在切点」。

```text
输出: D:\BD\[BD] 01.sc.ass
共 312 行，需检查 3 行
  偏移 +3.003s × 180 行
  偏移 -2.002s × 129 行
检查日志: D:\BD\[BD] 01.sc.check.log
```

| 选项 | 默认 | 说明 |
|---|---|---|
| `--sub <文件>` | 内封字幕 | 源字幕，`.ass` `.ssa` `.srt`，编码随意 |
| `--stream <n>` | `0` | 不给 `--sub` 时用第几条内封字幕 |
| `-o <文件>` | 见上 | 输出路径 |
| `--window <秒>` | `10` | 在上一行的偏移附近搜多远。两个版本差得很多就调大，找不到时会自动全片搜索 |
| `--max-cost <0~1>` | `0.4` | 匹配代价超过这个值算没找到，沿用前后行的偏移。0 是一模一样，1 是毫不相干。画面差异大（比如 BD 重新调色）可以调到 0.5 左右 |
| `--warn-cost <0~1>` | `0.2` | 超过这个值写进检查日志 |
| `--min-line <秒>` | `1.5` | 太短的行会往两边多取一点画面再比 |
| `--fps <帧率>` | 跟目标视频 | 分析用的帧率，一般不用改 |
| `--no-snap` | | 不把卡在切镜头上的时间点对齐到目标视频的切镜头 |
| `--hwaccel` | | 硬件解码，1080p 以上的片源会快不少 |
| `--no-cache` | | 不读写画面指纹缓存 |
| `--no-log` | | 不生成 `.check.log` |
| `--subset` | | 调完直接[字体子集化](#subset)，输出的字幕嵌好字体。可以一起给 `subset` 的 `--server` `--api-key` `--alias-salt` `--clean` `--strict`；子集化失败时保留未嵌字体的字幕，退出码为 1 |

## batch

```sh
submatcher-cli batch <源目录> <目标目录> [--out-dir 目录] [sync 的选项]
```

```sh
submatcher-cli batch ./tv ./bd
submatcher-cli batch ./tv ./bd --out-dir ./subs --window 30
```

- 两边的视频按集数配对，`- 01`、`[01]`、`第01话`、`EP01` 这类写法都认得。有一边认不出集数时按文件名排序一一对应
- 源目录里和视频同名开头的字幕都会处理，比如 `xx.sc.ass`、`xx.tc.ass` 会各出一份；一个都没有就用内封字幕
- 某一集失败不影响其他集，最后只要有失败的，退出码就是 1

## shift

```sh
submatcher-cli shift <字幕> (--offset <时间> | --from <时间> --to <时间>) [--range-start <时间>] [--range-end <时间>] [-o 输出]
```

```sh
# 整体提前 1.5 秒
submatcher-cli shift 01.ass --offset -1.5

# 同一句话旧片源在 10:35、新片源在 12:03，自动算出偏移
submatcher-cli shift 01.ass --from 10:35 --to 12:03

# 只动 12 分钟之后的行（比如中间插了一段）
submatcher-cli shift 01.ass --offset 2.5 --range-start 12:00
```

时间可以写 `0:10:35.20`、`10:35`、`635.2`，负数也行。区间按每行的开始时间算，包含 `--range-start`，不含 `--range-end`。不给 `-o` 时输出为 `原文件名.shifted.ass`。

## fps

```sh
submatcher-cli fps <字幕> --from <源帧率> --to <目标帧率> [-o 输出]
```

```sh
# PAL 加速版的字幕换到 23.976 的片源上
submatcher-cli fps 01.ass --from 25 --to 23.976
```

同一批帧换了播放速度时用，所有时间乘以 `源帧率 / 目标帧率`。`23.976`、`29.97`、`59.94` 会按精确的 N×1000/1001 算。不给 `-o` 时输出为 `原文件名.fps.ass`。

## encode

```sh
submatcher-cli encode <字幕> [-o 输出]
```

自动识别 UTF-8、UTF-16、GBK、BIG5，转成 UTF-8（带 BOM）。**不给 `-o` 会直接覆盖原文件。**

## subset

```sh
submatcher-cli subset <字幕或文件夹>... [-r] [--out-dir 目录 | --in-place] [--server URL] [--api-key KEY] [--strict] [--clean] [--alias-salt 文本]
```

把字幕用到的字体精简成只含这些字的子集，嵌进字幕里，没装字体的播放器也能正常显示。字幕会上传到 [FontInAss](https://github.com/Yuri-NagaSaki/FontInAss) 服务器处理，默认 `https://font.anibt.net`，协议和官方 `fontinass` 命令行一样，自建的服务器也能用。

```sh
# 默认输出 [BD] 01.sc.subset.ass，原文件不动
submatcher-cli subset "[BD] 01.sc.ass"

# 整个文件夹，包含子文件夹，直接覆盖原文件
submatcher-cli subset ./subs -r --in-place

# 简繁两条字幕要内封进同一个 MKV 时，用不同的别名盐，免得字体名撞车
submatcher-cli subset "[BD] 01.sc.ass" --alias-salt SC
submatcher-cli subset "[BD] 01.tc.ass" --alias-salt TC
```

| 选项 | 说明 |
|---|---|
| `-r` | 处理文件夹时包含子文件夹 |
| `--out-dir <目录>` | 输出到这个目录 |
| `--in-place` | 覆盖原文件 |
| `--server <URL>` | FontInAss 服务器 |
| `--api-key <KEY>` | 服务器要求鉴权时填，公共服务器不用 |
| `--strict` | 有字体没找到就算失败，不写文件 |
| `--clean` | 先去掉字幕里已经嵌入的字体。已经子集化过的字幕要重做时用 |
| `--alias-salt <文本>` | 字体别名的盐，见上面的例子 |

每个文件一行结果：`✓` 成功，`⚠` 成功但有字体没找到（会列出来），`✗` 失败（原因跟在后面）。自己生成的 `*.subset.*` 不会被再次处理。有失败时退出码是 1。

公共服务器目前不支持 SRT，要先转成 ASS。

## clear-cache

```sh
submatcher-cli clear-cache
```

删掉程序目录下的 `cache/`。同一个视频第二次调轴时会读这里的画面指纹，不用重新解码。

## 其他

**ffmpeg 从哪找：** 依次是环境变量 `SUBMATCHER_FFMPEG` 指的目录、程序目录、程序目录下的 `ffmpeg/`、`ffmpeg/bin/`，最后是 PATH。

**输出：** 结果在 stdout，进度和错误在 stderr，重定向时进度按阶段一行一行输出。

**退出码：**

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 出错（文件不存在、参数不对、画面完全对不上、batch 里有集数失败） |
| 2 | 命令不认识 |
| 130 | Ctrl+C 取消 |

**脚本里批量调（PowerShell）：**

```powershell
Get-ChildItem .\tv\*.mkv | ForEach-Object {
  $bd = ".\bd\" + ($_.BaseName -replace '\[TV\]', '[BD]') + ".mkv"
  submatcher-cli sync $_.FullName $bd --sub ($_.FullName -replace '\.mkv$', '.sc.ass')
  if ($LASTEXITCODE -ne 0) { Write-Warning "失败：$($_.Name)" }
}
```

文件名规律简单的话直接用 `batch` 就行，不用自己写循环。
