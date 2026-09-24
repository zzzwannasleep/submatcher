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
                    try { await RunSync(p.SrcVideo, sub, p.DstVideo, output); }
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
            var so = new SubsetOptions { Strict = Flag("strict"), Clean = Flag("clean"), AliasSalt = Get("alias-salt"), ApiKey = Get("api-key") };
            if (Get("server") is { } server) so.Server = server;
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
catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"错误：{e.Message}");
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
        Console.WriteLine($"  偏移 {g.Key / r.Fps:+0.000;-0.000}s × {g.Count()} 行");
    if (!Flag("no-log"))
    {
        var log = Path.ChangeExtension(r.OutputPath, ".check.log");
        File.WriteAllText(log, r.CheckLog, new UTF8Encoding(true));
        Console.WriteLine($"检查日志: {log}");
    }
    return 0;
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
    string[] flags = ["no-snap", "hwaccel", "no-cache", "no-log", "r", "recursive", "in-place", "strict", "clean"];
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

    submatcher-cli batch <源目录> <目标目录> [--out-dir 目录] [同 sync 的选项]
        按集数（找不到则按文件名顺序）配对，源目录里与视频同名前缀的字幕都会处理

    submatcher-cli shift <字幕> --offset -1.5 | --from 0:10:35 --to 0:12:03
        [--range-start 时间] [--range-end 时间] [-o 输出]   只平移开始时间落在区间内的行

    submatcher-cli fps <字幕> --from 25 --to 23.976 [-o 输出]
    submatcher-cli encode <字幕> [-o 输出]      任意编码 → UTF-8 BOM
    submatcher-cli subset <字幕或目录>... [-r] [--out-dir 目录 | --in-place]
        [--server https://font.anibt.net] [--api-key KEY] [--strict] [--clean] [--alias-salt SC]
        字体子集化：上传到 FontInAss 服务器，嵌入只含用到的字符的字体。默认输出 xx.subset.ass
    submatcher-cli clear-cache
    """);

/// <summary>Synchronous, serialized progress: Progress&lt;T&gt; would fire out of order on the thread pool in a console app.</summary>
sealed class Reporter(Action<SyncProgress> print) : IProgress<SyncProgress>
{
    public void Report(SyncProgress p) { lock (this) print(p); }
}

