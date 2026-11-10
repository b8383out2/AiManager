using System.Globalization;
using System.Text.Json;

namespace AiManager.Core;

/// <summary>Null-tolerant readers for transcript JSON. The format is internal to Claude Code and
/// changes between versions, so every access has to survive a missing or re-typed field.</summary>
internal static class Json
{
    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static JsonElement? Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static double? Dbl(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static long Long(this JsonElement e, string name) => (long)(e.Dbl(name) ?? 0);

    public static DateTime? Time(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

internal static class Text
{
    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    public static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return "";
    }

    /// <summary>Stable across runs and machines, unlike string.GetHashCode. Used for decision ids.</summary>
    public static string StableHash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h.ToString("x16");
    }
}

/// <summary>Append-only diagnostics file; failures to log never surface to the user.</summary>
public sealed class FileLog
{
    readonly string _file;
    readonly object _gate = new();

    public FileLog(string file) => _file = file;

    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
