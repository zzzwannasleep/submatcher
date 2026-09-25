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
    /// <summary>Black bars cut off before matching (null = none found, or auto-crop off).</summary>
    public Crop? SrcCrop { get; init; }
    public Crop? DstCrop { get; init; }
    /// <summary>The script's canvas moved onto the target's picture (比例调整), or null.</summary>
    public Canvas? Fitted { get; init; }
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
        foreach (var v in new[] { srcVideo, dstVideo }) RejectPartial(v);

        SubtitleDoc doc;
        if (string.IsNullOrEmpty(srcSub))
        {
            progress?.Report(new("提取内封字幕", 0));
            doc = SubtitleDoc.Parse(await FFmpeg.ExtractSubtitle(srcVideo, o.SubtitleStream, ct), SubFormat.Ass);
            if (!IsSub(output) || Path.GetExtension(output).Equals(".srt", StringComparison.OrdinalIgnoreCase)) output = Path.ChangeExtension(output, ".ass");
        }
        else doc = SubtitleDoc.Load(srcSub);
        if (doc.Events.Count == 0) throw new InvalidOperationException("字幕里没有任何事件行。");

        var srcInfo = await FFmpeg.Probe(srcVideo, ct);
        var dstInfo = await FFmpeg.Probe(dstVideo, ct);
        double fps = o.AnalysisFps > 0 ? Tools.ExactFps(o.AnalysisFps) : dstInfo.Fps;
        while (fps > 31) fps /= 2;

        // Black bars differ between releases (BD letterboxed, WEB not, or the other way round): compare the picture only.
        Crop? srcCrop = null, dstCrop = null;
        if (o.AutoCrop)
        {
            progress?.Report(new("检测黑边", 0));
            var cs = Fingerprint.DetectCrop(srcVideo, o.UseCache, ct);
            var cd = Fingerprint.DetectCrop(dstVideo, o.UseCache, ct);
            (srcCrop, dstCrop) = (await cs, await cd);
        }

        // Decode both videos at once; progress is frames decoded over frames expected.
        double expected = Math.Max(1, (srcInfo.DurationSeconds + dstInfo.DurationSeconds) * fps);
        int doneA = 0, doneB = 0;
        void Report() => progress?.Report(new("解码画面", Math.Min(1, (doneA + doneB) / expected) * 0.85));
        var ta = Fingerprint.FromVideo(srcVideo, fps, o.HwAccel, o.UseCache, f => { doneA = f; Report(); }, ct, srcCrop);
        var tb = Fingerprint.FromVideo(dstVideo, fps, o.HwAccel, o.UseCache, f => { doneB = f; Report(); }, ct, dstCrop);
        var sa = o.UseAudio ? Fingerprint.FromAudio(srcVideo, fps, o.UseCache, ct) : Task.FromResult<Fingerprint?>(null);
        var sb = o.UseAudio ? Fingerprint.FromAudio(dstVideo, fps, o.UseCache, ct) : Task.FromResult<Fingerprint?>(null);
        var a = await ta;
        var b = await tb;
        CheckComplete(srcVideo, srcInfo, a);
        CheckComplete(dstVideo, dstInfo, b);

        progress?.Report(new("声音粗对齐", 0.85));
        var (prior, soundNote) = await SoundPrior(await sa, await sb, doc.Events, o, (dstInfo.VideoStartMs - srcInfo.VideoStartMs) * fps / 1000, ct);
        var matcher = new Matcher(a, b, o) { SourceStartMs = srcInfo.VideoStartMs, TargetStartMs = dstInfo.VideoStartMs, Prior = prior };
        var results = await Task.Run(() => matcher.Match(doc.Events,
            new Progress<double>(f => progress?.Report(new("匹配画面", 0.85 + f * 0.15))), ct), ct);

        for (int i = 0; i < doc.Events.Count; i++) { doc.Events[i].Start = results[i].NewStart; doc.Events[i].End = results[i].NewEnd; }
        // 比例调整: the script is laid out on the source's picture; when the bars differ, move its canvas onto the target's picture.
        Canvas? fitted = null;
        if (o.AutoCrop && doc.Format == SubFormat.Ass && srcInfo.Width > 0 && dstInfo.Width > 0 && AssFrame.PlayRes(doc.Serialize()) is { } res)
        {
            var c = AssFrame.Map(res, (srcInfo.Width, srcInfo.Height), srcCrop, (dstInfo.Width, dstInfo.Height), dstCrop);
            if (!c.IsIdentity) { fitted = c; doc = SubtitleDoc.Parse(AssFrame.Apply(doc.Serialize(), c), SubFormat.Ass); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        doc.Save(output);

        var log = CropNote(o.AutoCrop, srcCrop, dstCrop, fitted) + soundNote + BuildCheckLog(results, fps, srcVideo, dstVideo);
        progress?.Report(new("完成", 1));
        return new SyncResult { Events = results, Doc = doc, OutputPath = output, Fps = fps, CheckLog = log, SrcCrop = srcCrop, DstCrop = dstCrop, Fitted = fitted };
    }

    /// <summary>One line for the UI: which side had black bars cut before matching; null when neither had any.</summary>
    public static string? CropSummary(Crop? src, Crop? dst, Canvas? fitted = null) => src == null && dst == null ? null
        : $"已切黑边 · 源 {(src == null ? "无黑边" : $"{src.W}×{src.H}")} · 目标 {(dst == null ? "无黑边" : $"{dst.W}×{dst.H}")}"
          + (fitted != null ? " · 字幕已按画面做比例调整" : "");

    /// <summary>
    /// Sushi's pass over the soundtracks: every line's shift found by sound, in picture frames (sound is on the container
    /// clock, pictures on their first frame's). Only lines the sound is sure about are passed on.
    /// </summary>
    static async Task<(int?[]?, string)> SoundPrior(Fingerprint? a, Fingerprint? b, IReadOnlyList<SubEvent> events, SyncOptions o, double startDiff,
        CancellationToken ct)
    {
        if (!o.UseAudio) return (null, "");
        if (a == null || b == null) return (null, "声音粗对齐：有一边没有音轨，只用画面\n\n");
        var ao = new SyncOptions { WindowSeconds = o.WindowSeconds, MinLineSeconds = o.MinLineSeconds, SnapToCuts = false };
        List<EventResult> r;
        try { r = await Task.Run(() => new Matcher(a, b, ao).Match(events, null, ct), ct); }
        catch (InvalidOperationException) { return (null, "声音粗对齐：两边声音对不上（重新混音？），只用画面\n\n"); }
        var prior = r.Select(x => x.Status is MatchStatus.Ok or MatchStatus.Smoothed || x.Status == MatchStatus.Jump && x.Cost <= o.WarnCost ? (int?)(int)Math.Round(x.ShiftFrames - startDiff) : null).ToArray();
        int sure = prior.Count(x => x != null), lines = events.Count(e => !e.IsComment);
        return (prior, $"声音粗对齐：{sure}/{lines} 行由声音定位，画面在其附近细调\n\n");
    }

    static string CropNote(bool auto, Crop? src, Crop? dst, Canvas? fitted)
    {
        if (!auto) return "黑边：未检测（已关闭自动切黑边）\n\n";
        var sb = new StringBuilder();
        sb.AppendLine($"黑边（匹配前切掉）: 源 {src?.ToString() ?? "无"} / 目标 {dst?.ToString() ?? "无"}");
        if (fitted != null)
            sb.AppendLine($"比例调整: 两边画面位置不同，字幕画布已挪到目标画面里（{fitted}）。字号不变，\\pos 等坐标整体平移");
        return sb.AppendLine().ToString();
    }

    /// <summary>
    /// Like Sushi's _check.log: the shift distribution, where the shift changes, every line the matcher wasn't sure
    /// about, and a list of typesetting (signs) to spot-check frame by frame in the target.
    /// </summary>
    public static string BuildCheckLog(List<EventResult> events, double fps, string src, string dst)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"源: {src}").AppendLine($"目标: {dst}").AppendLine($"分析帧率: {fps:0.###}").AppendLine();
        sb.AppendLine("偏移分布（整帧）:");
        foreach (var g in events.Where(e => !e.IsComment).GroupBy(e => e.ShiftFrames).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {FormatShift(g.Key, fps)} × {g.Count()} 行");
        sb.AppendLine();

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

        // Signs grouped by time (< 0.6 s apart) so a whole animated title shows up as one entry.
        var signs = sorted.Where(e => e.IsSign && !e.IsComment).ToList();
        if (signs.Count > 0)
        {
            sb.AppendLine().AppendLine($"屏幕字清单（{signs.Count} 行，建议在目标视频里逐帧抽查）:");
            var group = new List<EventResult>();
            void Flush()
            {
                if (group.Count == 0) return;
                var f = group[0];
                string flag = group.Any(x => x.NeedsCheck) ? "  ⚠ " + string.Join("、", group.Where(x => x.NeedsCheck).Select(x => StatusText(x.Status)).Distinct()) : "";
                sb.AppendLine($"  {SubtitleDoc.FormatAssTime(group.Min(x => x.NewStart))} - {SubtitleDoc.FormatAssTime(group.Max(x => x.NewEnd))}" +
                              $"  {FormatShift(f.ShiftFrames, fps)}  {(group.Count > 1 ? group.Count + " 行" : "")}{flag}  | {Trim(f.Text)}");
                group.Clear();
            }
            foreach (var e in signs)
            {
                if (group.Count > 0 && e.OldStart - group.Max(x => x.OldEnd) >= 600) Flush();
                group.Add(e);
            }
            Flush();
        }
        sb.Insert(0, $"需检查 {issues} 处 / 共 {events.Count} 行{Environment.NewLine}");
        return sb.ToString();
    }

    static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s;
    public static string FormatShift(int frames, double fps) => $"{frames:+0;-0;0} 帧 ({frames / fps:+0.000;-0.000;0.000}s)";

    /// <summary>Half-downloaded files decode to garbage offsets; refuse them by name before spending minutes decoding.</summary>
    static void RejectPartial(string path)
    {
        foreach (var ext in new[] { ".!qb", ".!ut", ".part", ".crdownload", ".bc!", ".td", ".downloading" })
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"文件还没下载完：{Path.GetFileName(path)}");
    }

    /// <summary>A truncated file still carries the full duration in its header but decodes to far fewer frames.</summary>
    internal static void CheckComplete(string path, VideoInfo info, Fingerprint fp)
    {
        double expected = info.DurationSeconds * fp.Fps;
        if (expected > 60 * fp.Fps && fp.Count < expected * 0.97 - fp.Fps)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} 只解出 {fp.Count / fp.Fps:0} 秒画面，文件标称 {info.DurationSeconds:0} 秒：可能没下载完或已损坏。");
    }

    public static string StatusText(MatchStatus s) => s switch
    {
        MatchStatus.Ok => "正常",
        MatchStatus.Low => "相似度低",
        MatchStatus.Jump => "大跳跃",
        MatchStatus.Smoothed => "已平滑",
        MatchStatus.Static => "静态·沿用邻近",
        MatchStatus.Inherited => "未匹配·沿用邻近",
        MatchStatus.Empty => "空行·沿用邻近",
        MatchStatus.CutMiss => "屏幕字未落在切点",
        MatchStatus.OutOfRange => "超出源视频",
        MatchStatus.Audio => "画面未确认·按声音",
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
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(path).Replace('_', ' '), @"\d{3,4}[pPiI]|[xXhH]\.?26[45]|\d+bit|\d{3,4}x\d{3,4}", " ");
        var ms = Episode.Matches(name);
        return ms.Count == 0 ? null : double.Parse(ms[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
