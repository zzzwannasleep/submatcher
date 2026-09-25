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
    readonly int _dim = Dim;

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

    Fingerprint(float[] v, int dim, double fps)
    {
        (_v, _dim, Fps, Count) = (v, dim, fps, v.Length / dim);
        Change = new float[Count]; // no cuts in sound
    }

    public ReadOnlySpan<float> Frame(int i) => _v.AsSpan(i * _dim, _dim);

    // ---------------- sound ----------------

    public const int AudioRate = 8000;
    const int Hop = AudioRate / 100, Fft = 256, Bands = 16, HopsPerFrame = 4;

    /// <summary>
    /// The soundtrack on the same frame grid as the pictures, so a Matcher over it gives shifts in the same frames:
    /// each frame is the log spectrogram (16 bands × 4 hops of 10 ms) from that frame's time, per-band average removed
    /// (a different EQ or mix level cancels), zero-mean and unit-length. Silence becomes the same constant vector.
    /// </summary>
    public static Fingerprint FromAudio(float[] bandLog, double fps)
    {
        int hops = bandLog.Length / Bands, dim = Bands;
        var mean = new double[Bands];
        for (int h = 0; h < hops; h++) for (int k = 0; k < Bands; k++) mean[k] += bandLog[h * Bands + k];
        for (int k = 0; k < Bands; k++) mean[k] /= Math.Max(1, hops);
        int count = hops <= HopsPerFrame ? 0 : (int)((hops - HopsPerFrame - 1) * fps / 100) + 1; // last frame's hops stay in range
        var v = new float[count * dim];
        float flat = 1f / MathF.Sqrt(dim);
        for (int f = 0; f < count; f++)
        {
            int h0 = (int)Math.Round(f * 100 / fps);
            var dst = v.AsSpan(f * dim, dim);
            float loud = float.MinValue;
            // Averaged over the frame's 40 ms, not per 10 ms: two releases' sound is rarely offset by whole frames.
            for (int k = 0; k < Bands; k++)
            {
                float x = 0;
                for (int j = 0; j < HopsPerFrame; j++) x += bandLog[(h0 + j) * Bands + k];
                x /= HopsPerFrame;
                loud = Math.Max(loud, x);
                dst[k] = x - (float)mean[k];
            }
            // ponytail: -6 (log10 power) ≈ -60 dBFS counts as silence; like flat pictures, it is evidence of nothing.
            if (loud < -6) { dst.Fill(flat); continue; }
            float avg = 0;
            foreach (var x in dst) avg += x;
            avg /= dim;
            double ss = 0;
            foreach (ref var x in dst) { x -= avg; ss += x * x; }
            if (ss < 1e-9) { dst.Fill(flat); continue; }
            var inv = (float)(1 / Math.Sqrt(ss));
            foreach (ref var x in dst) x *= inv;
        }
        return new Fingerprint(v, dim, fps);
    }

    /// <summary>Mono 8 kHz samples → log10 power in 16 log-spaced bands (60 Hz–4 kHz) every 10 ms.</summary>
    public static float[] BandLog(ReadOnlySpan<float> pcm)
    {
        int hops = pcm.Length < Fft ? 0 : (pcm.Length - Fft) / Hop + 1;
        var edges = Enumerable.Range(0, Bands + 1).Select(b => (int)Math.Round(2 * Math.Pow(Fft / 2 / 2.0, b / (double)Bands))).ToArray();
        var win = Enumerable.Range(0, Fft).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Fft))).ToArray();
        var outp = new float[hops * Bands];
        var samples = pcm.ToArray();
        Parallel.For(0, (hops + 1023) / 1024, chunk =>
        {
            var re = new float[Fft];
            var im = new float[Fft];
            for (int h = chunk * 1024; h < Math.Min(hops, chunk * 1024 + 1024); h++)
            {
                for (int i = 0; i < Fft; i++) { re[i] = samples[h * Hop + i] * win[i]; im[i] = 0; }
                FftInPlace(re, im);
                for (int b = 0; b < Bands; b++)
                {
                    double p = 0;
                    for (int k = edges[b]; k < Math.Max(edges[b] + 1, edges[b + 1]); k++) p += re[k] * re[k] + im[k] * im[k];
                    outp[h * Bands + b] = (float)Math.Log10(p / ((double)Fft * Fft) + 1e-10);
                }
            }
        });
        return outp;
    }

    static void FftInPlace(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < len / 2; k++)
                {
                    float wr = (float)Math.Cos(ang * k), wi = (float)Math.Sin(ang * k);
                    int a = i + k, b = a + len / 2;
                    float xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
        }
    }

    /// <summary>The first audio track as band energies (cached); null when the file has no sound.</summary>
    public static async Task<Fingerprint?> FromAudio(string path, double fps, bool useCache, CancellationToken ct)
    {
        var cache = CachePath(path, $"audio|{AudioRate}|{Bands}", ".abin");
        float[]? bands = null;
        if (useCache && File.Exists(cache))
        {
            try { bands = MemoryMarshal.Cast<byte, float>(await File.ReadAllBytesAsync(cache, ct)).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (bands == null)
        {
            var pcm = await FFmpeg.ReadAudio(path, AudioRate, ct);
            if (pcm == null) return null;
            bands = await Task.Run(() => BandLog(pcm), ct);
            if (useCache)
            {
                try { Directory.CreateDirectory(CacheDir); await File.WriteAllBytesAsync(cache, MemoryMarshal.AsBytes(bands.AsSpan()).ToArray(), ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return FromAudio(bands, fps);
    }

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

    public static async Task<Fingerprint> FromVideo(string path, double fps, bool hwaccel, bool useCache, Action<int>? onFrames, CancellationToken ct,
        Crop? crop = null)
    {
        var cache = CachePath(path, $"{FFmpeg.F(fps)}|{W}x{H}|{crop?.Filter}", ".bin");
        if (useCache && File.Exists(cache))
        {
            try { return new Fingerprint(await File.ReadAllBytesAsync(cache, ct), fps); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var raw = await FFmpeg.ReadThumbFrames(path, fps, W, H, hwaccel, onFrames, ct, crop);
        if (raw.Length < Dim) throw new InvalidOperationException($"没有解出任何画面：{path}");
        if (useCache)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(cache)!); await File.WriteAllBytesAsync(cache, raw, ct); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new Fingerprint(raw, fps);
    }

    /// <summary>Portable: the cache lives next to the executable, never in the user profile. Unwritable folder = no cache.</summary>
    public static string CacheDir => Path.Combine(AppContext.BaseDirectory, "cache");

    static string CachePath(string path, string what, string ext)
    {
        var fi = new FileInfo(path);
        var key = $"v1|{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{what}";
        return Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ext);
    }

    /// <summary>Black-bar detection, cached next to the fingerprints ("none" = no bars).</summary>
    public static async Task<Crop?> DetectCrop(string path, bool useCache, CancellationToken ct)
    {
        var cache = CachePath(path, "crop", ".crop");
        if (useCache && File.Exists(cache))
        {
            try
            {
                var t = (await File.ReadAllTextAsync(cache, ct)).Split(':');
                return t.Length == 6 ? new Crop(int.Parse(t[0]), int.Parse(t[1]), int.Parse(t[2]), int.Parse(t[3]), int.Parse(t[4]), int.Parse(t[5])) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
        }
        var crop = await FFmpeg.DetectCrop(path, ct);
        if (useCache)
        {
            try { Directory.CreateDirectory(CacheDir); await File.WriteAllTextAsync(cache, crop is { } c ? $"{c.W}:{c.H}:{c.X}:{c.Y}:{c.FrameW}:{c.FrameH}" : "none", ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return crop;
    }
}
