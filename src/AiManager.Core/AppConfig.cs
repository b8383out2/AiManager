using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiManager.Core;

public sealed class AppPaths
{
    /// <summary>Claude Code's own data folder (read only, except settings.json when the user enables the integration).</summary>
    public string ClaudeDir { get; }
    /// <summary>AiManager's own data: settings, the decision log, the session link cache.</summary>
    public string DataDir { get; }

    public string ProjectsDir => Path.Combine(ClaudeDir, "projects");
    public string LiveDir => Path.Combine(ClaudeDir, "sessions");
    public string SettingsFile => Path.Combine(ClaudeDir, "settings.json");
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string DecisionLogFile => Path.Combine(DataDir, "decisions.json");
    public string LinkCacheFile => Path.Combine(DataDir, "links.json");
    public string LogFile => Path.Combine(DataDir, "aimanager.log");

    public AppPaths(string? claudeDir = null, string? dataDir = null)
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        ClaudeDir = claudeDir
            ?? (string.IsNullOrWhiteSpace(env) ? null : env)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        DataDir = dataDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiManager");
        Directory.CreateDirectory(DataDir);
    }
}

public sealed class AppConfig
{
    public int Port { get; set; } = 47821;
    public string Token { get; set; } = "";
    public string Theme { get; set; } = "system";      // light | dark | system
    public string Lang { get; set; } = "zh";           // zh | ko
    public int HistoryDays { get; set; } = 3;
    /// <summary>How long a session is kept waiting for an answer given in AiManager before it is let go.</summary>
    public int HoldMinutes { get; set; } = 30;
    /// <summary>Also hold a turn that ends with a plain question in its text, not only a structured one.</summary>
    public bool HoldTrailingQuestions { get; set; } = true;
    public bool RelayPermissions { get; set; }
    /// <summary>"context" sends the answer as Stop-hook feedback; "block" uses decision/reason for older Claude Code.</summary>
    public string StopReplyMode { get; set; } = "context";
    public int[]? WindowBounds { get; set; }

    internal static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load(string file)
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(file) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), FileJson) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            cfg = new();
        }
        if (cfg.Token.Length < 16) cfg.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        cfg.Normalize();
        return cfg;
    }

    public void Normalize()
    {
        if (Theme is not ("light" or "dark" or "system")) Theme = "system";
        if (Lang is not ("zh" or "ko")) Lang = "zh";
        HistoryDays = Math.Clamp(HistoryDays, 1, 30);
        HoldMinutes = Math.Clamp(HoldMinutes, 1, 240);
        if (StopReplyMode is not ("context" or "block")) StopReplyMode = "context";
        if (Port is < 1024 or > 65000) Port = 47821;
    }

    public void Save(string file)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(this, FileJson));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
