using System.Text;
using System.Text.RegularExpressions;

namespace SubMatcher.Core;

public sealed record SyncProgress(string Stage, double Fraction);

public sealed class SyncResult
{
    public required List<EventResult> Events { get; init; }
    /// <summary>The shifted document, so callers can hand-correct lines and save again.</summary>
    public required SubtitleDoc Doc { get; init; }
    public required string OutputPath { get; init; }
    public required double Fps { get; init; }
    public required string CheckLog { get; init; }
    public int NeedsCheckCount => Events.Count(e => e.NeedsCheck);
}

public static class Sync
{
    public static readonly string[] SubExts = [".ass", ".ssa", ".srt"];
    public static readonly string[] VideoExts = [".mkv", ".mp4", ".m2ts", ".ts", ".avi", ".flv", ".webm", ".mov", ".wmv", ".m4v", ".vob"];

    public static bool IsSub(string p) => SubExts.Contains(Path.GetExtension(p).ToLowerInvariant());
    public static bool IsVideo(string p) => VideoExts.Contains(Path.GetExtension(p).ToLowerInvariant());

    /// <summary>"[Grp] Show - 01.sc.ass" next to "[Grp] Show - 01.mkv" → "&lt;target name&gt;.sc.ass" next to the target.</summary>
    public static string DefaultOutput(string srcVideo, string? srcSub, string dstVideo)
    {
        var baseName = Path.GetFileNameWithoutExtension(srcVideo);
        string suffix = ".ass";
        if (!string.IsNullOrEmpty(srcSub))
        {
            var subName = Path.GetFileName(srcSub);
            suffix = subName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ? subName[baseName.Length..] : Path.GetExtension(srcSub);
        }
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dstVideo))!, Path.GetFileNameWithoutExtension(dstVideo) + suffix);
        if (srcSub != null && string.Equals(Path.GetFullPath(srcSub), output, StringComparison.OrdinalIgnoreCase))
            output = Path.ChangeExtension(output, ".synced" + Path.GetExtension(output));
        return output;
    }

    public static async Task<SyncResult> Run(string srcVideo, string? srcSub, string dstVideo, string? output, SyncOptions o,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        output ??= DefaultOutput(srcVideo, srcSub, dstVideo);

        SubtitleDoc doc;
        if (string.IsNullOrEmpty(srcSub))
        {
            progress?.Report(new("提取内封字幕", 0));
            var tmp = Path.Combine(Path.GetTempPath(), $"submatcher_{Guid.NewGuid():N}.ass");
            try { await FFmpeg.ExtractSubtitle(srcVideo, o.SubtitleStream, tmp, ct); doc = SubtitleDoc.Load(tmp); }
            finally { File.Delete(tmp); }
            if (!IsSub(output) || Path.GetExtension(output).Equals(".srt", StringComparison.OrdinalIgnoreCase)) output = Path.ChangeExtension(output, ".ass");
        }
        else doc = SubtitleDoc.Load(srcSub);
        if (doc.Events.Count == 0) throw new InvalidOperationException("字幕里没有任何事件行。");

        var srcInfo = await FFmpeg.Probe(srcVideo, ct);
        var dstInfo = await FFmpeg.Probe(dstVideo, ct);
        double fps = o.AnalysisFps > 0 ? Tools.ExactFps(o.AnalysisFps) : dstInfo.Fps;
        while (fps > 31) fps /= 2;

        // Decode both videos at once; progress is frames decoded over frames expected.
        double expected = Math.Max(1, (srcInfo.DurationSeconds + dstInfo.DurationSeconds) * fps);
        int doneA = 0, doneB = 0;
        void Report() => progress?.Report(new("解码画面", Math.Min(1, (doneA + doneB) / expected) * 0.85));
        var ta = Fingerprint.FromVideo(srcVideo, fps, o.HwAccel, o.UseCache, f => { doneA = f; Report(); }, ct);
        var tb = Fingerprint.FromVideo(dstVideo, fps, o.HwAccel, o.UseCache, f => { doneB = f; Report(); }, ct);
        var a = await ta;
        var b = await tb;

        var matcher = new Matcher(a, b, o);
        var results = await Task.Run(() => matcher.Match(doc.Events,
            new Progress<double>(f => progress?.Report(new("匹配画面", 0.85 + f * 0.15))), ct), ct);

        for (int i = 0; i < doc.Events.Count; i++) { doc.Events[i].Start = results[i].NewStart; doc.Events[i].End = results[i].NewEnd; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        doc.Save(output);

        var log = BuildCheckLog(results, fps, srcVideo, dstVideo);
        progress?.Report(new("完成", 1));
        return new SyncResult { Events = results, Doc = doc, OutputPath = output, Fps = fps, CheckLog = log };
    }

    /// <summary>Like Sushi's _check.log: where the shift changes, and every line the matcher wasn't sure about.</summary>
    public static string BuildCheckLog(List<EventResult> events, double fps, string src, string dst)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"源: {src}").AppendLine($"目标: {dst}").AppendLine($"分析帧率: {fps:0.###}").AppendLine();
        var sorted = events.OrderBy(e => e.OldStart).ToList();
        int? last = null;
        int issues = 0;
        foreach (var e in sorted)
        {
            string? reason = null;
            if (last != null && e.ShiftFrames != last && !e.IsComment)
                reason = $"偏移变化 {FormatShift(last.Value, fps)} → {FormatShift(e.ShiftFrames, fps)}";
            if (e.NeedsCheck) reason = (reason == null ? "" : reason + "；") + StatusText(e.Status) + $" (代价 {e.Cost:0.000})";
            if (!e.IsComment) last = e.ShiftFrames;
            if (reason == null) continue;
            issues++;
            sb.AppendLine($"{SubtitleDoc.FormatAssTime(e.OldStart)} → {SubtitleDoc.FormatAssTime(e.NewStart)}  {reason}  | {Trim(e.Text)}");
        }
        sb.Insert(0, $"需检查 {issues} 处 / 共 {events.Count} 行{Environment.NewLine}");
        return sb.ToString();
    }

    static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s;
    static string FormatShift(int frames, double fps) => $"{frames * 1.0 / fps:+0.000;-0.000}s";

    public static string StatusText(MatchStatus s) => s switch
    {
        MatchStatus.Ok => "正常",
        MatchStatus.Low => "相似度低",
        MatchStatus.Jump => "大跳跃",
        MatchStatus.Smoothed => "已平滑",
        MatchStatus.Static => "静态(沿用邻近)",
        MatchStatus.Inherited => "未匹配(沿用邻近)",
        MatchStatus.OutOfRange => "超出源视频",
        _ => s.ToString(),
    };

    // ---------------- batch ----------------

    public sealed record BatchPair(string SrcVideo, string DstVideo, List<string> Subs);

    static readonly Regex Episode = new(@"(?:第|\bEP?|\[|-\s*|\s)(\d{1,3}(?:\.5)?)(?:v\d)?(?:话|話|集|\]|\s|\.|$)", RegexOptions.IgnoreCase);

    /// <summary>Pairs videos across two folders by episode number, falling back to sort order when numbers don't line up.</summary>
    public static List<BatchPair> PairFolders(string srcDir, string dstDir)
    {
        var src = Directory.GetFiles(srcDir).Where(IsVideo).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var dst = Directory.GetFiles(dstDir).Where(IsVideo).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var subs = Directory.GetFiles(srcDir).Where(IsSub).ToList();
        List<string> SubsFor(string v)
        {
            var b = Path.GetFileNameWithoutExtension(v);
            return subs.Where(s => Path.GetFileName(s).StartsWith(b + ".", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        }

        var es = src.Select(EpisodeOf).ToList();
        var ed = dst.Select(EpisodeOf).ToList();
        bool byNumber = es.All(x => x != null) && ed.All(x => x != null) && es.Distinct().Count() == es.Count && ed.Distinct().Count() == ed.Count;
        var pairs = new List<BatchPair>();
        if (byNumber)
        {
            foreach (var (v, e) in src.Zip(es))
            {
                int j = ed.IndexOf(e);
                if (j >= 0) pairs.Add(new(v, dst[j], SubsFor(v)));
            }
        }
        else
        {
            for (int i = 0; i < Math.Min(src.Count, dst.Count); i++) pairs.Add(new(src[i], dst[i], SubsFor(src[i])));
        }
        return pairs;
    }

    public static string? EpisodeOf(string path)
    {
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"\d{3,4}[pPiI]|[xXhH]\.?26[45]|\d+bit|\d{3,4}x\d{3,4}", " ");
        var ms = Episode.Matches(name);
        return ms.Count == 0 ? null : double.Parse(ms[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
