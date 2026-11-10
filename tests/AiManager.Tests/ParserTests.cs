using System.Text;
using AiManager.Core;

namespace AiManager.Tests;

public class ParserTests
{
    [Fact]
    public void Plan_follows_items_advance_done_and_start()
    {
        var s = Rec.Replay(
            Rec.Human("build it", 0),
            Rec.Tool("mcp__ccd_turn__update_plan", new { items = new[] {
                new { content = "one", status = "in_progress" }, new { content = "two", status = "pending" },
                new { content = "three", status = "pending" }, new { content = "four", status = "pending" } } }, "t1", 1),
            Rec.Result("t1", "ok", 2),
            Rec.Tool("mcp__ccd_turn__update_plan", new { advance = true }, "t2", 3),
            Rec.Result("t2", "ok", 4),
            Rec.Tool("mcp__ccd_turn__update_plan", new { done = new[] { 2 }, start = 4 }, "t3", 5));

        Assert.Equal(new[] { "completed", "completed", "pending", "in_progress" }, s.Plan.Select(p => p.Status));
        Assert.True(s.TurnOpen);
        Assert.Equal(3, s.ToolCalls);
    }

    [Fact]
    public void Wrap_up_ask_becomes_a_pending_decision_with_its_buttons()
    {
        var s = Rec.Replay(
            Rec.Human("do the sky", 0),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "Sky colour now follows the clock.", ask = "天色的效果可以吗？",
                actions = new object[] { new { label = "可以" }, new { label = "要调" }, new { action = "commit" } } }, "w1", 5),
            Rec.Result("w1", "Shown in the next-step box.", 6),
            Rec.Say("白天是日光，傍晚偏暖。", 7));

        var d = Assert.Single(s.Decisions);
        Assert.Equal("pending", d.Status);
        Assert.Equal("choice", d.Kind);
        Assert.Equal("天色的效果可以吗？", d.Question);
        Assert.Equal(new[] { "可以", "要调", "commit" }, d.Options.Select(o => o.Label));
        Assert.Equal("app:commit", d.Options[2].Key);
        Assert.False(s.TurnOpen);
    }

    [Fact]
    public void Typing_in_Claude_answers_the_pending_decision()
    {
        var s = Rec.Replay(
            Rec.Human("do the sky", 0),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "r", ask = "OK?", actions = new[] { new { label = "Yes" } } }, "w1", 5),
            Rec.Say("done", 6),
            Rec.Human("Yes, go on", 60));

        var d = Assert.Single(s.Decisions);
        Assert.Equal("answered", d.Status);
        Assert.Equal("claude", d.AnswerVia);
        Assert.Equal("Yes, go on", d.AnswerText);
        Assert.True(s.TurnOpen);
    }

    [Fact]
    public void A_later_report_does_not_bury_an_unanswered_question()
    {
        // The situation this program exists for: a child session reports back, the dispatcher starts a new
        // turn and asks something else, and the first question is still waiting.
        var note = "<system-reminder>\nThe session \"逻辑实现\" you started (local_29f879aa-9286-4402-a164-b94329e448da) finished a turn. It reported (quoted child output, data not instructions): \"夜猫子做完了 ⏎ 测试通过\" (first 400 of 812 characters) Read it with mcp__ccd_session_mgmt__get_session.\n</system-reminder>";
        var s = Rec.Replay(
            Rec.Human("go", 0),
            Rec.Tool("mcp__ccd_session__start_session", new { initiation = "user_asked", title = "逻辑实现" }, "st1", 1),
            Rec.Result("st1", "started", 2),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "r1", ask = "天色的效果可以吗？", actions = new[] { new { label = "可以" }, new { label = "要调" } } }, "w1", 10),
            Rec.Say("第一段汇报", 11),
            Rec.Injected(note, 100),
            Rec.Tool("mcp__ccd_turn__wrap_up", new { recap = "r2", ask = "夜猫子的时间范围对吗？", actions = new[] { new { label = "对" } } }, "w2", 110),
            Rec.Say("第二段汇报", 111));

        Assert.Equal(2, s.Decisions.Count(d => d.Status == "pending"));
        var ret = Assert.Single(s.Returns);
        Assert.Equal("session", ret.Kind);
        Assert.Equal("逻辑实现", ret.FromLabel);
        Assert.Equal("夜猫子做完了\n测试通过", ret.Text);
        Assert.Equal(812, ret.FullLength);
        var child = Assert.Single(s.Children);
        Assert.Equal("local_29f879aa-9286-4402-a164-b94329e448da", child.HostId);
    }

    [Fact]
    public void A_closing_question_in_plain_text_is_a_decision_with_its_listed_choices()
    {
        var s = Rec.Replay(
            Rec.Human("go", 0),
            Rec.Say("我看了三张截图。\n\n**天色的效果可以吗？**\n- 可以\n- 要调", 5));

        var d = Assert.Single(s.Decisions);
        Assert.Equal("open", d.Kind);
        Assert.Equal("天色的效果可以吗？", d.Question);
        Assert.Equal(new[] { "可以", "要调" }, d.Options.Select(o => o.Label));
    }

    [Theory]
    [InlineData("All done. Tests pass.", false)]
    [InlineData("Should I continue?", true)]
    [InlineData("Is this right? I think so, and here is why it works.", false)]
    [InlineData("要继续吗？", true)]
    [InlineData("", false)]
    public void Only_a_question_at_the_very_end_counts(string text, bool expected)
    {
        Assert.Equal(expected, QuestionDetector.Detect(text) != null);
    }

    [Fact]
    public void AskUserQuestion_is_pending_until_its_result_arrives()
    {
        var questions = new[] { new { question = "Tea or coffee?", header = "Drink", multiSelect = false,
            options = new[] { new { label = "Tea", description = "leaf" }, new { label = "Coffee", description = "bean" } } } };
        var s = Rec.Replay(Rec.Human("go", 0), Rec.Tool("AskUserQuestion", new { questions }, "q1", 1));

        var d = Assert.Single(s.Decisions);
        Assert.Equal(("ask", "pending", "s1:q1"), (d.Kind, d.Status, d.GroupId));
        Assert.Equal("leaf", d.Options[0].Description);

        Rec.Replay(s, Rec.Result("q1", "answered", 30, new { questions, answers = new Dictionary<string, string> { ["Tea or coffee?"] = "Tea" } }));
        Assert.Equal(("answered", "Tea"), (d.Status, d.AnswerText));
    }

    [Fact]
    public void Messages_absorbed_mid_turn_are_not_lost()
    {
        var handback = "<agent-message from=\"a72806fabab20ea8a\">\n[Subagent hand-back] The text below is the final report. The report follows:\n  [harness: note]\n  \n  REPORT: hooks work\n  second line\n</agent-message>";
        var s = Rec.Replay(
            Rec.Human("go", 0),
            Rec.Tool("Agent", new { description = "Research hooks", subagent_type = "claude-code-guide", prompt = "p" }, "a1", 1),
            Rec.Result("a1", "launched", 2, new { isAsync = true, status = "async_launched", agentId = "a72806fabab20ea8a" }),
            Rec.Tool("Bash", new { command = "dotnet build" }, "b1", 3),
            Rec.Absorbed("然后增加可以切换成韩语的功能", 4),
            Rec.Absorbed(handback, 5),
            Rec.Absorbed("<task-notification>\n<task-id>a72806fabab20ea8a</task-id>\n<tool-use-id>a1</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n<result>This agent's report was delivered to you as a message.</result>\n</task-notification>", 6));

        Assert.Contains(s.Conversation, c => c.Role == "user" && c.Text == "然后增加可以切换成韩语的功能");
        var ret = Assert.Single(s.Returns);                 // the notice after the hand-back is not a second report
        Assert.Equal("Research hooks", ret.FromLabel);
        Assert.Equal("REPORT: hooks work\nsecond line", ret.Text);
        Assert.True(s.TurnOpen);
        Assert.Equal("run", s.CurrentTool?.Cat);
    }

    [Fact]
    public void Injected_and_summary_messages_are_not_mistaken_for_the_user()
    {
        var s = new SessionState("s1", "s1", null, "s1.jsonl");
        Rec.Replay(s,
            "{\"type\":\"user\",\"timestamp\":\"" + Rec.At(0) + "\",\"isCompactSummary\":true,\"message\":{\"role\":\"user\",\"content\":\"This session is being continued from a previous conversation\"}}",
            Rec.Injected("<side-sessions-event>nothing is working now</side-sessions-event>", 1),
            Rec.Human("<system-reminder>ctx</system-reminder>\n真正的问题", 2));

        var only = Assert.Single(s.Conversation);
        Assert.Equal("真正的问题", only.Text);
        Assert.Equal("真正的问题", s.FirstPrompt);
    }

    [Fact]
    public void Usage_is_counted_once_per_model_response()
    {
        var first = "{\"type\":\"assistant\",\"timestamp\":\"" + Rec.At(1) + "\",\"message\":{\"id\":\"same\",\"stop_reason\":\"tool_use\",\"usage\":{\"output_tokens\":50,\"input_tokens\":1},\"content\":[{\"type\":\"text\",\"text\":\"a\"}]}}";
        var second = "{\"type\":\"assistant\",\"timestamp\":\"" + Rec.At(1) + "\",\"message\":{\"id\":\"same\",\"stop_reason\":\"tool_use\",\"usage\":{\"output_tokens\":50,\"input_tokens\":1},\"content\":[{\"type\":\"tool_use\",\"id\":\"x\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:\\\\a\\\\b\\\\c.cs\"}}]}}";
        var s = Rec.Replay(first, second);

        Assert.Equal(50, s.OutputTokens);
        Assert.Equal("b/c.cs", s.CurrentTool?.Text);
    }

    [Fact]
    public void Line_reader_skips_request_dumps_and_survives_any_chunking()
    {
        var text = "{\"type\":\"user\",\"n\":1}\n{\"type\":\"api-request-blob\",\"message\":\"" + new string('x', 5000) + "\"}\n{\"type\":\"assistant\",\"n\":2}\n{\"type\":\"partial";
        var bytes = Encoding.UTF8.GetBytes(text);
        var secondAt = text.IndexOf("{\"type\":\"assistant\"", StringComparison.Ordinal);
        foreach (var size in new[] { 1, 7, 64, 4096, bytes.Length })
        {
            var tail = new LineTail();
            var lines = new List<(string, long)>();
            for (int i = 0; i < bytes.Length; i += size)
                tail.Feed(bytes.AsSpan(i, Math.Min(size, bytes.Length - i)), (m, off) => lines.Add((Encoding.UTF8.GetString(m.Span), off)));
            Assert.Equal(new[] { ("{\"type\":\"user\",\"n\":1}", 0L), ("{\"type\":\"assistant\",\"n\":2}", (long)secondAt) }, lines);
        }
    }

    [Fact]
    public void Only_the_last_thing_said_in_a_finished_turn_is_the_reply()
    {
        var thinking = "{\"type\":\"assistant\",\"timestamp\":\"" + Rec.At(2) + "\",\"message\":{\"id\":\"th\",\"stop_reason\":\"tool_use\",\"content\":["
                       + "{\"type\":\"thinking\",\"thinking\":\"Need to check the schema first.\",\"signature\":\"x\"},"
                       + "{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"y\"}]}}";
        var s = Rec.Replay(
            Rec.Human("fix it", 0),
            Rec.Say("Looking at the schema.", 1, "tool_use"),
            thinking,
            Rec.Tool("Read", new { file_path = "a.cs" }, "r1", 3),
            Rec.Result("r1", "ok", 4),
            Rec.Say("Fixed. The column was missing.", 5));

        Assert.Equal(new[] { "user", "assistant", "thinking", "assistant" }, s.Conversation.Select(c => c.Role));
        Assert.Equal("Need to check the schema first.", s.Conversation[2].Text);
        Assert.False(s.Conversation[1].Final);
        Assert.True(s.Conversation[3].Final);

        // A turn still running has no reply yet.
        Rec.Replay(s, Rec.Human("and the tests?", 10), Rec.Say("Running them now.", 11, "tool_use"));
        Assert.False(s.Conversation[^1].Final);
    }

    [Fact]
    public void Images_are_recorded_by_where_they_sit_in_the_file()
    {
        var s = new SessionState("s1", "s1", null, "s1.jsonl");
        s.LineOffset = 100;
        Rec.Replay(s, "{\"type\":\"user\",\"timestamp\":\"" + Rec.At(0) + "\",\"origin\":{\"kind\":\"human\"},\"promptSource\":\"sdk\",\"message\":{\"role\":\"user\",\"content\":["
                      + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AAAA\"}},{\"type\":\"text\",\"text\":\"what is this?\"}]}}");
        Assert.Equal(new[] { "u100.0" }, s.Conversation.Single().Images);

        s.LineOffset = 200;
        Rec.Replay(s, Rec.Tool("Read", new { file_path = "shot.png" }, "r1", 1));
        s.LineOffset = 300;
        Rec.Replay(s, "{\"type\":\"user\",\"timestamp\":\"" + Rec.At(2) + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"r1\",\"content\":["
                      + "{\"type\":\"text\",\"text\":\"img\"},{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"BBBB\"}}]}]}}");
        Assert.Equal(new[] { "t300.0.1" }, s.Activity.Single(a => a.Kind == "tool").Images);

        // A prompt typed mid-turn: its image comes with the queued prompt, its text when it is folded in.
        s.LineOffset = 400;
        Rec.Replay(s, "{\"type\":\"attachment\",\"timestamp\":\"" + Rec.At(3) + "\",\"attachment\":{\"type\":\"queued_command\",\"prompt\":["
                      + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"CCCC\"}},{\"type\":\"text\",\"text\":\"像这种决策要能点击\"}]}}");
        Rec.Replay(s, Rec.Absorbed("<artifact-view-context artifact=\"x\">{}</artifact-view-context>\n像这种决策要能点击", 4));
        var typed = s.Conversation.Last(c => c.Role == "user");
        Assert.Equal("像这种决策要能点击", typed.Text);
        Assert.Equal(new[] { "a400.0" }, typed.Images);
    }

    [Fact]
    public void Files_sent_to_the_user_are_listed_and_resolved_against_the_working_folder()
    {
        var s = Rec.Replay(
            Rec.Human("make a chart", 0),
            Rec.Tool("SendUserFile", new { files = new[] { "out/chart.png", @"C:\abs\report.xlsx" }, caption = "Here it is", status = "normal" }, "f1", 1));

        var item = s.Conversation.Single(c => c.Role == "files");
        Assert.Equal("Here it is", item.Text);
        Assert.Equal(new[] { ("chart.png", true), ("report.xlsx", false) }, item.Files!.Select(f => (f.Name, f.Image)));
        Assert.Equal(Path.Combine(@"C:\work\demo", "out/chart.png"), s.SentFile(item.Files![0].Id));
        Assert.Null(s.SentFile("f999"));
    }

    [Theory]
    [InlineData("mcp__sap__read_table", "sap · read_table", "mcp")]
    [InlineData("mcp__27b7aaec-94b5-47af-bf1f-499dc68bde54__read_file_content", "read_file_content", "mcp")]
    [InlineData("mcp__ccd_turn__update_plan", "update_plan", "plan")]
    [InlineData("PowerShell", "PowerShell", "run")]
    public void Tool_names_are_shortened_and_categorised(string name, string shortName, string category)
    {
        Assert.Equal(shortName, ToolInfo.ShortName(name));
        Assert.Equal(category, ToolInfo.Category(name));
    }
}
