using System.Text.Json.Nodes;
using AiManager.Core;

namespace AiManager.Tests;

public class IntegrationTests
{
    static (Integration Integration, AppPaths Paths, AppConfig Config) Make()
    {
        var root = Path.Combine(Path.GetTempPath(), "aimanager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "claude"));
        var paths = new AppPaths(Path.Combine(root, "claude"), Path.Combine(root, "data"));
        var cfg = new AppConfig { Port = 47821, Token = "tok0123456789abcdef", HoldMinutes = 30 };
        return (new Integration(paths, cfg), paths, cfg);
    }

    [Fact]
    public void Install_adds_only_our_hooks_and_keeps_everything_else()
    {
        var (integration, paths, _) = Make();
        File.WriteAllText(paths.SettingsFile, """
        {
          // the user's own settings
          "model": "opus",
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "echo done" } ] } ],
            "PostToolUse": [ { "matcher": "Edit", "hooks": [ { "type": "command", "command": "fmt" } ] } ]
          }
        }
        """);
        Assert.False(integration.Status().Installed);

        integration.Install();

        var root = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Equal("echo done", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var ours = stop[1]!["hooks"]![0]!;
        Assert.Equal("http", ours["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:47821/aimanager/hook/tok0123456789abcdef/stop", ours["url"]!.GetValue<string>());
        Assert.Equal(30 * 60 + 30, ours["timeout"]!.GetValue<int>());
        Assert.Equal("AskUserQuestion", root["hooks"]!["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Null(root["hooks"]!["PermissionRequest"]);       // not relayed unless asked for
        Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray());

        var status = integration.Status();
        Assert.True(status.Installed && status.UpToDate);
        Assert.Single(Directory.GetFiles(paths.ClaudeDir, "settings.json.aimanager-*.bak"));
    }

    [Fact]
    public void Installing_twice_does_not_duplicate_and_a_changed_port_is_reported_stale()
    {
        var (integration, paths, cfg) = Make();
        integration.Install();
        integration.Install();
        var root = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!;
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());

        cfg.Port = 47822;
        var status = integration.Status();
        Assert.True(status.Installed);
        Assert.False(status.UpToDate);
    }

    [Fact]
    public void Uninstall_removes_only_our_hooks()
    {
        var (integration, paths, cfg) = Make();
        File.WriteAllText(paths.SettingsFile, """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "echo done" } ] } ] } }""");
        cfg.RelayPermissions = true;
        integration.Install();
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!["hooks"]!["PermissionRequest"]);

        integration.Uninstall();

        var hooks = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!["hooks"]!.AsObject();
        Assert.Equal(new[] { "Stop" }, hooks.Select(kv => kv.Key));
        Assert.Equal("echo done", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.False(integration.Status().Installed);
    }
}
