using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SubMatcher.Core;

public sealed class SubsetOptions
{
    public string Server { get; set; } = "https://font.anibt.net";
    public string? ApiKey { get; set; }
    /// <summary>Missing fonts count as failure (X-Fonts-Check).</summary>
    public bool Strict { get; set; }
    /// <summary>Drop fonts already embedded in the subtitle first (X-Clear-Fonts).</summary>
    public bool Clean { get; set; }
    /// <summary>Different salt per track when muxing several subtitles into one MKV, so font aliases don't collide.</summary>
    public string? AliasSalt { get; set; }
}

/// <summary>Code: 200 ok, 201 ok but some fonts missing, ≥300 failed (nothing written).</summary>
public sealed record SubsetResult(string Input, string? Output, int Code, IReadOnlyList<string> Messages)
{
    public bool Written => Output != null;
}

/// <summary>
/// Font subsetting through a FontInAss server (https://github.com/Yuri-NagaSaki/FontInAss): the subtitle is uploaded,
/// the server matches its fonts against its library, and returns it with minimal font subsets embedded.
/// Same wire protocol as the official fontinass CLI (POST /api/subset, raw body, X-* headers).
/// </summary>
public static class FontSubset
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<SubsetResult> Run(string input, string output, SubsetOptions o, CancellationToken ct = default)
    {
        if (new FileInfo(input).Length == 0) return new(input, null, 400, ["文件是空的"]);
        using var req = new HttpRequestMessage(HttpMethod.Post, o.Server.TrimEnd('/') + "/api/subset")
        {
            Content = new ByteArrayContent(await File.ReadAllBytesAsync(input, ct)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Add("X-Filename", B64(Path.GetFileName(input)));
        req.Headers.Add("X-Fonts-Check", o.Strict ? "1" : "0");
        req.Headers.Add("X-Clear-Fonts", o.Clean ? "1" : "0");
        if (!string.IsNullOrWhiteSpace(o.AliasSalt)) req.Headers.Add("X-Font-Alias-Salt", B64(o.AliasSalt.Trim()[..Math.Min(80, o.AliasSalt.Trim().Length)]));
        if (!string.IsNullOrWhiteSpace(o.ApiKey)) req.Headers.Add("X-API-Key", o.ApiKey.Trim());

        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (HttpRequestException e) { return new(input, null, 503, [$"连不上服务器 {o.Server}：{e.Message}"]); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(input, null, 504, ["服务器超时"]); }

        using (resp)
        {
            int code = Header(resp, "X-Code") is { } c && int.TryParse(c, out var n) ? n : (int)resp.StatusCode;
            var messages = Header(resp, "X-Message") is { } m ? DecodeMessages(m) : [];
            if (code == 200 && !resp.IsSuccessStatusCode) code = (int)resp.StatusCode;
            if (code >= 300 || (o.Strict && code == 201)) return new(input, null, code, messages.Count > 0 ? messages : [$"HTTP {(int)resp.StatusCode}"]);

            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            if (body.Length == 0) return new(input, null, 500, ["服务器返回了空文件"]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllBytesAsync(output, body, ct);
            return new(input, output, code, messages);
        }
    }

    /// <summary>Default output next to the input: "01.sc.ass" → "01.sc.subset.ass" (or in-place / into a folder).</summary>
    public static string OutputFor(string input, string? outDir, bool inPlace)
    {
        if (inPlace) return input;
        var name = Path.GetFileNameWithoutExtension(input) + ".subset" + Path.GetExtension(input);
        return Path.Combine(outDir ?? Path.GetDirectoryName(Path.GetFullPath(input))!, name);
    }

    /// <summary>Files, or every .ass/.ssa/.srt in the given folders. Skips our own *.subset.* outputs.</summary>
    public static List<string> Collect(IEnumerable<string> paths, bool recursive)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(Sync.IsSub));
            else if (File.Exists(p)) files.Add(p);
        }
        return files.Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".subset", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) || r.Content.Headers.TryGetValues(name, out v) ? v.FirstOrDefault() : null;

    /// <summary>X-Message is base64(UTF-8(JSON string array)); fall back to the raw text if it isn't.</summary>
    static List<string> DecodeMessages(string header)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header));
            try
            {
                using var doc = JsonDocument.Parse(text); // JsonDocument: no reflection, fine under Native AOT
                return doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().Select(e => e.ToString()).ToList()
                    : [text];
            }
            catch (JsonException) { return [text]; }
        }
        catch (FormatException) { return [header]; }
    }
}
