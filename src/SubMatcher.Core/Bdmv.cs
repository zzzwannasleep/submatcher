using System.Buffers.Binary;

namespace SubMatcher.Core;

/// <summary>One clip in a playlist: <see cref="Start"/> is where it begins on the playlist timeline (seconds).</summary>
public sealed record PlayItem(string Clip, double Start, double Length, double InTime, double ClipStart);

/// <summary>A chapter (entry mark) on the playlist timeline.</summary>
public sealed record Chapter(int Number, double Time, int Item);

/// <summary>A Blu-ray playlist (BDMV/PLAYLIST/*.mpls).</summary>
public sealed record Playlist(string Path, List<PlayItem> Items, List<Chapter> Chapters)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public double Duration => Items.Count == 0 ? 0 : Items[^1].Start + Items[^1].Length;
    /// <summary>Length without clips played twice — loops and repeated menus don't make a playlist the main one.</summary>
    public double UniqueDuration => Items.DistinctBy(i => (i.Clip, i.InTime)).Sum(i => i.Length);
}

/// <summary>
/// Reads Blu-ray playlists: which m2ts clips play in which order, and where the chapters are.
/// Layout: https://github.com/lw/BluRay/wiki/MPLS, clip start times from CLIPINF/*.clpi (SequenceInfo).
/// </summary>
public static class Bdmv
{
    const double Tick = 45000.0;

    /// <summary>The disc root (the folder holding BDMV) for any path inside a disc, or null.</summary>
    public static string? FindRoot(string path)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : path)); d != null; d = d.Parent)
        {
            if (d.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d.FullName, "PLAYLIST"))) return d.Parent?.FullName;
            if (Directory.Exists(Path.Combine(d.FullName, "BDMV", "PLAYLIST"))) return d.FullName;
        }
        return null;
    }

    /// <summary>Disc roots under the given paths (a disc, a folder of discs, or anything inside one), in name order.</summary>
    public static List<string> FindDiscs(IEnumerable<string> paths)
    {
        var roots = new List<string>();
        foreach (var p in paths)
        {
            if (FindRoot(p) is { } r) { roots.Add(r); continue; }
            if (!Directory.Exists(p)) continue;
            foreach (var bdmv in Directory.EnumerateDirectories(p, "BDMV", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, MatchCasing = MatchCasing.CaseInsensitive }))
                if (Directory.Exists(Path.Combine(bdmv, "PLAYLIST"))) roots.Add(Path.GetDirectoryName(bdmv)!);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).Order(NaturalOrder.Instance).ToList();
    }

    public static List<Playlist> Playlists(string root)
    {
        var dir = Path.Combine(root, "BDMV", "PLAYLIST");
        var list = new List<Playlist>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.mpls").Order(StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(Read(f)); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        return list;
    }

    /// <summary>
    /// The playlist that plays the episodes: the longest one (not counting repeats). Copies with the same clips are the same playlist,
    /// the lowest number wins.
    /// </summary>
    public static Playlist? MainPlaylist(IEnumerable<Playlist> lists) => lists
        .Where(p => p.Duration >= 60)
        .OrderByDescending(p => Math.Round(p.UniqueDuration))
        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    public static Playlist Read(string mpls)
    {
        var b = File.ReadAllBytes(mpls);
        if (b.Length < 20 || b[0] != 'M' || b[1] != 'P' || b[2] != 'L' || b[3] != 'S') throw new InvalidDataException("不是 MPLS 播放列表");
        uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        int U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
        var clipinf = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(mpls)))!, "CLIPINF");

        int pl = (int)U32(8), marks = (int)U32(12);
        var items = new List<PlayItem>();
        double t = 0;
        int n = U16(pl + 6), at = pl + 10;
        for (int i = 0; i < n; i++)
        {
            int len = U16(at);
            var clip = System.Text.Encoding.ASCII.GetString(b, at + 2, 5);
            double tin = U32(at + 14) / Tick, tout = U32(at + 18) / Tick;
            var item = new PlayItem(clip, t, Math.Max(0, tout - tin), tin, ClipStart(Path.Combine(clipinf, clip + ".clpi")) ?? tin);
            items.Add(item);
            t += item.Length;
            at += len + 2;
        }

        var chapters = new List<Chapter>();
        int m = U16(marks + 4);
        for (int i = 0; i < m; i++)
        {
            int o = marks + 6 + 14 * i;
            if (b[o + 1] != 1) continue; // 1 = entry mark (chapter); 2 = link point
            int item = U16(o + 2);
            if (item >= items.Count) continue;
            double time = items[item].Start + U32(o + 4) / Tick - items[item].InTime;
            chapters.Add(new(chapters.Count + 1, Math.Round(Math.Max(0, time), 3), item));
        }
        return new(Path.GetFullPath(mpls), items, chapters);
    }

    /// <summary>First presentation time of a clip (seconds, 45 kHz clock) — where a player's 0:00 of that m2ts is.</summary>
    static double? ClipStart(string clpi)
    {
        try
        {
            var b = File.ReadAllBytes(clpi);
            int seq = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
            // SequenceInfo: length(4) reserved(1) ATC count(1) | SPN(4) STC count(1) offset id(1) | PCR PID(2) SPN(4) start(4) end(4)
            if (b[seq + 5] == 0 || b[seq + 10] == 0) return null;
            return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(seq + 18)) / Tick;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException) { return null; }
    }
}

/// <summary>"Vol.2" before "Vol.10".</summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0');
                int c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (c != 0) return c;
                continue;
            }
            int d = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (d != 0) return d;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
