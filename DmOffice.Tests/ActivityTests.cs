using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Register der Hintergrundarbeit und GET /api/activity.</summary>
[Collection("server")]
public class ActivityTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public ActivityTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public void NothingReportedYetIsUnknown() => Assert.Null(Activity.Get("u-none", Activity.Thumbs));

    [Fact]
    public void ProgressAndFinish()
    {
        var job = Activity.Start("u-progress", Activity.FilesBlobs, "start", total: 10);
        job.Progress(4);
        var running = Activity.Get("u-progress", Activity.FilesBlobs)!;
        Assert.Equal(("running", 4L, 10L), (running.State, running.Done, running.Total));
        Assert.NotNull(running.Started);
        Assert.Null(running.Finished);
        job.Dispose();
        job.Dispose(); // zweimal ist harmlos
        var done = Activity.Get("u-progress", Activity.FilesBlobs)!;
        Assert.Equal("idle", done.State);
        Assert.Equal(1, done.Completed);
        Assert.NotNull(done.Finished);
    }

    [Fact]
    public void FailureStaysUntilNextSuccess()
    {
        Activity.Start("u-fail", Activity.MailQueue).Fail("SMTP weg");
        var item = Activity.Get("u-fail", Activity.MailQueue)!;
        Assert.Equal(("error", "SMTP weg"), (item.State, item.Error));
        using (Activity.Start("u-fail", Activity.MailQueue)) { }
        var ok = Activity.Get("u-fail", Activity.MailQueue)!;
        Assert.Equal(("idle", null), (ok.State, ok.Error));
    }

    [Fact]
    public void ParallelWorkCountsTogether()
    {
        var a = Activity.Start("u-par", Activity.Thumbs);
        var b = Activity.Start("u-par", Activity.Thumbs);
        a.Dispose();
        Assert.Equal("running", Activity.Get("u-par", Activity.Thumbs)!.State);
        b.Fail("kaputt");
        var item = Activity.Get("u-par", Activity.Thumbs)!;
        Assert.Equal(("error", 2L), (item.State, item.Completed));
    }

    [Fact]
    public void UsersAreSeparate()
    {
        using var _ = Activity.Start("u-one", Activity.Thumbs);
        Assert.Null(Activity.Get("u-two", Activity.Thumbs));
    }

    private static JsonElement Find(JsonElement root, string id) =>
        root.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == id);

    [Fact]
    public async Task EndpointListsServicesAndRunningWork()
    {
        var c = _fx.Client();
        var idle = await c.GetFromJsonAsync<JsonElement>("/api/activity");
        Assert.Equal("idle", Find(idle, "thumbs").GetProperty("state").GetString());
        Assert.Equal("idle", Find(idle, "files-cleanup").GetProperty("state").GetString());

        using (var job = Activity.Start(_fx.Db.UserId, Activity.FilesBlobs, total: 50))
        {
            job.Progress(20);
            var busy = await c.GetFromJsonAsync<JsonElement>("/api/activity");
            Assert.True(busy.GetProperty("running").GetInt32() >= 1);
            var blobs = Find(busy, "files-blobs");
            Assert.Equal("running", blobs.GetProperty("state").GetString());
            Assert.Equal(20, blobs.GetProperty("done").GetInt32());
            Assert.Equal(50, blobs.GetProperty("total").GetInt32());
        }
        var after = await c.GetFromJsonAsync<JsonElement>("/api/activity");
        var finished = Find(after, "files-blobs");
        Assert.Equal("idle", finished.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, finished.GetProperty("finished").ValueKind);
    }

    [Fact]
    public async Task EndpointNeedsSignIn() =>
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await _fx.Client(signedIn: false).GetAsync("/api/activity")).StatusCode);
}
