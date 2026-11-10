using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiManager.Core;

public sealed class IntegrationStatus
{
    public bool Installed, UpToDate, RelayPermissions;
    public string SettingsFile = "";
    public string? Error;
    public List<string> Events = new();
}

/// <summary>Adds or removes AiManager's hook entries in the user's Claude Code settings.json.
/// Only ever called because the user pressed the button in the app; every write keeps a backup and
/// touches nothing but entries whose URL carries AiManager's own marker.</summary>
public sealed class Integration
{
    public const string Marker = "/aimanager/hook/";

    readonly AppPaths _paths;
    readonly AppConfig _cfg;

    public Integration(AppPaths paths, AppConfig cfg)
    {
        _paths = paths;
        _cfg = cfg;
    }

    string BaseUrl => $"http://127.0.0.1:{_cfg.Port}{Marker}{_cfg.Token}/";

    public IntegrationStatus Status()
    {
        var st = new IntegrationStatus { SettingsFile = _paths.SettingsFile };
        try
        {
            var hooks = Read()?["hooks"] as JsonObject;
            if (hooks == null) return st;
            var urls = new List<string>();
            foreach (var (evt, groups) in hooks)
            {
                bool ours = false;
                foreach (var url in Urls(groups))
                    if (url.Contains(Marker, StringComparison.Ordinal)) { urls.Add(url); ours = true; }
                if (ours) st.Events.Add(evt);
            }
            st.Installed = st.Events.Contains("Stop");
            st.RelayPermissions = st.Events.Contains("PermissionRequest");
            // A changed port or token leaves stale URLs behind that Claude Code would call in vain.
            st.UpToDate = st.Installed && urls.All(u => u.StartsWith(BaseUrl, StringComparison.Ordinal))
                          && st.RelayPermissions == _cfg.RelayPermissions;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            st.Error = ex.Message;
        }
        return st;
    }

    public void Install()
    {
        var root = Read() ?? new JsonObject();
        Backup();
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null) root["hooks"] = hooks = new JsonObject();
        RemoveOurs(hooks);

        int hold = _cfg.HoldMinutes * 60 + 30;
        Add(hooks, "Stop", null, "stop", hold);
        Add(hooks, "PreToolUse", "AskUserQuestion", "ask", hold);
        Add(hooks, "Notification", null, "notify", 5);
        if (_cfg.RelayPermissions) Add(hooks, "PermissionRequest", null, "permission", hold);
        Write(root);
    }

    public void Uninstall()
    {
        var root = Read();
        if (root?["hooks"] is not JsonObject hooks) return;
        Backup();
        RemoveOurs(hooks);
        if (hooks.Count == 0) root.Remove("hooks");
        Write(root);
    }

    void Add(JsonObject hooks, string evt, string? matcher, string path, int timeoutSeconds)
    {
        var group = new JsonObject();
        if (matcher != null) group["matcher"] = matcher;
        group["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "http",
            ["url"] = BaseUrl + path,
            ["timeout"] = timeoutSeconds,
        });
        if (hooks[evt] is not JsonArray list) hooks[evt] = list = new JsonArray();
        list.Add(group);
    }

    static void RemoveOurs(JsonObject hooks)
    {
        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray groups) continue;
            for (int i = groups.Count - 1; i >= 0; i--)
                if (Urls(groups[i]).Any(u => u.Contains(Marker, StringComparison.Ordinal)))
                    groups.RemoveAt(i);
            if (groups.Count == 0) hooks.Remove(evt);
        }
    }

    static IEnumerable<string> Urls(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var u in Urls(item)) yield return u;
                break;
            case JsonObject obj:
                if (obj["url"] is JsonValue v && v.TryGetValue<string>(out var url)) yield return url;
                foreach (var u in Urls(obj["hooks"])) yield return u;
                break;
        }
    }

    JsonObject? Read()
    {
        if (!File.Exists(_paths.SettingsFile)) return null;
        var text = File.ReadAllText(_paths.SettingsFile);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new JsonException("settings.json is not a JSON object");
    }

    void Backup()
    {
        if (!File.Exists(_paths.SettingsFile)) return;
        File.Copy(_paths.SettingsFile, _paths.SettingsFile + $".aimanager-{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);
    }

    void Write(JsonObject root)
    {
        Directory.CreateDirectory(_paths.ClaudeDir);
        File.WriteAllText(_paths.SettingsFile, root.ToJsonString(AppConfig.FileJson));
    }
}
