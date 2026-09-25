using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SubMatcher.Gui;

/// <summary>
/// Self-update from GitHub Releases. Everything happens inside the portable folder: the zip is downloaded next to the exe,
/// files in use are renamed to *.old (Windows allows renaming a running exe/dll) and removed on the next start.
/// </summary>
static class Updater
{
    const string Api = "https://api.github.com/repos/zzzwannasleep/submatcher/releases/latest";
    static string Dir => AppContext.BaseDirectory;

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new();

    public sealed record Release(Version Version, string Tag, string Url, string? Sha256);

    static string Rid => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
                         + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SubMatcher/" + Current.ToString(3)); // GitHub API rejects requests without one
        return h;
    }

    /// <summary>The newer release for this platform, or null when already up to date.</summary>
    public static async Task<Release?> Check(CancellationToken ct = default)
    {
        using var http = Http();
        using var doc = JsonDocument.Parse(await http.GetStringAsync(Api, ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var v) || v <= Current) return null;
        // Always the edition without ffmpeg: app files only, the ffmpeg folder the user already has stays as it is
        // (and ZipFile would flatten the Linux .so symlinks anyway).
        var name = $"SubMatcher-{tag}-{Rid}.zip";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != name) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:") ? s[7..] : null;
            return new(v, tag, a.GetProperty("browser_download_url").GetString()!, sha);
        }
        throw new InvalidOperationException($"{tag} 里没有 {name}，请到 GitHub 手动下载");
    }

    public static async Task Install(Release r, IProgress<double> progress, CancellationToken ct = default)
    {
        var zip = Path.Combine(Dir, "update.zip");
        try
        {
            using (var http = Http())
            using (var resp = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[1 << 16];
                for (int n; (n = await src.ReadAsync(buf, ct)) > 0;)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    if (total > 0) progress.Report((double)(done += n) / total);
                }
            }
            if (r.Sha256 != null)
            {
                await using var f = File.OpenRead(zip);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("下载的更新包校验失败，请重试");
            }
            Apply(zip, Dir);
        }
        finally { File.Delete(zip); }
    }

    /// <summary>Replace the app files in <paramref name="dir"/> with the zip's; all-or-nothing.</summary>
    internal static void Apply(string zip, string dir)
    {
        var tmp = Path.Combine(dir, "update-tmp");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zip, tmp);
        var replaced = new List<(string Target, bool HadOld)>();
        try
        {
            // Release zips hold one top folder SubMatcher-vX-rid/.
            var root = Directory.GetFiles(tmp).Length == 0 && Directory.GetDirectories(tmp) is [var only] ? only : tmp;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f);
                if (rel.StartsWith("ffmpeg" + Path.DirectorySeparatorChar)) continue;
                var target = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool had = File.Exists(target);
                if (had)
                {
                    File.Move(target, target + ".old", true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, File.GetUnixFileMode(target + ".old"));
                }
                replaced.Add((target, had));
                File.Move(f, target);
            }
        }
        catch
        {
            // Put the old files back so a half-applied update can't leave the app unstartable.
            foreach (var (target, had) in Enumerable.Reverse(replaced))
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (had) File.Move(target + ".old", target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        finally { Directory.Delete(tmp, true); }
    }

    /// <summary>Remove the *.old files left by the previous update (they were in use then).</summary>
    public static void Cleanup()
    {
        foreach (var f in Directory.EnumerateFiles(Dir, "*.old"))
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
