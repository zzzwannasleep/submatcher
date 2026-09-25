using System.Buffers.Binary;
using System.Text;
using SubMatcher.Core;
using Xunit;

namespace SubMatcher.Tests;

public class MergeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("smmerge").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>A minimal MPLS: clips (name, seconds) back to back, chapters as (clip index, seconds into the clip).</summary>
    internal static byte[] Mpls((string Clip, double Len)[] items, (int Item, double At)[] marks, double inTime = 600)
    {
        var b = new List<byte>();
        void U16(int v) { var x = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(x, (ushort)v); b.AddRange(x); }
        void U32(long v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, (uint)v); b.AddRange(x); }
        b.AddRange("MPLS0200"u8.ToArray());
        int pl = 40, markAt = pl + 10 + items.Length * 22;
        U32(pl); U32(markAt); U32(0);
        while (b.Count < pl) b.Add(0);
        U32(0); U16(0); U16(items.Length); U16(0);
        foreach (var (clip, len) in items)
        {
            U16(20); b.AddRange(Encoding.ASCII.GetBytes(clip)); b.AddRange("M2TS"u8.ToArray()); U16(1); b.Add(0);
            U32((long)(inTime * 45000)); U32((long)((inTime + len) * 45000));
        }
        U32(0); U16(marks.Length);
        foreach (var (item, at) in marks) { b.Add(0); b.Add(1); U16(item); U32((long)((inTime + at) * 45000)); U16(0x1011); U32(0); }
        return [.. b];
    }

    string Disc(string name, (string Clip, double Len)[] items, (int Item, double At)[] marks)
    {
        var pl = Directory.CreateDirectory(Path.Combine(_dir, name, "BDMV", "PLAYLIST")).FullName;
        File.WriteAllBytes(Path.Combine(pl, "00001.mpls"), Mpls(items, marks));
        File.WriteAllBytes(Path.Combine(pl, "00002.mpls"), Mpls([("00099", 90)], [(0, 0)])); // a trailer
        return Path.Combine(_dir, name);
    }

    string Srt(string name, params double[] ends)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ends.Length; i++)
            sb.Append($"{i + 1}\n{SubtitleDoc.FormatSrtTime((long)(ends[i] * 1000) - 2000)} --> {SubtitleDoc.FormatSrtTime((long)(ends[i] * 1000))}\n台词{i + 1}\n\n");
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, sb.ToString());
        return p;
    }

    [Fact]
    public void ReadsClipsChaptersAndPicksTheMainPlaylist()
    {
        var disc = Disc("D1", [("00010", 1421.086), ("00011", 1421.086)], [(0, 0), (0, 258), (1, 0), (1, 75)]);
        var p = Bdmv.MainPlaylist(Bdmv.Playlists(disc))!;
        Assert.Equal("00001", p.Name);
        Assert.Equal(["00010", "00011"], p.Items.Select(i => i.Clip));
        Assert.Equal(2842.172, p.Duration, 2);
        Assert.Equal([0, 258, 1421.086, 1496.086], p.Chapters.Select(c => Math.Round(c.Time, 3)));
        Assert.Equal(disc, Bdmv.FindRoot(Path.Combine(disc, "BDMV", "PLAYLIST")));
    }

    [Fact]
    public void EpisodesInOneClipStartOnTheRightChapterEvenWhenSubsEndEarly()
    {
        // 4 episodes in one m2ts, chapters: avant, OP end, B part, ED, preview. The subtitles stop after the ED (no preview
        // lines), so "first chapter after the last line" is the preview — a greedy pick lands every episode a minute early.
        var marks = new List<(int, double)>();
        for (int e = 0; e < 4; e++) foreach (var at in new[] { 0.0, 90, 700, 1290, 1380 }) marks.Add((0, e * 1440 + at));
        var p = Bdmv.Read(Path.Combine(Disc("One", [("00001", 4 * 1440)], [.. marks]), "BDMV", "PLAYLIST", "00001.mpls"));
        var eps = Enumerable.Range(1, 4).Select(i => (Srt($"ep{i}.srt", 600, 1350 - i), 1350.0 - i)).ToList();
        var placed = SubMerge.Place(p, eps)!;
        Assert.Equal([0, 1440, 2880, 4320], placed.Select(e => Math.Round(e.Offset)));
        Assert.Equal([1, 6, 11, 16], placed.Select(e => e.Chapter));
    }

    [Fact]
    public void SplitsEpisodesAcrossDiscsByEpisodeNumber()
    {
        var d1 = Disc("Vol.1", [("00010", 1420), ("00011", 1420), ("00012", 60)], [(0, 0), (1, 0), (2, 0)]);
        var d2 = Disc("Vol.2", [("00010", 1420), ("00011", 1420)], [(0, 0), (1, 0)]);
        var subs = new[] { 3, 1, 4, 2 }.Select(n => Srt($"CHS 某番 第{n}集_Viu.srt", 30, 1400)).ToList();
        var (plans, notes) = SubMerge.Plan([_dir], subs);
        Assert.Empty(notes);
        Assert.Equal([d1, d2], plans.Select(p => p.Disc));
        Assert.All(plans, p => Assert.Equal("sc", p.Lang));
        Assert.Equal(["第1集", "第2集"], plans[0].Episodes.Select(e => Path.GetFileName(e.Sub)[7..10]));
        Assert.Equal(["第3集", "第4集"], plans[1].Episodes.Select(e => Path.GetFileName(e.Sub)[7..10]));
        Assert.Equal([0, 1420], plans[1].Episodes.Select(e => e.Offset));
        Assert.Equal([Path.Combine(_dir, "Vol.1.sc.srt"), Path.Combine(d1, "BDMV", "PLAYLIST", "00001.sc.srt")], plans[0].Outputs());
    }

    [Fact]
    public void SubtitleTimedToAnM2tsGoesExactlyOntoThatClip()
    {
        var disc = Disc("D", [("00010", 1420), ("00011", 1420)], [(0, 0), (0, 600), (1, 0), (1, 600)]);
        var stream = Directory.CreateDirectory(Path.Combine(disc, "BDMV", "STREAM")).FullName;
        var sub = Path.Combine(stream, "00011.tc.srt");
        File.Move(Srt("x.srt", 5, 700), sub);
        var (plans, _) = SubMerge.Plan([disc], [sub]);
        var e = Assert.Single(Assert.Single(plans).Episodes);
        Assert.Equal(("00011", 1420.0, 3), (e.Clip, e.Offset, e.Chapter));
        Assert.Equal("tc", plans[0].Lang);
    }

    [Fact]
    public void MergesAssKeepingStylesFontsAndNeverOverwritesForeignFiles()
    {
        string Ass(string name, string font, string fontBlock)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllText(p, $$"""
                [Script Info]
                ScriptType: v4.00+
                PlayResX: 1920
                PlayResY: 1080

                [V4+ Styles]
                Format: Name, Fontname, Fontsize
                Style: Default,{{font}},60
                Style: Sign,Arial,40

                [Fonts]
                fontname: {{fontBlock}}.ttf
                ABCD{{fontBlock}}

                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,你好{\rSign}世界
                Comment: 0,0:00:03.00,0:00:04.00,Sign,,0,0,0,,注释
                """);
            return p;
        }
        var a = Ass("e1.ass", "思源黑体", "F1");
        var b = Ass("e2.ass", "方正准圆", "F2");
        var (text, notes) = SubMerge.Merge([(a, 0), (b, 1421.086)]);
        Assert.Empty(notes);
        Assert.Contains("Style: Default,思源黑体,60", text);
        Assert.Contains("Style: Default (2),方正准圆,60", text);
        Assert.Single(text.Split('\n'), l => l.StartsWith("Style: Sign")); // identical style shared
        Assert.Contains("fontname: F1.ttf", text);
        Assert.Contains("fontname: F2.ttf", text);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,你好{\\rSign}世界", text);
        Assert.Contains("Dialogue: 0,0:23:42.09,0:23:43.59,Default (2),,0,0,0,,你好{\\rSign}世界", text);
        Assert.Contains("Comment: 0,0:23:44.09,0:23:45.09,Sign", text);

        var disc = Disc("Ass", [("00010", 1421.086), ("00011", 1421.086)], [(0, 0), (1, 0)]);
        var foreign = Path.Combine(_dir, "Ass.ass");
        File.WriteAllText(foreign, "别人的字幕");
        var (plans, _) = SubMerge.Plan([disc], [a, b]);
        var (written, wn) = SubMerge.Write(Assert.Single(plans));
        Assert.Equal("别人的字幕", File.ReadAllText(foreign));
        Assert.Contains(wn, n => n.Contains("没有覆盖"));
        var mine = Assert.Single(written);
        Assert.Equal(SubMerge.Write(plans[0]).Written, [mine]); // our own output is replaced on a re-run
        Assert.Equal(2, SubtitleDoc.Load(mine).Events.Count(e => !e.IsComment));
    }
}
