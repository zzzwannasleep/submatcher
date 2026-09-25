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
| `--no-crop` | | 不自动切黑边。默认两个视频都检测黑边（BD 有黑边 WEB 没有、或反过来），切掉后再比画面；检测结果写在检查日志开头 |
| `--no-audio` | | 不用声音粗对齐。默认先像 Sushi 那样按声音给每行定位，画面只在其附近 ±2 秒细调；画面对不上的行保留声音的结果，而不是沿用邻近行。没有音轨或声音对不上时自动只用画面 |
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

## fit

```sh
submatcher-cli fit <视频> [字幕]... [--in-place]
```

比例调整。字幕是按没有黑边的画面做的（比如 WEB 源 1920×816，PlayRes 也是 1920×816），却要配带黑边的视频（1920×1080 的 BD）播放：播放器会把 816 高的画布铺满整个 1080，字被竖向拉伸，屏幕字跑进黑边。

`fit` 检测视频的黑边，把字幕画布放进画面区域：PlayRes 改成整帧大小，`\pos` / `\move` / `\org` / `\clip`（含矢量 clip）和按对齐方式的边距整体平移；字号、边框、矢量绘图都不动，所以不会变形。

- 给了字幕：输出 `xx.fit.ass`，`--in-place` 覆盖原文件
- 只给视频：处理视频里所有内封 ASS 轨，输出到视频旁边，按轨道标题命名为 `视频名.sc.ass` / `视频名.tc.ass`（认不出语言就是 `视频名.trackN.ass`）
- 字幕画布本来就和视频整帧比例一致（按带黑边的画面做的）、视频没有黑边、或者比例对不上时，不做改动并说明原因

调轴时两边黑边不同（`sync` / `batch` 默认会检测），这一步会自动做，检查日志里会写明。

## zh

```sh
submatcher-cli zh <字幕或目录>... --to sc|tc|cn|tw|hk [-r] [--out-dir 目录 | --in-place]
```

简繁转换，由 [繁化姬](https://zhconvert.org) 提供。整份字幕交给它：只转对白，样式、特效标签、字体名都不动。

| `--to` | 效果 |
|---|---|
| `sc` | 简体化 |
| `tc` | 繁体化 |
| `cn` | 中国化：简体，并换成大陆用语（軟體 → 软件） |
| `tw` | 台湾化：繁体，并换成台湾用语（软件 → 軟體） |
| `hk` | 香港化 |

输出默认放在原文件旁边，文件名里的语言标记会跟着换：`CHS_xx.srt` → `CHT_xx.srt`，`xx.sc.ass` → `xx.tc.ass`；没有标记就加上 `.sc` / `.tc`。`--in-place` 覆盖原文件。输入可以是任意编码，输出 UTF-8 BOM。

繁化姬会在字幕开头加一行「Processed by 繁化姬」的注释（SRT 里是一条 0 时长、不显示的字幕），这是它免费使用的条件，别删。

## tg

从 Telegram 字幕频道检索、下载字幕。网页版频道搜不了中文，也没有下载链接，所以要登录一次：手机 Telegram → 设置 → 设备 → 连接桌面设备，扫终端里的二维码。登录状态存在程序目录的 `telegram.session`，删掉它（或 `tg logout`）就退出了。

```sh
submatcher-cli tg login
submatcher-cli tg search 遭到流放
submatcher-cli tg download 遭到流放 --pick 1 --lang sc -o ./subs
```

`search` 把结果按「片名 + 平台」合并，不分集，列出每组的简体 / 繁体数量：

```
  1. 遭到流放的转生重骑士凭借游戏知识大开无双  [Viu]  简 13 / 繁 13
  2. 遭到流放的轉生重騎士憑藉遊戲知識大開無雙  [iQIYI]  简 13 / 繁 13
```

平台不同，片名可能一个用简体一个用繁体，关键词两种都可以试试。

| 参数 | 说明 |
|---|---|
| `--files` | search 时列出每组包含的文件 |
| `--channel <名字>` | 频道，默认 `anime_chinese_subtitles`；合辑 / 旧番 `anime_chinese_subtitles_old`，非日本动画 `chinese_subtitles` |
| `--pick N` | 下载第 N 组（只有一组时可以不写） |
| `--lang sc\|tc\|all` | 简体 / 繁体 / 全部，默认全部 |
| `-o <目录>` | 保存位置，默认当前目录下的 `片名 [平台]/` |
| `--proxy <地址>` | 连不上时用：`socks5://主机:端口`，或 MTProxy 链接 |

同名文件频道里重发过的，只下最新那份。

## rename

```sh
submatcher-cli rename <视频目录> [字幕目录]... [--sc sc] [--tc tc] [--copy] [--no-backup] [--dry-run]
```

按集数把字幕改成对应视频的名字（`视频名.sc.ass` / `视频名.tc.ass`），播放器会自动加载。认得出 `CHS/CHT`、`简/繁`、`sc/tc`、`chs_jp`、`zh-Hans/zh-Hant` 等标记；认不出语言的不加后缀。

每集每种语言只取一个：`*.subset.*`（子集化过的）优先，其次最新的。所以调轴 → 子集化 → `rename`，最后留在视频旁边的就是嵌好字体的那份。

| 参数 | 说明 |
|---|---|
| `--sc` / `--tc` | 简体 / 繁体后缀，默认 `sc` / `tc`，给空字符串就不加 |
| `--copy` | 复制而不是改名，原文件留在原地 |
| `--no-backup` | 不备份。默认被改名或被覆盖的字幕先复制到旁边的 `字幕备份/` |
| `--dry-run` | 只打印对应关系，不动文件 |

从频道下载的字幕调轴的完整流程：

```sh
submatcher-cli tg download 片名 --pick 1 --lang sc -o ./web      # 下载
submatcher-cli rename ./web                                       # 先对上 WEB 视频的名字
submatcher-cli batch ./web ./bd                                   # 调轴，输出已经是 BD 视频名.sc.srt
submatcher-cli subset ./bd                                        # 子集化（ASS），生成 xx.subset.ass
submatcher-cli rename ./bd                                        # 子集化后的那份改回 BD 视频名
```

## merge

```sh
submatcher-cli merge <原盘>... [--chapters]
submatcher-cli merge <原盘>... <字幕目录或文件>... [--playlist 00001.mpls] [--dry-run]
```

原盘字幕合并（BDMV）。原盘一卷里几集连着放在一个播放列表里，字幕却是一集一个。`merge` 把每集字幕平移到这一集在播放列表里开始的位置，合成一个，写两份：

- `原盘文件夹名.sc.ass`：放在原盘文件夹旁边，PotPlayer 等打开文件夹时加载。
- `BDMV/PLAYLIST/00001.sc.ass`：放在播放列表旁边，给直接打开 `.mpls` 的播放器用。

`原盘` 可以是一卷，也可以是放着几卷的上级文件夹（按卷名自然排序）。只给原盘时会列出播放列表，★ 标记的是正片：取最长的一个，循环菜单这类重复片段不计入时长。

每集从哪里开始，是对所有集一起求解的。候选点是章节和片段（m2ts）边界，规则如下：

- 每集最后一句字幕都要在下一集开始前结束（允许 10 秒误差）。
- 满足这一条的方案里，选各集最像的那个：每集章节数一样，从片段开头开始，空余最少。

所以字幕不带预告、结尾比这一集早一分钟时，也不会把预告章节当成下一集的开头。

其他规则：
- 字幕按文件名里的集数排序。几卷一起给时，每卷放得下几集就分几集，剩下的接着放到下一卷。
- 简体、繁体分开合并，按文件名里的 CHS / CHT、sc / tc 区分。
- 文件名是 `00003.ass`、`00003.sc.ass` 这种片段号的字幕，按 m2ts 精确放到那个片段上，不做推算。比如用 `sync` 对着 `BDMV/STREAM/00003.m2ts` 调好的字幕。

ASS 合并时：
- 同名且定义相同的样式合成一个；同名但定义不同的，后面那集的样式改名（如 `Default (2)`），`\r样式名` 也跟着改。
- 内嵌字体（`[Fonts]`）全部保留，子集化过的字幕也能合。
- 各集分辨率（PlayRes）不同时会给出提示。

已有的同名文件不是本工具生成的，就不会被覆盖。本工具生成的合并字幕，重新跑时会直接更新。

| 参数 | 说明 |
|---|---|
| `--chapters` | 只列播放列表时，把正片的章节和所在片段也列出来 |
| `--playlist` | 不用自动选的正片，指定一个 `.mpls` |
| `--dry-run` | 只打印每集的开始时间、章节、空余，不写文件 |

```sh
submatcher-cli tg download 片名 --pick 1 --lang sc -o ./subs   # 下载每集字幕
submatcher-cli merge "D:/BD/[BDMV] 某番" ./subs --dry-run        # 看分到哪几卷、每集从第几章开始
submatcher-cli merge "D:/BD/[BDMV] 某番" ./subs                  # 写出 Vol.1.sc.srt、Vol.2.sc.srt …
```

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
