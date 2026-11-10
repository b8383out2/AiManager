using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiManager.Core;

/// <summary>Everything AiManager knows about one transcript file, built by replaying its records in order.
/// Not thread safe: the store applies records and reads state under one lock.</summary>
public sealed partial class SessionState
{
    const int MaxActivity = 300, MaxConversation = 160, MaxReturns = 120, MaxSpans = 400, MaxDecisions = 200, MaxSentFiles = 200;
    static readonly TimeSpan SpanGap = TimeSpan.FromSeconds(90);

    /// <summary>Unique across the store: the session id, or "parent~agent" for a subagent.</summary>
    public string Key { get; }
    public string Id { get; }
    public string? ParentId { get; }
    public string FilePath { get; }
    public bool IsSubagent => ParentId != null;

    public string? CustomTitle, AgentName, AiTitle, FirstPrompt;
    public string? AgentType, AgentDescription, AgentToolUseId;
    public bool AgentFinished;
    public string? Cwd, Entrypoint, Version, Model, GitBranch;
    public DateTime? StartedAt, LastAt, LastHumanAt, TurnStartedAt, TurnEndedAt, LastEnqueueAt;
    public bool TurnOpen;
    public string? LastStopReason;
    public string LastAssistantText = "";
    public string? ApiError;
    public int ToolCalls;
    public long OutputTokens, ContextTokens;
    public double? CostUsd;
    public int LinesAdded, LinesRemoved;
    public string? AttentionType, AttentionMessage;
    public DateTime? AttentionAt;

    public List<PlanItem> Plan = new();
    public readonly List<ActivityItem> Activity = new();
    public readonly List<ConvItem> Conversation = new();
    public readonly List<ReturnItem> Returns = new();
    public readonly List<ChildRef> Children = new();
    public readonly List<Decision> Decisions = new();
    public readonly Dictionary<string, int> Files = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Stretches of continuous work as [start, end] pairs; a gap over 90 seconds starts a new one.</summary>
    public readonly List<DateTime[]> Spans = new();

    /// <summary>File offset of the record being applied; image ids point back to it.</summary>
    public long LineOffset;

    readonly Dictionary<string, PendingTool> _pending = new();
    readonly Dictionary<string, long> _outByMessage = new();
    readonly Dictionary<string, string> _sentFiles = new();
    /// <summary>Images of prompts typed while a turn was running; they arrive before the prompt's text does.</summary>
    readonly List<(string Text, List<string> Images)> _queuedImages = new();
    ConvItem? _lastReply;
    bool _turnHasAsk;
    int _returnSeq, _fileSeq;

    sealed record PendingTool(string Name, ActivityItem Item, DateTime At);

    public SessionState(string key, string id, string? parentId, string filePath)
    {
        Key = key;
        Id = id;
        ParentId = parentId;
        FilePath = filePath;
    }

    public string Title =>
        Text.NonEmpty(CustomTitle) ?? Text.NonEmpty(AgentName) ?? Text.NonEmpty(AiTitle)
        ?? (IsSubagent ? Text.NonEmpty(AgentDescription) : null)
        ?? Text.NonEmpty(Text.Clip(Text.FirstLine(FirstPrompt), 60))
        ?? (IsSubagent ? Text.NonEmpty(AgentType) : null)
        ?? "";

    /// <summary>The tool call that is running right now, if any.</summary>
    public ActivityItem? CurrentTool => _pending.Count == 0 ? null : _pending.Values.OrderBy(p => p.At).Last().Item;

    public void Apply(JsonElement rec)
    {
        if (rec.ValueKind != JsonValueKind.Object) return;
        switch (rec.Str("type"))
        {
            case "user": Common(rec); ApplyUser(rec, Stamp(rec)); break;
            case "assistant": Common(rec); ApplyAssistant(rec, Stamp(rec)); break;
            case "system": Common(rec); ApplySystem(rec, Stamp(rec)); break;
            case "queue-operation":
                var op = rec.Str("operation");
                if (op == "enqueue") LastEnqueueAt = Stamp(rec) ?? LastEnqueueAt;
                else if (op == "remove" && rec.Str("reason") == "absorbed_mid_turn" && rec.Str("content") is { Length: > 0 } absorbed)
                    OnAbsorbed(absorbed, Stamp(rec) ?? LastAt ?? DateTime.UtcNow);
                break;
            case "custom-title": CustomTitle = Text.NonEmpty(rec.Str("customTitle")) ?? CustomTitle; break;
            case "agent-name": AgentName = Text.NonEmpty(rec.Str("agentName")) ?? AgentName; break;
            case "ai-title": AiTitle = Text.NonEmpty(rec.Str("aiTitle")) ?? AiTitle; break;
            case "relocated": Cwd = Text.NonEmpty(rec.Str("relocatedCwd")) ?? Cwd; break;
            case "cost-state":
                CostUsd = rec.Dbl("totalCostUSD") ?? CostUsd;
                LinesAdded = (int)rec.Long("totalLinesAdded");
                LinesRemoved = (int)rec.Long("totalLinesRemoved");
                break;
            case "started": OnWorkflowStarted(rec); break;
            case "result": OnWorkflowResult(rec); break;
            case "attachment": OnAttachment(rec); break;
        }
    }

    public string? SentFile(string id) => _sentFiles.GetValueOrDefault(id);

    void OnAttachment(JsonElement rec)
    {
        if (rec.Obj("attachment") is not { } a || a.Str("type") != "queued_command" || a.Arr("prompt") is not { } prompt) return;
        var images = new List<string>();
        var text = new StringBuilder();
        int i = 0;
        foreach (var block in prompt.EnumerateArray())
        {
            if (IsImage(block)) images.Add($"a{LineOffset}.{i}");
            else if (block.Str("type") == "text") text.AppendLine(block.Str("text"));
            i++;
        }
        if (images.Count == 0) return;
        _queuedImages.Add((CleanHumanText(text.ToString()), images));
        if (_queuedImages.Count > 10) _queuedImages.RemoveAt(0);
    }

    static bool IsImage(JsonElement block) =>
        block.Str("type") == "image" && block.Obj("source")?.Str("type") == "base64";

    void Common(JsonElement rec)
    {
        Cwd = Text.NonEmpty(rec.Str("cwd")) ?? Cwd;
        Entrypoint = rec.Str("entrypoint") ?? Entrypoint;
        Version = rec.Str("version") ?? Version;
        GitBranch = rec.Str("gitBranch") ?? GitBranch;
    }

    DateTime? Stamp(JsonElement rec)
    {
        var ts = rec.Time("timestamp");
        if (ts is not { } t) return null;
        StartedAt ??= t;
        if (LastAt == null || t > LastAt) LastAt = t;
        if (Spans.Count > 0 && t - Spans[^1][1] <= SpanGap)
        {
            if (t > Spans[^1][1]) Spans[^1][1] = t;
        }
        else
        {
            Spans.Add(new[] { t, t });
            if (Spans.Count > MaxSpans) Spans.RemoveAt(0);
        }
        return t;
    }

    // ---------------------------------------------------------------- user records

    void ApplyUser(JsonElement rec, DateTime? ts)
    {
        if (rec.Obj("message") is not { } msg) return;
        var at = ts ?? LastAt ?? DateTime.UtcNow;

        string text;
        List<string>? images = null;
        if (msg.Arr("content") is { } content)
        {
            var sb = new StringBuilder();
            int i = 0;
            foreach (var item in content.EnumerateArray())
            {
                switch (item.Str("type"))
                {
                    case "tool_result": OnToolResult(item, rec, at, i); break;
                    case "text": sb.AppendLine(item.Str("text")); break;
                    case "image" when IsImage(item): (images ??= new()).Add($"u{LineOffset}.{i}"); break;
                }
                i++;
            }
            text = sb.ToString();
        }
        else text = msg.Str("content") ?? "";

        if (string.IsNullOrWhiteSpace(text) && images == null) return;
        if (rec.Bool("isCompactSummary") || rec.Bool("isMeta")) return;

        var origin = rec.Obj("origin");
        var kind = origin?.Str("kind");
        if (kind == "peer") { OnPeerMessage(origin!.Value, text, at); BeginTurn(at); return; }
        if (text.Contains("<task-notification>", StringComparison.Ordinal)) { OnTaskNotification(text, at); BeginTurn(at); return; }
        if (text.Contains("you started (local_", StringComparison.Ordinal)) { OnSessionNotes(text, at); BeginTurn(at); return; }
        if (text.Contains("[Request interrupted by user", StringComparison.Ordinal)) { EndTurn(at, interrupted: true); return; }

        var clean = CleanHumanText(text);
        if (IsSubagent)
        {
            // A subagent's first prompt is the brief its parent wrote, not something a person typed.
            FirstPrompt ??= clean.Length > 0 ? clean : text.Trim();
            BeginTurn(at);
            return;
        }
        // The desktop app tags typed prompts; in the terminal an untagged, non-injected prompt is the user's.
        bool human = kind == "human" || (kind == null && rec.Str("promptSource") == null);
        if (!human || (clean.Length == 0 && images == null) || LooksInjected(clean)) { BeginTurn(at); return; }
        OnHumanMessage(clean, at, images);
    }

    void OnHumanMessage(string text, DateTime at, List<string>? images = null)
    {
        if (text.Length > 0) FirstPrompt ??= text;
        LastHumanAt = at;
        var answer = text.Length > 0 ? Text.Clip(text, 300) : "(image)";
        // Typing anything answers whatever the session was waiting on, exactly as it does in Claude Code.
        foreach (var d in Decisions)
        {
            if (d.Status != "pending" || d.RaisedAt > at) continue;
            if (d.Kind == "ask") { d.Status = "dismissed"; d.AnsweredAt = at; }
            else MarkAnswered(d, answer, "native", "claude", at);
        }
        BeginTurn(at);
        AddConv("user", Text.Clip(text, 4000), at).Images = images;
        AddActivity(new ActivityItem { At = at, Kind = "user", Text = Text.Clip(text.Length > 0 ? text : answer, 500) });
    }

    void BeginTurn(DateTime at)
    {
        TurnOpen = true;
        TurnStartedAt = at;
        _turnHasAsk = false;
        _lastReply = null;
        ApiError = null;
        AttentionType = null;
        AttentionMessage = null;
    }

    void EndTurn(DateTime at, bool interrupted = false)
    {
        TurnOpen = false;
        TurnEndedAt = at;
        foreach (var p in _pending.Values) p.Item.Pending = false;
        _pending.Clear();
        // What the session said last before it stopped is its answer; the rest was progress along the way.
        if (!interrupted && _lastReply != null) _lastReply.Final = true;
        _lastReply = null;
        if (interrupted || IsSubagent || _turnHasAsk) return;
        if (QuestionDetector.Detect(LastAssistantText) is not { } found) return;
        _turnHasAsk = true;
        AddDecision(OpenDecision(Key, LastAssistantText, found, at));
    }

    /// <summary>The same id whether the question was read from the transcript or from a Stop hook's
    /// last_assistant_message, so the two sources never produce a duplicate.</summary>
    public static Decision OpenDecision(string sessionKey, string fullText, QuestionDetector.Found found, DateTime at)
    {
        var d = new Decision
        {
            Id = $"{sessionKey}:q:{Text.StableHash(fullText.Trim())}",
            Kind = "open",
            Question = found.Question,
            Context = Text.Clip(fullText.Trim(), 1500),
            RaisedAt = at,
        };
        foreach (var o in found.Options) d.Options.Add(new DecisionOption { Label = o });
        return d;
    }

    static string CleanHumanText(string text)
    {
        text = SystemReminder().Replace(text, "");
        text = ViewContext().Replace(text, "");
        return text.Trim();
    }

    static readonly string[] InjectedPrefixes =
    {
        "<task-notification", "<local-command", "<command-name", "<command-message", "<command-args",
        "<bash-input", "<bash-stdout", "<bash-stderr", "<side-sessions-event", "<ci-monitor-event",
        "<user-prompt-submit-hook", "<agent-message", "<teammate-message", "<system-reminder",
        "Caveat: The messages below", "This session is being continued from a previous conversation",
        "Another Claude session sent a message", "Stop hook feedback", "[SYSTEM NOTIFICATION",
    };

    static bool LooksInjected(string clean)
    {
        foreach (var p in InjectedPrefixes)
            if (clean.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Something that arrived while a turn was already running is not written as a user record;
    /// it only shows up as the queue entry that was folded into the turn. That covers prompts the user
    /// typed mid-turn as well as reports from subagents and started sessions.</summary>
    void OnAbsorbed(string text, DateTime at)
    {
        if (AgentMessage().Match(text) is { Success: true } m)
        {
            var body = m.Groups["body"].Value;
            int marker = body.IndexOf("The report follows:", StringComparison.Ordinal);
            if (marker >= 0) body = body[(marker + "The report follows:".Length)..];
            // The harness indents every line of a hand-back by two spaces.
            // and may put its own "[harness: …]" note ahead of the report.
            body = string.Join('\n', body.Split('\n')
                .Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l[2..] : l)
                .SkipWhile(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("[harness:", StringComparison.Ordinal))).Trim();
            AddAgentReport(m.Groups["from"].Value, null, body, at);
            return;
        }
        if (text.Contains("<task-notification>", StringComparison.Ordinal)) { OnTaskNotification(text, at); return; }
        if (text.Contains("you started (local_", StringComparison.Ordinal)) { OnSessionNotes(text, at); return; }

        var clean = CleanHumanText(text);
        if (IsSubagent || LooksInjected(clean)) return;
        // Its images were written earlier, with the queued prompt; match them up by the prompt's text.
        var queued = _queuedImages.FindIndex(q => q.Text == clean);
        if (queued < 0 && clean.Length > 0)
            queued = _queuedImages.FindIndex(q => q.Text.Length > 0 && (clean.Contains(q.Text, StringComparison.Ordinal) || q.Text.Contains(clean, StringComparison.Ordinal)));
        List<string>? images = null;
        if (queued >= 0)
        {
            images = _queuedImages[queued].Images;
            _queuedImages.RemoveAt(queued);
        }
        if (clean.Length == 0 && images == null) return;
        var (started, hadAsk, reply) = (TurnStartedAt, _turnHasAsk, _lastReply);
        OnHumanMessage(clean, at, images);
        // The turn it interrupted carries on; this was not the start of a new one.
        TurnStartedAt = started ?? at;
        _turnHasAsk = hadAsk;
        _lastReply = reply;
    }

    void OnPeerMessage(JsonElement origin, string text, DateTime at)
    {
        var report = Text.NonEmpty(origin.Str("handbackReportText")) ?? Text.NonEmpty(origin.Str("body")) ?? text.Trim();
        AddAgentReport(origin.Str("from") ?? "", origin.Str("name"), report, at);
    }

    void AddAgentReport(string from, string? agentName, string report, DateTime at)
    {
        var child = Children.LastOrDefault(c => c.Key == from);
        AddReturn(new ReturnItem
        {
            At = at,
            FromKey = from,
            FromLabel = child?.Label ?? agentName ?? "subagent",
            Kind = "subagent",
            Status = "done",
            Text = Text.Clip(report, 6000),
            FullLength = report.Length,
        });
    }

    void OnTaskNotification(string text, DateTime at)
    {
        string? Tag(string name) => Regex.Match(text, $"<{name}>(.*?)</{name}>", RegexOptions.Singleline) is { Success: true } m
            ? m.Groups[1].Value.Trim() : null;

        var taskId = Tag("task-id") ?? "";
        var toolUseId = Tag("tool-use-id");
        var status = Tag("status") ?? "completed";
        var summary = Tag("summary") ?? "";
        var result = Tag("result") ?? "";
        var child = Children.LastOrDefault(c => c.Key == taskId)
                    ?? Children.LastOrDefault(c => toolUseId != null && c.ToolUseId == toolUseId);
        var fromKey = child is { Key.Length: > 0 } ? child.Key : taskId;

        // A hand-back already delivered the real report; this notice only says so.
        if (result.StartsWith("This agent's report was delivered", StringComparison.Ordinal)
            && Returns.Any(r => r.FromKey == fromKey)) return;

        var body = result.Length > 0 ? result : summary;
        AddReturn(new ReturnItem
        {
            At = at,
            FromKey = fromKey,
            FromLabel = child?.Label ?? Text.Clip(summary, 80),
            Kind = "task",
            Status = status == "completed" ? "done" : "failed",
            Text = Text.Clip(body, 6000),
            FullLength = body.Length,
        });
    }

    void OnSessionNotes(string text, DateTime at)
    {
        foreach (Match m in SessionNote().Matches(text))
        {
            var title = m.Groups["title"].Value;
            var hostId = m.Groups["id"].Value;
            var body = m.Groups["text"].Value.Replace(" ⏎ ", "\n").Replace("⏎", "\n");
            var child = Children.LastOrDefault(c => c.Kind == "session" && (c.HostId == hostId || c.Label == title));
            if (child == null)
            {
                child = new ChildRef { Kind = "session", Label = title, At = at };
                Children.Add(child);
            }
            child.HostId = hostId;
            if (child.Key.Length == 0) child.Key = hostId;
            AddReturn(new ReturnItem
            {
                At = at,
                FromKey = hostId,
                FromLabel = title,
                Kind = "session",
                Status = "done",
                Text = Text.Clip(body, 6000),
                FullLength = int.TryParse(m.Groups["len"].Value, out var n) ? n : body.Length,
            });
        }
    }

    void OnToolResult(JsonElement item, JsonElement rec, DateTime at, int index)
    {
        var id = item.Str("tool_use_id");
        if (id == null || !_pending.Remove(id, out var p)) return;
        p.Item.Pending = false;
        p.Item.Error = item.Bool("is_error");
        if (item.Arr("content") is { } blocks)
        {
            int j = 0;
            foreach (var b in blocks.EnumerateArray())
            {
                if (IsImage(b)) (p.Item.Images ??= new()).Add($"t{LineOffset}.{index}.{j}");
                j++;
            }
        }
        var seconds = (at - p.At).TotalSeconds;
        if (seconds >= 1) p.Item.Seconds = Math.Round(seconds, 1);

        var tur = rec.TryGetProperty("toolUseResult", out var t) ? t : default;
        var resultText = ResultText(item);

        switch (p.Name)
        {
            case "Agent" or "Task":
            {
                var child = Children.LastOrDefault(c => c.ToolUseId == id);
                var agentId = tur.Str("agentId");
                if (child != null && agentId != null) child.Key = agentId;
                bool launchedOnly = tur.Bool("isAsync") || tur.Str("status") == "async_launched";
                if (!launchedOnly && resultText.Length > 0)
                    AddReturn(new ReturnItem
                    {
                        At = at,
                        FromKey = agentId ?? id,
                        FromLabel = child?.Label ?? "subagent",
                        Kind = "subagent",
                        Status = p.Item.Error ? "failed" : "done",
                        Text = Text.Clip(resultText, 6000),
                        FullLength = resultText.Length,
                    });
                break;
            }
            case "AskUserQuestion":
            {
                var answers = tur.Obj("answers");
                foreach (var d in Decisions.Where(d => d.GroupId == $"{Key}:{id}" && d.Status == "pending"))
                {
                    var answer = answers?.Str(d.Question);
                    if (answer != null) MarkAnswered(d, answer, "native", "claude", at);
                    else { d.Status = "dismissed"; d.AnsweredAt = at; }
                }
                break;
            }
            default:
                if (p.Name.EndsWith("__start_session", StringComparison.Ordinal)
                    && LocalId().Match(resultText) is { Success: true } m
                    && Children.LastOrDefault(c => c.ToolUseId == id) is { } started)
                {
                    started.HostId = m.Value;
                    if (started.Key.Length == 0) started.Key = m.Value;
                }
                break;
        }
    }

    static string ResultText(JsonElement item)
    {
        if (item.Str("content") is { } s) return s;
        if (item.Arr("content") is not { } arr) return "";
        var sb = new StringBuilder();
        foreach (var c in arr.EnumerateArray())
            if (c.Str("type") == "text") sb.AppendLine(c.Str("text"));
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- assistant records

    void ApplyAssistant(JsonElement rec, DateTime? ts)
    {
        if (rec.Obj("message") is not { } msg) return;
        var at = ts ?? LastAt ?? DateTime.UtcNow;

        var model = msg.Str("model");
        if (!string.IsNullOrEmpty(model) && model != "<synthetic>") Model = model;

        if (msg.Obj("usage") is { } usage)
        {
            // One model response is written as several records that repeat the same usage block.
            var mid = msg.Str("id") ?? rec.Str("uuid") ?? "";
            var output = usage.Long("output_tokens");
            _outByMessage.TryGetValue(mid, out var counted);
            OutputTokens += output - counted;
            _outByMessage[mid] = output;
            ContextTokens = usage.Long("input_tokens") + usage.Long("cache_read_input_tokens") + usage.Long("cache_creation_input_tokens");
        }

        bool apiError = rec.Bool("isApiErrorMessage");
        if (msg.Arr("content") is { } content)
        {
            foreach (var c in content.EnumerateArray())
            {
                switch (c.Str("type"))
                {
                    case "text":
                        var text = c.Str("text") ?? "";
                        if (text.Trim().Length == 0) break;
                        if (apiError)
                        {
                            ApiError = Text.Clip(text.Trim(), 300);
                            AddActivity(new ActivityItem { At = at, Kind = "error", Text = ApiError });
                            break;
                        }
                        ApiError = null;
                        LastAssistantText = text;
                        _lastReply = AddConv("assistant", Text.Clip(text.Trim(), 6000), at);
                        AddActivity(new ActivityItem { At = at, Kind = "say", Text = Text.Clip(text.Trim(), 500) });
                        break;
                    case "thinking":
                        // Often empty: only a summary is kept, and only on some turns.
                        if (Text.NonEmpty(c.Str("thinking")) is { } thought) AddConv("thinking", Text.Clip(thought, 3000), at);
                        break;
                    case "tool_use":
                        OnToolUse(c, at);
                        break;
                }
            }
        }

        var stop = msg.Str("stop_reason");
        if (stop == null) return;
        LastStopReason = stop;
        if (stop == "tool_use") TurnOpen = true;
        else if (!apiError) EndTurn(at);
        else TurnOpen = false;
    }

    void OnToolUse(JsonElement c, DateTime at)
    {
        var name = c.Str("name") ?? "";
        var id = c.Str("id") ?? Guid.NewGuid().ToString("N");
        var input = c.Obj("input") ?? default;
        ToolCalls++;
        ApiError = null;

        var item = new ActivityItem
        {
            At = at,
            Kind = "tool",
            Cat = ToolInfo.Category(name),
            Tool = ToolInfo.ShortName(name),
            Text = ToolInfo.Target(name, input),
            Pending = true,
        };
        AddActivity(item);
        _pending[id] = new PendingTool(name, item, at);

        if (name is "Edit" or "MultiEdit" or "Write" or "NotebookEdit")
        {
            var path = input.Str("file_path") ?? input.Str("notebook_path");
            if (!string.IsNullOrEmpty(path)) Files[path] = Files.GetValueOrDefault(path) + 1;
        }
        else if (name is "Agent" or "Task")
        {
            Children.Add(new ChildRef
            {
                ToolUseId = id,
                Label = Text.NonEmpty(input.Str("description")) ?? "subagent",
                Kind = "subagent",
                AgentType = input.Str("subagent_type"),
                At = at,
            });
        }
        else if (name == "AskUserQuestion")
        {
            if (input.Arr("questions") is { } questions) AddAskDecisions(id, questions, at);
        }
        else if (name == "SendUserFile")
        {
            OnSendFiles(input, at);
        }
        else if (name == "TodoWrite")
        {
            if (input.Arr("todos") is { } todos) Plan = ReadPlan(todos);
        }
        else if (name == "TaskCreate")
        {
            Plan.Add(new PlanItem { Text = input.Str("subject") ?? input.Str("description") ?? "" });
        }
        else if (name == "TaskUpdate")
        {
            if (int.TryParse(input.Str("taskId"), out var n) && n >= 1 && n <= Plan.Count && input.Str("status") is { } st)
                Plan[n - 1].Status = st == "deleted" ? "completed" : st;
        }
        else if (name.EndsWith("__update_plan", StringComparison.Ordinal))
        {
            ApplyPlanUpdate(input);
        }
        else if (name.EndsWith("__wrap_up", StringComparison.Ordinal))
        {
            foreach (var p in Plan) p.Status = "completed";
            if (Text.NonEmpty(input.Str("ask")) is { } ask)
            {
                var d = new Decision
                {
                    Id = $"{Key}:{id}",
                    Kind = "choice",
                    Question = ask,
                    Context = Text.Clip(input.Str("recap"), 1500),
                    RaisedAt = at,
                };
                if (input.Arr("actions") is { } actions)
                    foreach (var a in actions.EnumerateArray())
                    {
                        if (Text.NonEmpty(a.Str("label")) is { } label) d.Options.Add(new DecisionOption { Label = label });
                        else if (Text.NonEmpty(a.Str("action")) is { } action) d.Options.Add(new DecisionOption { Label = action, Key = "app:" + action });
                    }
                AddDecision(d);
                _turnHasAsk = true;
            }
        }
        else if (name.EndsWith("__start_session", StringComparison.Ordinal))
        {
            if (Text.NonEmpty(input.Str("title")) is { } title && !Children.Any(ch => ch.Kind == "session" && ch.Label == title))
                Children.Add(new ChildRef { ToolUseId = id, Label = title, Kind = "session", At = at });
        }
    }

    /// <summary>Files the session handed to the user. Only paths registered here can be served back, and
    /// only images are shown inline.</summary>
    void OnSendFiles(JsonElement input, DateTime at)
    {
        if (input.Arr("files") is not { } files) return;
        var refs = new List<FileRef>();
        foreach (var f in files.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.String || Text.NonEmpty(f.GetString()) is not { } p) continue;
            var full = Path.IsPathRooted(p) ? p : Path.Combine(Cwd ?? "", p);
            var id = $"f{++_fileSeq}";
            _sentFiles[id] = full;
            if (_sentFiles.Count > MaxSentFiles) _sentFiles.Remove(_sentFiles.Keys.First());
            refs.Add(new FileRef { Id = id, Name = Path.GetFileName(p), Image = SessionStore.IsImageFile(p) });
        }
        if (refs.Count == 0) return;
        var item = AddConv("files", Text.Clip(input.Str("caption"), 500), at);
        item.Files = refs;
    }

    /// <summary>Also called by the hook gateway, which can see an AskUserQuestion before it reaches the transcript.</summary>
    public List<Decision> AddAskDecisions(string toolUseId, JsonElement questions, DateTime at)
    {
        var list = new List<Decision>();
        int i = 0;
        foreach (var q in questions.EnumerateArray())
        {
            var d = new Decision
            {
                Id = $"{Key}:{toolUseId}:{i++}",
                GroupId = $"{Key}:{toolUseId}",
                Kind = "ask",
                Question = q.Str("question") ?? "",
                Header = q.Str("header"),
                MultiSelect = q.Bool("multiSelect"),
                Context = Text.Clip(LastAssistantText.Trim(), 1500),
                RaisedAt = at,
            };
            if (q.Arr("options") is { } options)
                foreach (var o in options.EnumerateArray())
                    d.Options.Add(new DecisionOption { Label = o.Str("label") ?? "", Description = Text.NonEmpty(o.Str("description")) });
            list.Add(AddDecision(d));
        }
        _turnHasAsk = true;
        return list;
    }

    static List<PlanItem> ReadPlan(JsonElement items)
    {
        var plan = new List<PlanItem>();
        foreach (var i in items.EnumerateArray())
            plan.Add(new PlanItem { Text = i.Str("content") ?? i.Str("subject") ?? "", Status = i.Str("status") ?? "pending" });
        return plan;
    }

    void ApplyPlanUpdate(JsonElement input)
    {
        if (input.Arr("items") is { } items) { Plan = ReadPlan(items); return; }
        if (input.Bool("advance"))
        {
            var current = Plan.FindIndex(p => p.Status == "in_progress");
            if (current >= 0) Plan[current].Status = "completed";
            var next = Plan.FindIndex(p => p.Status == "pending");
            if (next >= 0) Plan[next].Status = "in_progress";
        }
        if (input.Arr("done") is { } done)
            foreach (var d in done.EnumerateArray())
                if (d.ValueKind == JsonValueKind.Number && (int)d.GetDouble() is var n && n >= 1 && n <= Plan.Count)
                    Plan[n - 1].Status = "completed";
        if (input.Dbl("start") is { } start && (int)start is var s && s >= 1 && s <= Plan.Count)
            Plan[s - 1].Status = "in_progress";
    }

    // ---------------------------------------------------------------- other records

    void ApplySystem(JsonElement rec, DateTime? ts)
    {
        var at = ts ?? LastAt ?? DateTime.UtcNow;
        switch (rec.Str("subtype"))
        {
            case "api_error":
                var err = rec.Obj("error");
                ApiError = Text.Clip(err?.Str("formatted") ?? err?.Str("message") ?? "API error", 300);
                break;
            case "stop_hook_summary":
                // Written once the Stop hooks have run, i.e. the turn really is over.
                if (TurnOpen && LastStopReason != "tool_use") EndTurn(at);
                break;
        }
    }

    void OnWorkflowStarted(JsonElement rec)
    {
        var agentId = rec.Str("agentId");
        if (agentId == null || Children.Any(c => c.Key == agentId)) return;
        Children.Add(new ChildRef
        {
            Key = agentId,
            Label = Text.NonEmpty(rec.Str("label")) ?? "agent",
            Kind = "workflow",
            AgentType = rec.Str("phase"),
            At = LastAt ?? DateTime.UtcNow,
        });
    }

    void OnWorkflowResult(JsonElement rec)
    {
        var agentId = rec.Str("agentId");
        if (agentId == null) return;
        var body = rec.Str("result") ?? "";
        AddReturn(new ReturnItem
        {
            At = LastAt ?? DateTime.UtcNow,
            FromKey = agentId,
            FromLabel = Children.LastOrDefault(c => c.Key == agentId)?.Label ?? "agent",
            Kind = "workflow",
            Status = "done",
            Text = Text.Clip(body, 6000),
            FullLength = body.Length,
        });
    }

    // ---------------------------------------------------------------- helpers

    public Decision AddDecision(Decision d)
    {
        var existing = Decisions.FirstOrDefault(x => x.Id == d.Id);
        if (existing != null) return existing;
        d.SessionKey = Key;
        Decisions.Add(d);
        if (Decisions.Count > MaxDecisions)
        {
            var old = Decisions.FindIndex(x => x.Status != "pending");
            Decisions.RemoveAt(old >= 0 ? old : 0);
        }
        return d;
    }

    public static void MarkAnswered(Decision d, string text, string how, string via, DateTime at)
    {
        d.Status = "answered";
        d.AnswerText = text;
        d.AnswerHow = how;
        d.AnswerVia = via;
        d.AnsweredAt = at;
    }

    void AddActivity(ActivityItem item)
    {
        Activity.Add(item);
        if (Activity.Count > MaxActivity) Activity.RemoveAt(0);
    }

    ConvItem AddConv(string role, string text, DateTime at, string? refId = null)
    {
        var item = new ConvItem { At = at, Role = role, Text = text, RefId = refId };
        Conversation.Add(item);
        if (Conversation.Count > MaxConversation) Conversation.RemoveAt(0);
        return item;
    }

    void AddReturn(ReturnItem r)
    {
        r.Id = $"{Key}:r{++_returnSeq}";
        Returns.Add(r);
        if (Returns.Count > MaxReturns) Returns.RemoveAt(0);
        AddConv("return", "", r.At, r.Id);
        AddActivity(new ActivityItem { At = r.At, Kind = "return", Text = r.FromLabel, Error = r.Status == "failed" });
    }

    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminder();

    [GeneratedRegex("<artifact-view-context.*?</artifact-view-context>", RegexOptions.Singleline)]
    private static partial Regex ViewContext();

    [GeneratedRegex(@"local_[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}")]
    private static partial Regex LocalId();

    [GeneratedRegex("""<agent-message from="(?<from>[^"]+)">\s*(?<body>.*?)\s*</agent-message>""", RegexOptions.Singleline)]
    private static partial Regex AgentMessage();

    [GeneratedRegex("""The session "(?<title>[^\n]*?)" you started \((?<id>local_[0-9a-fA-F-]+)\) finished a turn\. It reported[^:]*: "(?<text>.*?)"(?: \(first \d+ of (?<len>\d+) characters\))?\s+(?:Read it with|</system-reminder>)""", RegexOptions.Singleline)]
    private static partial Regex SessionNote();
}
