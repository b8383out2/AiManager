using System.Net;
using System.Text.Json;

namespace AiManager.Tests;

public class ImageTests
{
    const string Sid = "22222222-3333-4444-5555-666666666666";

    // A 1×1 PNG.
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    static string PastedImage(int second) => JsonSerializer.Serialize(new
    {
        type = "user", timestamp = Rec.At(second), origin = new { kind = "human" }, promptSource = "sdk",
        message = new
        {
            role = "user",
            content = new object[]
            {
                new { type = "image", source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(Png) } },
                new { type = "text", text = "这个报错是什么意思？" },
            },
        },
    });

    [Fact]
    public async Task A_pasted_image_is_served_back_from_the_transcript_with_the_token_only()
    {
        await using var h = await TestHost.StartAsync();
        h.Write(Sid, Rec.Human("first", 0), Rec.Say("ok", 1), PastedImage(2));
        h.Host.Store.CatchUp(Sid, h.Transcript(Sid));

        var msg = h.Host.Store.BuildDetail(Sid)!.Conversation.Single(c => c.Images != null);
        Assert.Equal("这个报错是什么意思？", msg.Text);
        var id = Assert.Single(msg.Images!);

        var url = $"{h.Host.Url}api/image?key={Sid}&id={id}&t={h.Host.Config.Token}";
        var resp = await h.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await resp.Content.ReadAsByteArrayAsync());

        var noToken = await h.Http.GetAsync($"{h.Host.Url}api/image?key={Sid}&id={id}&t=wrong");
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        var notAnImage = await h.Http.GetAsync($"{h.Host.Url}api/image?key={Sid}&id=u0.0&t={h.Host.Config.Token}");
        Assert.Equal(HttpStatusCode.NotFound, notAnImage.StatusCode);   // the first record is text, not an image
    }

    [Fact]
    public async Task An_image_file_the_session_sent_is_served_only_by_its_registered_id()
    {
        await using var h = await TestHost.StartAsync();
        var work = Path.Combine(h.Root, "work");
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, "chart.png"), Png);
        h.Write(Sid,
            JsonSerializer.Serialize(new { type = "user", timestamp = Rec.At(0), cwd = work, origin = new { kind = "human" }, promptSource = "sdk", message = new { role = "user", content = "chart please" } }),
            Rec.Tool("SendUserFile", new { files = new[] { "chart.png" }, caption = "the chart" }, "f1", 1));
        h.Host.Store.CatchUp(Sid, h.Transcript(Sid));

        var file = h.Host.Store.BuildDetail(Sid)!.Conversation.Single(c => c.Role == "files").Files!.Single();
        Assert.True(file.Image);
        var resp = await h.Http.GetAsync($"{h.Host.Url}api/file?key={Sid}&id={file.Id}&t={h.Host.Config.Token}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Png, await resp.Content.ReadAsByteArrayAsync());

        var unknown = await h.Http.GetAsync($"{h.Host.Url}api/file?key={Sid}&id=f42&t={h.Host.Config.Token}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
