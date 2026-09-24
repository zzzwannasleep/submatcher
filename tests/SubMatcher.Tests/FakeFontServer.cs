using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SubMatcher.Tests;

/// <summary>Minimal local stand-in for a FontInAss server's POST /api/subset (no network in tests).</summary>
sealed class FakeFontServer : IDisposable
{
    public record Request(string File, string? Check, string? Clean, string? Salt, string? Key, string Body);

    readonly HttpListener _http = new();
    public List<Request> Seen { get; } = [];
    public string Url { get; }

    /// <param name="reply">file name + body → (X-Code, messages JSON, response body)</param>
    public FakeFontServer(Func<string, string, (int Code, string Messages, string Body)> reply)
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        Url = $"http://localhost:{port}/";
        _http.Prefixes.Add(Url);
        _http.Start();
        _ = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); } catch { return; }
                var name = Encoding.UTF8.GetString(Convert.FromBase64String(ctx.Request.Headers["X-Filename"]!));
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var salt = ctx.Request.Headers["X-Font-Alias-Salt"] is { } s ? Encoding.UTF8.GetString(Convert.FromBase64String(s)) : null;
                lock (Seen) Seen.Add(new(name, ctx.Request.Headers["X-Fonts-Check"], ctx.Request.Headers["X-Clear-Fonts"], salt, ctx.Request.Headers["X-API-Key"], body));
                var (code, msg, text) = reply(name, body);
                ctx.Response.Headers["X-Code"] = code.ToString();
                ctx.Response.Headers["X-Message"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(msg));
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _http.Stop();
}
