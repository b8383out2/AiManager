namespace AiManager.Core;

public sealed class ActionDto
{
    public string Cat = "", Tool = "", Text = "";
}

public sealed class SessionDto
{
    public string Key = "", Id = "", Title = "";
    public string Kind = "main";        // main | linked | subagent | workflow | stub
    public string Status = "idle";      // running | waiting | idle | done | failed | ended
    public bool Live, Returned;
    public string? Cwd, Project, Model, Entrypoint, AgentType, Attention, ParentKey, LastText, PlanCurrent, Error;
    public DateTime? StartedAt, LastAt, TurnStartedAt, ReturnedAt;
    public ActionDto? Action;
    public int PlanDone, PlanTotal, Tools, Pending, Returns;
    public long OutTokens, CtxTokens;
    public double? CostUsd;
    public List<SessionDto> Children = new();
    public List<DateTime[]>? Spans;
}

public sealed class DecisionDto
{
    public string Id = "", SessionKey = "", SessionTitle = "", RootKey = "", RootTitle = "";
    public string Kind = "choice", Question = "", Context = "";
    public string Status = "pending";   // pending | answered | dismissed | expired
    public string? Header, AnswerText, AnswerHow, AnswerVia;
    public List<DecisionOption> Options = new();
    public bool MultiSelect, Held, Live;
    public DateTime RaisedAt;
    public DateTime? HoldUntil, AnsweredAt;
}

public sealed class StateDto
{
    public DateTime Now;
    public bool Loaded;
    public long Version;
    public int PendingCount;
    public List<SessionDto> Roots = new();
    public List<DecisionDto> Decisions = new();
}

public sealed class FileDto
{
    public string Path = "", Short = "";
    public int Edits;
}

public sealed class DetailDto
{
    public SessionDto Session = new();
    public string? Brief;
    public int LinesAdded, LinesRemoved;
    public List<PlanItem> Plan = new();
    public List<ConvItem> Conversation = new();
    public List<ReturnItem> Returns = new();
    public List<ActivityItem> Activity = new();
    public List<FileDto> Files = new();
    public List<DecisionDto> Decisions = new();
}

public sealed partial class SessionStore
{
    sealed class Tree
    {
        public List<SessionDto> Roots = new();
        public Dictionary<string, SessionDto> ByKey = new();
        public Dictionary<string, string> RootOf = new();
    }

    public StateDto BuildState()
    {
        lock (_gate)
        {
            var tree = BuildTree(withSpans: false);
            var live = _live.Current;
            var all = new List<DecisionDto>();
            foreach (var t in _byKey.Values)
            {
                var s = t.State;
                if (s.IsSubagent) continue;
                foreach (var d in s.Decisions) all.Add(ToDto(d, s, live.ContainsKey(s.Id), tree));
            }

            var pending = all.Where(d => d.Status == "pending").OrderBy(d => d.RaisedAt).ToList();
            var known = all.Select(d => d.Id).ToHashSet();
            var resolved = all.Where(d => d.Status != "pending")
                .Concat(_decisions.All().Where(e => !known.Contains(e.Id)).Select(FromLog))
                .OrderByDescending(d => d.AnsweredAt ?? d.RaisedAt)
                .Take(60);

            return new StateDto
            {
                Now = DateTime.UtcNow,
                Loaded = Loaded,
                Version = Version,
                PendingCount = pending.Count,
                Roots = tree.Roots,
                Decisions = pending.Concat(resolved).ToList(),
            };
        }
    }

    public DetailDto? BuildDetail(string key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var tracked)) return null;
            var s = tracked.State;
            var tree = BuildTree(withSpans: true);
            if (!tree.ByKey.TryGetValue(key, out var dto)) return null;
            bool isLive = _live.Current.ContainsKey(s.Id);
            return new DetailDto
            {
                Session = dto,
                Brief = Text.Clip(s.FirstPrompt, 4000),
                LinesAdded = s.LinesAdded,
                LinesRemoved = s.LinesRemoved,
                Plan = s.Plan.ToList(),
                Conversation = s.Conversation.ToList(),
                Returns = s.Returns.ToList(),
                Activity = s.Activity.AsEnumerable().Reverse().Take(200).ToList(),
                Files = s.Files.OrderByDescending(f => f.Value).Take(40)
                    .Select(f => new FileDto { Path = f.Key, Short = ToolInfo.ShortPath(f.Key) ?? f.Key, Edits = f.Value }).ToList(),
                Decisions = s.Decisions.Select(d => ToDto(d, s, isLive, tree)).ToList(),
            };
        }
    }

    /// <summary>A pending decision can only be acted on while its session is alive or a hook is parked on it;
    /// otherwise it is reported as expired rather than cluttering the queue forever.</summary>
    static DecisionDto ToDto(Decision d, SessionState s, bool isLive, Tree tree)
    {
        var rootKey = tree.RootOf.GetValueOrDefault(s.Key, s.Key);
        bool actionable = d.Held || isLive;
        return new DecisionDto
        {
            Id = d.Id,
            SessionKey = s.Key,
            SessionTitle = s.Title,
            RootKey = rootKey,
            RootTitle = tree.ByKey.TryGetValue(rootKey, out var root) ? root.Title : s.Title,
            Kind = d.Kind,
            Question = d.Question,
            Header = d.Header,
            Context = d.Context,
            Options = d.Options,
            MultiSelect = d.MultiSelect,
            RaisedAt = d.RaisedAt,
            Status = d.Status == "pending" && !actionable ? "expired" : d.Status,
            Held = d.Held,
            HoldUntil = d.HoldUntil,
            Live = isLive,
            AnswerText = d.AnswerText,
            AnswerHow = d.AnswerHow,
            AnswerVia = d.AnswerVia,
            AnsweredAt = d.AnsweredAt,
        };
    }

    static DecisionDto FromLog(DecisionLog.Entry e) => new()
    {
        Id = e.Id,
        SessionKey = e.SessionKey,
        SessionTitle = e.SessionTitle,
        RootKey = e.SessionKey,
        RootTitle = e.SessionTitle,
        Kind = e.Kind,
        Question = e.Question,
        Status = e.Status,
        RaisedAt = e.RaisedAt,
        AnswerText = e.AnswerText,
        AnswerHow = e.AnswerHow,
        AnswerVia = e.AnswerVia,
        AnsweredAt = e.At,
    };

    Tree BuildTree(bool withSpans)
    {
        var now = DateTime.UtcNow;
        var live = _live.Current;
        var states = _byKey.Values.Select(t => t.State).ToList();
        var mains = states.Where(s => !s.IsSubagent).ToList();
        var subs = states.Where(s => s.IsSubagent).ToLookup(s => s.ParentId!);
        var byId = mains.ToDictionary(s => s.Id);

        // Work out which sessions were started by another session.
        var parentOf = new Dictionary<string, string>();
        var targets = new Dictionary<ChildRef, SessionState>(ReferenceEqualityComparer.Instance);
        foreach (var p in mains)
        {
            foreach (var c in p.Children)
            {
                if (c.Kind != "session") continue;
                SessionState? child = null;
                if (c.HostId != null && _links.TryGetValue(c.HostId, out var sid)) byId.TryGetValue(sid, out child);
                // The link is only recorded while the child is alive; fall back to its title, which the
                // dispatcher chose when it started the session.
                child ??= mains.Where(m => m != p && m.Title == c.Label && m.StartedAt >= c.At.AddMinutes(-2))
                    .OrderBy(m => m.StartedAt).FirstOrDefault();
                if (child == null || child == p || parentOf.ContainsKey(child.Id)) continue;
                bool cycle = false;
                for (var up = p.Id; parentOf.TryGetValue(up, out var next); up = next)
                    if (next == child.Id) { cycle = true; break; }
                if (cycle) continue;
                parentOf[child.Id] = p.Id;
                targets[c] = child;
            }
        }

        var tree = new Tree();

        SessionDto Base(SessionState s, string kind, string? parentKey, string rootKey)
        {
            var current = s.CurrentTool ?? (s.TurnOpen ? s.Activity.LastOrDefault(a => a.Kind == "tool") : null);
            var dto = new SessionDto
            {
                Key = s.Key,
                Id = s.Id,
                Title = s.Title,
                Kind = kind,
                Cwd = s.Cwd,
                Project = s.Cwd == null ? null : Path.GetFileName(s.Cwd.TrimEnd('\\', '/')),
                Model = s.Model,
                Entrypoint = s.Entrypoint,
                AgentType = s.AgentType,
                ParentKey = parentKey,
                StartedAt = s.StartedAt,
                LastAt = s.LastAt,
                TurnStartedAt = s.TurnStartedAt,
                Action = current == null ? null : new ActionDto { Cat = current.Cat, Tool = current.Tool, Text = current.Text },
                PlanTotal = s.Plan.Count,
                PlanDone = s.Plan.Count(p => p.Status == "completed"),
                PlanCurrent = s.Plan.FirstOrDefault(p => p.Status == "in_progress")?.Text,
                Tools = s.ToolCalls,
                OutTokens = s.OutputTokens,
                CtxTokens = s.ContextTokens,
                CostUsd = s.CostUsd,
                Returns = s.Returns.Count,
                LastText = Text.NonEmpty(Text.Clip(s.LastAssistantText.Trim(), 280)),
                Error = s.ApiError,
                Spans = withSpans ? s.Spans.TakeLast(200).ToList() : null,
            };
            tree.ByKey[s.Key] = dto;
            tree.RootOf[s.Key] = rootKey;
            return dto;
        }

        void MarkReturned(SessionDto dto, ReturnItem? ret)
        {
            if (ret == null) return;
            dto.Returned = true;
            dto.ReturnedAt = ret.At;
        }

        SessionDto BuildSub(SessionState sub, SessionState parent, string rootKey, bool parentLive)
        {
            var viaRef = parent.Children.LastOrDefault(c => c.Key == sub.Id || (sub.AgentToolUseId != null && c.ToolUseId == sub.AgentToolUseId));
            var dto = Base(sub, viaRef?.Kind == "workflow" ? "workflow" : "subagent", parent.Key, rootKey);
            if (dto.Title.Length == 0) dto.Title = viaRef?.Label ?? sub.Id;
            var ret = parent.Returns.LastOrDefault(r => r.FromKey == sub.Id);
            MarkReturned(dto, ret);
            var idle = now - (sub.LastAt ?? DateTime.MinValue);
            dto.Status =
                ret != null ? (ret.Status == "failed" ? "failed" : "done")
                : sub.AgentFinished ? "done"
                : idle < TimeSpan.FromMinutes(2) ? "running"
                : parentLive && sub.TurnOpen && idle < TimeSpan.FromMinutes(30) ? "running"
                : "ended";
            return dto;
        }

        SessionDto BuildMain(SessionState s, string kind, SessionState? parent, string rootKey, int depth)
        {
            live.TryGetValue(s.Id, out var info);
            bool isLive = info != null;
            var dto = Base(s, kind, parent?.Key, rootKey);
            dto.Live = isLive;
            dto.Pending = s.Decisions.Count(d => d.Status == "pending" && (d.Held || isLive));
            dto.Attention = s.AttentionType;
            // A question dialog or permission prompt stops the session in the middle of a turn. A question
            // left at the end of a turn only means "waiting" once that turn is over: after one answer goes
            // back, the session is working again even if another question is still open.
            // A parked hook keeps the process "busy" even though it is really waiting for the user.
            bool blocked = s.AttentionType != null
                || s.Decisions.Any(d => d.Status == "pending" && (d.Held || (d.Kind is "ask" or "permission" && isLive)));
            // Claude Code's own busy/idle flag, where it reports one, beats what the transcript implies:
            // an interrupted turn never writes its end, and a new one has not written its start yet.
            bool turnOpen = info?.Status switch { "busy" => true, "idle" => false, _ => s.TurnOpen };
            dto.Status =
                blocked ? "waiting"
                : turnOpen ? (isLive || now - (s.LastAt ?? DateTime.MinValue) < TimeSpan.FromMinutes(2) ? "running" : "ended")
                : dto.Pending > 0 ? "waiting"
                : s.ApiError != null ? "failed"
                : isLive ? "idle" : "ended";

            var claimed = new HashSet<string>();
            foreach (var sub in subs[s.Id])
            {
                dto.Children.Add(BuildSub(sub, s, rootKey, isLive));
                claimed.Add(sub.Id);
                if (sub.AgentToolUseId != null) claimed.Add(sub.AgentToolUseId);
            }

            int index = 0;
            foreach (var c in s.Children)
            {
                index++;
                if (c.Kind == "session")
                {
                    var retKey = c.HostId ?? c.Key;
                    var ret = s.Returns.LastOrDefault(r => r.FromKey == retKey);
                    if (targets.TryGetValue(c, out var child) && depth < 4)
                    {
                        var cd = BuildMain(child, "linked", s, rootKey, depth + 1);
                        MarkReturned(cd, ret);
                        // A started session that reported back and has since closed did its job.
                        if (ret != null && cd.Status == "ended") cd.Status = ret.Status == "failed" ? "failed" : "done";
                        dto.Children.Add(cd);
                        continue;
                    }
                    dto.Children.Add(Stub(s, c, index, ret, isLive, rootKey));
                }
                else if (!(claimed.Contains(c.Key) || (c.ToolUseId != null && claimed.Contains(c.ToolUseId))))
                {
                    // Started, but no transcript of its own was found (e.g. it ran inline).
                    var ret = s.Returns.LastOrDefault(r => r.FromKey == c.Key || r.FromKey == c.ToolUseId);
                    dto.Children.Add(Stub(s, c, index, ret, isLive, rootKey));
                }
            }
            dto.Children = dto.Children.OrderBy(c => c.StartedAt ?? DateTime.MaxValue).ToList();
            return dto;
        }

        SessionDto Stub(SessionState parent, ChildRef c, int index, ReturnItem? ret, bool parentLive, string rootKey)
        {
            var dto = new SessionDto
            {
                Key = $"{parent.Key}~ref{index}",
                Id = c.Key,
                Title = c.Label,
                Kind = "stub",
                AgentType = c.Kind == "session" ? "session" : c.AgentType,
                ParentKey = parent.Key,
                StartedAt = c.At,
                LastAt = ret?.At ?? c.At,
                Status = ret != null ? (ret.Status == "failed" ? "failed" : "done")
                    : parentLive && (parent.TurnOpen || c.Kind == "session") ? "running" : "ended",
            };
            MarkReturned(dto, ret);
            tree.RootOf[dto.Key] = rootKey;
            return dto;
        }

        foreach (var s in mains.Where(m => !parentOf.ContainsKey(m.Id)).OrderByDescending(m => m.LastAt ?? DateTime.MinValue))
            tree.Roots.Add(BuildMain(s, "main", null, s.Key, 0));
        return tree;
    }
}
