namespace AiManager.Core;

public sealed class AppHostOptions
{
    public string? ClaudeDir { get; set; }
    public string? DataDir { get; set; }
    /// <summary>Serve the UI from this folder instead of the embedded copy (for working on the UI).</summary>
    public string? DevWww { get; set; }
    public int? Port { get; set; }
}

/// <summary>Wires the pieces together: transcript store, hook gateway, settings integration and the local server.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppPaths Paths { get; }
    public AppConfig Config { get; }
    public FileLog Log { get; }
    public DecisionLog Decisions { get; }
    public SessionStore Store { get; }
    public HookGateway Gateway { get; }
    public Integration Integration { get; }
    public WebServer Server { get; }

    public string Url => $"http://127.0.0.1:{Config.Port}/";
    public static string Version => typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public AppHost(AppHostOptions? options = null)
    {
        options ??= new AppHostOptions();
        Paths = new AppPaths(options.ClaudeDir, options.DataDir);
        Config = AppConfig.Load(Paths.ConfigFile);
        if (options.Port is { } port) Config.Port = port;
        Log = new FileLog(Paths.LogFile);
        Decisions = new DecisionLog(Paths.DecisionLogFile);
        Store = new SessionStore(Paths, Config, Decisions, Log);
        Gateway = new HookGateway(Store, Config, Log);
        Integration = new Integration(Paths, Config);
        Server = new WebServer(this, options.DevWww);
    }

    public async Task StartAsync()
    {
        await Server.StartAsync();
        SaveConfig();
        Store.Start();
        Log.Write($"started v{Version} on {Url} reading {Paths.ClaudeDir}");
    }

    public void SaveConfig() => Config.Save(Paths.ConfigFile);

    public async ValueTask DisposeAsync()
    {
        Store.Dispose();
        await Server.DisposeAsync();
    }
}
