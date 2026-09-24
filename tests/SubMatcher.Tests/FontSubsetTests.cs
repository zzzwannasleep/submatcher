using SubMatcher.Core;
using Xunit;

namespace SubMatcher.Tests;

/// <summary>FontInAss wire protocol against a local fake server (no network, no public server in tests).</summary>
public class FontSubsetTests
{
    [Fact]
    public async Task SpeaksTheFontInAssProtocol()
    {
        using var server = new FakeFontServer((name, body) => name switch
        {
            "ok.ass" => (200, "[]", "EMBEDDED " + body),
            "miss.ass" => (201, "[\"Missing font: [Nope]\"]", "PARTIAL"),
            _ => (400, "[\"SRT→ASS conversion not configured\"]", ""),
        });

        var dir = Directory.CreateTempSubdirectory("subset").FullName;
        foreach (var n in new[] { "ok.ass", "miss.ass", "x.srt" }) File.WriteAllText(Path.Combine(dir, n), "content of " + n);
        File.WriteAllText(Path.Combine(dir, "empty.ass"), "");
        File.WriteAllText(Path.Combine(dir, "old.subset.ass"), "already done"); // our own output: skipped
        var o = new SubsetOptions { Server = server.Url, ApiKey = "k", AliasSalt = "SC", Clean = true };

        var files = FontSubset.Collect([dir], recursive: false);
        Assert.Equal(["empty.ass", "miss.ass", "ok.ass", "x.srt"], files.Select(Path.GetFileName));

        var ok = await FontSubset.Run(Path.Combine(dir, "ok.ass"), FontSubset.OutputFor(Path.Combine(dir, "ok.ass"), null, false), o);
        Assert.Equal(200, ok.Code);
        Assert.Equal("EMBEDDED content of ok.ass", File.ReadAllText(Path.Combine(dir, "ok.subset.ass")));
        Assert.Equal("content of ok.ass", File.ReadAllText(Path.Combine(dir, "ok.ass"))); // original untouched

        var miss = await FontSubset.Run(Path.Combine(dir, "miss.ass"), Path.Combine(dir, "miss.out.ass"), o);
        Assert.True(miss.Written);
        Assert.Equal(["Missing font: [Nope]"], miss.Messages);

        var strict = await FontSubset.Run(Path.Combine(dir, "miss.ass"), Path.Combine(dir, "strict.out.ass"), new SubsetOptions { Server = server.Url, Strict = true });
        Assert.False(strict.Written); // 201 under strict = failure, nothing written
        Assert.False(File.Exists(Path.Combine(dir, "strict.out.ass")));

        var srt = await FontSubset.Run(Path.Combine(dir, "x.srt"), Path.Combine(dir, "x.out.ass"), o);
        Assert.Equal(400, srt.Code);
        Assert.Equal(["SRT→ASS conversion not configured"], srt.Messages);

        var empty = await FontSubset.Run(Path.Combine(dir, "empty.ass"), Path.Combine(dir, "e.out.ass"), o);
        Assert.False(empty.Written);

        var first = server.Seen.First(s => s.File == "ok.ass");
        Assert.Equal(("0", "1", "SC", "k"), (first.Check, first.Clean, first.Salt, first.Key));
        Assert.Equal("1", server.Seen.Last(s => s.File == "miss.ass").Check);
        Assert.DoesNotContain(server.Seen, s => s.File == "empty.ass"); // rejected locally

        var down = await FontSubset.Run(Path.Combine(dir, "ok.ass"), Path.Combine(dir, "d.ass"), new SubsetOptions { Server = "http://localhost:1/" });
        Assert.Equal(503, down.Code); // unreachable server is a result, not a crash
    }
}
