using System.Text.RegularExpressions;

namespace SubMatcher.Core;

/// <summary>One planned rename: <see cref="Sub"/> becomes <see cref="Target"/> (named after <see cref="Video"/>).</summary>
public sealed record RenamePlan(string Sub, string Video, string Target, string? Lang)
{
    public bool NoOp => string.Equals(Path.GetFullPath(Sub), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Auto-rename subtitles to their video's name so players load them (like SubRenamer): match by episode number,
/// one subtitle per episode and language (简 → .sc, 繁 → .tc). A subset (fonts embedded) version wins over a plain one.
/// </summary>
public static partial class Renamer
{
    [GeneratedRegex(@"(?:^|[\s_.\-\[(])(CHS|SC|GB|简|簡|简体|簡體|简日|簡日|简中|簡中|zh-?Hans|zh-?CN|chs_jp|sc_jp)(?=$|[\s_.\-\])])", RegexOptions.IgnoreCase)]
    private static partial Regex Simplified();

    [GeneratedRegex(@"(?:^|[\s_.\-\[(])(CHT|TC|BIG5|繁|繁体|繁體|繁日|繁中|zh-?Hant|zh-?TW|zh-?HK|cht_jp|tc_jp)(?=$|[\s_.\-\])])", RegexOptions.IgnoreCase)]
    private static partial Regex Traditional();

    /// <summary>"sc", "tc" or null from the file name.</summary>
    public static string? LangOf(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        bool s = Simplified().IsMatch(n), t = Traditional().IsMatch(n);
        return s == t ? null : s ? "sc" : "tc";
    }

    static bool IsSubset(string p) => Path.GetFileName(p).Contains(".subset.", StringComparison.OrdinalIgnoreCase);

    /// <param name="sc">suffix for simplified (default "sc" → video.sc.ass); "" = none</param>
    /// <param name="tc">suffix for traditional</param>
    public static List<RenamePlan> Plan(IEnumerable<string> videos, IEnumerable<string> subs, string sc = "sc", string tc = "tc")
    {
        var vs = videos.Where(Sync.IsVideo).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var ss = subs.Where(Sync.IsSub).Distinct().Where(File.Exists).ToList();
        var ev = vs.Select(Sync.EpisodeOf).ToList();
        bool byNumber = vs.Count > 0 && ev.All(e => e != null) && ev.Distinct().Count() == ev.Count;
        var plans = new List<RenamePlan>();

        foreach (var lang in ss.GroupBy(LangOf))
        {
            // Best candidate per video: subset beats plain, then the newest file.
            var byVideo = new Dictionary<string, string>();
            var list = lang.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                string? video = byNumber
                    ? Sync.EpisodeOf(list[i]) is { } e && ev.IndexOf(e) is var j and >= 0 ? vs[j] : null
                    : list.Count == vs.Count ? vs[i] : null; // no usable numbers: pair by name order when the counts agree
                if (video == null) continue;
                if (byVideo.TryGetValue(video, out var cur) && (IsSubset(cur), File.GetLastWriteTimeUtc(cur)).CompareTo((IsSubset(list[i]), File.GetLastWriteTimeUtc(list[i]))) >= 0)
                    continue;
                byVideo[video] = list[i];
            }
            string suffix = lang.Key switch { "sc" => sc, "tc" => tc, _ => "" };
            foreach (var (video, sub) in byVideo)
            {
                var name = Path.GetFileNameWithoutExtension(video) + (suffix.Length > 0 ? "." + suffix.Trim('.') : "") + Path.GetExtension(sub).ToLowerInvariant();
                plans.Add(new(sub, video, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video))!, name), lang.Key));
            }
        }
        return plans.OrderBy(p => p.Target, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Applies the plan. Nothing is lost: with <paramref name="backup"/> every subtitle that is moved or overwritten is first copied
    /// to "字幕备份" next to it; with <paramref name="copy"/> the originals stay where they are.
    /// </summary>
    public static int Apply(IEnumerable<RenamePlan> plans, bool copy, bool backup)
    {
        int n = 0;
        foreach (var p in plans.Where(p => !p.NoOp))
        {
            if (backup)
            {
                if (!copy) Backup(p.Sub);
                if (File.Exists(p.Target)) Backup(p.Target);
            }
            if (copy) File.Copy(p.Sub, p.Target, true);
            else File.Move(p.Sub, p.Target, true);
            n++;
        }
        return n;
    }

    static void Backup(string file)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "字幕备份")).FullName;
        var dest = Path.Combine(dir, Path.GetFileName(file));
        if (!File.Exists(dest)) File.Copy(file, dest);
    }

    /// <summary>Videos and subtitles in the given files/folders (subfolder "字幕备份" skipped).</summary>
    public static (List<string> Videos, List<string> Subs) Collect(IEnumerable<string> paths)
    {
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p) : File.Exists(p) ? [p] : []).ToList();
        return (files.Where(Sync.IsVideo).ToList(), files.Where(Sync.IsSub).ToList());
    }
}
