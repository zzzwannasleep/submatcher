using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SubMatcher.Core;

/// <param name="VideoStartMs">First video frame's time relative to the container start (0 for most mkv/mp4).</param>
public sealed record VideoInfo(double Fps, double DurationSeconds, double VideoStartMs = 0, int Width = 0, int Height = 0);

/// <summary>The picture inside black bars: W×H at (X, Y) of a FrameW×FrameH frame.</summary>
public sealed record Crop(int W, int H, int X, int Y, int FrameW, int FrameH)
{
    public string Filter => $"crop={W}:{H}:{X}:{Y},";
    public override string ToString()
    {
        var bars = new List<string>();
        if (FrameH - H > 0) bars.Add($"上 {Y} 下 {FrameH - H - Y}");
        if (FrameW - W > 0) bars.Add($"左 {X} 右 {FrameW - W - X}");
        return $"{FrameW}×{FrameH} → {W}×{H}（黑边 {string.Join("，", bars)}）";
    }
}

/// <summary>Thin wrapper around the ffmpeg/ffprobe executables (next to the app, in SUBMATCHER_FFMPEG, or on PATH).</summary>
public static partial class FFmpeg
{
    public static string Exe(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("SUBMATCHER_FFMPEG"), AppContext.BaseDirectory,
                     Path.Combine(AppContext.BaseDirectory, "ffmpeg"), Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin") })
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, file))) return Path.Combine(dir, file);
        return name; // let the OS search PATH
    }

    static Process Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Exe(exe))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { return Process.Start(psi) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"找不到 {exe}：请把 {exe} 放到程序目录、设置 SUBMATCHER_FFMPEG，或加入 PATH。", e);
        }
    }

    static async Task<string> RunText(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Start(exe, args);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} 失败：{err.Trim()}");
        return await outTask;
    }

    public static async Task<VideoInfo> Probe(string path, CancellationToken ct = default)
    {
        var json = await RunText("ffprobe", ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=avg_frame_rate,r_frame_rate,start_time,width,height:format=duration,start_time", "-of", "json", path], ct);
        using var doc = JsonDocument.Parse(json); // JsonDocument: reflection-free, fine under Native AOT
        var root = doc.RootElement;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
        double Num(string? s) => double.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : 0;

        var stream = root.TryGetProperty("streams", out var ss) && ss.GetArrayLength() > 0 ? ss[0] : default;
        if (stream.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"读不到视频流：{path}");
        double fps = ParseRate(Str(stream, "avg_frame_rate") ?? "");
        if (fps <= 0) fps = ParseRate(Str(stream, "r_frame_rate") ?? "");
        if (fps <= 0) throw new InvalidOperationException($"读不到视频帧率：{path}");
        var format = root.TryGetProperty("format", out var f) ? f : default;
        double dur = format.ValueKind == JsonValueKind.Object ? Num(Str(format, "duration")) : 0;
        // Players show time relative to the container start; the first video frame may come later (m2ts/ts often do).
        double start = Num(Str(stream, "start_time")) - (format.ValueKind == JsonValueKind.Object ? Num(Str(format, "start_time")) : 0);
        int Int(string n) => stream.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new VideoInfo(fps, dur, Math.Max(0, start) * 1000, Int("width"), Int("height"));
    }

    static double ParseRate(string s)
    {
        var p = s.Split('/');
        if (!double.TryParse(p[0], CultureInfo.InvariantCulture, out var n)) return 0;
        if (p.Length == 1) return n;
        return double.TryParse(p[1], CultureInfo.InvariantCulture, out var d) && d != 0 ? n / d : 0;
    }

    public static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Black bars (letterbox / pillarbox): one full-size gray frame at each of a few points spread over the video, bars found
    /// on each, then the union of the pictures, so a dark scene can't make it cut into the image. Null when there are none.
    /// Done here rather than with ffmpeg's cropdetect, which is GPL-only and missing from the bundled LGPL build.
    /// </summary>
    public static async Task<Crop?> DetectCrop(string path, CancellationToken ct = default)
    {
        var json = await RunText("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", path], ct);
        int w, h;
        double dur;
        using (var doc = JsonDocument.Parse(json))
        {
            var s = doc.RootElement.GetProperty("streams")[0];
            (w, h) = (s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32());
            dur = doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d)
                  && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        var found = new List<(int T, int B, int L, int R)>();
        foreach (var at in new[] { 0.08, 0.2, 0.32, 0.44, 0.56, 0.68, 0.8, 0.92 })
        {
            var px = await GrayFrame(path, dur * at, ct);
            if (px.Length >= w * h && Bars(px, w, h) is { } b) found.Add(b);
        }
        if (found.Count == 0) return null;
        // Union of the pictures = the thinnest bar seen on each side; even numbers keep chroma planes aligned.
        int Even(int v) => v & ~1;
        int t = Even(found.Min(x => x.T)), bo = Even(found.Min(x => x.B)), l = Even(found.Min(x => x.L)), r = Even(found.Min(x => x.R));
        var crop = new Crop(w - l - r, h - t - bo, l, t, w, h);
        // Bars thinner than 2% of the frame are noise (the 5% edge trim covers them anyway).
        return crop.W >= w * 0.98 && crop.H >= h * 0.98 || crop.W < w / 4 || crop.H < h / 4 ? null : crop;
    }

    /// <summary>Top/bottom/left/right black bar thickness of one gray frame; null for a frame that is dark all over.</summary>
    internal static (int T, int B, int L, int R)? Bars(ReadOnlySpan<byte> px, int w, int h)
    {
        // A line is "black" when it is dark on average and has almost no bright pixels (noise and dither allowed).
        const int Bright = 40;
        bool Dark(ReadOnlySpan<byte> px, int start, int count, int step)
        {
            int sum = 0, bright = 0;
            for (int i = 0, p = start; i < count; i++, p += step) { sum += px[p]; if (px[p] > Bright) bright++; }
            return sum < 30 * count && bright * 100 < count;
        }
        int t = 0;
        while (t < h && Dark(px, t * w, w, 1)) t++;
        if (t == h) return null;
        int b = 0;
        while (b < h - t && Dark(px, (h - 1 - b) * w, w, 1)) b++;
        int rows = h - t - b, l = 0, r = 0;
        while (l < w && Dark(px, t * w + l, rows, w)) l++;
        while (r < w - l && Dark(px, t * w + w - 1 - r, rows, w)) r++;
        return (t, b, l, r);
    }

    static async Task<byte[]> GrayFrame(string path, double seconds, CancellationToken ct)
    {
        using var p = Start("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(Math.Max(0, seconds)), "-i", path,
            "-map", "0:v:0", "-frames:v", "1", "-vf", "format=gray", "-f", "rawvideo", "-pix_fmt", "gray", "-"]);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        await errTask;
        return ms.ToArray();
    }

    /// <summary>Streams the video as tiny grayscale frames at a fixed rate. onFrames gets the running frame count.</summary>
    public static async Task<byte[]> ReadThumbFrames(string path, double fps, int w, int h, bool hwaccel, Action<int>? onFrames, CancellationToken ct,
        Crop? crop = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (hwaccel) args.AddRange(["-hwaccel", "auto"]);
        // Black bars off first (so both sides compare the picture itself), then 5% off each edge:
        // TV logos, overscan junk and small letterbox differences stop mattering.
        args.AddRange(["-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={F(fps)},{crop?.Filter}crop=iw*0.9:ih*0.9,scale={w}:{h}:flags=area,format=gray",
            "-f", "rawvideo", "-pix_fmt", "gray", "-"]);

        using var p = Start("ffmpeg", args);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var ms = new MemoryStream();
        var buf = new byte[w * h * 256];
        int frameSize = w * h, read;
        var stdout = p.StandardOutput.BaseStream;
        while ((read = await stdout.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            onFrames?.Invoke((int)(ms.Length / frameSize));
        }
        await p.WaitForExitAsync(ct);
        var err = await errTask;
        if (p.ExitCode != 0 && ms.Length < frameSize) throw new InvalidOperationException($"ffmpeg 解码失败 {Path.GetFileName(path)}：{err.Trim()}");
        return ms.ToArray();
    }

    /// <summary>
    /// The first audio track as mono float samples, sample 0 at the container's time zero (like the players' clock and the
    /// subtitles), gaps filled with silence. Null when there is no audio track.
    /// </summary>
    public static async Task<float[]?> ReadAudio(string path, int rate, CancellationToken ct)
    {
        using var p = Start("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", "0:a:0?", "-vn", "-sn", "-dn",
            "-af", $"aresample={rate}:async=1:first_pts=0", "-ac", "1", "-f", "f32le", "-"]);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        await errTask;
        return ms.Length < rate * 4 ? null : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(ms.GetBuffer().AsSpan(0, (int)ms.Length & ~3)).ToArray();
    }

    /// <summary>One frame as PNG bytes, for previews.</summary>
    public static async Task<byte[]> GrabFrame(string path, double seconds, int width, CancellationToken ct = default)
    {
        using var p = Start("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(Math.Max(0, seconds)), "-i", path,
            "-frames:v", "1", "-vf", $"scale={width}:-2", "-f", "image2pipe", "-c:v", "png", "-"]);
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        if (ms.Length == 0) throw new InvalidOperationException($"截图失败：{(await errTask).Trim()}");
        return ms.ToArray();
    }

    /// <summary>Subtitle streams in order (N = index among subtitle streams, as ExtractSubtitle takes it).</summary>
    public static async Task<List<(int N, string Codec, string Title)>> SubtitleTracks(string video, CancellationToken ct = default)
    {
        var json = await RunText("ffprobe", ["-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name:stream_tags=title,language", "-of", "json", video], ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(int, string, string)>();
        if (!doc.RootElement.TryGetProperty("streams", out var ss)) return list;
        int n = 0;
        foreach (var s in ss.EnumerateArray())
        {
            string? tag = s.TryGetProperty("tags", out var t) ? (t.TryGetProperty("title", out var ti) ? ti.GetString() : t.TryGetProperty("language", out var la) ? la.GetString() : null) : null;
            list.Add((n++, s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "", tag ?? ""));
        }
        return list;
    }

    /// <summary>Returns an embedded text subtitle track (0-based among subtitle streams) as ASS text. Piped, no temp file.</summary>
    public static Task<string> ExtractSubtitle(string video, int stream, CancellationToken ct = default) =>
        RunText("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video, "-map", $"0:s:{stream}", "-f", "ass", "-"], ct);
}
