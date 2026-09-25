using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SubMatcher.Core;

/// <param name="Offset">where the subtitle's 0:00 lands on the playlist (seconds)</param>
/// <param name="Chapter">chapter the episode starts at (0 = before the first chapter)</param>
/// <param name="Slack">seconds between the subtitle's last line and the next episode (negative = overlaps it)</param>
/// <param name="Clip">set when the subtitle was timed to that m2ts, so it is placed exactly there</param>
public sealed record MergeEpisode(string Sub, double Offset, int Chapter, double Slack, string? Clip);

/// <summary>Per-episode subtitles merged into one for a disc's playlist (what players load for a BDMV).</summary>
public sealed record MergePlan(string Disc, Playlist Playlist, string? Lang, string Ext, List<MergeEpisode> Episodes)
{
    public string Suffix => Lang is { } l ? "." + l : "";

    /// <summary>Next to the disc folder (PotPlayer, mpv with the folder) and next to the playlist (players that open the .mpls).</summary>
    public List<string> Outputs()
    {
        var o = new List<string>();
        if (Path.GetDirectoryName(Disc.TrimEnd('\\', '/')) is { Length: > 0 } parent)
            o.Add(Path.Combine(parent, Path.GetFileName(Disc.TrimEnd('\\', '/')) + Suffix + Ext));
        o.Add(Path.Combine(Path.GetDirectoryName(Playlist.Path)!, Playlist.Name + Suffix + Ext));
        return o;
    }
}

/// <summary>
/// BDMV subtitle merge (like BluraySubtitle's "merge subtitles"): a disc plays several episodes as one playlist, so the
/// per-episode subtitles are shifted to where each episode starts and joined into one file.
/// Where each episode starts is solved for all episodes at once (not greedily): starts sit on chapters or clip boundaries,
/// every subtitle must fit before the next episode, and episodes of a show are alike (same number of chapters, clip starts).
/// A subtitle timed to one m2ts (e.g. 00003.sc.ass from 画面调轴 against BDMV/STREAM/00003.m2ts) goes exactly onto that clip.
/// </summary>
public static partial class SubMerge
{
    public const string Marker = "; Merged for Blu-ray playlist by SubMatcher";
    const double Tolerance = 10; // a last line may run this far into the next episode

    public static (List<MergePlan> Plans, List<string> Notes) Plan(IEnumerable<string> discPaths, IEnumerable<string> subs, IReadOnlyDictionary<string, string>? playlistOf = null)
    {
        var notes = new List<string>();
        var discs = new List<(string Root, Playlist List)>();
        foreach (var root in Bdmv.FindDiscs(discPaths))
        {
            var pl = playlistOf != null && playlistOf.TryGetValue(root, out var chosen) ? Bdmv.Read(chosen) : Bdmv.MainPlaylist(Bdmv.Playlists(root));
            if (pl == null) notes.Add($"{Path.GetFileName(root)}：没有找到正片播放列表");
            else discs.Add((root, pl));
        }
        var plans = new List<MergePlan>();
        if (discs.Count == 0) { notes.Add("没有找到原盘（含 BDMV/PLAYLIST 的文件夹）"); return (plans, notes); }

        foreach (var group in subs.Where(Sync.IsSub).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).GroupBy(Renamer.LangOf))
        {
            string label = group.Key switch { "sc" => "简体", "tc" => "繁体", _ => "字幕" };
            var exts = group.Select(s => Path.GetExtension(s).ToLowerInvariant()).Distinct().ToList();
            if (exts.Count > 1) { notes.Add($"{label}：格式不一致（{string.Join(" / ", exts)}），先统一格式再合并"); continue; }

            // Timed to a clip: placed on that clip, no guessing.
            var anchored = new Dictionary<int, List<MergeEpisode>>();
            var rest = new List<string>();
            foreach (var s in group)
            {
                var clip = ClipName().Match(Path.GetFileName(s));
                int d = !clip.Success ? -1 : FindRootIndex(discs, s) is int i and >= 0 ? i : discs.Count == 1 ? 0 : -1;
                var item = d < 0 ? null : discs[d].List.Items.FirstOrDefault(x => x.Clip == clip.Groups[1].Value);
                if (item == null) { rest.Add(s); continue; }
                double offset = item.Start + item.ClipStart - item.InTime;
                (anchored.TryGetValue(d, out var l) ? l : anchored[d] = []).Add(new(s, offset, ChapterAt(discs[d].List, item.Start), item.Length - LastEnd(s), item.Clip));
            }

            var queue = rest.OrderBy(s => Sync.EpisodeOf(s) is { } e ? double.Parse(e, CultureInfo.InvariantCulture) : double.MaxValue)
                            .ThenBy(s => Path.GetFileName(s), NaturalOrder.Instance).ToList();
            var ends = queue.ToDictionary(s => s, LastEnd);
            for (int d = 0; d < discs.Count; d++)
            {
                var (root, list) = discs[d];
                if (anchored.TryGetValue(d, out var fixedEps))
                {
                    plans.Add(new(root, list, group.Key, exts[0], [.. fixedEps.OrderBy(e => e.Offset)]));
                    continue;
                }
                if (queue.Count == 0) continue;
                // As many of the remaining episodes as fit on this disc; the rest go to the next one.
                List<MergeEpisode>? best = null;
                for (int k = queue.Count; k >= 1 && best == null; k--) best = Place(list, [.. queue.Take(k).Select(s => (s, ends[s]))]);
                if (best == null) { notes.Add($"{label}：{Path.GetFileName(queue[0])} 比 {Path.GetFileName(root)} 的正片还长，放不下"); break; }
                queue.RemoveRange(0, best.Count);
                plans.Add(new(root, list, group.Key, exts[0], best));
            }
            if (queue.Count > 0) notes.Add($"{label}：{queue.Count} 个字幕没有位置（原盘里的集数不够）：{string.Join("、", queue.Select(Path.GetFileName))}");
        }
        return (plans, notes);
    }

    [GeneratedRegex(@"^(\d{5})(?:\.|$)")]
    private static partial Regex ClipName();

    static int FindRootIndex(List<(string Root, Playlist List)> discs, string sub) =>
        Bdmv.FindRoot(sub) is { } r ? discs.FindIndex(d => string.Equals(d.Root, r, StringComparison.OrdinalIgnoreCase)) : -1;

    static int ChapterAt(Playlist p, double t) => p.Chapters.Count(c => c.Time <= t + 0.05);

    /// <summary>End of the subtitle's last dialogue line (seconds); a stray line far after everything else doesn't count.</summary>
    public static double LastEnd(string sub)
    {
        var ends = SubtitleDoc.Load(sub).Events.Where(e => !e.IsComment && e.End > e.Start).Select(e => e.End / 1000.0).Order().ToList();
        if (ends.Count == 0) return 0;
        int n = ends.Count - 1;
        // ponytail: one or two lines ending 10+ minutes after a full script are a typo / long sign, not the episode length
        while (ends.Count >= 20 && n >= ends.Count - 2 && ends[n] - ends[n - 1] > 600) n--;
        return ends[n];
    }

    /// <summary>
    /// Where each episode starts. Candidates: every chapter and clip start. Hard rule: a subtitle ends before the next
    /// episode (± <see cref="Tolerance"/>). Among the ways that fit, the one where episodes look alike: same number of
    /// chapters each, starting on clips rather than mid-clip, least spare time. Null when the episodes don't fit.
    /// </summary>
    internal static List<MergeEpisode>? Place(Playlist p, List<(string Sub, double End)> eps)
    {
        var cand = p.Chapters.Select(c => (T: c.Time, Item: p.Items.Any(i => Math.Abs(i.Start - c.Time) < 0.05)))
            .Concat(p.Items.Select(i => (T: i.Start, Item: true)))
            .Append((T: 0.0, Item: true))
            .OrderBy(c => c.T).ThenByDescending(c => c.Item)
            .Aggregate(new List<(double T, bool Item)>(), (l, c) => { if (l.Count == 0 || c.T - l[^1].T > 0.05) l.Add(c); return l; });
        int m = cand.Count, k = eps.Count;
        double total = p.Duration;
        if (eps.Sum(e => e.End) > total + Tolerance * k) return null;
        // chapters before each candidate: an episode from q to j holds ch[j] - ch[q], the last one chTotal - ch[j]
        var ch = cand.Select(c => p.Chapters.Count(x => x.Time < c.T - 0.05)).ToArray();
        int chTotal = p.Chapters.Count;

        // ponytail: O(C·k·m²) with m ≤ a few hundred marks — instant
        const double SlackWeight = 0.02, OffClip = 2, Irregular = 2;
        double bestCost = double.MaxValue;
        int[]? bestPick = null;
        int maxPer = Math.Max(1, chTotal);
        for (int c = 0; c <= maxPer; c++)
        {
            var cost = new double[k, m];
            var from = new int[k, m];
            for (int i = 0; i < k; i++) for (int j = 0; j < m; j++) cost[i, j] = double.MaxValue;
            for (int j = 0; j < m; j++) cost[0, j] = SlackWeight * Sq(cand[j].T / 60) + (cand[j].Item ? 0 : OffClip);
            for (int i = 1; i < k; i++)
                for (int j = 1; j < m; j++)
                {
                    double add = cand[j].Item ? 0 : OffClip;
                    for (int q = 0; q < j; q++)
                    {
                        if (cost[i - 1, q] == double.MaxValue) continue;
                        double slack = cand[j].T - cand[q].T - eps[i - 1].End;
                        if (slack < -Tolerance) break; // later q only shrink the slack
                        double v = cost[i - 1, q] + add + SlackWeight * Sq(slack / 60) + Irregular * Sq(ch[j] - ch[q] - c);
                        if (v < cost[i, j]) { cost[i, j] = v; from[i, j] = q; }
                    }
                }
            for (int j = 0; j < m; j++)
            {
                if (cost[k - 1, j] == double.MaxValue) continue;
                double slack = total - cand[j].T - eps[k - 1].End;
                if (slack < -Tolerance) continue;
                double v = cost[k - 1, j] + SlackWeight * Sq(slack / 60) + Irregular * Sq(chTotal - ch[j] - c);
                if (v >= bestCost) continue;
                bestCost = v;
                bestPick = new int[k];
                for (int i = k - 1, at = j; i >= 0; at = from[i, at], i--) bestPick[i] = at;
            }
        }
        if (bestPick == null) return null;
        return [.. eps.Select((e, i) =>
        {
            double start = cand[bestPick[i]].T, next = i + 1 < k ? cand[bestPick[i + 1]].T : total;
            return new MergeEpisode(e.Sub, start, ChapterAt(p, start), next - start - e.End, null);
        })];
    }

    static double Sq(double x) => x * x;

    /// <summary>Episode i is moved to start at <paramref name="time"/> by hand; the others keep their places.</summary>
    public static MergePlan Move(MergePlan plan, int i, double time)
    {
        var eps = plan.Episodes.ToList();
        eps[i] = eps[i] with { Offset = time, Chapter = ChapterAt(plan.Playlist, time), Clip = null };
        for (int j = 0; j < eps.Count; j++)
        {
            double next = j + 1 < eps.Count ? eps[j + 1].Offset : plan.Playlist.Duration;
            eps[j] = eps[j] with { Slack = next - eps[j].Offset - LastEnd(eps[j].Sub) };
        }
        return plan with { Episodes = eps };
    }

    /// <summary>Writes the merged subtitle(s). Returns what was written; never replaces a file SubMatcher didn't make.</summary>
    public static (List<string> Written, List<string> Notes) Write(MergePlan plan)
    {
        var (text, notes) = Merge(plan.Episodes.Select(e => (e.Sub, e.Offset)).ToList());
        var written = new List<string>();
        foreach (var o in plan.Outputs())
        {
            if (File.Exists(o) && !TextEncoding.Read(o).Contains(Marker))
            {
                notes.Add($"没有覆盖已有的 {o}（不是本工具生成的）");
                continue;
            }
            try
            {
                File.WriteAllText(o, text, new UTF8Encoding(true));
                written.Add(o);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add($"写不进 {o}：{e.Message}"); }
        }
        return (written, notes);
    }

    /// <summary>Joins subtitles, each shifted by its offset (seconds). ASS/SSA keep their styles and embedded fonts.</summary>
    public static (string Text, List<string> Notes) Merge(IReadOnlyList<(string Path, double Offset)> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("没有字幕");
        return Path.GetExtension(parts[0].Path).Equals(".srt", StringComparison.OrdinalIgnoreCase)
            ? (MergeSrt(parts), [])
            : MergeAss(parts);
    }

    // Offsets in whole centiseconds so every line moves by exactly the same amount ASS can store.
    static long OffsetMs(double seconds) => (long)Math.Round(seconds * 100) * 10;

    static string MergeSrt(IReadOnlyList<(string Path, double Offset)> parts)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var (path, offset) in parts)
            foreach (var e in SubtitleDoc.Load(path).Events)
                sb.Append(++n).Append("\r\n")
                  .Append(SubtitleDoc.FormatSrtTime(e.Start + OffsetMs(offset))).Append(" --> ").Append(SubtitleDoc.FormatSrtTime(e.End + OffsetMs(offset))).Append("\r\n")
                  .Append(e.Text.Replace("\\N", "\r\n")).Append("\r\n\r\n");
        return sb.ToString();
    }

    sealed class Section(string header)
    {
        public string Header = header;
        public List<string> Lines = [];
    }

    static List<Section> Sections(string text)
    {
        var list = new List<Section> { new("") };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) list.Add(new(t));
            else list[^1].Lines.Add(line);
        }
        return list;
    }

    static bool IsStyles(string h) => h.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) || h.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
    static bool Is(string h, string name) => h.Equals(name, StringComparison.OrdinalIgnoreCase);
    static bool Starts(string line, string key) => line.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase);
    static string After(string line) => line[(line.IndexOf(':') + 1)..];

    static List<string> FormatOf(IEnumerable<string> lines) =>
        lines.FirstOrDefault(l => Starts(l, "Format:")) is { } f ? [.. After(f).Split(',').Select(s => s.Trim().ToLowerInvariant())] : [];

    static (string? ResX, string? ResY) PlayRes(List<Section> s)
    {
        var info = s.FirstOrDefault(x => Is(x.Header, "[Script Info]"))?.Lines ?? [];
        string? Get(string k) => info.FirstOrDefault(l => Starts(l, k + ":")) is { } l ? After(l).Trim() : null;
        return (Get("PlayResX"), Get("PlayResY"));
    }

    static (string Text, List<string> Notes) MergeAss(IReadOnlyList<(string Path, double Offset)> parts)
    {
        var notes = new List<string>();
        var docs = parts.Select(p => Sections(TextEncoding.Read(p.Path))).ToList();
        var first = docs[0];
        var info = first.FirstOrDefault(s => Is(s.Header, "[Script Info]")) ?? new Section("[Script Info]");
        var stylesHeader = first.FirstOrDefault(s => IsStyles(s.Header))?.Header ?? "[V4+ Styles]";
        var styleFormat = docs.SelectMany(d => d).FirstOrDefault(s => IsStyles(s.Header))?.Lines.FirstOrDefault(l => Starts(l, "Format:"));
        var eventFormatLine = docs.SelectMany(d => d).FirstOrDefault(s => Is(s.Header, "[Events]"))?.Lines.FirstOrDefault(l => Starts(l, "Format:"))
            ?? "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
        var eventFormat = FormatOf([eventFormatLine]);

        var res0 = PlayRes(first);
        var styles = new List<string>();
        var styleBody = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var embedded = new Dictionary<string, List<(string Name, List<string> Data)>>(StringComparer.OrdinalIgnoreCase);
        var events = new List<string>();

        for (int d = 0; d < docs.Count; d++)
        {
            var doc = docs[d];
            string name = Path.GetFileName(parts[d].Path);
            if (d > 0 && PlayRes(doc) != res0)
                notes.Add($"{name} 的分辨率（PlayRes {PlayRes(doc).ResX}x{PlayRes(doc).ResY}）和第一集不同，这一集的位置、字号可能不对");

            // Styles: same name + same definition = shared; same name, different look = renamed for this episode.
            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in doc.Where(s => IsStyles(s.Header)).SelectMany(s => s.Lines).Where(l => Starts(l, "Style:")))
            {
                var body = After(line);
                int comma = body.IndexOf(',');
                if (comma < 0) continue;
                string sname = body[..comma].Trim(), rest = body[comma..].Replace(" ", "");
                if (!styleBody.TryGetValue(sname, out var have)) { styleBody[sname] = rest; styles.Add("Style: " + body.Trim()); continue; }
                if (have == rest) continue;
                string alias = sname;
                for (int n = d + 1; ; n++)
                {
                    alias = $"{sname} ({n})";
                    if (!styleBody.TryGetValue(alias, out var a)) { styleBody[alias] = rest; styles.Add($"Style: {alias}{body[comma..].TrimEnd()}"); break; }
                    if (a == rest) break;
                }
                rename[sname] = alias;
            }

            // Embedded fonts / pictures: kept once each.
            foreach (var sec in doc.Where(s => Is(s.Header, "[Fonts]") || Is(s.Header, "[Graphics]")))
            {
                var list = embedded.TryGetValue(sec.Header, out var l) ? l : embedded[sec.Header] = [];
                (string Name, List<string> Data)? cur = null;
                void Flush()
                {
                    if (cur is not { } c) return;
                    if (list.Any(x => x.Name == c.Name && x.Data.SequenceEqual(c.Data))) return;
                    list.Add(list.Any(x => x.Name == c.Name) ? (Path.GetFileNameWithoutExtension(c.Name) + $"_{d + 1}" + Path.GetExtension(c.Name), c.Data) : c);
                }
                foreach (var line in sec.Lines)
                {
                    if (Starts(line, "fontname:") || Starts(line, "filename:")) { Flush(); cur = (After(line).Trim(), []); }
                    else if (cur != null && line.Trim().Length > 0) cur.Value.Data.Add(line.Trim());
                }
                Flush();
            }

            long shift = OffsetMs(parts[d].Offset);
            foreach (var sec in doc.Where(s => Is(s.Header, "[Events]")))
            {
                var fmt = FormatOf(sec.Lines);
                if (fmt.Count == 0) fmt = eventFormat;
                foreach (var line in sec.Lines)
                {
                    bool comment = Starts(line, "Comment:");
                    if (!comment && !Starts(line, "Dialogue:")) continue;
                    var f = After(line).Split(',', fmt.Count);
                    if (f.Length < fmt.Count) continue;
                    string Field(string key) => fmt.IndexOf(key) is var i and >= 0 ? f[i] : "";
                    var outF = eventFormat.Select(key => key switch
                    {
                        "start" or "end" => SubtitleDoc.TryParseAssTime(Field(key), out var ms) ? SubtitleDoc.FormatAssTime(ms + shift) : Field(key),
                        "style" => rename.TryGetValue(Field(key).Trim(), out var r) ? r : Field(key).Trim(),
                        "text" => rename.Count == 0 ? Field(key) : InlineStyle().Replace(Field(key), m => rename.TryGetValue(m.Groups[1].Value, out var r) ? @"\r" + r : m.Value),
                        _ => Field(key).Trim(),
                    });
                    events.Add((comment ? "Comment: " : "Dialogue: ") + string.Join(',', outF));
                }
            }
        }

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");
        Line("[Script Info]");
        Line(Marker + $"（{parts.Count} 个字幕：{string.Join("、", parts.Select(p => Path.GetFileName(p.Path)))}）");
        foreach (var l in info.Lines) if (!string.IsNullOrWhiteSpace(l)) Line(l);
        Line("");
        Line(stylesHeader);
        if (styleFormat != null) Line(styleFormat.Trim());
        foreach (var s in styles) Line(s);
        Line("");
        foreach (var (header, list) in embedded.Where(e => e.Value.Count > 0))
        {
            Line(header);
            foreach (var (n, data) in list)
            {
                Line((Is(header, "[Fonts]") ? "fontname: " : "filename: ") + n);
                foreach (var l in data) Line(l);
            }
            Line("");
        }
        Line("[Events]");
        Line(eventFormatLine.Trim());
        foreach (var e in events) Line(e);
        return (sb.ToString(), notes);
    }

    [GeneratedRegex(@"\\r([^\\}]+)")]
    private static partial Regex InlineStyle();
}
