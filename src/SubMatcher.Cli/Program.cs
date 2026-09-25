using System.Globalization;
using System.Text;
using SubMatcher.Core;

Console.OutputEncoding = Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return 0; }
var cmd = args[0];
var (pos, opt) = ParseArgs(args[1..]);

try
{
    switch (cmd)
    {
        case "sync":
            Need(pos, 2, "sync <源视频> <目标视频>");
            return await RunSync(pos[0], Get("sub"), pos[1], Get("o"));

        case "batch":
            Need(pos, 2, "batch <源目录> <目标目录>");
            var pairs = Sync.PairFolders(pos[0], pos[1]);
            if (pairs.Count == 0) { Console.Error.WriteLine("两个目录里没有能配对的视频。"); return 1; }
            int failed = 0;
            foreach (var p in pairs)
            {
                Console.WriteLine($"\n== {Path.GetFileName(p.SrcVideo)}  →  {Path.GetFileName(p.DstVideo)}");
                var subs = p.Subs.Count > 0 ? p.Subs.Cast<string?>().ToList() : [null]; // null = embedded track
                foreach (var sub in subs)
                {
                    string? output = null;
                    if (Get("out-dir") is { } dir) output = Path.Combine(dir, Path.GetFileName(Sync.DefaultOutput(p.SrcVideo, sub, p.DstVideo)));
                    try { if (await RunSync(p.SrcVideo, sub, p.DstVideo, output) != 0) failed++; }
                    catch (Exception e) when (e is not OperationCanceledException) { failed++; Console.Error.WriteLine($"失败：{e.Message}"); }
                }
            }
            return failed == 0 ? 0 : 1;

        case "shift":
        {
            Need(pos, 1, "shift <字幕>");
            var doc = SubtitleDoc.Load(pos[0]);
            long offset = Get("offset") is { } off ? SubtitleDoc.ParseFlexibleTime(off)
                : SubtitleDoc.ParseFlexibleTime(Req("to")) - SubtitleDoc.ParseFlexibleTime(Req("from"));
            long from = Get("range-start") is { } rs ? SubtitleDoc.ParseFlexibleTime(rs) : long.MinValue;
            long to = Get("range-end") is { } re ? SubtitleDoc.ParseFlexibleTime(re) : long.MaxValue;
            int n = Tools.Shift(doc, offset, from, to);
            Save(doc, pos[0], ".shifted");
            Console.WriteLine($"平移 {offset / 1000.0:+0.000;-0.000}s，共 {n} 行");
            return 0;
        }

        case "fps":
        {
            Need(pos, 1, "fps <字幕> --from <源帧率> --to <目标帧率>");
            var doc = SubtitleDoc.Load(pos[0]);
            Tools.ConvertFps(doc, double.Parse(Req("from"), CultureInfo.InvariantCulture), double.Parse(Req("to"), CultureInfo.InvariantCulture));
            Save(doc, pos[0], ".fps");
            return 0;
        }

        case "encode":
            Need(pos, 1, "encode <字幕>");
            var bytes = File.ReadAllBytes(pos[0]);
            var text = TextEncoding.Decode(bytes, out var enc);
            var outPath = Get("o") ?? pos[0];
            File.WriteAllText(outPath, text, new UTF8Encoding(true));
            Console.WriteLine($"{enc.WebName} → UTF-8 BOM: {outPath}");
            return 0;

        case "subset":
        {
            Need(pos, 1, "subset <字幕或目录>...");
            var files = FontSubset.Collect(pos, Flag("r") || Flag("recursive"));
            if (files.Count == 0) { Console.Error.WriteLine("没有找到 .ass / .ssa / .srt 字幕。"); return 1; }
            var so = SubsetOpts();
            Console.Error.WriteLine($"{files.Count} 个字幕 → {so.Server}");
            int bad = 0;
            // 3 at a time: fast enough for a season, gentle on a shared public server.
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token }, async (f, ct) =>
            {
                var r = await FontSubset.Run(f, FontSubset.OutputFor(f, Get("out-dir"), Flag("in-place")), so, ct);
                lock (files)
                {
                    if (!r.Written) bad++;
                    Console.WriteLine($"{(r.Code == 200 ? "✓" : r.Written ? "⚠" : "✗")} {Path.GetFileName(f)}{(r.Written ? " → " + r.Output : "")}");
                    foreach (var m in r.Messages) Console.WriteLine($"    {m}");
                }
            });
            Console.WriteLine($"完成：成功 {files.Count - bad}，失败 {bad}");
            return bad == 0 ? 0 : 1;
        }

        case "zh":
        {
            Need(pos, 1, "zh <字幕或目录>... --to sc|tc|cn|tw|hk");
            var mode = ZhConvert.Mode(Req("to"));
            var files = FontSubset.Collect(pos, Flag("r") || Flag("recursive"));
            if (files.Count == 0) { Console.Error.WriteLine("没有找到 .ass / .ssa / .srt 字幕。"); return 1; }
            Console.Error.WriteLine($"{files.Count} 个字幕，{mode.Label}（由繁化姬 {ZhConvert.Home} 提供）");
            int bad = 0;
            // 2 at a time: a free public service.
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token }, async (f, ct) =>
            {
                string? output = Flag("in-place") ? f : Get("out-dir") is { } d ? ZhConvert.OutputFor(f, mode.Tag, d) : null;
                try
                {
                    var o = await ZhConvert.ConvertFile(f, mode.Key, output, ct);
                    lock (files) Console.WriteLine($"✓ {Path.GetFileName(f)} → {o}");
                }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
                {
                    lock (files) { bad++; Console.WriteLine($"✗ {Path.GetFileName(f)}  {e.Message}"); }
                }
            });
            Console.WriteLine($"完成：成功 {files.Count - bad}，失败 {bad}");
            return bad == 0 ? 0 : 1;
        }

        case "tg":
        {
            var sub = pos.Count > 0 ? pos[0] : "";
            if (sub == "logout") { TgClient.Logout(); Console.WriteLine("已退出 Telegram 登录"); return 0; }
            using var tg = new TgClient(Get("proxy"), () => { Console.Write("两步验证密码: "); return Console.ReadLine(); });
            if (!await tg.Resume())
            {
                if (Get("phone") is { } phone)
                {
                    try
                    {
                        for (var need = await tg.LoginPhone(phone); need != null;)
                        {
                            Console.Error.Write(need switch { "verification_code" => "验证码（发到你的 Telegram 或短信）: ", "password" => "两步验证密码: ", _ => need + ": " });
                            need = await tg.LoginPhone(Console.ReadLine() ?? "");
                        }
                    }
                    catch (TL.RpcException e) { throw new InvalidOperationException(TgClient.Explain(e)); }
                }
                else
                {
                    Console.Error.WriteLine("用手机 Telegram 扫码登录：设置 → 设备 → 连接桌面设备（或者 tg login --phone +86…）");
                    await tg.LoginQr(PrintQr, cts.Token);
                }
            }
            Console.Error.WriteLine($"已登录 {tg.UserName}");
            if (sub == "login") return 0;
            Need(pos, 2, "tg search|download <关键词>");
            var channel = Get("channel") ?? TgSubs.DefaultChannel;
            var groups = TgSubs.Group(await tg.Search(channel, string.Join(' ', pos[1..]), null, cts.Token));
            if (groups.Count == 0) { Console.Error.WriteLine("没找到，换个关键词（简繁体都试试）"); return 1; }
            if (sub == "search")
            {
                for (int i = 0; i < groups.Count; i++)
                {
                        Console.WriteLine($"{i + 1,3}. {groups[i].Title}  [{(groups[i].Platform is { Length: > 0 } pl ? pl : "未知平台")}]  简 {groups[i].Chs.Count} / 繁 {groups[i].Cht.Count}{(groups[i].Other.Count > 0 ? $" / 其他 {groups[i].Other.Count}" : "")}");
                    if (Flag("files")) foreach (var f in (List<TgFile>)[.. groups[i].Chs, .. groups[i].Cht, .. groups[i].Other]) Console.WriteLine($"       {f.Name}");
                }
                return 0;
            }
            if (sub != "download") { Usage(); return 2; }
            int pick = (int)Num("pick", groups.Count == 1 ? 1 : 0);
            if (pick < 1 || pick > groups.Count) throw new ArgumentException("有多组结果，用 --pick N 选一组（序号见 tg search）");
            var g = groups[pick - 1];
            List<TgFile> files = (Get("lang") ?? "all") switch { "sc" => g.Chs, "tc" => g.Cht, _ => [.. g.Chs, .. g.Cht, .. g.Other] };
            var dir = Get("o") ?? Path.Combine(Environment.CurrentDirectory, $"{g.Title} [{g.Platform}]");
            var got = await tg.Download(channel, files, dir, new Progress<(int Done, int Total)>(p => Console.Error.Write($"\r下载 {p.Done}/{p.Total}   ")), cts.Token);
            Console.Error.WriteLine();
            Console.WriteLine($"{got.Count} 个字幕 → {dir}");
            return 0;
        }

        case "rename":
        {
            Need(pos, 1, "rename <视频目录> [字幕目录]...");
            var (videos, subs) = Renamer.Collect(pos);
            var plan = Renamer.Plan(videos, subs, Get("sc") ?? "sc", Get("tc") ?? "tc");
            foreach (var p in plan) Console.WriteLine(p.NoOp ? $"  = {Path.GetFileName(p.Target)}" : $"  {Path.GetFileName(p.Sub)}\n    → {Path.GetFileName(p.Target)}");
            if (plan.Count == 0) { Console.Error.WriteLine($"{videos.Count} 个视频、{subs.Count} 个字幕，没有能对上集数的"); return 1; }
            if (Flag("dry-run")) return 0;
            int n = Renamer.Apply(plan, Flag("copy"), !Flag("no-backup"));
            Console.WriteLine($"已{(Flag("copy") ? "复制" : "重命名")} {n} 个{(Flag("no-backup") || Flag("copy") || n == 0 ? "" : "，原字幕备份在「字幕备份」文件夹")}");
            return 0;
        }

        case "clear-cache":
            if (Directory.Exists(Fingerprint.CacheDir)) Directory.Delete(Fingerprint.CacheDir, true);
            Console.WriteLine("缓存已清空");
            return 0;

        default:
            Usage();
            return 2;
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("\n已取消"); return 130; }
catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or ArgumentException or UnauthorizedAccessException or TL.RpcException)
{
    Console.Error.WriteLine($"错误：{TgClient.Explain(e)}");
    return 1;
}

async Task<int> RunSync(string src, string? sub, string dst, string? output)
{
    var o = new SyncOptions
    {
        WindowSeconds = Num("window", 10), AnalysisFps = Num("fps", 0), MaxCost = Num("max-cost", 0.4), WarnCost = Num("warn-cost", 0.2),
        MinLineSeconds = Num("min-line", 1.5), SnapToCuts = !Flag("no-snap"), HwAccel = Flag("hwaccel"), UseCache = !Flag("no-cache"),
        SubtitleStream = (int)Num("stream", 0),
    };
    string lastStage = "";
    var progress = new Reporter(p =>
    {
        if (Console.IsErrorRedirected) { if (p.Stage != lastStage) Console.Error.WriteLine(p.Stage); }
        else Console.Error.Write($"\r{p.Stage} {p.Fraction * 100,5:0.0}%   ");
        lastStage = p.Stage;
    });
    var r = await Sync.Run(src, sub, dst, output, o, progress, cts.Token);
    Console.Error.WriteLine();
    Console.WriteLine($"输出: {r.OutputPath}");
    Console.WriteLine($"共 {r.Events.Count} 行，需检查 {r.NeedsCheckCount} 行");
    foreach (var g in r.Events.GroupBy(e => e.ShiftFrames).OrderByDescending(g => g.Count()).Take(5))
        Console.WriteLine($"  偏移 {Sync.FormatShift(g.Key, r.Fps)} × {g.Count()} 行");
    if (!Flag("no-log"))
    {
        var log = Path.ChangeExtension(r.OutputPath, ".check.log");
        File.WriteAllText(log, r.CheckLog, new UTF8Encoding(true));
        Console.WriteLine($"检查日志: {log}");
    }
    if (Flag("subset"))
    {
        var sr = await FontSubset.Run(r.OutputPath, r.OutputPath, SubsetOpts(), cts.Token);
        Console.WriteLine(sr.Code == 200 ? "字体子集化: 已嵌入" : sr.Written ? "字体子集化: 已嵌入，但有字体没找到" : "字体子集化失败，已保留未嵌字体的字幕");
        foreach (var m in sr.Messages) Console.WriteLine($"    {m}");
        if (!sr.Written) return 1;
    }
    return 0;
}

void PrintQr(string url)
{
    var m = TgSubs.QrMatrix(url);
    int n = m.Length;
    bool At(int y, int x) => y >= 0 && y < n && x >= 0 && x < n && m[y][x];
    var sb = new StringBuilder("\n");
    // Two rows per line with half blocks; light modules as blocks so it scans on dark terminals too.
    for (int y = 0; y < n; y += 2)
    {
        sb.Append("  ");
        for (int x = 0; x < n; x++)
            sb.Append((!At(y, x), !At(y + 1, x)) switch { (true, true) => '█', (true, false) => '▀', (false, true) => '▄', _ => ' ' });
        sb.Append('\n');
    }
    Console.Error.WriteLine(sb.ToString() + "（二维码约 30 秒刷新一次）");
}

SubsetOptions SubsetOpts()
{
    var so = new SubsetOptions { Strict = Flag("strict"), Clean = Flag("clean"), AliasSalt = Get("alias-salt"), ApiKey = Get("api-key") };
    if (Get("server") is { } server) so.Server = server;
    return so;
}

void Save(SubtitleDoc doc, string input, string tag)
{
    var output = Get("o") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + tag + Path.GetExtension(input));
    doc.Save(output);
    Console.WriteLine($"输出: {output}");
}

string? Get(string k) => opt.TryGetValue(k, out var v) ? v : null;
string Req(string k) => Get(k) ?? throw new ArgumentException($"缺少参数 --{k}");
bool Flag(string k) => opt.ContainsKey(k);
double Num(string k, double def) => Get(k) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : def;

static void Need(List<string> pos, int n, string usage)
{
    if (pos.Count < n) throw new ArgumentException($"用法: submatcher-cli {usage}");
}

static (List<string>, Dictionary<string, string>) ParseArgs(string[] a)
{
    string[] flags = ["no-snap", "hwaccel", "no-cache", "no-log", "subset", "r", "recursive", "in-place", "strict", "clean", "copy", "no-backup", "dry-run", "files"];
    var pos = new List<string>();
    var opt = new Dictionary<string, string>();
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith('-') || a[i].Length < 2 || char.IsDigit(a[i][1])) { pos.Add(a[i]); continue; }
        var k = a[i].TrimStart('-');
        if (flags.Contains(k)) opt[k] = "1";
        else if (i + 1 < a.Length) opt[k] = a[++i];
        else throw new ArgumentException($"--{k} 需要一个值");
    }
    return (pos, opt);
}

static void Usage() => Console.WriteLine("""
    SubMatcher —— 用画面给字幕调轴（视频版 Sushi）

    submatcher-cli sync <源视频> <目标视频> [--sub 源字幕] [-o 输出]
        不给 --sub 时使用源视频内封字幕（--stream N 选第 N 条，从 0 起）
        --window 10      搜索窗口 ±秒
        --fps 0          分析帧率，0 = 跟随目标视频
        --max-cost 0.4   超过视为未匹配（0 完全一致，1 毫不相干）
        --warn-cost 0.2  超过则写入检查日志
        --min-line 1.5   短于此秒数的行会扩展取样窗口
        --no-snap        不吸附镜头切换点
        --hwaccel        使用硬件解码
        --no-cache       不使用画面指纹缓存
        --no-log         不写 .check.log
        --subset         调完直接字体子集化（可加 subset 命令的 --server 等选项）

    submatcher-cli batch <源目录> <目标目录> [--out-dir 目录] [同 sync 的选项]
        按集数（找不到则按文件名顺序）配对，源目录里与视频同名前缀的字幕都会处理

    submatcher-cli shift <字幕> --offset -1.5 | --from 0:10:35 --to 0:12:03
        [--range-start 时间] [--range-end 时间] [-o 输出]   只平移开始时间落在区间内的行

    submatcher-cli fps <字幕> --from 25 --to 23.976 [-o 输出]
    submatcher-cli encode <字幕> [-o 输出]      任意编码 → UTF-8 BOM
    submatcher-cli subset <字幕或目录>... [-r] [--out-dir 目录 | --in-place]
        [--server https://font.anibt.net] [--api-key KEY] [--strict] [--clean] [--alias-salt SC]
        字体子集化：上传到 FontInAss 服务器，嵌入只含用到的字符的字体。默认输出 xx.subset.ass
    submatcher-cli zh <字幕或目录>... --to sc|tc|cn|tw|hk [-r] [--out-dir 目录 | --in-place]
        简繁转换（繁化姬 https://zhconvert.org）：sc 简体化 / tc 繁体化 / cn 中国化 / tw 台湾化 / hk 香港化
        输出文件名里的语言标记会跟着换（CHS→CHT、.sc→.tc），没有标记就加上
    submatcher-cli tg login [--phone +86…] | logout      Telegram 扫码或手机号登录 / 退出（会话存在程序目录）
    submatcher-cli tg search <关键词> [--files] [--channel anime_chinese_subtitles] [--proxy socks5://主机:端口]
        在字幕频道里检索，按「片名 + 平台」合并，列出简/繁数量（不分集）
    submatcher-cli tg download <关键词> [--pick N] [--lang sc|tc|all] [-o 目录]
        下载第 N 组的简体/繁体/全部字幕
    submatcher-cli rename <视频目录> [字幕目录]... [--sc sc] [--tc tc] [--copy] [--no-backup] [--dry-run]
        按集数把字幕改成视频名（xx.sc.ass / xx.tc.ass），子集化过的优先；被改名/覆盖的原字幕备份到「字幕备份」
    submatcher-cli clear-cache
    """);

/// <summary>Synchronous, serialized progress: Progress&lt;T&gt; would fire out of order on the thread pool in a console app.</summary>
sealed class Reporter(Action<SyncProgress> print) : IProgress<SyncProgress>
{
    public void Report(SyncProgress p) { lock (this) print(p); }
}

