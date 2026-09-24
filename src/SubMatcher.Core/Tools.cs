namespace SubMatcher.Core;

/// <summary>The small manual helpers (AdjustAssTime-style): fixed shift, FPS rescale, re-encode to UTF-8.</summary>
public static class Tools
{
    /// <summary>Shifts every event whose start lies in [from, to) by offset ms. Returns how many moved.</summary>
    public static int Shift(SubtitleDoc doc, long offset, long from = long.MinValue, long to = long.MaxValue)
    {
        int n = 0;
        foreach (var e in doc.Events.Where(e => e.Start >= from && e.Start < to))
        {
            e.Start = Math.Max(0, e.Start + offset);
            e.End = Math.Max(0, e.End + offset);
            n++;
        }
        return n;
    }

    /// <summary>"23.976", "29.97", "59.94" are shorthand for the NTSC n·1000/1001 rates; make them exact.</summary>
    public static double ExactFps(double f) =>
        Math.Abs(f - Math.Round(f)) > 0.01 && Math.Abs(f * 1.001 - Math.Round(f * 1.001)) < 0.01 ? Math.Round(f * 1.001) / 1.001 : f;

    /// <summary>Same frames played at a different rate (e.g. 25 → 23.976 PAL slowdown): t' = t · src / dst.</summary>
    public static void ConvertFps(SubtitleDoc doc, double srcFps, double dstFps)
    {
        double k = ExactFps(srcFps) / ExactFps(dstFps);
        foreach (var e in doc.Events) { e.Start = (long)Math.Round(e.Start * k); e.End = (long)Math.Round(e.End * k); }
    }
}
