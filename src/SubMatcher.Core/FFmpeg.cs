using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SubMatcher.Core;

public sealed record VideoInfo(double Fps, double DurationSeconds);

/// <summary>Thin wrapper around the ffmpeg/ffprobe executables (next to the app, in SUBMATCHER_FFMPEG, or on PATH).</summary>
public static class FFmpeg
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
        var text = await RunText("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate,r_frame_rate:format=duration", "-of", "default=nw=1", path], ct);
        double fps = 0, dur = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = line.Split('=', 2);
            if (kv.Length < 2) continue;
            if (kv[0] == "duration") double.TryParse(kv[1], CultureInfo.InvariantCulture, out dur);
            else if (fps == 0 || kv[0] == "avg_frame_rate") { var f = ParseRate(kv[1]); if (f > 0) fps = f; }
        }
        if (fps <= 0) throw new InvalidOperationException($"读不到视频流：{path}");
        return new VideoInfo(fps, dur);
    }

    static double ParseRate(string s)
    {
        var p = s.Split('/');
        if (!double.TryParse(p[0], CultureInfo.InvariantCulture, out var n)) return 0;
        if (p.Length == 1) return n;
        return double.TryParse(p[1], CultureInfo.InvariantCulture, out var d) && d != 0 ? n / d : 0;
    }

    public static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Streams the video as tiny grayscale frames at a fixed rate. onFrames gets the running frame count.</summary>
    public static async Task<byte[]> ReadThumbFrames(string path, double fps, int w, int h, bool hwaccel, Action<int>? onFrames, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (hwaccel) args.AddRange(["-hwaccel", "auto"]);
        // Crop 5% off each edge: TV logos, overscan junk and small letterbox differences stop mattering.
        args.AddRange(["-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={F(fps)},crop=iw*0.9:ih*0.9,scale={w}:{h}:flags=area,format=gray",
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

    /// <summary>Returns an embedded text subtitle track (0-based among subtitle streams) as ASS text. Piped, no temp file.</summary>
    public static Task<string> ExtractSubtitle(string video, int stream, CancellationToken ct = default) =>
        RunText("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video, "-map", $"0:s:{stream}", "-f", "ass", "-"], ct);
}
