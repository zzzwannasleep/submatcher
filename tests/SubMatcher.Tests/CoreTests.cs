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
    public void FindsBlackBarsDespiteNoiseAndSkipsDarkFrames()
    {
        const int w = 200, h = 120;
        var px = new byte[w * h];
        var rnd = new Random(1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)(y < 15 || y >= h - 15 || x < 20 || x >= w - 20 ? 16 + rnd.Next(6) : 60 + rnd.Next(150)); // tv-range black + dither
        px[3 * w + 50] = 255; // a stray bright pixel in the bar
        Assert.Equal((15, 15, 20, 20), FFmpeg.Bars(px, w, h));

        var dark = new byte[w * h];
        Array.Fill(dark, (byte)16);
        Assert.Null(FFmpeg.Bars(dark, w, h)); // a black frame says nothing about bars
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
            if (r.OldEnd <= 50_000) AssertFrameExact(r, 72, Fps);
            else if (r.OldStart >= 55_000) AssertFrameExact(r, -48, Fps);
            else Assert.True(r.NeedsCheck, $"line at {r.OldStart} sits in removed footage and must be flagged");
        }
        // Only lines touching the removed footage (49–51 s straddles the cut, 53–55 s is gone) get flagged; no noise elsewhere.
        Assert.Equal(results.Count(r => r.OldEnd > 50_000 && r.OldStart < 55_000), results.Count(r => r.NeedsCheck));
    }

    /// <summary>The frame a renderer switches a line on at: the first frame with pts ≥ t.</summary>
    static long FrameOf(long ms, double fps) => (long)Math.Ceiling(ms * fps / 1000 - 1e-6);

    /// <summary>Shifted by exactly `frames`, and still on that frame after being written as ASS centiseconds.</summary>
    static void AssertFrameExact(EventResult r, int frames, double fps)
    {
        Assert.Equal(frames, r.ShiftFrames);
        foreach (var (oldT, newT) in new[] { (r.OldStart, r.NewStart), (r.OldEnd, r.NewEnd) })
        {
            SubtitleDoc.TryParseAssTime(SubtitleDoc.FormatAssTime(newT), out var written);
            Assert.Equal(FrameOf(oldT, fps) + frames, FrameOf(written, fps));
        }
    }

    [Fact]
    public void SignsStayOnTheirFrameThroughCentisecondRounding()
    {
        // NTSC rate, 24-frame shift (the Meido case: +1.001 s; a naive +0.955 s or +1.00 s lands a frame off for some lines).
        const double fps = 24000 / 1001.0;
        var src = SyntheticVideo(60 * 24, 3);
        var dst = new byte[24 * Fingerprint.Dim].Select(_ => (byte)16).Concat(src).ToArray();
        // Lines timed the way Aegisub does it (frame midpoints), at every phase of the 41.7 ms frame.
        var lines = Enumerable.Range(0, 40).Select(i =>
        {
            long s = (long)Math.Round((100 + i * 31 - 0.5) * 1000 / fps), e = (long)Math.Round((100 + i * 31 + 40 - 0.5) * 1000 / fps);
            return $"Dialogue: 0,{SubtitleDoc.FormatAssTime(s)},{SubtitleDoc.FormatAssTime(e)},Sign,,0,0,0,,{{\\pos(100,100)}}sign{i}\n";
        });
        var doc = SubtitleDoc.Parse("[Events]\n" + string.Concat(lines), SubFormat.Ass);
        var results = new Matcher(new Fingerprint(src, fps), new Fingerprint(dst, fps), new SyncOptions()).Match(doc.Events);
        Assert.All(results, r => { Assert.True(r.IsSign); AssertFrameExact(r, 24, fps); });
    }

    [Fact]
    public void FrameByFrameAnimationMovesAsOneUnit()
    {
        var src = SyntheticVideo(60 * Fps, 4);
        var dst = new byte[36 * Fingerprint.Dim].Select(_ => (byte)16).Concat(src).ToArray();
        // 120 one-frame lines (a 5 s animated title) plus normal dialogue around it.
        var sb = new System.Text.StringBuilder("[Events]\n");
        for (int i = 0; i < 120; i++)
            sb.Append($"Dialogue: 0,{SubtitleDoc.FormatAssTime((long)((480 + i) * 1000.0 / Fps))},{SubtitleDoc.FormatAssTime((long)((481 + i) * 1000.0 / Fps))},T,,0,0,0,,{{\\fscx{100 + i}}}雪\n");
        for (int i = 0; i < 10; i++)
            sb.Append($"Dialogue: 0,{SubtitleDoc.FormatAssTime(1000 + i * 5000L)},{SubtitleDoc.FormatAssTime(3000 + i * 5000L)},D,,0,0,0,,line{i}\n");
        var results = new Matcher(new Fingerprint(src, Fps), new Fingerprint(dst, Fps), new SyncOptions()).Match(SubtitleDoc.Parse(sb.ToString(), SubFormat.Ass).Events);
        Assert.All(results, r => AssertFrameExact(r, 36, Fps));
    }

    [Fact]
    public void PlaceholderAndStaticLinesFollowTheirNeighbours()
    {
        var src = SyntheticVideo(60 * Fps, 5);
        int d = Fingerprint.Dim;
        // 20–28 s of the source is one frozen frame (an ED card): matches equally well at any offset.
        for (int f = 20 * Fps; f < 28 * Fps; f++) Array.Copy(src, 20 * Fps * d, src, f * d, d);
        var dst = new byte[48 * d].Select(_ => (byte)16).Concat(src).ToArray();
        var doc = SubtitleDoc.Parse("[Events]\n" +
            "Dialogue: 0,0:00:00.00,0:00:00.00,D,,0,0,0,,\n" + // platform placeholder
            "Dialogue: 0,0:00:10.00,0:00:12.00,D,,0,0,0,,before\n" +
            "Dialogue: 0,0:00:23.00,0:00:25.00,D,,0,0,0,,frozen\n" +
            "Dialogue: 0,0:00:35.00,0:00:37.00,D,,0,0,0,,after\n", SubFormat.Ass);
        var r = new Matcher(new Fingerprint(src, Fps), new Fingerprint(dst, Fps), new SyncOptions()).Match(doc.Events);
        Assert.Equal(MatchStatus.Empty, r[0].Status);
        Assert.False(r[0].NeedsCheck);
        Assert.Equal(MatchStatus.Static, r[2].Status); // not trusted on its own…
        Assert.Equal(48, r[2].ShiftFrames);            // …takes its neighbours' shift
        Assert.All(r.Skip(1), x => Assert.Equal(48, x.ShiftFrames));
    }

    static SubtitleDoc Lines(int count, int everyMs) => SubtitleDoc.Parse("[Events]\n" + string.Concat(Enumerable.Range(0, count).Select(i =>
        $"Dialogue: 0,{SubtitleDoc.FormatAssTime(1000 + i * everyMs)},{SubtitleDoc.FormatAssTime(3000 + i * everyMs)},D,,0,0,0,,line{i}\n")), SubFormat.Ass);

    [Fact]
    public void SoundFindsTheShiftThroughVolumeAndNoise()
    {
        // "Speech": random tones of random length with pauses. Target: 1.5 s of silence first, half as loud, with hiss.
        var rng = new Random(7);
        var src = new List<float>();
        while (src.Count < 60 * Fingerprint.AudioRate)
        {
            double f = 150 + rng.NextDouble() * 2500, len = 0.08 + rng.NextDouble() * 0.3;
            for (int i = 0; i < len * Fingerprint.AudioRate; i++) src.Add((float)(0.3 * Math.Sin(2 * Math.PI * f * i / Fingerprint.AudioRate)));
            src.AddRange(new float[rng.Next(0, 1200)]);
        }
        var dst = new float[(int)(1.5 * Fingerprint.AudioRate)].Concat(src.Select(x => x * 0.5f + (float)(rng.NextDouble() - 0.5) * 0.01f)).ToArray();
        var a = Fingerprint.FromAudio(Fingerprint.BandLog(src.ToArray()), Fps);
        var b = Fingerprint.FromAudio(Fingerprint.BandLog(dst), Fps);
        var r = new Matcher(a, b, new SyncOptions { SnapToCuts = false }).Match(Lines(14, 4000).Events);
        Assert.All(r, x => { Assert.Equal(36, x.ShiftFrames); Assert.True(x.Cost < 0.4); });
    }

    [Fact]
    public void WhereThePictureFailsTheSoundsShiftIsKeptNotANeighbours()
    {
        // Target: 5 s of extra footage inserted at 40 s, and 40–60 s of the source altered beyond recognition (a censored
        // TV cut). Pictures alone would hand those lines the shift from before the insert; the sound knows better.
        var src = SyntheticVideo(100 * Fps, 8);
        int d = Fingerprint.Dim;
        var dst = src[..(40 * Fps * d)].Concat(SyntheticVideo(5 * Fps, 9)).Concat(src[(40 * Fps * d)..]).ToArray();
        new Random(10).NextBytes(dst.AsSpan(45 * Fps * d, 20 * Fps * d));
        var doc = Lines(24, 4000);
        var prior = doc.Events.Select(e => (int?)(e.Start >= 40_000 ? 5 * Fps : 0)).ToArray();
        var r = new Matcher(new Fingerprint(src, Fps), new Fingerprint(dst, Fps), new SyncOptions()) { Prior = prior }.Match(doc.Events);
        foreach (var x in r)
        {
            Assert.Equal(x.OldStart >= 40_000 ? 5 * Fps : 0, x.ShiftFrames);
            if (x.OldStart >= 41_000 && x.OldEnd <= 60_000) Assert.Equal(MatchStatus.Audio, x.Status);
        }
        Assert.DoesNotContain(r, x => x.NeedsCheck);
    }

    [Fact]
    public async Task RefusesHalfDownloadedFiles()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Sync.Run("ep01.mkv.!qB", "a.ass", "bd.mkv", "o.ass", new SyncOptions()));
        Assert.Contains("没下载完", e.Message);
        var fp = new Fingerprint(new byte[600 * Fingerprint.Dim], Fps); // 25 s decoded…
        Assert.Throws<InvalidOperationException>(() => Sync.CheckComplete("x.mkv", new VideoInfo(Fps, 1440), fp)); // …of a 24 min file
    }
}
