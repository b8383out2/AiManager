using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiManager.Core;

namespace AiManager.Tests;

/// <summary>Builds transcript records in the shapes Claude Code writes them (observed in real transcripts).</summary>
static class Rec
{
    static int _seq;

    public static string At(int second) => new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc).AddSeconds(second).ToString("o");

    public static string Human(string text, int second) => JsonSerializer.Serialize(new
    {
        type = "user", timestamp = At(second), cwd = @"C:\work\demo", entrypoint = "claude-desktop",
        origin = new { kind = "human" }, promptSource = "sdk",
        message = new { role = "user", content = text },
    });

    public static string Injected(string text, int second) => JsonSerializer.Serialize(new
    {
        type = "user", timestamp = At(second), promptSource = "sdk", turnOrigin = "sdk",
        message = new { role = "user", content = text },
    });

    public static string Say(string text, int second, string stop = "end_turn") => JsonSerializer.Serialize(new
    {
        type = "assistant", timestamp = At(second),
        message = new
        {
            id = "msg" + ++_seq, model = "claude-test", stop_reason = stop,
            usage = new { input_tokens = 10, output_tokens = 20, cache_read_input_tokens = 100, cache_creation_input_tokens = 5 },
            content = new object[] { new { type = "text", text } },
        },
    });

    public static string Tool(string name, object input, string toolId, int second) => JsonSerializer.Serialize(new
    {
        type = "assistant", timestamp = At(second),
        message = new
        {
            id = "msg" + ++_seq, model = "claude-test", stop_reason = "tool_use",
            content = new object[] { new { type = "tool_use", id = toolId, name, input } },
        },
    });

    public static string Result(string toolId, string content, int second, object? toolUseResult = null, bool error = false) => JsonSerializer.Serialize(new
    {
        type = "user", timestamp = At(second), toolUseResult,
        message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolId, content, is_error = error } } },
    });

    public static string Absorbed(string content, int second) => JsonSerializer.Serialize(new
    {
        type = "queue-operation", operation = "remove", reason = "absorbed_mid_turn", timestamp = At(second), content,
    });

    public static string Enqueue(string content, int second) => JsonSerializer.Serialize(new
    {
        type = "queue-operation", operation = "enqueue", timestamp = At(second), content,
    });

    public static SessionState Replay(params string[] lines) => Replay(new SessionState("s1", "s1", null, "s1.jsonl"), lines);

    public static SessionState Replay(SessionState state, params string[] lines)
    {
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            state.Apply(doc.RootElement);
        }
        return state;
    }
}

/// <summary>A throwaway Claude data folder plus a running AiManager host pointed at it.</summary>
sealed class TestHost : IAsyncDisposable
{
    public string Root { get; }
    public string ClaudeDir => Path.Combine(Root, "claude");
    public AppHost Host { get; }
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    TestHost(string root, AppHost host)
    {
        Root = root;
        Host = host;
    }

    public static async Task<TestHost> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aimanager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "claude", "projects", "demo"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new AppHost(new AppHostOptions { ClaudeDir = Path.Combine(root, "claude"), DataDir = Path.Combine(root, "data"), Port = port });
        await host.StartAsync();
        return new TestHost(root, host);
    }

    public string Transcript(string sessionId) => Path.Combine(ClaudeDir, "projects", "demo", sessionId + ".jsonl");

    public void Write(string sessionId, params string[] lines) =>
        File.AppendAllText(Transcript(sessionId), string.Join("\n", lines) + "\n");

    public string HookUrl(string evt) => $"{Host.Url}aimanager/hook/{Host.Config.Token}/{evt}";

    public Task<HttpResponseMessage> PostHook(string evt, object body) =>
        Http.PostAsync(HookUrl(evt), JsonBody(body));

    public async Task<JsonDocument> Api(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Host.Url.TrimEnd('/') + path) { Content = JsonBody(body) };
        req.Headers.Add("X-AiManager-Token", Host.Config.Token);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    public static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Polls until a decision matching the predicate shows up in the state the UI reads.</summary>
    public async Task<DecisionDto> WaitForDecision(Func<DecisionDto, bool> match, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var found = Host.Store.BuildState().Decisions.FirstOrDefault(match);
            if (found != null) return found;
            await Task.Delay(100);
        }
        throw new TimeoutException("decision did not appear");
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Host.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
