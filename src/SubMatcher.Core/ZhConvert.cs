using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SubMatcher.Core;

/// <summary>
/// 简繁转换 via 繁化姬 (https://zhconvert.org). Whole ASS/SRT files are sent as-is: the service recognises the subtitle format,
/// converts only dialogue and leaves styles, tags and font names alone.
/// Its terms: programs must say they use 繁化姬 and link https://zhconvert.org, and the "Processed by 繁化姬" line it adds stays.
/// </summary>
public static partial class ZhConvert
{
    public const string Api = "https://api.zhconvert.org/convert";
    public const string Home = "https://zhconvert.org";

    /// <summary>CLI/GUI name → 繁化姬 converter, tag written into the output name.</summary>
    public static readonly (string Key, string Converter, string Tag, string Label)[] Modes =
    [
        ("sc", "Simplified", "sc", "简体化"),
        ("tc", "Traditional", "tc", "繁体化"),
        ("cn", "China", "sc", "中国化（简体 + 大陆用语）"),
        ("tw", "Taiwan", "tc", "台湾化（繁体 + 台湾用语）"),
        ("hk", "Hongkong", "tc", "香港化（繁体 + 香港用语）"),
    ];

    public static (string Key, string Converter, string Tag, string Label) Mode(string key) =>
        Modes.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || m.Converter.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } m
            ? m : throw new ArgumentException($"转换方式只能是 {string.Join(" / ", Modes.Select(x => x.Key))}");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static async Task<string> Convert(string text, string converter, CancellationToken ct = default)
    {
        using var body = new FormUrlEncodedContent([new("converter", converter), new("text", text)]);
        using var resp = await Http.PostAsync(Api, body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetInt32() != 0) throw new InvalidOperationException("繁化姬：" + root.GetProperty("msg").GetString());
            return root.GetProperty("data").GetProperty("text").GetString() ?? "";
        }
        catch (JsonException) { throw new InvalidOperationException($"繁化姬服务暂时不可用（HTTP {(int)resp.StatusCode}）"); }
    }

    [GeneratedRegex(@"(?<=^|[\s_.\-\[(])(CHS|CHT|SC|TC|GB|BIG5|简体|簡體|繁体|繁體|简|簡|繁|zh-?Hans|zh-?Hant)(?=$|[\s_.\-\])])", RegexOptions.IgnoreCase)]
    private static partial Regex LangTag();

    /// <summary>"CHS_xx.srt" → "CHT_xx.srt", "xx.sc.ass" → "xx.tc.ass"; untagged names get ".tc" / ".sc" before the extension.</summary>
    public static string OutputFor(string input, string tag, string? outDir = null)
    {
        var name = Path.GetFileNameWithoutExtension(input);
        bool sc = tag == "sc";
        var swapped = LangTag().Replace(name, m =>
        {
            var v = m.Value;
            var r = v.ToLowerInvariant() switch
            {
                "chs" or "cht" => sc ? "chs" : "cht",
                "sc" or "tc" or "gb" or "big5" => sc ? "sc" : "tc",
                var l when l.StartsWith("zh") => sc ? "zh-Hans" : "zh-Hant",
                _ => sc ? "简体" : "繁體",
            };
            return v.Any(char.IsUpper) && !r.StartsWith("zh") ? r.ToUpperInvariant() : r;
        });
        if (swapped == name) swapped = name + "." + tag;
        var dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(input))!;
        var output = Path.Combine(dir, swapped + Path.GetExtension(input));
        return string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(dir, name + "." + tag + Path.GetExtension(input)) // already that language: don't overwrite the source
            : output;
    }

    /// <summary>Converts one subtitle file (any encoding in, UTF-8 BOM out). Returns the output path.</summary>
    public static async Task<string> ConvertFile(string input, string mode, string? output = null, CancellationToken ct = default)
    {
        var m = Mode(mode);
        var text = TextEncoding.Decode(await File.ReadAllBytesAsync(input, ct), out _);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("字幕是空的");
        var result = await Convert(text, m.Converter, ct);
        output ??= OutputFor(input, m.Tag);
        await File.WriteAllTextAsync(output, result, new UTF8Encoding(true), ct);
        return output;
    }
}
