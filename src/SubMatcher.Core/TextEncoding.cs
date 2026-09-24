using System.Text;

namespace SubMatcher.Core;

/// <summary>Reads subtitle text without caring whether it's UTF-8/UTF-16/GBK/BIG5 (the classic Sushi pain point).</summary>
public static class TextEncoding
{
    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // Characters common in either script, plus ones that differ between Simplified and Traditional.
    const string Shared = "的是了不在我有人一你他她好就也要说說";
    const string Simplified = "这们个来为时会对里后么发还没过经问学长门开关东车见现应话写书样让给";
    const string Traditional = "這們個來為時會對裡後麼發還沒過經問學長門開關東車見現應話寫書樣讓給";

    public static string Read(string path) => Decode(File.ReadAllBytes(path), out _);

    public static string Decode(byte[] b, out Encoding detected)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { detected = new UTF8Encoding(true); return Encoding.UTF8.GetString(b, 3, b.Length - 3); }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { detected = Encoding.Unicode; return Encoding.Unicode.GetString(b, 2, b.Length - 2); }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { detected = Encoding.BigEndianUnicode; return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2); }
        if (LooksUtf16Le(b)) { detected = Encoding.Unicode; return Encoding.Unicode.GetString(b); }

        if (TryStrict(new UTF8Encoding(false, true), b, out var s)) { detected = new UTF8Encoding(false); return s; }

        var gb = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var big5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        bool okGb = TryStrict(gb, b, out var sGb), okBig5 = TryStrict(big5, b, out var sBig5);
        if (okGb && (!okBig5 || Score(sGb, Simplified) >= Score(sBig5, Traditional))) { detected = gb; return sGb; }
        if (okBig5) { detected = big5; return sBig5; }

        detected = Encoding.GetEncoding(54936);
        return detected.GetString(b); // lossy last resort
    }

    static bool LooksUtf16Le(byte[] b)
    {
        int n = Math.Min(b.Length, 4000) & ~1, zeros = 0;
        if (n < 20) return false;
        for (int i = 1; i < n; i += 2) if (b[i] == 0) zeros++;
        return zeros > n / 4; // ASCII-heavy UTF-16 has a zero every other byte
    }

    static bool TryStrict(Encoding e, byte[] b, out string s)
    {
        try { s = e.GetString(b); return true; }
        catch (DecoderFallbackException) { s = ""; return false; }
    }

    static int Score(string text, string script)
    {
        int n = 0;
        foreach (var c in text) if (Shared.Contains(c) || script.Contains(c)) n++;
        return n;
    }
}
