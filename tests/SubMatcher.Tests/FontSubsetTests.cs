using System.Net;
using System.Net.Sockets;
using System.Text;
using SubMatcher.Core;
using Xunit;

namespace SubMatcher.Tests;

/// <summary>FontInAss wire protocol against a local fake server (no network, no public server in tests).</summary>
public class FontSubsetTests
{
    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task SpeaksTheFontInAssProtocol()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        using var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{port}/");
        http.Start();
        var seen = new List<(string File, string? Check, string? Clean, string? Salt, string? Key, string Body)>();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); } catch { return; }
                var name = Encoding.UTF8.GetString(Convert.FromBase64String(ctx.Request.Headers["X-Filename"]!));
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var salt = ctx.Request.Headers["X-Font-Alias-Salt"] is { } s ? Encoding.UTF8.GetString(Convert.FromBase64String(s)) : null;
                lock (seen) seen.Add((name, ctx.Request.Headers["X-Fonts-Check"], ctx.Request.Headers["X-Clear-Fonts"], salt, ctx.Request.Headers["X-API-Key"], body));
                var (code, msg, reply) = name switch
                {
                    "ok.ass" => (200, "[]", "EMBEDDED " + body),
                    "miss.ass" => (201, "[\"Missing font: [Nope]\"]", "PARTIAL"),
                    _ => (400, "[\"SRT→ASS conversion not configured\"]", ""),
                };
                ctx.Response.Headers["X-Code"] = code.ToString();
                ctx.Response.Headers["X-Message"] = B64(msg);
                var bytes = Encoding.UTF8.GetBytes(reply);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var dir = Directory.CreateTempSubdirectory("subset").FullName;
        foreach (var n in new[] { "ok.ass", "miss.ass", "x.srt" }) File.WriteAllText(Path.Combine(dir, n), "content of " + n);
        File.WriteAllText(Path.Combine(dir, "empty.ass"), "");
        File.WriteAllText(Path.Combine(dir, "old.subset.ass"), "already done"); // our own output: skipped
        var o = new SubsetOptions { Server = $"http://localhost:{port}/", ApiKey = "k", AliasSalt = "SC", Clean = true };

        var files = FontSubset.Collect([dir], recursive: false);
        Assert.Equal(["empty.ass", "miss.ass", "ok.ass", "x.srt"], files.Select(Path.GetFileName));

        var ok = await FontSubset.Run(Path.Combine(dir, "ok.ass"), FontSubset.OutputFor(Path.Combine(dir, "ok.ass"), null, false), o);
        Assert.Equal(200, ok.Code);
        Assert.Equal("EMBEDDED content of ok.ass", File.ReadAllText(Path.Combine(dir, "ok.subset.ass")));
        Assert.Equal("content of ok.ass", File.ReadAllText(Path.Combine(dir, "ok.ass"))); // original untouched

        var miss = await FontSubset.Run(Path.Combine(dir, "miss.ass"), Path.Combine(dir, "miss.out.ass"), o);
        Assert.True(miss.Written);
        Assert.Equal(["Missing font: [Nope]"], miss.Messages);

        var strict = await FontSubset.Run(Path.Combine(dir, "miss.ass"), Path.Combine(dir, "strict.out.ass"), new SubsetOptions { Server = o.Server, Strict = true });
        Assert.False(strict.Written); // 201 under strict = failure, nothing written
        Assert.False(File.Exists(Path.Combine(dir, "strict.out.ass")));

        var srt = await FontSubset.Run(Path.Combine(dir, "x.srt"), Path.Combine(dir, "x.out.ass"), o);
        Assert.Equal(400, srt.Code);
        Assert.Equal(["SRT→ASS conversion not configured"], srt.Messages);

        var empty = await FontSubset.Run(Path.Combine(dir, "empty.ass"), Path.Combine(dir, "e.out.ass"), o);
        Assert.False(empty.Written);

        var first = seen.First(s => s.File == "ok.ass");
        Assert.Equal(("0", "1", "SC", "k"), (first.Check, first.Clean, first.Salt, first.Key));
        Assert.Equal("1", seen.Last(s => s.File == "miss.ass").Check);
        Assert.DoesNotContain(seen, s => s.File == "empty.ass"); // rejected locally

        var down = await FontSubset.Run(Path.Combine(dir, "ok.ass"), Path.Combine(dir, "d.ass"), new SubsetOptions { Server = "http://localhost:1/" });
        Assert.Equal(503, down.Code); // unreachable server is a result, not a crash
        http.Stop();
    }
}
