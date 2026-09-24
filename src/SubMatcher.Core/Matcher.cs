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

public enum MatchStatus { Ok, Low, Jump, Smoothed, Static, Inherited, OutOfRange }

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
    public long ShiftMs => NewStart - OldStart;
    public bool NeedsCheck => Status is MatchStatus.Low or MatchStatus.Jump or MatchStatus.Inherited or MatchStatus.OutOfRange;
}

/// <summary>
/// Sushi's idea with pictures instead of sound: for every line, slide its frames over the target
/// and keep the offset where the pictures correlate best. Then clean up like Sushi does: lines that
/// found nothing inherit a neighbour's shift, lone outliers get voted down, runs of equal shifts are
/// refined together.
/// </summary>
public sealed class Matcher(Fingerprint a, Fingerprint b, SyncOptions o)
{
    const double TieEps = 0.01;
    readonly double _fps = a.Fps;

    readonly record struct Hit(int Offset, double Cost, bool Ambiguous, bool Jump);

    public List<EventResult> Match(IReadOnlyList<SubEvent> events, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int n = events.Count;
        var order = Enumerable.Range(0, n).OrderBy(i => events[i].Start).ThenBy(i => i).ToArray();
        var win = new (int S, int E)[n];
        var hits = new Hit?[n];
        var cache = new Dictionary<(int, int), Hit>();
        int radius = Math.Max(1, (int)Math.Round(o.WindowSeconds * _fps));

        // Pass 1: search each line near the last trustworthy shift.
        int prior = 0;
        for (int k = 0; k < n; k++)
        {
            ct.ThrowIfCancellationRequested();
            int i = order[k];
            win[i] = Window(events[i]);
            if (win[i].S < 0) continue; // line starts after the source video ends
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
            if (hits[i] is not { } h) { status[i] = MatchStatus.OutOfRange; continue; }
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

        var results = new List<EventResult>(n);
        for (int i = 0; i < n; i++)
        {
            var ev = events[i];
            var r = new EventResult
            {
                Index = i, OldStart = ev.Start, OldEnd = ev.End, Text = ev.Text, IsComment = ev.IsComment,
                ShiftFrames = shift[i], Cost = cost[i], Status = status[i],
                NewStart = Snap(ev.Start, shift[i]), NewEnd = Snap(ev.End, shift[i]),
            };
            if (r.NewEnd < r.NewStart) r.NewEnd = r.NewStart + (ev.End - ev.Start);
            results.Add(r);
        }
        return results;
    }

    bool Reliable(Hit h) => h.Cost <= o.MaxCost && !h.Ambiguous;

    static int? Neighbour(int[] order, int k, int dir, Func<int, bool> ok)
    {
        for (int j = k + dir; j >= 0 && j < order.Length; j += dir) if (ok(order[j])) return order[j];
        return null;
    }

    (int S, int E) Window(SubEvent ev)
    {
        int s = (int)Math.Floor(ev.Start * _fps / 1000), e = (int)Math.Ceiling(ev.End * _fps / 1000);
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
        return new Hit(pick, costs[pick - lo], tieMax - tieMin > 2, false);
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
        return Search(s, e, c - 3, c + 3, c, Step(s, e));
    }

    /// <summary>Shifts a timestamp; if it sat on a hard cut in the source, lands it on the matching cut in the target.</summary>
    long Snap(long t, int shiftFrames)
    {
        long shifted = t + (long)Math.Round(shiftFrames * 1000 / _fps);
        if (!o.SnapToCuts) return shifted;
        double f = t * _fps / 1000;
        int? ca = NearestCut(a, f, 1);
        if (ca is null) return shifted;
        int? cb = NearestCut(b, ca.Value + shiftFrames, 1);
        return cb is null ? shifted : t + (long)Math.Round((cb.Value - ca.Value) * 1000 / _fps);
    }

    static int? NearestCut(Fingerprint fp, double f, int tol)
    {
        int? best = null;
        for (int c = (int)Math.Floor(f) - tol; c <= (int)Math.Ceiling(f) + tol; c++)
            if (fp.IsCut(c) && Math.Abs(c - f) <= tol && (best is null || Math.Abs(c - f) < Math.Abs(best.Value - f))) best = c;
        return best;
    }
}
