using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiManager.Core;

/// <summary>Receives Claude Code hook calls and parks them until the user answers in AiManager.
/// A parked request is the one supported way to get an answer back into a running session, so a
/// decision is only "deliverable" while its request is being held here.</summary>
public sealed class HookGateway
{
    sealed class Hold
    {
        public required string GroupId, SessionKey, Event;
        public required List<string> Ids;
        /// <summary>Questions asked in one dialog go back together; a parked turn goes on as soon as any one is answered.</summary>
        public required bool NeedAll;
        public readonly Dictionary<string, string> Answers = new();
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public enum AnswerResult { Delivered, Queued, NotDeliverable, NotFound }

    readonly SessionStore _store;
    readonly AppConfig _cfg;
    readonly FileLog _log;
    readonly ConcurrentDictionary<string, Hold> _holds = new();

    /// <summary>A decision became answerable from AiManager.</summary>
    public event Action<Decision>? Held;

    public HookGateway(SessionStore store, AppConfig cfg, FileLog log)
    {
        _store = store;
        _cfg = cfg;
        _log = log;
        // Typing in Claude Code takes over: let the parked turn end so the typed prompt is processed.
        store.HumanTyped += key =>
        {
            foreach (var h in _holds.Values)
                if (h.SessionKey == key && h.Event == "Stop") h.Done.TrySetResult(false);
        };
    }

    // ---------------------------------------------------------------- hook events

    /// <summary>Stop: the session is about to end its turn. If the turn ends on a question, wait for the answer
    /// and hand it back so the session carries on with it.</summary>
    public async Task<object?> OnStop(JsonElement body, CancellationToken ct)
    {
        var sid = body.Str("session_id");
        if (sid == null) return null;
        var last = body.Str("last_assistant_message") ?? "";
        var ds = await FindStopDecisions(sid, body.Str("transcript_path"), last, ct);
        if (ds.Count == 0) return null;

        var answers = await HoldAsync("Stop", sid, sid + ":stop", ds, needAll: false, ct);
        if (answers == null || answers.Count == 0) return null;
        var text = string.Join("\n\n", ds.Where(d => answers.ContainsKey(d.Id)).Select(d => ComposeReply(d, answers[d.Id])));
        _log.Write($"stop answered {string.Join(",", answers.Keys)}");
        return _cfg.StopReplyMode == "block"
            ? new { decision = "block", reason = text }
            : new { hookSpecificOutput = new { hookEventName = "Stop", additionalContext = text } };
    }

    async Task<List<Decision>> FindStopDecisions(string sid, string? transcriptPath, string last, CancellationToken ct)
    {
        bool open = _cfg.HoldTrailingQuestions;
        // The transcript can lag behind the hook, so give it a moment to show this turn's question
        // before falling back to the message text the hook itself carries.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            _store.CatchUp(sid, transcriptPath);
            if (_store.CurrentTurnDecision(sid, open) != null || _store.HasFinalMessage(sid, last)) break;
            await Task.Delay(150, ct);
        }
        if (open && _store.CurrentTurnDecision(sid, true) == null && !_store.HasFinalMessage(sid, last)
            && QuestionDetector.Detect(last) is { } q)
            _store.EnsureOpenDecision(sid, last, q);
        return _store.PendingForHold(sid, open);
    }

    /// <summary>PreToolUse on AskUserQuestion: answer the question dialog from AiManager.</summary>
    public async Task<object?> OnAsk(JsonElement body, CancellationToken ct)
    {
        var sid = body.Str("session_id");
        var toolUseId = body.Str("tool_use_id");
        if (sid == null || toolUseId == null || body.Str("tool_name") != "AskUserQuestion") return null;
        if (body.Obj("tool_input") is not { } input || input.Arr("questions") is not { } questions) return null;

        _store.CatchUp(sid, body.Str("transcript_path"));
        var ds = _store.EnsureAskDecisions(sid, toolUseId, questions).Where(d => d.Status == "pending").ToList();
        if (ds.Count == 0) return null;

        var answers = await HoldAsync("PreToolUse", sid, $"{sid}:{toolUseId}", ds, needAll: true, ct);
        if (answers == null) return null;

        // The tool only runs without prompting when the whole original input is echoed back with the answers added.
        var updated = JsonNode.Parse(input.GetRawText())!.AsObject();
        var map = new JsonObject();
        foreach (var d in ds) map[d.Question] = answers[d.Id];
        updated["answers"] = map;
        _log.Write($"ask answered {sid}:{toolUseId}");
        return new
        {
            hookSpecificOutput = new { hookEventName = "PreToolUse", permissionDecision = "allow", updatedInput = updated },
        };
    }

    /// <summary>PermissionRequest: relay an allow/deny prompt. Off unless the user turned it on, and never
    /// answered automatically — only an explicit click produces a decision.</summary>
    public async Task<object?> OnPermission(JsonElement body, CancellationToken ct)
    {
        if (!_cfg.RelayPermissions) return null;
        var sid = body.Str("session_id");
        var tool = body.Str("tool_name");
        if (sid == null || tool == null || tool is "AskUserQuestion" or "ExitPlanMode") return null;

        _store.CatchUp(sid, body.Str("transcript_path"));
        var preview = body.Obj("tool_input") is { } input ? Text.Clip(input.GetRawText(), 1200) : "";
        var d = _store.AddPermissionDecision(sid, ToolInfo.ShortName(tool), preview);
        if (d == null) return null;

        var answers = await HoldAsync("PermissionRequest", sid, d.Id, new[] { d }, needAll: true, ct);
        if (answers == null) return null;
        var answer = answers[d.Id];
        _log.Write($"permission {answer} {d.Id}");
        object decision = answer == "allow"
            ? new { behavior = "allow" }
            : new { behavior = "deny", message = "The user denied this request from the AiManager app." };
        return new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision } };
    }

    /// <summary>Notification: Claude Code is showing a prompt of its own that AiManager cannot answer.</summary>
    public void OnNotify(JsonElement body)
    {
        var sid = body.Str("session_id");
        var type = body.Str("notification_type");
        if (sid == null || type is not ("permission_prompt" or "agent_needs_input" or "elicitation_dialog" or "elicitation_url_dialog")) return;
        if (_holds.Values.Any(h => h.SessionKey == sid)) return;    // already shown as a decision here
        _store.SetAttention(sid, type, body.Str("message"));
    }

    // ---------------------------------------------------------------- answering

    public AnswerResult Answer(string decisionId, string text, string how)
    {
        var hold = _holds.Values.FirstOrDefault(h => h.Ids.Contains(decisionId));
        if (hold == null)
            return _store.FindDecision(decisionId) == null ? AnswerResult.NotFound : AnswerResult.NotDeliverable;
        lock (hold)
        {
            // Already on its way back with an earlier answer: this one has to wait for the next hold.
            if (hold.Done.Task.IsCompleted) return AnswerResult.NotDeliverable;
            hold.Answers[decisionId] = text;
            _store.MarkAnswered(decisionId, text, how, "aimanager");
            if (hold.NeedAll && !hold.Ids.All(hold.Answers.ContainsKey)) return AnswerResult.Queued;
            hold.Done.TrySetResult(true);
            return AnswerResult.Delivered;
        }
    }

    /// <summary>The user chose not to answer this one here. When nothing else is parked with it, the session
    /// is released and behaves as if AiManager were absent.</summary>
    public bool Dismiss(string decisionId)
    {
        var hold = _holds.Values.FirstOrDefault(h => h.Ids.Contains(decisionId));
        if (hold != null)
            lock (hold)
            {
                hold.Ids.Remove(decisionId);
                if (hold.NeedAll || hold.Ids.Count == 0) hold.Done.TrySetResult(false);
            }
        return _store.MarkDismissed(decisionId) != null;
    }

    async Task<Dictionary<string, string>?> HoldAsync(string evt, string sessionKey, string groupId, IReadOnlyList<Decision> ds, bool needAll, CancellationToken ct)
    {
        var hold = new Hold { GroupId = groupId, SessionKey = sessionKey, Event = evt, NeedAll = needAll, Ids = ds.Select(d => d.Id).ToList() };
        if (_holds.TryRemove(groupId, out var stale)) stale.Done.TrySetResult(false);
        _holds[groupId] = hold;

        var wait = TimeSpan.FromMinutes(_cfg.HoldMinutes);
        _store.SetHeld(hold.Ids, true, DateTime.UtcNow + wait);
        foreach (var d in ds) Held?.Invoke(d);
        _log.Write($"hold {evt} {groupId} for {wait.TotalMinutes:0} min");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // ct fires when Claude Code gives up on the hook (its own timeout, or the turn was interrupted).
            var finished = await Task.WhenAny(hold.Done.Task, Task.Delay(wait, cts.Token));
            if (finished != hold.Done.Task || !hold.Done.Task.Result) return null;
            lock (hold) return new Dictionary<string, string>(hold.Answers);
        }
        finally
        {
            cts.Cancel();
            hold.Done.TrySetResult(false);      // from here on, late answers are refused rather than lost
            _holds.TryRemove(new KeyValuePair<string, Hold>(groupId, hold));
            _store.SetHeld(ds.Select(d => d.Id), false, null);
        }
    }

    static string ComposeReply(Decision d, string answer) =>
        "[AiManager] The user answered your question from the AiManager desktop app instead of typing in this window.\n" +
        $"Question: {d.Question}\n" +
        $"User's answer: {answer}\n" +
        "Treat this as the user's reply to that question and continue accordingly.";
}
