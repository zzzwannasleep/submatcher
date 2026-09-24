using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SubMatcher.Core;

public enum SubFormat { Ass, Srt }

public sealed class SubEvent
{
    public int LineIndex { get; init; }
    public long Start { get; set; } // ms
    public long End { get; set; }   // ms
    public string Text { get; init; } = "";
    public bool IsComment { get; init; }

    // ASS: the comma-split fields after "Dialogue:"; SRT: text after the second timestamp.
    internal string Prefix = "";
    internal string[] Fields = [];
    internal int StartField, EndField;
    internal string Suffix = "";
}

/// <summary>
/// ASS/SSA/SRT document. Only event timestamps are rewritten on save; every other byte of the file is kept as-is.
/// </summary>
public sealed class SubtitleDoc
{
    public SubFormat Format { get; private init; }
    public List<string> Lines { get; private init; } = [];
    public List<SubEvent> Events { get; } = [];
    string _newline = "\r\n";

    static readonly Regex SrtTime = new(@"^\s*(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)(.*)$", RegexOptions.Compiled);

    public static SubtitleDoc Load(string path) => Parse(TextEncoding.Read(path),
        Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase) ? SubFormat.Srt : SubFormat.Ass);

    public static SubtitleDoc Parse(string text, SubFormat format)
    {
        var doc = new SubtitleDoc { Format = format, Lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r'))] };
        doc._newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (format == SubFormat.Ass) doc.ParseAss(); else doc.ParseSrt();
        return doc;
    }

    void ParseAss()
    {
        bool inEvents = false;
        int startField = 1, endField = 2, fieldCount = 10;
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var t = line.TrimStart();
            if (t.StartsWith('[')) { inEvents = t.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inEvents) continue;
            if (t.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = t[7..].Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
                fieldCount = names.Count;
                startField = Math.Max(0, names.IndexOf("start"));
                endField = Math.Max(0, names.IndexOf("end"));
                continue;
            }
            bool comment = t.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase);
            if (!comment && !t.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            int colon = line.IndexOf(':');
            var fields = line[(colon + 1)..].Split(',', fieldCount);
            if (fields.Length < fieldCount) continue;
            if (!TryParseAssTime(fields[startField], out var s) || !TryParseAssTime(fields[endField], out var e)) continue;
            Events.Add(new SubEvent
            {
                LineIndex = i, Start = s, End = e, Text = fields[^1], IsComment = comment,
                Prefix = line[..(colon + 1)], Fields = fields, StartField = startField, EndField = endField,
            });
        }
    }

    void ParseSrt()
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            var m = SrtTime.Match(Lines[i]);
            if (!m.Success) continue;
            long T(int g) => long.Parse(m.Groups[g].Value) * 3600000 + long.Parse(m.Groups[g + 1].Value) * 60000
                           + long.Parse(m.Groups[g + 2].Value) * 1000 + FracMs(m.Groups[g + 3].Value);
            var text = new StringBuilder();
            for (int j = i + 1; j < Lines.Count && Lines[j].Trim().Length > 0; j++) text.Append(text.Length > 0 ? "\\N" : "").Append(Lines[j]);
            Events.Add(new SubEvent { LineIndex = i, Start = T(1), End = T(5), Text = text.ToString(), Suffix = m.Groups[9].Value });
        }
    }

    public string Serialize()
    {
        var lines = Lines.ToArray();
        foreach (var ev in Events)
        {
            if (Format == SubFormat.Srt)
            {
                lines[ev.LineIndex] = $"{FormatSrtTime(ev.Start)} --> {FormatSrtTime(ev.End)}{ev.Suffix}";
                continue;
            }
            var f = (string[])ev.Fields.Clone();
            // keep any padding the original field had
            f[ev.StartField] = f[ev.StartField][..^f[ev.StartField].TrimStart().Length] + FormatAssTime(ev.Start);
            f[ev.EndField] = f[ev.EndField][..^f[ev.EndField].TrimStart().Length] + FormatAssTime(ev.End);
            lines[ev.LineIndex] = ev.Prefix + string.Join(',', f);
        }
        return string.Join(_newline, lines);
    }

    /// <summary>Always writes UTF-8 with BOM — what Aegisub/VSFilter/libass all read without guessing.</summary>
    public void Save(string path) => File.WriteAllText(path, Serialize(), new UTF8Encoding(true));

    public static bool TryParseAssTime(string s, out long ms)
    {
        ms = 0;
        var p = s.Trim().Split(':');
        if (p.Length != 3) return false;
        var sec = p[2].Split('.');
        if (!long.TryParse(p[0], out var h) || !long.TryParse(p[1], out var m) || !long.TryParse(sec[0], out var ss)) return false;
        ms = h * 3600000 + m * 60000 + ss * 1000 + (sec.Length > 1 ? FracMs(sec[1]) : 0);
        return true;
    }

    static long FracMs(string frac) => (long)Math.Round(double.Parse("0." + frac, CultureInfo.InvariantCulture) * 1000);

    public static string FormatAssTime(long ms)
    {
        long cs = Math.Max(0, (ms + 5) / 10);
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    public static string FormatSrtTime(long ms)
    {
        ms = Math.Max(0, ms);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";
    }

    /// <summary>Accepts "h:mm:ss.xx", "mm:ss", or plain seconds ("-1.5").</summary>
    public static long ParseFlexibleTime(string s)
    {
        s = s.Trim().Replace(',', '.');
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        double total = 0;
        foreach (var part in s.Split(':')) total = total * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return (long)Math.Round((neg ? -total : total) * 1000);
    }
}
