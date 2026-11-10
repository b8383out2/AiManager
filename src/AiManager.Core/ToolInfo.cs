using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiManager.Core;

/// <summary>Turns a tool call into the short "verb + target" line shown in the live views.</summary>
public static partial class ToolInfo
{
    public static string Category(string name) => name switch
    {
        "Read" or "NotebookRead" => "read",
        "Edit" or "MultiEdit" or "NotebookEdit" => "edit",
        "Write" => "write",
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => "run",
        "Grep" or "Glob" or "LS" or "ToolSearch" => "search",
        "WebFetch" or "WebSearch" => "web",
        "Agent" or "Task" or "Workflow" => "agent",
        "TodoWrite" or "TaskCreate" or "TaskUpdate" or "TaskList" or "TaskGet" => "plan",
        "AskUserQuestion" or "ExitPlanMode" => "ask",
        "SendMessage" or "SubagentHandback" => "message",
        "Skill" => "skill",
        _ when name.EndsWith("__update_plan", StringComparison.Ordinal) => "plan",
        _ when name.EndsWith("__wrap_up", StringComparison.Ordinal) => "ask",
        _ when name.StartsWith("mcp__ccd_session", StringComparison.Ordinal) => "agent",
        _ when name.StartsWith("mcp__", StringComparison.Ordinal) => "mcp",
        _ => "other",
    };

    /// <summary>"mcp__sap__read_table" → "sap · read_table"; connector ids that are just UUIDs are dropped.</summary>
    public static string ShortName(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
        var parts = name.Split("__", 3);
        if (parts.Length < 3) return name;
        return UuidLike().IsMatch(parts[1]) || parts[1].StartsWith("ccd_", StringComparison.Ordinal)
            ? parts[2]
            : parts[1] + " · " + parts[2];
    }

    public static string Target(string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        string? s = name switch
        {
            "Read" or "Edit" or "MultiEdit" or "Write" => ShortPath(input.Str("file_path")),
            "NotebookEdit" or "NotebookRead" => ShortPath(input.Str("notebook_path")),
            "Bash" or "PowerShell" => Text.FirstLine(input.Str("command")),
            "Grep" or "Glob" => input.Str("pattern"),
            "ToolSearch" or "WebSearch" => input.Str("query"),
            "WebFetch" => input.Str("url"),
            "Agent" or "Task" => input.Str("description"),
            "Workflow" => input.Str("name") ?? input.Str("scriptPath"),
            "Skill" => input.Str("skill"),
            "SendMessage" => input.Str("summary") ?? input.Str("to"),
            "AskUserQuestion" => FirstQuestion(input),
            _ when name.EndsWith("__wrap_up", StringComparison.Ordinal) => input.Str("ask") ?? input.Str("recap"),
            _ when name.EndsWith("__update_plan", StringComparison.Ordinal) => "",
            _ when name.EndsWith("__start_session", StringComparison.Ordinal) => input.Str("title"),
            _ => FirstString(input),
        };
        return Text.Clip(s?.Trim(), 160);
    }

    static string? FirstQuestion(JsonElement input)
    {
        if (input.Arr("questions") is { } qs)
            foreach (var q in qs.EnumerateArray())
                return q.Str("question");
        return null;
    }

    static string? FirstString(JsonElement input)
    {
        foreach (var p in input.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                return v;
        return null;
    }

    /// <summary>Keeps the last two path segments, which is what tells files apart at a glance.</summary>
    public static string? ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? string.Join('/', parts) : parts[^2] + "/" + parts[^1];
    }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}$")]
    private static partial Regex UuidLike();
}

/// <summary>Recognises a turn that ends by asking the user something in plain text.</summary>
public static partial class QuestionDetector
{
    public sealed record Found(string Question, List<string> Options);

    /// <summary>Returns the closing question when the text ends with one, optionally followed only by a
    /// short list of choices. A question in the middle of a longer message is not a pending decision.</summary>
    public static Found? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;

        var options = new List<string>();
        int i = lines.Count - 1;
        while (i >= 0 && options.Count < 6 && ListItem().Match(lines[i]) is { Success: true } m)
        {
            options.Insert(0, Text.Clip(Strip(m.Groups[1].Value), 80));
            i--;
        }
        if (i < 0) return null;
        var line = Strip(lines[i]);
        if (!line.EndsWith('?') && !line.EndsWith('？')) return null;
        if (options.Count == 1) options.Clear();    // a single bullet is not a choice
        return new Found(Text.Clip(line, 300), options);
    }

    static string Strip(string s) => s.Trim().Trim('*', '_', '`', '#', '>', ' ', '"', '”', '“', '」', '「').Trim();

    [GeneratedRegex(@"^(?:[-*•·]|\d+[.)、])\s+(.+)$")]
    private static partial Regex ListItem();
}
