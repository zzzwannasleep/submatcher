using System.Text;
using SubMatcher.Core;
using Xunit;

namespace SubMatcher.Tests;

public class CoreTests
{
    const string Ass = "[Script Info]\r\nTitle: t\r\n\r\n[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n" +
                       "Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,你好,世界\r\n" +
                       "Comment: 0,0:01:00.00,0:01:01.00,Default,,0,0,0,,注释\r\n";

    [Fact]
    public void AssRoundTripOnlyTouchesTimes()
    {
        var doc = SubtitleDoc.Parse(Ass, SubFormat.Ass);
        Assert.Equal(2, doc.Events.Count);
        Assert.Equal(1000, doc.Events[0].Start);
        Assert.Equal("你好,世界", doc.Events[0].Text);
        Assert.True(doc.Events[1].IsComment);
        Assert.Equal(Ass, doc.Serialize());

        Tools.Shift(doc, 1500);
        Assert.Equal(Ass.Replace("0:00:01.00,0:00:02.50", "0:00:02.50,0:00:04.00").Replace("0:01:00.00,0:01:01.00", "0:01:01.50,0:01:02.50"), doc.Serialize());
    }

    [Fact]
    public void SrtParseAndRangeShift()
    {
        var doc = SubtitleDoc.Parse("1\n00:00:01,000 --> 00:00:02,000\nA\n\n2\n00:10:00,000 --> 00:10:02,500\nB\nB2\n", SubFormat.Srt);
        Assert.Equal("B\\NB2", doc.Events[1].Text);
        Assert.Equal(1, Tools.Shift(doc, -500, from: 60_000));
        Assert.Contains("00:09:59,500 --> 00:10:02,000", doc.Serialize());
        Assert.Contains("00:00:01,000 --> 00:00:02,000", doc.Serialize());
    }

    [Fact]
    public void FpsAndTimeParsing()
    {
        Assert.Equal(24000 / 1001.0, Tools.ExactFps(23.976), 9);
        Assert.Equal(25, Tools.ExactFps(25));
        Assert.Equal(-1500, SubtitleDoc.ParseFlexibleTime("-1.5"));
        Assert.Equal(635_200, SubtitleDoc.ParseFlexibleTime("10:35.2"));
        Assert.Equal(3_600_000 + 1234, SubtitleDoc.ParseFlexibleTime("1:00:01.234"));
    }

    [Theory]
    [InlineData(54936)] // GBK/GB18030
    [InlineData(950)]   // BIG5
    [InlineData(1200)]  // UTF-16LE, no BOM
    public void DetectsLegacyEncodings(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = codePage == 950 ? "這是第一句對白，我們來說話。" : "这是第一句对白，我们来说话。";
        var bytes = Encoding.GetEncoding(codePage).GetBytes(Ass.Replace("你好,世界", text));
        Assert.Contains(text, TextEncoding.Decode(bytes, out _));
    }

    [Fact]
    public void EpisodeNumbers()
    {
        Assert.Equal("1", Sync.EpisodeOf("[VCB-Studio] Show [01][Ma10p_1080p][x265_flac].mkv"));
        Assert.Equal("12", Sync.EpisodeOf("Show - 12 (BD 1920x1080 x264).mkv"));
        Assert.Equal("3", Sync.EpisodeOf("某番 第03话.mp4"));
        Assert.Equal("7", Sync.EpisodeOf("Show EP07.mkv"));
        Assert.Equal("5", Sync.EpisodeOf("[Grp] Show - 05 [1080p].mkv"));
        Assert.Equal(Path.Combine(Path.GetFullPath("bd"), "Show BD 01.sc.ass"),
            Sync.DefaultOutput("tv/Show TV 01.mkv", "tv/Show TV 01.sc.ass", "bd/Show BD 01.mkv"));
    }

    // ---- matcher on a synthetic "video": no ffmpeg needed ----

    const int Fps = 24;

    static byte[] SyntheticVideo(int frames, int seed)
    {
        var rng = new Random(seed);
        var raw = new byte[frames * Fingerprint.Dim];
        byte[] scene = [];
        for (int f = 0; f < frames; f++)
        {
            if (f % (3 * Fps) == 0) { scene = new byte[Fingerprint.Dim * 2]; rng.NextBytes(scene); } // hard cut every 3 s
            int pan = f % (3 * Fps) / 4; // slow pan within the shot
            for (int y = 0; y < Fingerprint.H; y++)
                for (int x = 0; x < Fingerprint.W; x++)
                    raw[f * Fingerprint.Dim + y * Fingerprint.W + x] = scene[y * Fingerprint.W * 2 + x + pan];
        }
        return raw;
    }

    [Fact]
    public void MatcherFindsInsertedIntroAndRemovedSegment()
    {
        var src = SyntheticVideo(100 * Fps, 1);
        int d = Fingerprint.Dim;
        // Target: 3 s of black in front, 50–55 s cut out, plus mild noise and a contrast change.
        var dst = new byte[3 * Fps * d].Select(_ => (byte)16)
            .Concat(src[..(50 * Fps * d)]).Concat(src[(55 * Fps * d)..]).ToArray();
        var rng = new Random(2);
        for (int i = 0; i < dst.Length; i++) dst[i] = (byte)Math.Clamp(dst[i] * 0.8 + 20 + rng.Next(-4, 5), 0, 255);

        var doc = SubtitleDoc.Parse(string.Join("", Enumerable.Range(0, 24).Select(i =>
            $"Dialogue: 0,{SubtitleDoc.FormatAssTime(1000 + i * 4000)},{SubtitleDoc.FormatAssTime(3000 + i * 4000)},D,,0,0,0,,line{i}\n")).Insert(0, "[Events]\n"), SubFormat.Ass);
        var results = new Matcher(new Fingerprint(src, Fps), new Fingerprint(dst, Fps), new SyncOptions()).Match(doc.Events);

        foreach (var r in results)
        {
            if (r.OldEnd <= 50_000) Assert.Equal(3000, r.ShiftMs);
            else if (r.OldStart >= 55_000) Assert.Equal(-2000, r.ShiftMs);
            else Assert.True(r.NeedsCheck, $"line at {r.OldStart} sits in removed footage and must be flagged");
        }
        // Only lines touching the removed footage (49–51 s straddles the cut, 53–55 s is gone) get flagged; no noise elsewhere.
        Assert.Equal(results.Count(r => r.OldEnd > 50_000 && r.OldStart < 55_000), results.Count(r => r.NeedsCheck));
    }
}
