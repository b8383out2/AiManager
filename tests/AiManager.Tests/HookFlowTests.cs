using System.Net;
using System.Text.Json;
using AiManager.Core;

namespace AiManager.Tests;

/// <summary>Drives the real local server with the HTTP calls Claude Code makes for its hooks
/// (request and response shapes taken from the Claude Code hooks reference).</summary>
public class HookFlowTests
{
    const string Sid = "11111111-2222-3333-4444-555555555555";

    static object StopBody(TestHost h, string last) => new
    {
        session_id = Sid, transcript_path = h.Transcript(Sid), cwd = @"C:\work\demo", permission_mode = "default",
        hook_event_name = "Stop", stop_hook_active = false, last_assistant_message = last,
    };

    [Fact]
    public async Task Stop_is_held_until_answered_and_the_answer_goes_back_to_the_session()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid,
            Rec.Human("do the sky", 0),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "Sky follows the clock.", ask = "天色的效果可以吗？", actions = new[] { new { label = "可以" }, new { label = "要调" } } }, "w1", 5),
            Rec.Result("w1", "shown", 6),
            Rec.Say("白天是日光，傍晚偏暖。", 7));

        var stop = h.PostHook("stop", StopBody(h, "白天是日光，傍晚偏暖。"));
        var d = await h.WaitForDecision(x => x.Held);
        Assert.Equal("天色的效果可以吗？", d.Question);
        Assert.Equal("pending", d.Status);
        Assert.False(stop.IsCompleted);                      // the session is parked, waiting for the user

        using var answered = await h.Api("/api/answer", new { id = d.Id, text = "要调", how = "click" });
        Assert.Equal("Delivered", answered.RootElement.GetProperty("result").GetString());

        var resp = await stop;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var output = body.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("Stop", output.GetProperty("hookEventName").GetString());
        var context = output.GetProperty("additionalContext").GetString()!;
        Assert.Contains("天色的效果可以吗？", context);
        Assert.Contains("User's answer: 要调", context);

        var after = h.Host.Store.BuildState().Decisions.Single(x => x.Id == d.Id);
        Assert.Equal(("answered", "aimanager", "click", "要调", false), (after.Status, after.AnswerVia, after.AnswerHow, after.AnswerText, after.Held));
    }

    [Fact]
    public async Task A_question_buried_by_a_later_turn_is_parked_too_and_answered_on_its_own()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid,
            Rec.Human("go", 0),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "r1", ask = "First question?", actions = new[] { new { label = "A" } } }, "w1", 10),
            Rec.Say("first report", 11),
            Rec.Injected("<task-notification>\n<task-id>t9</task-id>\n<status>completed</status>\n<summary>child done</summary>\n<result>ok</result>\n</task-notification>", 100),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "r2", ask = "Second question?", actions = new[] { new { label = "B" } } }, "w2", 110),
            Rec.Say("second report", 111));

        var stop = h.PostHook("stop", StopBody(h, "second report"));
        var first = await h.WaitForDecision(x => x.Held && x.Question == "First question?");
        var second = await h.WaitForDecision(x => x.Held && x.Question == "Second question?");

        // Answer the older one: the turn goes on with just that answer.
        using var r = await h.Api("/api/answer", new { id = first.Id, text = "A", how = "click" });
        Assert.Equal("Delivered", r.RootElement.GetProperty("result").GetString());
        using var body = JsonDocument.Parse(await (await stop).Content.ReadAsStringAsync());
        var context = body.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("First question?", context);
        Assert.DoesNotContain("Second question?", context);

        // The other one is still waiting, and is parked again the next time the session stops.
        var still = h.Host.Store.BuildState().Decisions.Single(x => x.Id == second.Id);
        Assert.False(still.Held);
        using var late = await h.Api("/api/answer", new { id = second.Id, text = "B", how = "click" });
        Assert.Equal("NotDeliverable", late.RootElement.GetProperty("result").GetString());

        h.Write(Sid, Rec.Say("carrying on with A", 200));
        var again = h.PostHook("stop", StopBody(h, "carrying on with A"));
        await h.WaitForDecision(x => x.Held && x.Id == second.Id);
        using var r2 = await h.Api("/api/answer", new { id = second.Id, text = "B", how = "click" });
        Assert.Equal("Delivered", r2.RootElement.GetProperty("result").GetString());
        Assert.Contains("Second question?", await (await again).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stop_without_a_question_returns_at_once_and_changes_nothing()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0), Rec.Say("All done. Tests pass.", 5));

        var resp = await h.PostHook("stop", StopBody(h, "All done. Tests pass."));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("", await resp.Content.ReadAsStringAsync());   // empty 2xx = behave as if no hook were installed
        Assert.Empty(h.Host.Store.BuildState().Decisions);
    }

    [Fact]
    public async Task Stop_falls_back_to_the_hooks_own_message_when_the_transcript_lags()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0), Rec.Tool("Bash", new { command = "dotnet test" }, "b1", 1), Rec.Result("b1", "ok", 2));

        var stop = h.PostHook("stop", StopBody(h, "Tests pass.\n\nShould I commit this?"));   // final message not in the file yet
        var d = await h.WaitForDecision(x => x.Held);
        Assert.Equal(("open", "Should I commit this?"), (d.Kind, d.Question));

        using var _ = await h.Api("/api/answer", new { id = d.Id, text = "yes, commit", how = "input" });
        using var body = JsonDocument.Parse(await (await stop).Content.ReadAsStringAsync());
        Assert.Contains("yes, commit", body.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());

        // When the transcript catches up, the same question must not come back as a second decision.
        h.Write(Sid, Rec.Say("Tests pass.\n\nShould I commit this?", 3));
        h.Host.Store.CatchUp(Sid, h.Transcript(Sid));
        var all = h.Host.Store.BuildState().Decisions;
        Assert.Single(all);
        Assert.Equal("answered", all[0].Status);
    }

    [Fact]
    public async Task Dismissing_releases_the_session_without_an_answer()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0), Rec.Say("Should I continue?", 5));

        var stop = h.PostHook("stop", StopBody(h, "Should I continue?"));
        var d = await h.WaitForDecision(x => x.Held);
        using var _ = await h.Api("/api/dismiss", new { id = d.Id });

        Assert.Equal("", await (await stop).Content.ReadAsStringAsync());
        Assert.Equal("dismissed", h.Host.Store.BuildState().Decisions.Single().Status);
    }

    [Fact]
    public async Task Typing_in_Claude_releases_a_held_turn()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0), Rec.Say("Should I continue?", 5));
        h.Host.Store.CatchUp(Sid, h.Transcript(Sid));

        var stop = h.PostHook("stop", StopBody(h, "Should I continue?"));
        await h.WaitForDecision(x => x.Held);
        h.Write(Sid, Rec.Enqueue("yes please", 30));          // the user typed into Claude Code instead

        var resp = await stop.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AskUserQuestion_is_answered_through_PreToolUse_with_the_input_echoed_back()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0));
        var input = new
        {
            questions = new object[]
            {
                new { question = "Tea or coffee?", header = "Drink", multiSelect = false, options = new[] { new { label = "Tea", description = "leaf" }, new { label = "Coffee", description = "bean" } } },
                new { question = "Which sizes?", header = "Size", multiSelect = true, options = new[] { new { label = "S", description = "" }, new { label = "L", description = "" } } },
            },
        };
        var ask = h.PostHook("ask", new { session_id = Sid, transcript_path = h.Transcript(Sid), hook_event_name = "PreToolUse", tool_name = "AskUserQuestion", tool_use_id = "toolu_1", tool_input = input });

        var first = await h.WaitForDecision(x => x.Held && x.Question == "Tea or coffee?");
        var second = await h.WaitForDecision(x => x.Held && x.Question == "Which sizes?");
        Assert.True(second.MultiSelect);

        using var r1 = await h.Api("/api/answer", new { id = first.Id, text = "Coffee", how = "click" });
        Assert.Equal("Queued", r1.RootElement.GetProperty("result").GetString());   // both questions go back together
        Assert.False(ask.IsCompleted);
        using var r2 = await h.Api("/api/answer", new { id = second.Id, text = "S, L", how = "click" });
        Assert.Equal("Delivered", r2.RootElement.GetProperty("result").GetString());

        using var body = JsonDocument.Parse(await (await ask).Content.ReadAsStringAsync());
        var output = body.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        var updated = output.GetProperty("updatedInput");
        Assert.Equal(2, updated.GetProperty("questions").GetArrayLength());
        Assert.Equal("Coffee", updated.GetProperty("answers").GetProperty("Tea or coffee?").GetString());
        Assert.Equal("S, L", updated.GetProperty("answers").GetProperty("Which sizes?").GetString());
    }

    [Fact]
    public async Task Permission_prompts_are_only_relayed_when_switched_on_and_never_auto_approved()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0));
        var body = new { session_id = Sid, transcript_path = h.Transcript(Sid), hook_event_name = "PermissionRequest", tool_name = "Bash", tool_input = new { command = "rm -rf build" } };

        var off = await h.PostHook("permission", body);
        Assert.Equal("", await off.Content.ReadAsStringAsync());      // off by default: Claude Code shows its own prompt

        h.Host.Config.RelayPermissions = true;
        var held = h.PostHook("permission", body);
        var d = await h.WaitForDecision(x => x.Held && x.Kind == "permission");
        Assert.Contains("rm -rf build", d.Context);
        Assert.Equal(new[] { "allow", "deny" }, d.Options.Select(o => o.Key));
        Assert.False(held.IsCompleted);

        using var _ = await h.Api("/api/answer", new { id = d.Id, text = "deny", how = "click" });
        using var reply = JsonDocument.Parse(await (await held).Content.ReadAsStringAsync());
        var decision = reply.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task Requests_without_the_token_or_from_another_host_name_are_refused()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0), Rec.Say("Should I continue?", 5));

        var badHook = await h.Http.PostAsync($"{h.Host.Url}aimanager/hook/wrong-token/stop", TestHost.JsonBody(StopBody(h, "Should I continue?")));
        Assert.Equal(HttpStatusCode.Forbidden, badHook.StatusCode);

        var noToken = await h.Http.PostAsync(h.Host.Url + "api/answer", TestHost.JsonBody(new { id = "x", text = "y" }));
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, h.Host.Url + "api/state");
        rebinding.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(rebinding)).StatusCode);
    }

    [Fact]
    public async Task Claude_Codes_own_busy_flag_decides_running_versus_idle()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("go", 0), Rec.Say("All done.", 5));          // the transcript says the turn is over
        var registry = Path.Combine(h.ClaudeDir, "sessions");
        Directory.CreateDirectory(registry);
        void Register(string status) => File.WriteAllText(Path.Combine(registry, "1.json"),
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, sessionId = Sid, kind = "interactive", status }));

        async Task<string> StatusAsync(string expected)
        {
            for (int i = 0; i < 80; i++)
            {
                h.Host.Store.CatchUp(Sid, h.Transcript(Sid));
                var root = h.Host.Store.BuildState().Roots.FirstOrDefault();
                if (root?.Status == expected) return expected;
                await Task.Delay(100);
            }
            return h.Host.Store.BuildState().Roots.FirstOrDefault()?.Status ?? "(none)";
        }

        Register("busy");                                                    // a new turn started but wrote nothing yet
        Assert.Equal("running", await StatusAsync("running"));
        Register("idle");
        Assert.Equal("idle", await StatusAsync("idle"));
    }

    [Fact]
    public async Task Sessions_and_their_subagents_show_up_as_a_tree()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid,
            Rec.Human("investigate", 0),
            Rec.Tool("Agent", new { description = "Read the docs", subagent_type = "Explore", prompt = "p" }, "a1", 1),
            Rec.Result("a1", "Found three pages.", 20, new { status = "completed", agentId = "abc123" }),
            Rec.Say("Done.", 21));
        var subDir = Path.Combine(h.ClaudeDir, "projects", "demo", Sid, "subagents");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "agent-abc123.jsonl"),
            "{\"type\":\"user\",\"isSidechain\":true,\"timestamp\":\"" + Rec.At(2) + "\",\"message\":{\"role\":\"user\",\"content\":\"p\"}}\n" + Rec.Say("Found three pages.", 19) + "\n");
        File.WriteAllText(Path.Combine(subDir, "agent-abc123.meta.json"), "{\"agentType\":\"Explore\",\"description\":\"Read the docs\",\"toolUseId\":\"a1\",\"finished\":true}");

        h.Host.Store.CatchUp(Sid, h.Transcript(Sid));
        h.Host.Store.CatchUp(Sid + "~abc123", Path.Combine(subDir, "agent-abc123.jsonl"));
        var root = Assert.Single(h.Host.Store.BuildState().Roots);

        Assert.Equal("investigate", root.Title);
        var child = Assert.Single(root.Children);
        Assert.Equal(("subagent", "done", "Read the docs", true), (child.Kind, child.Status, child.Title, child.Returned));
        var detail = h.Host.Store.BuildDetail(Sid)!;
        Assert.Equal("Found three pages.", Assert.Single(detail.Returns).Text);
    }
}
