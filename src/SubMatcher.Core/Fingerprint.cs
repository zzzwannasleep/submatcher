using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SubMatcher.Core;

/// <summary>
/// A video reduced to one small normalized grayscale vector per frame at a fixed analysis rate.
/// Frame similarity is a plain dot product (= Pearson correlation), so brightness/contrast/range
/// differences between encodes cancel out.
/// </summary>
public sealed class Fingerprint
{
    public const int W = 32, H = 18, Dim = W * H;
    public double Fps { get; }
    public int Count { get; }
    readonly float[] _v;

    /// <summary>1 - similarity between frame i-1 and i; high = hard cut.</summary>
    public float[] Change { get; }

    public Fingerprint(byte[] raw, double fps)
    {
        Fps = fps;
        Count = raw.Length / Dim;
        _v = new float[Count * Dim];
        float flat = 1f / MathF.Sqrt(Dim);
        Span<int> hist = stackalloc int[256];
        Span<float> rankOf = stackalloc float[256];
        for (int f = 0; f < Count; f++)
        {
            var src = raw.AsSpan(f * Dim, Dim);
            var dst = _v.AsSpan(f * Dim, Dim);
            hist.Clear();
            foreach (var b in src) hist[b]++;
            int lo = 0, hi = 255;
            while (hist[lo] == 0) lo++;
            while (hist[hi] == 0) hi--;
            // ponytail: flat frames (black/white/fades) all map to the same constant vector: they match each
            // other perfectly and are orthogonal to every real frame, so they carry no false evidence.
            if (hi - lo < 8) { dst.Fill(flat); continue; }
            // Pixel values → ranks (ties share the average rank), then zero-mean/unit-length: Spearman correlation.
            // A burned-in TV logo becomes a few outlying ranks instead of dominating the variance, and any
            // monotonic difference (gamma, contrast, TV/PC range) disappears entirely.
            int below = 0;
            for (int v = 0; v < 256; v++) { rankOf[v] = below + (hist[v] - 1) / 2f; below += hist[v]; }
            double mean = (Dim - 1) / 2.0, ss = 0;
            for (int i = 0; i < Dim; i++) { var d = rankOf[src[i]] - mean; ss += d * d; }
            var inv = 1 / Math.Sqrt(ss);
            for (int i = 0; i < Dim; i++) dst[i] = (float)((rankOf[src[i]] - mean) * inv);
        }
        Change = new float[Count];
        for (int f = 1; f < Count; f++) Change[f] = 1 - Dot(Frame(f - 1), Frame(f));
    }

    public ReadOnlySpan<float> Frame(int i) => _v.AsSpan(i * Dim, Dim);

    public bool IsCut(int f) => f > 0 && f < Count && Change[f] > 0.35f
        && Change[f] >= Change[f - 1] && (f + 1 >= Count || Change[f] >= Change[f + 1]);

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        float sum = Vector.Sum(acc);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    public static async Task<Fingerprint> FromVideo(string path, double fps, bool hwaccel, bool useCache, Action<int>? onFrames, CancellationToken ct)
    {
        var cache = CachePath(path, fps);
        if (useCache && File.Exists(cache))
        {
            try { return new Fingerprint(await File.ReadAllBytesAsync(cache, ct), fps); } catch (IOException) { }
        }
        var raw = await FFmpeg.ReadThumbFrames(path, fps, W, H, hwaccel, onFrames, ct);
        if (raw.Length < Dim) throw new InvalidOperationException($"没有解出任何画面：{path}");
        if (useCache)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(cache)!); await File.WriteAllBytesAsync(cache, raw, ct); } catch (IOException) { }
        }
        return new Fingerprint(raw, fps);
    }

    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubMatcher", "cache");

    static string CachePath(string path, double fps)
    {
        var fi = new FileInfo(path);
        var key = $"v1|{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{FFmpeg.F(fps)}|{W}x{H}";
        return Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    }
}
