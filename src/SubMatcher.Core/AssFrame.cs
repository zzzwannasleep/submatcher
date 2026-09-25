using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SubMatcher.Core;

/// <summary>
/// How an ASS canvas moves onto a new frame: new PlayRes, and every coordinate shifted by (Ox, Oy) script units.
/// Units keep their size, so font sizes, borders and drawings stay exactly as they were — nothing gets stretched.
/// </summary>
public sealed record Canvas(double ResX, double ResY, double NewResX, double NewResY, double Ox, double Oy)
{
    public bool IsIdentity => Math.Abs(NewResX - ResX) < 0.5 && Math.Abs(NewResY - ResY) < 0.5 && Math.Abs(Ox) < 0.5 && Math.Abs(Oy) < 0.5;
    public override string ToString() => $"PlayRes {ResX:0.##}×{ResY:0.##} → {NewResX:0.##}×{NewResY:0.##}，坐标平移 ({Ox:0.##}, {Oy:0.##})";
}

/// <summary>
/// 比例调整: fit subtitles to the picture when the video's black bars differ from what the script was made for —
/// e.g. a script typeset on a 1920×816 WEB (PlayRes 1920×816) played on a letterboxed 1920×1080 BD, where players
/// would stretch it over the whole frame. Like Aegisub's letterbox resample, but without rescaling any numbers.
/// </summary>
public static partial class AssFrame
{
    [GeneratedRegex(@"^\s*PlayRes([XY])\s*:\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlayResLine();

    /// <summary>PlayResX/Y from the [Script Info] section; null when either is missing.</summary>
    public static (double X, double Y)? PlayRes(string ass)
    {
        double x = 0, y = 0;
        foreach (var line in ass.Split('\n'))
        {
            if (line.TrimStart().StartsWith("[V4", StringComparison.OrdinalIgnoreCase)) break;
            if (PlayResLine().Match(line) is { Success: true } m && double.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var v))
                if (m.Groups[1].Value.Equals("X", StringComparison.OrdinalIgnoreCase)) x = v; else y = v;
        }
        return x > 0 && y > 0 ? (x, y) : null;
    }

    /// <summary>
    /// The script covers the picture of a source frame (srcCrop = its bars, null = none); put that picture where the
    /// target's picture is (dstCrop, null = whole target frame).
    /// </summary>
    public static Canvas Map((double X, double Y) res, (int W, int H) srcFrame, Crop? srcCrop, (int W, int H) dstFrame, Crop? dstCrop)
    {
        (double NewRes, double O) Axis(double r, int srcF, int srcPos, int srcLen, int dstF, int dstPos, int dstLen)
        {
            double k = srcF / r;                         // source pixels per script unit
            double picStart = srcPos / k, picLen = srcLen / k; // the picture, in script units
            double px = dstLen / picLen;                 // target pixels per script unit
            return (dstF / px, dstPos / px - picStart);
        }
        var sc = srcCrop ?? new Crop(srcFrame.W, srcFrame.H, 0, 0, srcFrame.W, srcFrame.H);
        var dc = dstCrop ?? new Crop(dstFrame.W, dstFrame.H, 0, 0, dstFrame.W, dstFrame.H);
        var (nx, ox) = Axis(res.X, srcFrame.W, sc.X, sc.W, dstFrame.W, dc.X, dc.W);
        var (ny, oy) = Axis(res.Y, srcFrame.H, sc.Y, sc.H, dstFrame.H, dc.Y, dc.H);
        return new Canvas(res.X, res.Y, nx, ny, ox, oy);
    }

    /// <summary>
    /// For a script on its own (no source video): does its canvas match the target's picture rather than the whole frame?
    /// Then it was made for a frame without bars and needs fitting; otherwise null with the reason.
    /// </summary>
    public static (Canvas? Canvas, string Why) Fit(string ass, (int W, int H) frame, Crop? picture)
    {
        if (PlayRes(ass) is not { } res) return (null, "字幕没写 PlayResX / PlayResY，不知道它按什么画面做的");
        double canvas = res.X / res.Y, full = (double)frame.W / frame.H;
        if (picture == null) return (null, "视频没有黑边，不需要调整");
        double pic = (double)picture.W / picture.H;
        if (Math.Abs(canvas / full - 1) < 0.02) return (null, $"字幕画布 {res.X:0}×{res.Y:0} 和视频整帧比例一致，本来就是按带黑边的画面做的");
        if (Math.Abs(canvas / pic - 1) > 0.03)
            return (null, $"字幕画布 {res.X:0}×{res.Y:0}（{canvas:0.00}:1）和视频画面 {picture.W}×{picture.H}（{pic:0.00}:1）比例对不上，没法自动调整");
        var c = Map(res, ((int)Math.Round(res.X), (int)Math.Round(res.Y)), null, frame, picture);
        return (c, c.ToString());
    }

    public sealed record FitResult(string Input, string? Output, string Message);

    /// <summary>
    /// Fits subtitles to a video's picture. No subtitle files = every embedded ASS track, written next to the video as
    /// "video.sc.ass" / "video.tc.ass" / "video.trackN.ass" by track title. Files are written as "xx.fit.ass" unless in place.
    /// </summary>
    public static async Task<List<FitResult>> FitToVideo(string video, IReadOnlyList<string> subs, bool inPlace, bool useCache = true, CancellationToken ct = default)
    {
        var info = await FFmpeg.Probe(video, ct);
        var picture = await Fingerprint.DetectCrop(video, useCache, ct);
        var items = new List<(string Label, Func<Task<string>> Text, string Output)>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(video))!;
        if (subs.Count > 0)
        {
            foreach (var s in subs)
                items.Add((Path.GetFileName(s), () => Task.FromResult(TextEncoding.Read(s)),
                    inPlace ? s : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(s))!, Path.GetFileNameWithoutExtension(s) + ".fit" + Path.GetExtension(s))));
        }
        else
        {
            foreach (var (n, codec, title) in await FFmpeg.SubtitleTracks(video, ct))
            {
                if (codec is not ("ass" or "ssa")) continue;
                var tag = Renamer.LangOf(title.Replace('&', ' ')) ?? $"track{n}";
                if (items.Any(x => x.Output.EndsWith($".{tag}.ass"))) tag += $".track{n}";
                items.Add(($"内封第 {n} 条 {title}", () => FFmpeg.ExtractSubtitle(video, n, ct), Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(video)}.{tag}.ass")));
            }
            if (items.Count == 0) return [new(Path.GetFileName(video), null, "视频里没有 ASS 字幕轨")];
        }
        var results = new List<FitResult>();
        foreach (var (label, read, output) in items)
        {
            var text = await read();
            var (canvas, why) = Fit(text, (info.Width, info.Height), picture);
            if (canvas == null) { results.Add(new(label, null, why)); continue; }
            await File.WriteAllTextAsync(output, Apply(text, canvas), new UTF8Encoding(true), ct);
            results.Add(new(label, output, why));
        }
        return results;
    }

    // --- rewriting ---

    [GeneratedRegex(@"\\(pos|org)\(\s*([-\d.]+)\s*,\s*([-\d.]+)\s*\)")]
    private static partial Regex PosOrg();

    [GeneratedRegex(@"\\move\(\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)\s*((?:,\s*[-\d.]+\s*){0,2})\)")]
    private static partial Regex Move();

    [GeneratedRegex(@"\\(i?clip)\(\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)\s*\)")]
    private static partial Regex RectClip();

    [GeneratedRegex(@"\\(i?clip)\(\s*(?:(\d+)\s*,)?\s*([mnlbspc][^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex VectorClip();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"\\an(\d)|\\a(\d+)")]
    private static partial Regex Align();

    static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Shift every coordinate in an event's text; returns whether it positions itself (\pos / \move).</summary>
    internal static string ShiftText(string text, Canvas c, out bool positioned)
    {
        positioned = text.Contains(@"\pos(") || text.Contains(@"\move(");
        text = PosOrg().Replace(text, m => $@"\{m.Groups[1].Value}({N(D(m.Groups[2].Value) + c.Ox)},{N(D(m.Groups[3].Value) + c.Oy)})");
        text = Move().Replace(text, m =>
            $@"\move({N(D(m.Groups[1].Value) + c.Ox)},{N(D(m.Groups[2].Value) + c.Oy)},{N(D(m.Groups[3].Value) + c.Ox)},{N(D(m.Groups[4].Value) + c.Oy)}{m.Groups[5].Value})");
        text = RectClip().Replace(text, m =>
            $@"\{m.Groups[1].Value}({N(D(m.Groups[2].Value) + c.Ox)},{N(D(m.Groups[3].Value) + c.Oy)},{N(D(m.Groups[4].Value) + c.Ox)},{N(D(m.Groups[5].Value) + c.Oy)})");
        text = VectorClip().Replace(text, m =>
        {
            // vector clip coordinates are in 2^(scale-1) sub-units; they alternate x, y
            double f = m.Groups[2].Success ? Math.Pow(2, int.Parse(m.Groups[2].Value) - 1) : 1;
            int i = 0;
            var drawing = Number().Replace(m.Groups[3].Value, n => N(D(n.Value) + (i++ % 2 == 0 ? c.Ox : c.Oy) * f));
            return $@"\{m.Groups[1].Value}({(m.Groups[2].Success ? m.Groups[2].Value + "," : "")}{drawing})";
        });
        return text;
    }

    /// <summary>numpad alignment from \an or legacy \a; null when the text doesn't override it.</summary>
    static int? AlignOf(string text)
    {
        int? a = null;
        foreach (Match m in Align().Matches(text))
        {
            int v = int.Parse(m.Groups[m.Groups[1].Success ? 1 : 2].Value);
            if (!m.Groups[1].Success) v = v >= 9 ? v - 5 : v >= 5 ? v + 2 : v; // legacy \a: 1-3 bottom, 5-7 top, 9-11 middle
            if (v is >= 1 and <= 9) a = v;
        }
        return a;
    }

    /// <summary>Margins (L, R, V) after the move, for text laid out by alignment instead of \pos.</summary>
    static (int L, int R, int V) Margins(Canvas c, int align, int l, int r, int v)
    {
        double right = c.NewResX - c.ResX - c.Ox, bottom = c.NewResY - c.ResY - c.Oy;
        int col = (align - 1) % 3, row = (align - 1) / 3; // col 0 left 1 centre 2 right; row 0 bottom 1 middle 2 top
        return ((int)Math.Round(l + (col == 0 ? c.Ox : 0)), (int)Math.Round(r + (col == 2 ? right : 0)),
                (int)Math.Round(v + (row == 0 ? bottom : row == 2 ? c.Oy : 0)));
    }

    /// <summary>Rewrites PlayRes, style margins and every event's coordinates / margins. Everything else is kept byte for byte.</summary>
    public static string Apply(string ass, Canvas c)
    {
        var nl = ass.Contains("\r\n") ? "\r\n" : "\n";
        var lines = ass.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        string section = "";
        string[] styleFmt = [], eventFmt = [];
        var styles = new Dictionary<string, (int Align, int L, int R, int V)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            if (t.StartsWith('[')) { section = t.ToLowerInvariant(); continue; }
            if (section == "[script info]")
            {
                if (Regex.Match(t, @"^(PlayRes|LayoutRes)([XY])\s*:", RegexOptions.IgnoreCase) is { Success: true } m)
                    lines[i] = $"{m.Groups[1].Value}{m.Groups[2].Value}: {N(m.Groups[2].Value.Equals("X", StringComparison.OrdinalIgnoreCase) ? c.NewResX : c.NewResY)}";
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                var names = line[(colon + 1)..].Split(',').Select(s => s.Trim().ToLowerInvariant()).ToArray();
                if (section.StartsWith("[v4")) styleFmt = names; else if (section == "[events]") eventFmt = names;
                continue;
            }
            if (section.StartsWith("[v4") && key.Equals("Style", StringComparison.OrdinalIgnoreCase) && styleFmt.Length > 0)
            {
                var f = line[(colon + 1)..].Split(',', styleFmt.Length);
                int I(string n) => Array.IndexOf(styleFmt, n);
                if (f.Length < styleFmt.Length || I("alignment") < 0 || I("marginv") < 0) continue;
                int P(string n) => int.TryParse(f[I(n)].Trim(), out var v) ? v : 0;
                var orig = (P("alignment"), P("marginl"), P("marginr"), P("marginv"));
                styles[f[I("name")].Trim()] = orig;
                var (l, r, v) = Margins(c, Math.Clamp(orig.Item1, 1, 9), orig.Item2, orig.Item3, orig.Item4);
                f[I("marginl")] = l.ToString(); f[I("marginr")] = r.ToString(); f[I("marginv")] = v.ToString();
                lines[i] = line[..(colon + 1)] + string.Join(',', f);
                continue;
            }
            if (section == "[events]" && eventFmt.Length > 0 && (key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) || key.Equals("Comment", StringComparison.OrdinalIgnoreCase)))
            {
                var f = line[(colon + 1)..].Split(',', eventFmt.Length);
                if (f.Length < eventFmt.Length) continue;
                int I(string n) => Array.IndexOf(eventFmt, n);
                var text = ShiftText(f[^1], c, out bool positioned);
                f[^1] = text;
                if (!positioned && I("marginv") >= 0 && I("style") >= 0)
                {
                    var st = styles.TryGetValue(f[I("style")].Trim(), out var s) ? s : (Align: 2, L: 0, R: 0, V: 0);
                    int align = AlignOf(text) ?? st.Align;
                    int E(string n, int styleValue) => int.TryParse(f[I(n)].Trim(), out var v) && v != 0 ? v : styleValue;
                    int el = E("marginl", st.L), er = E("marginr", st.R), ev = E("marginv", st.V);
                    // Only write the event's margins when the style's adjusted ones wouldn't give the same result.
                    var mine = Margins(c, Math.Clamp(align, 1, 9), el, er, ev);
                    var viaStyle = Margins(c, Math.Clamp(st.Align, 1, 9), st.L, st.R, st.V);
                    bool eventHadOwn = new[] { "marginl", "marginr", "marginv" }.Any(n => int.TryParse(f[I(n)].Trim(), out var v) && v != 0);
                    if (eventHadOwn || mine != viaStyle)
                    {
                        f[I("marginl")] = mine.L.ToString(); f[I("marginr")] = mine.R.ToString(); f[I("marginv")] = mine.V.ToString();
                    }
                }
                lines[i] = line[..(colon + 1)] + string.Join(',', f);
            }
        }
        return string.Join(nl, lines);
    }
}
