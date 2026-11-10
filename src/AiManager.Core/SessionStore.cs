using System.Buffers;
using System.Text.Json;

namespace AiManager.Core;

/// <summary>Splits an append-only file into lines across reads, and drops the bulky request-dump records
/// without buffering them: they are most of a transcript's bytes and carry nothing the views need.</summary>
internal sealed class LineTail
{
    static ReadOnlySpan<byte> SkipPrefix => "{\"type\":\"api-request"u8;

    readonly ArrayBufferWriter<byte> _line = new(4096);
    bool _skip, _decided;
    long _pos, _lineStart;

    /// <param name="start">File offset of the first byte that will be fed.</param>
    public LineTail(long start = 0)
    {
        _pos = start;
        _lineStart = start;
    }

    /// <summary>Calls back with each complete line and the file offset it starts at, which is how an
    /// image is fetched again later without keeping its bytes in memory.</summary>
    public void Feed(ReadOnlySpan<byte> chunk, Action<ReadOnlyMemory<byte>, long> onLine)
    {
        while (!chunk.IsEmpty)
        {
            int nl = chunk.IndexOf((byte)'\n');
            var part = nl >= 0 ? chunk[..nl] : chunk;
            if (!_skip)
            {
                _line.Write(part);
                if (!_decided && _line.WrittenCount >= SkipPrefix.Length)
                {
                    _decided = true;
                    if (_line.WrittenSpan.StartsWith(SkipPrefix))
                    {
                        _skip = true;
                        _line.ResetWrittenCount();
                    }
                }
            }
            _pos += nl >= 0 ? nl + 1 : chunk.Length;
            if (nl < 0) break;
            if (!_skip && _line.WrittenCount > 0) onLine(_line.WrittenMemory, _lineStart);
            _line.ResetWrittenCount();
            _skip = false;
            _decided = false;
            _lineStart = _pos;
            chunk = chunk[(nl + 1)..];
        }
    }
}

/// <summary>Follows every recent transcript under ~/.claude/projects and keeps one SessionState per file.</summary>
public sealed partial class SessionStore : IDisposable
{
    sealed class Tracked
    {
        public required string Path;
        public required SessionState State;
        public LineTail Lines = new();
        public long Offset, Length;
        public DateTime Stamp;
        public bool Loaded;
    }

    readonly AppPaths _paths;
    readonly AppConfig _cfg;
    readonly FileLog _log;
    readonly LiveRegistry _live;
    readonly DecisionLog _decisions;
    readonly object _gate = new();
    readonly Dictionary<string, Tracked> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Tracked> _byKey = new();
    readonly Dictionary<string, DateTime> _shadowed = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _links;          // local_… host id → session id
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _worker;
    FileSystemWatcher? _watcher;
    volatile bool _stop;
    int _permSeq;

    /// <summary>False until the first full pass over the transcripts has finished.</summary>
    public bool Loaded { get; private set; }
    public long Version { get; private set; }
    public event Action? Changed;
    /// <summary>The user typed a prompt into Claude Code for this session.</summary>
    public event Action<string>? HumanTyped;

    public SessionStore(AppPaths paths, AppConfig cfg, DecisionLog decisions, FileLog log)
    {
        _paths = paths;
        _cfg = cfg;
        _log = log;
        _decisions = decisions;
        _live = new LiveRegistry(paths.LiveDir);
        _links = LoadLinks();
        _worker = new Thread(Loop) { IsBackground = true, Name = "transcripts" };
    }

    public void Start()
    {
        try
        {
            if (Directory.Exists(_paths.ProjectsDir))
            {
                _watcher = new FileSystemWatcher(_paths.ProjectsDir)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.jsonl",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Changed += (_, _) => _wake.Set();
                _watcher.Created += (_, _) => _wake.Set();
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _log.Write("watcher unavailable, polling only: " + ex.Message);
        }
        _worker.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _watcher?.Dispose();
    }

    /// <summary>Re-reads the transcripts now, e.g. after the history window setting changed.</summary>
    public void Poke() => _wake.Set();

    void Loop()
    {
        while (!_stop)
        {
            try
            {
                if (Scan()) RaiseChanged();
            }
            catch (Exception ex)
            {
                _log.Write("scan failed: " + ex);
            }
            // The watcher wakes this early; the timeout covers anything it missed.
            _wake.WaitOne(2000);
            if (!_stop) Thread.Sleep(120);
        }
    }

    bool Scan()
    {
        bool changed = _live.Refresh();
        var live = _live.Current;
        var typed = new List<string>();

        lock (_gate)
        {
            bool linksDirty = false;
            foreach (var li in live.Values)
                if (li.HostId != null && (!_links.TryGetValue(li.HostId, out var known) || known != li.SessionId))
                {
                    _links[li.HostId] = li.SessionId;
                    linksDirty = true;
                }
            if (linksDirty) SaveLinks();
        }

        var dir = new DirectoryInfo(_paths.ProjectsDir);
        if (dir.Exists)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_cfg.HistoryDays);
            var files = dir.EnumerateFiles("*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var fi in files)
            {
                if (_stop) return changed;
                Tracked? t;
                lock (_gate) _files.TryGetValue(fi.FullName, out t);
                if (t == null)
                {
                    var name = Path.GetFileNameWithoutExtension(fi.Name);
                    if (fi.LastWriteTimeUtc < cutoff && !live.ContainsKey(name)) continue;
                    t = Track(fi.FullName, fi.LastWriteTimeUtc);
                    if (t == null) continue;
                }
                if (fi.Length == t.Length && fi.LastWriteTimeUtc == t.Stamp) continue;
                var before = t.State.LastEnqueueAt;
                Tail(t, fi.LastWriteTimeUtc);
                if (t.Loaded && !t.State.IsSubagent && t.State.LastEnqueueAt != before) typed.Add(t.State.Key);
                t.Loaded = true;
                changed = true;
            }
        }

        if (!Loaded) { Loaded = true; changed = true; }
        foreach (var key in typed) HumanTyped?.Invoke(key);
        return changed;
    }

    static (string Key, string Id, string? Parent) Identify(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int idx = Array.LastIndexOf(parts, "subagents");
        if (idx > 0)
        {
            var agent = name.StartsWith("agent-", StringComparison.Ordinal) ? name[6..] : name;
            return ($"{parts[idx - 1]}~{agent}", agent, parts[idx - 1]);
        }
        return (name, name, null);
    }

    /// <summary>Starts following a file. A session id can exist in two project folders after its working
    /// directory moved; only the most recently written copy is followed.</summary>
    Tracked? Track(string path, DateTime stamp)
    {
        var (key, id, parent) = Identify(path);
        lock (_gate)
        {
            if (_files.TryGetValue(path, out var known)) return known;
            if (_byKey.TryGetValue(key, out var owner))
            {
                if (_shadowed.TryGetValue(path, out var seen) && seen == stamp) return null;
                if (stamp <= owner.Stamp)
                {
                    _shadowed[path] = stamp;
                    return null;
                }
                _files.Remove(owner.Path);
                _shadowed[owner.Path] = owner.Stamp;
            }
            var t = new Tracked { Path = path, State = new SessionState(key, id, parent, path) };
            _files[path] = t;
            _byKey[key] = t;
            _shadowed.Remove(path);
            return t;
        }
    }

    void Tail(Tracked t, DateTime stamp)
    {
        try
        {
            using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (fs.Length < t.Offset)
            {
                // The file was rewritten; replay it from the start.
                lock (_gate)
                {
                    t.State = new SessionState(t.State.Key, t.State.Id, t.State.ParentId, t.Path);
                    t.Lines = new LineTail();
                    t.Offset = 0;
                }
            }
            fs.Seek(t.Offset, SeekOrigin.Begin);
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // Locking per chunk keeps the UI responsive while a large file is replayed.
                    lock (_gate) t.Lines.Feed(buffer.AsSpan(0, n), (line, offset) => ApplyLine(t.State, line, offset));
                    t.Offset += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            t.Length = t.Offset;
            t.Stamp = stamp;
            if (t.State.IsSubagent && !t.State.AgentFinished) LoadAgentMeta(t);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void ApplyLine(SessionState state, ReadOnlyMemory<byte> line, long offset)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            state.LineOffset = offset;
            state.Apply(doc.RootElement);
        }
        catch (JsonException) { }
    }

    // ---------------------------------------------------------------- images

    public sealed record Blob(byte[] Data, string MediaType);

    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
    };

    public static bool IsImageFile(string path) => ImageTypes.ContainsKey(Path.GetExtension(path));

    /// <summary>Reads one embedded image back out of a transcript. The id names the record's file offset
    /// and where in that record the image block sits; nothing but a base64 image is ever returned.</summary>
    public Blob? ReadImage(string key, string id)
    {
        if (ImageId().Match(id) is not { Success: true } m) return null;
        string path;
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var t)) return null;
            path = t.Path;
        }
        long offset = long.Parse(m.Groups["off"].Value);
        int i = int.Parse(m.Groups["i"].Value);
        int j = m.Groups["j"].Success ? int.Parse(m.Groups["j"].Value) : -1;
        try
        {
            var line = ReadLineAt(path, offset);
            if (line == null) return null;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            JsonElement? block = m.Groups["where"].Value switch
            {
                "u" => At(root.Obj("message")?.Arr("content"), i),
                "a" => At(root.Obj("attachment")?.Arr("prompt"), i),
                "t" => At(At(root.Obj("message")?.Arr("content"), i)?.Arr("content"), j),
                _ => null,
            };
            if (block is not { } b || b.Str("type") != "image" || b.Obj("source") is not { } src || src.Str("type") != "base64") return null;
            var media = src.Str("media_type") ?? "";
            if (!ImageTypes.ContainsValue(media)) return null;
            return new Blob(Convert.FromBase64String(src.Str("data") ?? ""), media);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException) { return null; }
    }

    /// <summary>An image file a session sent to the user, by the id it was registered under.</summary>
    public Blob? ReadSentFile(string key, string id)
    {
        string? path;
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var t)) return null;
            path = t.State.SentFile(id);
        }
        if (path == null || !ImageTypes.TryGetValue(Path.GetExtension(path), out var media)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 40_000_000) return null;
            return new Blob(File.ReadAllBytes(path), media);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? SentFilePath(string key, string id)
    {
        lock (_gate) return _byKey.TryGetValue(key, out var t) ? t.State.SentFile(id) : null;
    }

    static JsonElement? At(JsonElement? array, int index) =>
        array is { } a && index >= 0 && index < a.GetArrayLength() ? a[index] : null;

    static byte[]? ReadLineAt(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (offset < 0 || offset >= fs.Length) return null;
        fs.Seek(offset, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                int nl = Array.IndexOf(buffer, (byte)'\n', 0, n);
                ms.Write(buffer, 0, nl >= 0 ? nl : n);
                if (nl >= 0 || ms.Length > 64_000_000) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return ms.ToArray();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<where>[uat])(?<off>\d{1,15})\.(?<i>\d{1,4})(?:\.(?<j>\d{1,4}))?$")]
    private static partial System.Text.RegularExpressions.Regex ImageId();

    void LoadAgentMeta(Tracked t)
    {
        var meta = Path.ChangeExtension(t.Path, ".meta.json");
        try
        {
            if (!File.Exists(meta)) return;
            using var fs = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            lock (_gate)
            {
                t.State.AgentType = r.Str("agentType") ?? t.State.AgentType;
                t.State.AgentDescription = r.Str("description") ?? t.State.AgentDescription;
                t.State.AgentToolUseId = r.Str("toolUseId") ?? t.State.AgentToolUseId;
                t.State.AgentFinished = r.Bool("finished");
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    void RaiseChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    Dictionary<string, string> LoadLinks()
    {
        try
        {
            if (File.Exists(_paths.LinkCacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_paths.LinkCacheFile)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new();
    }

    void SaveLinks()
    {
        try
        {
            File.WriteAllText(_paths.LinkCacheFile, JsonSerializer.Serialize(_links));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- used by the hook gateway

    /// <summary>Brings one session's state up to date right now instead of waiting for the next scan.</summary>
    public void CatchUp(string sessionId, string? transcriptPath)
    {
        Tracked? t;
        lock (_gate) _byKey.TryGetValue(sessionId, out t);
        if (t == null && transcriptPath != null && File.Exists(transcriptPath))
            t = Track(Path.GetFullPath(transcriptPath), File.GetLastWriteTimeUtc(transcriptPath));
        if (t == null) return;
        Tail(t, File.GetLastWriteTimeUtc(t.Path));
        t.Loaded = true;
    }

    /// <summary>The unanswered question raised in the session's latest turn, if any.</summary>
    public Decision? CurrentTurnDecision(string sessionId, bool includeOpen)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(sessionId, out var t)) return null;
            var s = t.State;
            var since = s.TurnStartedAt ?? DateTime.MinValue;
            return s.Decisions.LastOrDefault(d =>
                d.Status == "pending" && d.RaisedAt >= since && (d.Kind == "choice" || (includeOpen && d.Kind == "open")));
        }
    }

    /// <summary>Every unanswered question a Stop hook should park for: the one from the turn that just
    /// ended and any earlier ones that later turns pushed out of sight. Questions older than a day are
    /// left alone so a long-forgotten one does not stall the session.</summary>
    public List<Decision> PendingForHold(string sessionId, bool includeOpen)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(sessionId, out var t)) return new List<Decision>();
            var s = t.State;
            var since = s.TurnStartedAt ?? DateTime.MinValue;
            var recent = DateTime.UtcNow.AddHours(-24);
            return s.Decisions.Where(d =>
                d.Status == "pending"
                && (d.Kind == "choice" || (includeOpen && d.Kind == "open"))
                && (d.RaisedAt >= since || d.RaisedAt >= recent)).ToList();
        }
    }

    /// <summary>True when the transcript already holds the message the Stop hook reported as final.
    /// The hook's text can be several text blocks joined, so the transcript's last block may only be its tail.</summary>
    public bool HasFinalMessage(string sessionId, string lastAssistantMessage)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(sessionId, out var t) || t.State.TurnOpen) return false;
            var mine = t.State.LastAssistantText.Trim();
            return mine.Length > 0 && lastAssistantMessage.Trim().EndsWith(mine, StringComparison.Ordinal);
        }
    }

    public Decision? EnsureOpenDecision(string sessionId, string text, QuestionDetector.Found found)
    {
        lock (_gate)
        {
            return _byKey.TryGetValue(sessionId, out var t)
                ? t.State.AddDecision(SessionState.OpenDecision(sessionId, text, found, DateTime.UtcNow))
                : null;
        }
    }

    public List<Decision> EnsureAskDecisions(string sessionId, string toolUseId, JsonElement questions)
    {
        lock (_gate)
        {
            return _byKey.TryGetValue(sessionId, out var t)
                ? t.State.AddAskDecisions(toolUseId, questions, DateTime.UtcNow)
                : new List<Decision>();
        }
    }

    public Decision? AddPermissionDecision(string sessionId, string toolName, string preview)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(sessionId, out var t)) return null;
            var d = new Decision
            {
                Id = $"{sessionId}:perm:{Interlocked.Increment(ref _permSeq)}:{DateTime.UtcNow.Ticks:x}",
                Kind = "permission",
                Question = toolName,
                Context = preview,
                RaisedAt = DateTime.UtcNow,
            };
            d.Options.Add(new DecisionOption { Label = "allow", Key = "allow" });
            d.Options.Add(new DecisionOption { Label = "deny", Key = "deny" });
            return t.State.AddDecision(d);
        }
    }

    public void SetHeld(IEnumerable<string> ids, bool held, DateTime? until)
    {
        lock (_gate)
            foreach (var id in ids)
                if (Find(id) is { } d)
                {
                    d.Held = held;
                    d.HoldUntil = held ? until : null;
                }
        RaiseChanged();
    }

    public Decision? MarkAnswered(string id, string text, string how, string via)
    {
        Decision? d;
        string title;
        lock (_gate)
        {
            d = Find(id);
            if (d == null) return null;
            SessionState.MarkAnswered(d, text, how, via, DateTime.UtcNow);
            title = _byKey.TryGetValue(d.SessionKey, out var t) ? t.State.Title : "";
        }
        _decisions.Record(d, title);
        RaiseChanged();
        return d;
    }

    public Decision? MarkDismissed(string id)
    {
        Decision? d;
        string title;
        lock (_gate)
        {
            d = Find(id);
            if (d == null) return null;
            d.Status = "dismissed";
            d.AnsweredAt = DateTime.UtcNow;
            title = _byKey.TryGetValue(d.SessionKey, out var t) ? t.State.Title : "";
        }
        _decisions.Record(d, title);
        RaiseChanged();
        return d;
    }

    public void SetAttention(string sessionId, string type, string? message)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(sessionId, out var t)) return;
            t.State.AttentionType = type;
            t.State.AttentionMessage = Text.Clip(message, 300);
            t.State.AttentionAt = DateTime.UtcNow;
        }
        RaiseChanged();
    }

    public Decision? FindDecision(string id)
    {
        lock (_gate) return Find(id);
    }

    Decision? Find(string id)
    {
        int colon = id.IndexOf(':');
        if (colon <= 0 || !_byKey.TryGetValue(id[..colon], out var t)) return null;
        return t.State.Decisions.FirstOrDefault(d => d.Id == id);
    }
}
