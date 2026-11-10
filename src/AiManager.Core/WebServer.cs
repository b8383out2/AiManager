using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiManager.Core;

/// <summary>The loopback-only HTTP server that feeds the window's UI and receives Claude Code's hook calls.</summary>
public sealed partial class WebServer : IAsyncDisposable
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly AppHost _host;
    readonly string? _devRoot;
    readonly List<Channel<bool>> _listeners = new();
    WebApplication? _app;
    IntegrationStatus? _integration;
    DateTime _integrationAt;

    public WebServer(AppHost host, string? devRoot)
    {
        _host = host;
        _devRoot = devRoot;
        host.Store.Changed += Notify;
    }

    /// <summary>Binds the configured port, or the next free one after it.</summary>
    public async Task StartAsync()
    {
        Exception? last = null;
        for (int i = 0; i < 10; i++)
        {
            int port = _host.Config.Port + i;
            try
            {
                _app = Build(port);
                await _app.StartAsync();
                if (i > 0)
                {
                    _host.Config.Port = port;
                    _host.SaveConfig();
                }
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                if (_app != null) await _app.DisposeAsync();
                _app = null;
            }
        }
        throw new InvalidOperationException("AiManager could not find a free local port.", last);
    }

    public async ValueTask DisposeAsync()
    {
        _host.Store.Changed -= Notify;
        if (_app != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _app.StopAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            await _app.DisposeAsync();
        }
    }

    WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        // Only this machine may talk to the server, and only by its loopback name: a web page that
        // points some other hostname at 127.0.0.1 is turned away.
        app.Use(async (ctx, next) =>
        {
            var name = ctx.Request.Host.Host;
            if (name is not ("127.0.0.1" or "localhost"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });

        app.MapGet("/", ctx => Asset(ctx, "index.html"));
        app.MapGet("/{file}", (HttpContext ctx, string file) => Asset(ctx, file));

        app.MapGet("/api/state", () => Results.Json(StatePayload(), Web));
        app.MapGet("/api/session", (string key) =>
            _host.Store.BuildDetail(key) is { } detail ? Results.Json(detail, Web) : Results.NotFound());
        app.MapGet("/api/events", Events);

        // Images go into <img src>, which cannot send a header, so these take the token in the query.
        // That also keeps any other web page from embedding the user's screenshots.
        IResult Image(SessionStore.Blob? blob, bool cache)
        {
            if (blob == null) return Results.NotFound();
            return Results.File(blob.Data, blob.MediaType, lastModified: null, entityTag: null, enableRangeProcessing: false)
                is var r && cache ? new CachedResult(r) : r;
        }
        app.MapGet("/api/image", (string key, string id, string t) =>
            t != _host.Config.Token ? Results.StatusCode(StatusCodes.Status403Forbidden) : Image(_host.Store.ReadImage(key, id), cache: true));
        app.MapGet("/api/file", (string key, string id, string t) =>
            t != _host.Config.Token ? Results.StatusCode(StatusCodes.Status403Forbidden) : Image(_host.Store.ReadSentFile(key, id), cache: false));

        // Cast to Delegate so these are route handlers whose IResult is written to the response;
        // a bare HttpContext lambda would bind as a RequestDelegate and drop the result.
        void Post(string path, Func<JsonElement, IResult> handle) =>
            app.MapPost(path, (Delegate)((HttpContext ctx) => Guarded(ctx, handle)));

        Post("/api/answer", body =>
        {
            var id = body.Str("id");
            var text = body.Str("text")?.Trim();
            if (id == null || string.IsNullOrEmpty(text)) return Results.BadRequest();
            var result = _host.Gateway.Answer(id, text, body.Str("how") == "input" ? "input" : "click");
            return Results.Json(new { result = result.ToString() }, Web);
        });

        Post("/api/dismiss", body =>
            body.Str("id") is { } id && _host.Gateway.Dismiss(id) ? Results.Ok() : Results.NotFound());

        Post("/api/settings", body =>
        {
            var cfg = _host.Config;
            cfg.Theme = body.Str("theme") ?? cfg.Theme;
            cfg.Lang = body.Str("lang") ?? cfg.Lang;
            if (body.Dbl("historyDays") is { } days) cfg.HistoryDays = (int)days;
            if (body.Dbl("holdMinutes") is { } hold) cfg.HoldMinutes = (int)hold;
            if (body.TryGetProperty("holdTrailingQuestions", out var q) && q.ValueKind is JsonValueKind.True or JsonValueKind.False)
                cfg.HoldTrailingQuestions = q.GetBoolean();
            if (body.TryGetProperty("relayPermissions", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                cfg.RelayPermissions = p.GetBoolean();
            cfg.Normalize();
            _host.SaveConfig();
            // Keep our own hook entries in step with the settings, but only where the user already installed them.
            if (_host.Integration.Status().Installed) TryIntegration(() => _host.Integration.Install());
            _integration = null;
            _host.Store.Poke();
            Notify();
            return Results.Json(StatePayload(), Web);
        });

        Post("/api/integration", body =>
        {
            var error = TryIntegration(body.Bool("enable") ? _host.Integration.Install : _host.Integration.Uninstall);
            _integration = null;
            Notify();
            return Results.Json(new { error, integration = _host.Integration.Status() }, Web);
        });

        Post("/api/reveal-file", body =>
        {
            var path = body.Str("key") is { } key && body.Str("id") is { } id ? _host.Store.SentFilePath(key, id) : null;
            if (path == null) return Results.NotFound();
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
            else return Results.NotFound();
            return Results.Ok();
        });

        Post("/api/reveal", body =>
        {
            var cwd = body.Str("key") is { } key ? _host.Store.BuildDetail(key)?.Session.Cwd : null;
            if (cwd == null || !Directory.Exists(cwd)) return Results.NotFound();
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { cwd }, UseShellExecute = false });
            return Results.Ok();
        });

        app.MapPost(Integration.Marker + "{token}/{evt}", Hook);
        return app;
    }

    object StatePayload()
    {
        if (_integration == null || DateTime.UtcNow - _integrationAt > TimeSpan.FromSeconds(3))
        {
            _integration = _host.Integration.Status();
            _integrationAt = DateTime.UtcNow;
        }
        var cfg = _host.Config;
        return new
        {
            state = _host.Store.BuildState(),
            integration = _integration,
            settings = new { cfg.Theme, cfg.Lang, cfg.HistoryDays, cfg.HoldMinutes, cfg.HoldTrailingQuestions, cfg.RelayPermissions },
            app = new { version = AppHost.Version, port = cfg.Port },
        };
    }

    string? TryIntegration(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _host.Log.Write("integration change failed: " + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>Requests that change something must carry the token baked into the page the window loaded.</summary>
    async Task<IResult> Guarded(HttpContext ctx, Func<JsonElement, IResult> handle)
    {
        if (ctx.Request.Headers["X-AiManager-Token"] != _host.Config.Token) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return handle(doc.RootElement);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
    }

    async Task<IResult> Hook(HttpContext ctx, string token, string evt)
    {
        if (token != _host.Config.Token) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var body = doc.RootElement;
            object? reply = null;
            switch (evt)
            {
                case "stop": reply = await _host.Gateway.OnStop(body, ctx.RequestAborted); break;
                case "ask": reply = await _host.Gateway.OnAsk(body, ctx.RequestAborted); break;
                case "permission": reply = await _host.Gateway.OnPermission(body, ctx.RequestAborted); break;
                case "notify": _host.Gateway.OnNotify(body); break;
            }
            // An empty 2xx tells Claude Code to carry on exactly as if no hook were installed.
            return reply == null ? Results.Ok() : Results.Json(reply, Web);
        }
        catch (JsonException)
        {
            return Results.Ok();
        }
        catch (OperationCanceledException)
        {
            return Results.Ok();
        }
        catch (Exception ex)
        {
            _host.Log.Write($"hook {evt} failed: {ex}");
            return Results.Ok();
        }
    }

    async Task Asset(HttpContext ctx, string name)
    {
        if (!AssetName().IsMatch(name) || name.Contains("..", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        byte[]? bytes = null;
        if (_devRoot != null)
        {
            var file = Path.Combine(_devRoot, name);
            if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
        }
        else
        {
            await using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("AiManager.Core.wwwroot." + name);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
        }
        if (bytes == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (name == "index.html")
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("__AIM_TOKEN__", _host.Config.Token));

        ctx.Response.ContentType = Path.GetExtension(name) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.Body.WriteAsync(bytes);
    }

    // ---------------------------------------------------------------- live updates (server-sent events)

    void Notify()
    {
        lock (_listeners)
            foreach (var ch in _listeners) ch.Writer.TryWrite(true);
    }

    async Task Events(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_listeners) _listeners.Add(channel);
        var ct = ctx.RequestAborted;
        try
        {
            await ctx.Response.WriteAsync("data: hello\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                string frame;
                try
                {
                    await channel.Reader.ReadAsync(timeout.Token);
                    frame = "data: change\n\n";
                    await Task.Delay(200, ct);          // let a burst of file writes settle into one update
                    while (channel.Reader.TryRead(out _)) { }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    frame = ": keep-alive\n\n";
                }
                await ctx.Response.WriteAsync(frame, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_listeners) _listeners.Remove(channel);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex AssetName();

    /// <summary>An embedded image never changes for a given id, so the window can keep it instead of
    /// re-reading a large transcript line on every refresh.</summary>
    sealed class CachedResult(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return inner.ExecuteAsync(ctx);
        }
    }
}
