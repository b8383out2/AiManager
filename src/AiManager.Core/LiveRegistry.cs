using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AiManager.Core;

/// <param name="Status">"busy" or "idle" as Claude Code itself reports it; null on versions that do not.</param>
public sealed record LiveInfo(int Pid, string SessionId, string? HostId, string? Name, string? Entrypoint, string? Status);

/// <summary>Which sessions have a running Claude Code process, read from the per-process files Claude Code
/// keeps in ~/.claude/sessions. Only the *.json descriptors are read; the key files beside them are never opened.</summary>
public sealed class LiveRegistry
{
    readonly string _dir;
    Dictionary<string, LiveInfo> _current = new();

    public LiveRegistry(string dir) => _dir = dir;

    public IReadOnlyDictionary<string, LiveInfo> Current => _current;

    /// <summary>Returns true when the set of live sessions changed.</summary>
    public bool Refresh()
    {
        var next = new Dictionary<string, LiveInfo>();
        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
                {
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var doc = JsonDocument.Parse(fs);
                        var r = doc.RootElement;
                        var sid = r.Str("sessionId");
                        var pid = (int)r.Long("pid");
                        if (sid == null || pid <= 0 || !IsAlive(pid, r.Str("procStart"))) continue;
                        next[sid] = new LiveInfo(pid, sid, r.Str("hostSessionId"), r.Str("name"), r.Str("entrypoint"), r.Str("status"));
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        bool changed = next.Count != _current.Count
                       || next.Any(kv => !_current.TryGetValue(kv.Key, out var old) || old.Status != kv.Value.Status);
        _current = next;
        return changed;
    }

    /// <summary>A pid alone can be reused by an unrelated process, so the recorded start time is compared too.</summary>
    static bool IsAlive(int pid, string? procStart)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (long.TryParse(procStart, out var recorded))
            {
                try
                {
                    var actual = p.StartTime.ToFileTimeUtc();
                    return Math.Abs(actual - recorded) < TimeSpan.FromSeconds(5).Ticks;
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }
}

/// <summary>Remembers what the user answered or dismissed in AiManager. The transcript cannot show this:
/// an answer sent through a hook leaves no user message behind.</summary>
public sealed class DecisionLog
{
    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "answered";
        public string SessionKey { get; set; } = "";
        public string SessionTitle { get; set; } = "";
        public string Kind { get; set; } = "choice";
        public string Question { get; set; } = "";
        public string? AnswerText { get; set; }
        public string? AnswerHow { get; set; }
        public string? AnswerVia { get; set; }
        public DateTime RaisedAt { get; set; }
        public DateTime At { get; set; }
    }

    const int Keep = 300;
    readonly string _file;
    readonly object _gate = new();
    readonly Dictionary<string, Entry> _entries = new();

    public DecisionLog(string file)
    {
        _file = file;
        try
        {
            if (!File.Exists(file)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), AppConfig.FileJson);
            if (list != null)
                foreach (var e in list) _entries[e.Id] = e;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    public Entry? Get(string id)
    {
        lock (_gate) return _entries.GetValueOrDefault(id);
    }

    public List<Entry> All()
    {
        lock (_gate) return _entries.Values.ToList();
    }

    public void Record(Decision d, string sessionTitle)
    {
        lock (_gate)
        {
            _entries[d.Id] = new Entry
            {
                Id = d.Id,
                Status = d.Status,
                SessionKey = d.SessionKey,
                SessionTitle = sessionTitle,
                Kind = d.Kind,
                Question = d.Question,
                AnswerText = d.AnswerText,
                AnswerHow = d.AnswerHow,
                AnswerVia = d.AnswerVia,
                RaisedAt = d.RaisedAt,
                At = d.AnsweredAt ?? DateTime.UtcNow,
            };
            if (_entries.Count > Keep)
                foreach (var old in _entries.Values.OrderBy(e => e.At).Take(_entries.Count - Keep).ToList())
                    _entries.Remove(old.Id);
            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.At).ToList(), AppConfig.FileJson));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
