using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TL;

namespace SubMatcher.Core;

/// <summary>One subtitle file posted in a channel.</summary>
public sealed record TgFile(int Id, string Name, long Size);

/// <summary>All files of one show from one platform, split by 简/繁. Episodes are not told apart.</summary>
public sealed record TgGroup(string Title, string Platform, List<TgFile> Chs, List<TgFile> Cht, List<TgFile> Other)
{
    public int Count => Chs.Count + Cht.Count + Other.Count;
}

/// <summary>
/// Subtitle channels such as @anime_chinese_subtitles. Files are named like "CHS_片名_第13集_iQIYI.srt" /
/// "CHT 片名 EP12_Viu.srt": language first, platform after the last underscore.
/// </summary>
public static partial class TgSubs
{
    public const string DefaultChannel = "anime_chinese_subtitles";

    [GeneratedRegex(@"^\s*(CHS|CHT|简|簡|繁)[\s_\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex LangPrefix();

    // Language as the last part instead: "…_EP14.BG.zh-Hans", "… 12_简体中文", "….sc"
    [GeneratedRegex(@"[\s_.\-]+(zh-?Hans|zh-?CN|zh-?SG|chs|sc|简体中文|簡體中文|简体|簡體|简中|簡中|zh-?Hant|zh-?TW|zh-?HK|cht|tc|繁体中文|繁體中文|繁体|繁體|繁中)$", RegexOptions.IgnoreCase)]
    private static partial Regex LangSuffix();

    [GeneratedRegex(@"^(zh-?Hans|zh-?CN|zh-?SG|chs|sc|简|簡)", RegexOptions.IgnoreCase)]
    private static partial Regex SimplifiedTag();

    // Episode marks at the end of the title: 第13集 / 第7話 / EP12 / E05 / 12 / 12.5 / 12v2
    [GeneratedRegex(@"[\s_.\-]*(?:第\s*\d+(?:\.\d+)?\s*[話话集回]|EP?\s*\d+(?:\.\d+)?|\d+(?:\.\d+)?)(?:v\d)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeTail();

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Spaces();

    /// <summary>"CHT_片名_第13集_iQIYI.srt" → ("CHT", "片名", "iQIYI").</summary>
    public static (string Lang, string Title, string Platform) Parse(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        string lang = "";
        if (LangPrefix().Match(name) is { Success: true } m)
        {
            lang = m.Groups[1].Value.ToUpperInvariant() switch { "CHS" or "简" or "簡" => "CHS", _ => "CHT" };
            name = name[m.Length..];
        }
        else if (LangSuffix().Match(name) is { Success: true } s)
        {
            lang = SimplifiedTag().IsMatch(s.Groups[1].Value) ? "CHS" : "CHT";
            name = name[..s.Index];
        }
        string platform = "";
        // platform after the last "_" or "." ("…_iQIYI", "…_EP14.BG"); "…_第13集" / "…_12" is not one
        int us = name.LastIndexOfAny(['_', '.']);
        if (us > 0 && name[(us + 1)..].Any(char.IsLetter) && EpisodeTail().Match(name[us..]) is not { Success: true, Index: 0 })
        {
            platform = name[(us + 1)..].Trim();
            name = name[..us];
        }
        var title = Spaces().Replace(EpisodeTail().Replace(name, ""), " ").Trim(' ', '-', '_');
        return (lang, title.Length > 0 ? title : name.Trim(), platform);
    }

    static double EpisodeNum(TgFile f) => Sync.EpisodeOf(f.Name) is { } e ? double.Parse(e, System.Globalization.CultureInfo.InvariantCulture) : double.MaxValue;

    /// <summary>Files in episode order (第2集 before 第10集).</summary>
    public static List<TgFile> ByEpisode(IEnumerable<TgFile> files) =>
        files.OrderBy(EpisodeNum).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"第 1–7、9–12 集" — gaps show at a glance; "" when no episode numbers are found.</summary>
    public static string EpisodeSummary(IEnumerable<TgFile> files)
    {
        var eps = files.Select(EpisodeNum).Where(e => e != double.MaxValue).Distinct().Order().ToList();
        if (eps.Count == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < eps.Count;)
        {
            int j = i;
            while (j + 1 < eps.Count && eps[j + 1] == eps[j] + 1) j++;
            parts.Add(j == i ? $"{eps[i]}" : $"{eps[i]}–{eps[j]}");
            i = j + 1;
        }
        return $"第 {string.Join("、", parts)} 集";
    }

    /// <summary>Merge search hits by (title, platform). Reposted files keep only the newest copy.</summary>
    public static List<TgGroup> Group(IEnumerable<TgFile> files)
    {
        var latest = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(f => f.Id)!);
        var groups = latest
            .Select(f => (File: f, P: Parse(f.Name)))
            .GroupBy(x => (x.P.Title.ToLowerInvariant(), x.P.Platform.ToLowerInvariant()))
            .Select(g =>
            {
                List<TgFile> Of(string lang) => ByEpisode(g.Where(x => x.P.Lang == lang).Select(x => x.File));
                var first = g.First().P;
                return new TgGroup(first.Title, first.Platform, Of("CHS"), Of("CHT"), Of(""));
            })
            .ToList();
        // Some platforms write the CHS file's title in simplified and the CHT one's in traditional characters.
        // ponytail: same platform + same title length + one side 简-only and the other 繁-only = same show; a real 简繁 table if this misfires
        foreach (var t in groups.Where(g => g.Chs.Count == 0 && g.Other.Count == 0).ToList())
        {
            var s = groups.FirstOrDefault(g => g.Cht.Count == 0 && g.Other.Count == 0 && g.Chs.Count > 0 && g.Title.Length == t.Title.Length
                                              && string.Equals(g.Platform, t.Platform, StringComparison.OrdinalIgnoreCase));
            if (s == null) continue;
            s.Cht.AddRange(t.Cht);
            groups.Remove(t);
        }
        return groups.OrderByDescending(g => g.Count).ThenBy(g => g.Title, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// App credentials, never in the repo: baked in at build time (-p:TgApiId=… from CI secrets), or TG_API_ID / TG_API_HASH
    /// in the environment for local builds.
    /// </summary>
    internal static (string Id, string Hash) Api()
    {
        string Get(string k, string env) =>
            typeof(TgSubs).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == k)?.Value is { Length: > 0 } v
                ? v : Environment.GetEnvironmentVariable(env) ?? "";
        return (Get("TgApiId", "TG_API_ID"), Get("TgApiHash", "TG_API_HASH"));
    }

    /// <summary>QR modules for a tg://login link (true = dark), for the GUI to draw or the CLI to print.</summary>
    public static bool[][] QrMatrix(string text)
    {
        using var data = new QRCoder.QRCodeGenerator().CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.L);
        return data.ModuleMatrix.Select(row => row.Cast<bool>().ToArray()).ToArray();
    }
}

/// <summary>
/// Telegram user client (MTProto). The public web preview can't search Chinese and has no download links, so this logs in
/// once by scanning a QR code with the Telegram app; the session lives next to the executable.
/// </summary>
public sealed class TgClient : IDisposable
{
    public static string SessionPath => Path.Combine(AppContext.BaseDirectory, "telegram.session");

    readonly WTelegram.Client _c;
    readonly Dictionary<string, Channel> _peers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="proxy">empty = direct; socks5://host:port; or an MTProxy link (t.me/proxy?server=…)</param>
    /// <param name="askPassword">two-step verification password, asked only for accounts that have one (called off the UI thread)</param>
    public TgClient(string? proxy = null, Func<string?>? askPassword = null)
    {
        var (id, hash) = TgSubs.Api();
        if (!int.TryParse(id, out _) || hash.Length == 0) throw new InvalidOperationException("这个版本没有内置 Telegram 接口参数，请下载 Release 里的正式版");
        // Keep the library's recent log lines in memory: shown when a login fails, so problems can be told apart.
        WTelegram.Helpers.Log = (level, s) => { if (level >= 2) lock (Recent) { Recent.Enqueue($"{DateTime.Now:HH:mm:ss} {s}"); while (Recent.Count > 30) Recent.Dequeue(); } };
        _c = new WTelegram.Client(k => k switch
        {
            "api_id" => id,
            "api_hash" => hash,
            "session_pathname" => SessionPath,
            "password" => askPassword?.Invoke() ?? throw new OperationCanceledException(),
            _ => null,
        });
        if (string.IsNullOrWhiteSpace(proxy)) return;
        if (proxy.Contains("proxy?", StringComparison.OrdinalIgnoreCase)) _c.MTProxyUrl = proxy.Trim();
        else if (Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var u) && u.Scheme.StartsWith("socks5"))
            _c.TcpHandler = (host, port) => Socks5(u, host, port);
        else throw new InvalidOperationException("代理格式：socks5://主机:端口，或 MTProxy 链接");
    }

    User? _me;
    public bool LoggedIn => (_me ??= _c.User) != null;
    public string? UserName => LoggedIn && _me is { } u ? (u.username is { Length: > 0 } n ? "@" + n : u.first_name) : null;

    /// <summary>Resumes the saved session if there is one; true when logged in.</summary>
    public async Task<bool> Resume()
    {
        if (!File.Exists(SessionPath)) return false;
        await _c.ConnectAsync();
        if (_c.UserId == 0) return false;
        // ConnectAsync doesn't fill Client.User for a saved session: ask who we are (fails if the session was revoked).
        try { _me = (await _c.Users_GetUsers(InputUser.Self)).OfType<User>().FirstOrDefault(); }
        catch (RpcException e) when (e.Code == 401) { _me = null; }
        return _me != null;
    }

    /// <summary>
    /// QR login: <paramref name="showQr"/> gets a tg://login link to show as a QR code (it is refreshed every ~30 s);
    /// scan it in Telegram → Settings → Devices → Link Desktop Device.
    /// </summary>
    public async Task LoginQr(Action<string> showQr, CancellationToken ct) => await _c.LoginWithQRCode(showQr, logoutFirst: false, ct: ct);

    /// <summary>
    /// Phone login, one step at a time: pass the phone number (+86…), then the code, then the 2FA password.
    /// Returns what is needed next ("verification_code" / "password"), or null once logged in.
    /// </summary>
    public async Task<string?> LoginPhone(string info) => await _c.Login(info.Trim()) switch
    {
        "name" => throw new InvalidOperationException("这个手机号还没注册 Telegram"),
        var next => next,
    };

    static readonly Queue<string> Recent = new();

    /// <summary>The Telegram library's last log lines (warnings and up).</summary>
    public static string RecentLog() { lock (Recent) return string.Join('\n', Recent); }

    /// <summary>Readable text for common Telegram errors.</summary>
    public static string Explain(Exception e) => e is RpcException r ? r.Message switch
    {
        "PHONE_NUMBER_INVALID" => "手机号格式不对，要带国家码，如 +8613800000000",
        "PHONE_CODE_INVALID" => "验证码不对",
        "PHONE_CODE_EXPIRED" => "验证码过期了，重新发送",
        "PASSWORD_HASH_INVALID" => "两步验证密码不对",
        "AUTH_TOKEN_EXPIRED" or "AUTH_TOKEN_INVALID" => "二维码过期了，点「刷新二维码」",
        "AUTH_TOKEN_ALREADY_ACCEPTED" => "这个二维码已经被扫过了，点「刷新二维码」再扫",
        var m when m.StartsWith("FLOOD_WAIT") => $"操作太频繁，{r.X} 秒后再试",
        var m => $"Telegram 返回 {r.Code} {m}",
    } : e.Message;

    public static void Logout()
    {
        if (File.Exists(SessionPath)) File.Delete(SessionPath);
    }

    async Task<Channel> Peer(string channel)
    {
        channel = channel.Trim().TrimStart('@');
        if (channel.Contains('/')) channel = channel.TrimEnd('/')[(channel.TrimEnd('/').LastIndexOf('/') + 1)..];
        if (_peers.TryGetValue(channel, out var p)) return p;
        var r = await _c.Contacts_ResolveUsername(channel);
        return _peers[channel] = r.Chat as Channel ?? throw new InvalidOperationException($"@{channel} 不是频道");
    }

    /// <summary>Every document in the channel whose name matches <paramref name="query"/> (server-side search, pages of 100).</summary>
    public async Task<List<TgFile>> Search(string channel, string query, IProgress<int>? found = null, CancellationToken ct = default)
    {
        var peer = await Peer(channel);
        var files = new List<TgFile>();
        for (int offset = 0; ;)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _c.Messages_Search(peer, query, new InputMessagesFilterDocument(), offset_id: offset, limit: 100);
            foreach (var m in page.Messages)
            {
                offset = m.ID;
                if (m is Message { media: MessageMediaDocument { document: Document d } } && d.Filename is { } name)
                    files.Add(new(m.ID, name, d.size));
            }
            found?.Report(files.Count);
            if (page.Messages.Length == 0) return files;
        }
    }

    /// <summary>Downloads into <paramref name="dir"/> under the posted file names; returns the written paths.</summary>
    public async Task<List<string>> Download(string channel, IReadOnlyList<TgFile> files, string dir, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var peer = await Peer(channel);
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (var chunk in files.Chunk(100))
        {
            var ids = chunk.Select(f => (InputMessage)new InputMessageID { id = f.Id }).ToArray();
            var msgs = await _c.Channels_GetMessages(peer, ids);
            foreach (var m in msgs.Messages)
            {
                ct.ThrowIfCancellationRequested();
                if (m is not Message { media: MessageMediaDocument { document: Document d } }) continue;
                var path = Path.Combine(dir, SafeName(d.Filename ?? $"{m.ID}.srt"));
                var tmp = path + ".part";
                await using (var fs = File.Create(tmp)) await _c.DownloadFileAsync(d, fs);
                File.Move(tmp, path, true);
                written.Add(path);
                progress?.Report((written.Count, files.Count));
            }
        }
        return written;
    }

    static string SafeName(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c));

    /// <summary>Minimal SOCKS5 CONNECT without auth (Clash / v2rayN local ports).</summary>
    static async Task<TcpClient> Socks5(Uri proxy, string host, int port)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port);
            var s = tcp.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 0 });
            var buf = new byte[262];
            await s.ReadExactlyAsync(buf.AsMemory(0, 2));
            if (buf[1] != 0) throw new IOException("SOCKS5 代理要求认证，暂不支持");
            var h = Encoding.ASCII.GetBytes(host);
            await s.WriteAsync((byte[])[5, 1, 0, 3, (byte)h.Length, .. h, (byte)(port >> 8), (byte)port]);
            await s.ReadExactlyAsync(buf.AsMemory(0, 4));
            if (buf[1] != 0) throw new IOException($"SOCKS5 代理连接失败（{buf[1]}）");
            int addr = buf[3] switch { 1 => 4, 4 => 16, _ => -1 };
            if (addr < 0) { await s.ReadExactlyAsync(buf.AsMemory(0, 1)); addr = buf[0]; }
            await s.ReadExactlyAsync(buf.AsMemory(0, addr + 2));
            return tcp;
        }
        catch { tcp.Dispose(); throw; }
    }

    public void Dispose() => _c.Dispose();
}
