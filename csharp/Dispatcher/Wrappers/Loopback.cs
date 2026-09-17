using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace LlmDispatcher.Wrappers;

/// <summary>
/// Loopback HTTP helpers shared by the Claude Code and Codex wrappers. The proxy binds to
/// 127.0.0.1 only, forwards the CLI's own request headers to the upstream unchanged
/// (authorization included), and never reads, logs, or stores credential values.
/// </summary>
public static partial class Loopback
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AllowAutoRedirect = false,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly HashSet<string> DropRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "host", "content-length", "connection", "transfer-encoding", "accept-encoding", "keep-alive" };
    private static readonly HashSet<string> DropResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "content-length", "content-encoding", "transfer-encoding", "connection", "keep-alive" };

    public delegate Task Handler(HttpContext ctx, byte[] body);

    public sealed class Server(WebApplication app, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;
        public ValueTask DisposeAsync() => new(app.StopAsync());
    }

    public static async Task<Server> StartAsync(Handler handler)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
        {
            o.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
            o.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
        });
        var app = builder.Build();
        app.Run(async ctx =>
        {
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                await handler(ctx, ms.ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 502;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync($$$"""{"type":"error","error":{"type":"dispatcher_proxy_error","message":"{{{Redact(ex.Message).Replace("\"", "'")}}}"}}""");
                }
            }
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var port = new Uri(addresses.First()).Port;
        return new Server(app, port);
    }

    /// <summary>
    /// Forward a request to <paramref name="upstreamUrl"/> and stream the response back verbatim.
    /// Compression is disabled on the upstream leg so SSE pings and deltas arrive as produced.
    /// </summary>
    public static async Task ForwardAsync(string upstreamUrl, HttpContext ctx, byte[]? body)
    {
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamUrl);
        if (body is { Length: > 0 } && ctx.Request.Method is not ("GET" or "HEAD"))
        {
            req.Content = new ByteArrayContent(body);
            if (ctx.Request.ContentType is { } ctype) req.Content.Headers.TryAddWithoutValidation("content-type", ctype);
        }
        foreach (var (k, v) in ctx.Request.Headers)
        {
            if (DropRequestHeaders.Contains(k) || k.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
            if (!req.Headers.TryAddWithoutValidation(k, (IEnumerable<string?>)v)) req.Content?.Headers.TryAddWithoutValidation(k, (IEnumerable<string?>)v);
        }
        req.Headers.TryAddWithoutValidation("accept-encoding", "identity");

        using var upstream = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        foreach (var (k, v) in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (DropResponseHeaders.Contains(k)) continue;
            ctx.Response.Headers[k] = v.ToArray();
        }
        if (ctx.Request.Method == "HEAD") return;
        await using var stream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }

    public static async Task WriteJsonAsync(HttpContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }

    public static string Redact(string m) => KeyLike().Replace(m, "[redacted]");

    [GeneratedRegex(@"(sk|key|bearer)[-_ ][A-Za-z0-9_.-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLike();
}
