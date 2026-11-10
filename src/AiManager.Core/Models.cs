namespace AiManager.Core;

public sealed class PlanItem
{
    public string Text { get; set; } = "";
    public string Status { get; set; } = "pending";    // pending | in_progress | completed
}

/// <summary>One line of a session's work log: a tool call, something it said, a prompt, or a report coming back.</summary>
public sealed class ActivityItem
{
    public DateTime At { get; set; }
    public string Kind { get; set; } = "tool";         // tool | say | user | return | error
    public string Cat { get; set; } = "";              // tool category, translated by the UI
    public string Tool { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Pending { get; set; }
    public bool Error { get; set; }
    public double? Seconds { get; set; }
    /// <summary>Images the tool returned (screenshots, image files it read), as ids for /api/image.</summary>
    public List<string>? Images { get; set; }
}

/// <summary>A file a session sent to the user.</summary>
public sealed class FileRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Image { get; set; }
}

/// <summary>A result that came back to a dispatching session from something it started.</summary>
public sealed class ReturnItem
{
    public string Id { get; set; } = "";
    public DateTime At { get; set; }
    public string FromKey { get; set; } = "";          // agent id, local_ session id, or workflow agent id
    public string FromLabel { get; set; } = "";
    public string Kind { get; set; } = "subagent";     // subagent | session | task | workflow
    public string Status { get; set; } = "done";       // done | failed
    public string Text { get; set; } = "";
    public int FullLength { get; set; }
}

/// <summary>Something a session started: a subagent, a workflow agent, or another session.</summary>
public sealed class ChildRef
{
    public string Key { get; set; } = "";              // agent id once known
    public string? HostId { get; set; }                // local_… id of a started session
    public string? ToolUseId { get; set; }
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "subagent";     // subagent | session | workflow
    public string? AgentType { get; set; }
    public DateTime At { get; set; }
}

public sealed class ConvItem
{
    public DateTime At { get; set; }
    public string Role { get; set; } = "assistant";    // user | assistant | thinking | files | return
    public string Text { get; set; } = "";
    public string? RefId { get; set; }
    /// <summary>The reply that closed a finished turn. Other assistant text is progress along the way.</summary>
    public bool Final { get; set; }
    /// <summary>Images pasted into a prompt, as ids for /api/image.</summary>
    public List<string>? Images { get; set; }
    public List<FileRef>? Files { get; set; }
}

public sealed class DecisionOption
{
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Fixed meaning the UI translates (allow / deny); null for free-form labels.</summary>
    public string? Key { get; set; }
}

/// <summary>A question a session is waiting on the user for.</summary>
public sealed class Decision
{
    public string Id { get; set; } = "";
    public string SessionKey { get; set; } = "";
    public string? GroupId { get; set; }               // questions asked together are delivered together
    public string Kind { get; set; } = "choice";       // choice | ask | open | permission
    public string Question { get; set; } = "";
    public string? Header { get; set; }
    public string Context { get; set; } = "";
    public List<DecisionOption> Options { get; set; } = new();
    public bool MultiSelect { get; set; }
    public DateTime RaisedAt { get; set; }

    public string Status { get; set; } = "pending";    // pending | answered | dismissed
    public string? AnswerText { get; set; }
    public string? AnswerHow { get; set; }             // click | input | native
    public string? AnswerVia { get; set; }             // aimanager | claude
    public DateTime? AnsweredAt { get; set; }

    /// <summary>True while a hook request is parked on this decision, which is the only time an
    /// answer given in AiManager can reach the session.</summary>
    public bool Held { get; set; }
    public DateTime? HoldUntil { get; set; }
}
