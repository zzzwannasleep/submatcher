using System.Text.RegularExpressions;

namespace SubMatcher.Core;

public sealed class SyncOptions
{
    /// <summary>Search ±this many seconds around the last good shift before falling back to a full-video search.</summary>
    public double WindowSeconds { get; set; } = 10;
    /// <summary>0 = use the target video's frame rate (halved until ≤ 31).</summary>
    public double AnalysisFps { get; set; }
    /// <summary>Mean (1 - correlation) above which a line counts as "not found".</summary>
    public double MaxCost { get; set; } = 0.4;
    /// <summary>Found, but worth a look in the check log.</summary>
    public double WarnCost { get; set; } = 0.2;
    public bool SnapToCuts { get; set; } = true;
    public double MinLineSeconds { get; set; } = 1.5;
    public bool HwAccel { get; set; }
    public bool UseCache { get; set; } = true;
    /// <summary>Embedded subtitle stream to use when no subtitle file is given.</summary>
    public int SubtitleStream { get; set; }
}

public enum MatchStatus { Ok, Low, Jump, Smoothed, Static, Empty, Inherited, CutMiss, OutOfRange }

public sealed class EventResult
{
    public int Index { get; init; }
    public long OldStart { get; init; }
    public long OldEnd { get; init; }
    public long NewStart { get; set; }
    public long NewEnd { get; set; }
    public int ShiftFrames { get; set; }
    public double Cost { get; set; }
    public MatchStatus Status { get; set; }
    public string Text { get; init; } = "";
    public bool IsComment { get; init; }
    /// <summary>Typesetting (positioned / animated / very short) rather than dialogue: must be frame-exact.</summary>
    public bool IsSign { get; init; }
    public long ShiftMs => NewStart - OldStart;
    public bool NeedsCheck => Status is MatchStatus.Low or MatchStatus.Jump or MatchStatus.Inherited or MatchStatus.CutMiss or MatchStatus.OutOfRange;
}

/// <summary>
/// Sushi's idea with pictures instead of sound: for every line, slide its frames over the target
/// and keep the offset where the pictures correlate best. Then clean up like Sushi does: lines that
/// found nothing inherit a neighbour's shift, lone outliers get voted down, runs of equal shifts are
/// refined together. Everything works in whole target frames, and output times are placed inside
/// the frame they must start/stop on, so signs stay frame-exact through ASS's 10 ms rounding.
/// </summary>
public sealed class Matcher(Fingerprint a, Fingerprint b, SyncOptions o)
{
    const double TieEps = 0.01;
    readonly double _fps = a.Fps;

    /// <summary>Presentation time of each video's first frame relative to playback zero (ms): frame k is shown at start + k/fps.
    /// Usually 0; m2ts/ts rips often start later, and that difference is part of the real shift.</summary>
    public double SourceStartMs { get; init; }
    public double TargetStartMs { get; init; }

    readonly record struct Hit(int Offset, double Cost, bool Ambiguous, bool Jump);

    static readonly Regex SignTags = new(@"\\(pos|move|org|clip|iclip|t|frz|frx|fry|fax|fay|p[1-9])\s*\(|\\p[1-9]\b|\\fr[xyz]?-?\d", RegexOptions.Compiled);
    static readonly Regex AnyTag = new(@"\{[^}]*\}", RegexOptions.Compiled);

    public List<EventResult> Match(IReadOnlyList<SubEvent> events, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int n = events.Count;
        var order = Enumerable.Range(0, n).OrderBy(i => events[i].Start).ThenBy(i => i).ToArray();
        var empty = events.Select(e => e.End <= e.Start || AnyTag.Replace(e.Text, "").Replace("\\N", "").Trim().Length == 0 && !SignTags.IsMatch(e.Text)).ToArray();
        var sign = events.Select((e, i) => !empty[i] && IsSign(e)).ToArray();

        // Frame-by-frame animated typesetting (runs of 1–3 frame lines) is one unit: a single line has too few
        // frames to match on its own, and the whole run must move by the same number of frames.
        var unit = Clusters(events, order, empty);
        var win = new (int S, int E)[n];
        for (int i = 0; i < n; i++) win[i] = empty[i] ? (-1, -1) : Window(unit[i].Start, unit[i].End);

        var hits = new Hit?[n];
        var cache = new Dictionary<(int, int), Hit>();
        int radius = Math.Max(1, (int)Math.Round(o.WindowSeconds * _fps));

        // Pass 1: search each line near the last trustworthy shift.
        int prior = 0;
        for (int k = 0; k < n; k++)
        {
            ct.ThrowIfCancellationRequested();
            int i = order[k];
            if (win[i].S < 0) continue; // placeholder line, or starts after the source video ends
            if (!cache.TryGetValue(win[i], out var hit))
            {
                var (s, e) = win[i];
                hit = Search(s, e, prior - radius, prior + radius, prior, Step(s, e));
                if (hit.Cost > o.MaxCost)
                {
                    var far = FullSearch(s, e);
                    if (far.Cost < hit.Cost - 0.05) hit = far with { Jump = true };
                }
                cache[win[i]] = hit;
            }
            hits[i] = hit;
            if (Reliable(hit)) prior = hit.Offset;
            progress?.Report((k + 1.0) / n);
        }

        if (!hits.Any(h => h is { } x && Reliable(x)))
            throw new InvalidOperationException("两个片源的画面完全对不上：确认选对了视频，或调高「最大代价」。");

        var shift = new int[n];
        var cost = new double[n];
        var status = new MatchStatus[n];
        for (int i = 0; i < n; i++)
        {
            if (hits[i] is not { } h) { status[i] = empty[i] ? MatchStatus.Empty : MatchStatus.OutOfRange; continue; }
            shift[i] = h.Offset; cost[i] = h.Cost;
            status[i] = h.Jump ? MatchStatus.Jump : h.Cost > o.WarnCost ? MatchStatus.Low : MatchStatus.Ok;
        }
        bool IsReliable(int i) => hits[i] is { } h && Reliable(h) && status[i] != MatchStatus.Inherited;

        // Pass 2: unreliable lines take whichever neighbouring shift fits their frames better.
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            if (IsReliable(i)) continue;
            int? p = Neighbour(order, k, -1, IsReliable), q = Neighbour(order, k, +1, IsReliable);
            var cands = new[] { p, q }.Where(x => x != null).Select(x => shift[x!.Value]).Distinct().ToList();
            if (win[i].S < 0) { shift[i] = cands[0]; continue; }
            var (s, e) = win[i];
            int best = cands.MinBy(c => WindowCost(s, e, c, Step(s, e)));
            double bestCost = WindowCost(s, e, best, Step(s, e));
            // An ambiguous (static) shot keeps its own answer only if it is clearly better than the neighbours'.
            if (hits[i] is { Ambiguous: true } h && h.Cost <= o.MaxCost && h.Cost < bestCost - 2 * TieEps) continue;
            shift[i] = best; cost[i] = bestCost;
            // A static shot that fits the neighbours well is fine; anything else gets a human look.
            status[i] = hits[i] is { Ambiguous: true } && bestCost <= o.WarnCost ? MatchStatus.Static : MatchStatus.Inherited;
        }

        // Pass 3: a lone line disagreeing with two agreeing neighbours is voted down if their shift fits nearly as well.
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            if (!IsReliable(i)) continue;
            int? p = Neighbour(order, k, -1, IsReliable), q = Neighbour(order, k, +1, IsReliable);
            if (p is not { } pi || q is not { } qi || Math.Abs(shift[pi] - shift[qi]) > 1 || Math.Abs(shift[i] - shift[pi]) <= 1) continue;
            var (s, e) = win[i];
            double c = WindowCost(s, e, shift[pi], Step(s, e));
            if (c <= cost[i] + 0.03) { shift[i] = shift[pi]; cost[i] = c; status[i] = MatchStatus.Smoothed; }
        }

        // Pass 4: runs whose shifts differ by at most one frame are one segment; search it as a whole to kill jitter.
        for (int k = 0; k < n;)
        {
            int lo = shift[order[k]], hi = lo, end = k + 1;
            while (end < n && Math.Max(hi, shift[order[end]]) - Math.Min(lo, shift[order[end]]) <= 1)
            {
                lo = Math.Min(lo, shift[order[end]]); hi = Math.Max(hi, shift[order[end]]); end++;
            }
            if (hi > lo)
            {
                var run = order[k..end].Where(i => win[i].S >= 0).ToList();
                if (run.Count > 0)
                {
                    int s = run.Min(i => win[i].S), e = run.Max(i => win[i].E);
                    var r = Search(s, e, lo, hi, shift[run[run.Count / 2]], Step(s, e));
                    foreach (var i in order[k..end]) shift[i] = r.Offset;
                }
            }
            k = end;
        }

        // An animation unit never splits: everything in it takes the unit's most common shift.
        foreach (var g in Enumerable.Range(0, n).GroupBy(i => unit[i]).Where(g => g.Count() > 1))
        {
            int common = g.GroupBy(i => shift[i]).MaxBy(x => x.Count())!.Key;
            foreach (var i in g) shift[i] = common;
        }

        var results = new List<EventResult>(n);
        for (int i = 0; i < n; i++)
        {
            var ev = events[i];
            var (ns, startCutMiss) = Place(ev.Start, shift[i]);
            var (ne, endCutMiss) = Place(ev.End, shift[i]);
            var st = status[i];
            // Typesetting that sat on a hard cut in the source but lands off any cut in the target flashes for a frame.
            if (sign[i] && (startCutMiss || endCutMiss) && st is MatchStatus.Ok or MatchStatus.Smoothed or MatchStatus.Static) st = MatchStatus.CutMiss;
            var r = new EventResult
            {
                Index = i, OldStart = ev.Start, OldEnd = ev.End, Text = ev.Text, IsComment = ev.IsComment, IsSign = sign[i],
                ShiftFrames = shift[i], Cost = cost[i], Status = st, NewStart = ns, NewEnd = ne,
            };
            if (r.NewEnd < r.NewStart) r.NewEnd = r.NewStart + (ev.End - ev.Start);
            results.Add(r);
        }
        return results;
    }

    /// <summary>Positioned, moving, drawn, rotated or ≤3-frame lines are typesetting, not dialogue.</summary>
    bool IsSign(SubEvent e) => SignTags.IsMatch(e.Text) || e.End - e.Start <= 3 * 1000 / _fps + 1;

    /// <summary>Runs of consecutive very short lines (frame-by-frame animation) become one (start, end) unit.</summary>
    (long Start, long End)[] Clusters(IReadOnlyList<SubEvent> ev, int[] order, bool[] empty)
    {
        var unit = ev.Select(e => (e.Start, e.End)).ToArray();
        double frame = 1000 / _fps, shortLine = 3 * frame + 1, gap = 2 * frame + 1;
        var run = new List<int>();
        void Flush()
        {
            if (run.Count > 1)
            {
                var span = (run.Min(i => ev[i].Start), run.Max(i => ev[i].End));
                foreach (var i in run) unit[i] = span;
            }
            run.Clear();
        }
        foreach (var i in order)
        {
            var e = ev[i];
            bool isShort = !empty[i] && e.End - e.Start <= shortLine;
            if (!isShort) { Flush(); continue; }
            if (run.Count > 0 && e.Start - run.Max(j => ev[j].End) > gap) Flush();
            run.Add(i);
        }
        Flush();
        return unit;
    }

    bool Reliable(Hit h) => h.Cost <= o.MaxCost && !h.Ambiguous;

    static int? Neighbour(int[] order, int k, int dir, Func<int, bool> ok)
    {
        for (int j = k + dir; j >= 0 && j < order.Length; j += dir) if (ok(order[j])) return order[j];
        return null;
    }

    (int S, int E) Window(long startMs, long endMs)
    {
        int s = (int)Math.Floor((startMs - SourceStartMs) * _fps / 1000), e = (int)Math.Ceiling((endMs - SourceStartMs) * _fps / 1000);
        if (s >= a.Count) return (-1, -1);
        if (e <= s) e = s + 1;
        int min = Math.Max(1, (int)Math.Round(o.MinLineSeconds * _fps));
        if (e - s < min) { s = (s + e - min) / 2; e = s + min; }
        if (s < 0) { e -= s; s = 0; }
        if (e > a.Count) { s = Math.Max(0, s - (e - a.Count)); e = a.Count; }
        return (s, e);
    }

    // ponytail: long windows (signs spanning half a minute, pass-4 segments) are subsampled to ~240 frames; plenty of evidence.
    static int Step(int s, int e) => Math.Max(1, (e - s) / 240);

    double WindowCost(int s, int e, int offset, int step)
    {
        double sum = 0;
        int cnt = 0;
        for (int i = s; i < e; i += step, cnt++)
        {
            int j = i + offset;
            sum += j < 0 || j >= b.Count ? 1 : 1 - Fingerprint.Dot(a.Frame(i), b.Frame(j));
        }
        return sum / cnt;
    }

    Hit Search(int s, int e, int lo, int hi, int prefer, int step)
    {
        var costs = new double[hi - lo + 1];
        Parallel.For(0, costs.Length, k => costs[k] = WindowCost(s, e, lo + k, step));
        double best = costs.Min();
        int pick = int.MinValue, tieMin = int.MaxValue, tieMax = int.MinValue;
        for (int k = 0; k < costs.Length; k++)
        {
            if (costs[k] > best + TieEps) continue;
            int off = lo + k;
            tieMin = Math.Min(tieMin, off); tieMax = Math.Max(tieMax, off);
            if (pick == int.MinValue || Math.Abs(off - prefer) < Math.Abs(pick - prefer)) pick = off;
        }
        // Confidence: the best offset must clearly beat the best one that isn't its immediate neighbour. Static shots
        // (an ED card, a dark scene) match almost equally well everywhere and are not evidence of anything.
        double second = double.MaxValue;
        for (int k = 0; k < costs.Length; k++) if (Math.Abs(lo + k - pick) > 3) second = Math.Min(second, costs[k]);
        bool flat = second < costs[pick - lo] * 2 + 0.02;
        return new Hit(pick, costs[pick - lo], tieMax - tieMin > 2 || flat, false);
    }

    /// <summary>Coarse scan of every possible offset with a handful of frames, then a fine local search.</summary>
    Hit FullSearch(int s, int e)
    {
        int lo = -s, hi = b.Count - e;
        if (hi < lo) return new Hit(0, double.MaxValue, true, false);
        int coarseStep = Math.Max(1, (e - s) / 16);
        var costs = new double[hi - lo + 1];
        Parallel.For(0, costs.Length, k => costs[k] = WindowCost(s, e, lo + k, coarseStep));
        int c = lo + Array.IndexOf(costs, costs.Min());
        return Search(s, e, c - 3, c + 3, c, Step(s, e)) with { Ambiguous = false };
    }

    /// <summary>
    /// New timestamp for a start/end: shift by whole frames, snap onto the matching hard cut when the original sat on
    /// one, then write the time halfway between the target frame it must switch on and the frame before. A renderer
    /// shows a line on frames with pts ≥ start and &lt; end, so a mid-frame time survives ASS's 10 ms rounding and
    /// never slips to a neighbouring frame (unlike e.g. a raw +0.955 s that lands a hair before the cut).
    /// Also reports whether a source cut had no counterpart in the target.
    /// </summary>
    (long Time, bool CutMiss) Place(long t, int shiftFrames)
    {
        double shifted = t - SourceStartMs + TargetStartMs + shiftFrames * 1000 / _fps;
        // first target frame whose pts ≥ shifted (the frame the line switches on / off at)
        double k = Math.Ceiling((shifted - TargetStartMs) * _fps / 1000 - 1e-6);
        bool miss = false;
        // the source frame the line switches on/off at
        int n = (int)Math.Ceiling((t - SourceStartMs) * _fps / 1000 - 1e-6);
        if (o.SnapToCuts && NearestCut(a, n, 1) is { } ca)
        {
            // It was timed against a cut in the source: keep exactly the same frame offset from the matching cut in the
            // target (usually 0). Equal to the plain shift when the grids line up; fixes resampling jitter when they don't.
            if (NearestCut(b, ca + shiftFrames, 1) is { } cb) k = cb + (n - ca);
            else miss = true;
        }
        if (k <= 0) return (Math.Max(0, (long)Math.Round(shifted)), miss);
        return ((long)Math.Round(TargetStartMs + (k - 0.5) * 1000 / _fps), miss);
    }

    static int? NearestCut(Fingerprint fp, double f, int tol)
    {
        int? best = null;
        for (int c = (int)Math.Floor(f) - tol; c <= (int)Math.Ceiling(f) + tol; c++)
            if (fp.IsCut(c) && Math.Abs(c - f) <= tol && (best is null || Math.Abs(c - f) < Math.Abs(best.Value - f))) best = c;
        return best;
    }
}
