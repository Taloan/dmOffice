using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Jobs anhalten, fortsetzen, überspringen, abbrechen (jobcontrol).</summary>
[Collection("server")]
public class JobControlTests : IClassFixture<ChatFixture>, IDisposable
{
    private readonly ChatFixture _fx;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dmjobs");

    public JobControlTests(ChatFixture fx) { _fx = fx; JobControl.Reset(); GalleryList.Grace = TimeSpan.Zero; GalleryTimeline.Grace = TimeSpan.Zero; }

    public void Dispose()
    {
        JobControl.Reset();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public async Task Anhalten_Ueberspringen_Abbrechen_und_ueber_einen_Neustart()
    {
        JobControl.Init(_dir.FullName);
        const string u = "u1", g = Activity.GalleryScan;
        Assert.False(JobControl.Blocked(u, g));
        // Abbrechen nur, wenn er läuft; gilt nur für diesen Lauf
        Assert.False(JobControl.Cancel(u, g));
        using (JobControl.Run(u, g))
        {
            Assert.True(JobControl.Cancel(u, g));
            Assert.Throws<JobControl.Stopped>(() => JobControl.Check(u, g));
        }
        JobControl.Check(u, g);
        Assert.False(JobControl.Blocked(u, g));

        // Anhalten: laufender Lauf endet an der Prüfstelle, neue beginnen nicht - auch nach einem Neustart
        JobControl.Pause(u, g);
        Assert.True(JobControl.Blocked(u, g));
        await Assert.ThrowsAsync<JobControl.Stopped>(() => JobControl.CheckAsync(u, g, CancellationToken.None));
        JobControl.Reset();
        JobControl.Init(_dir.FullName);
        Assert.True(JobControl.Paused(u, g));
        var resumed = new List<string>();
        JobControl.Resumed += (user, kind) => resumed.Add(user + "|" + kind);
        JobControl.Resume(u, g);
        Assert.False(JobControl.Blocked(u, g));
        Assert.Contains(u + "|" + g, resumed);

        // Überspringen bis zum nächsten Termin; ein ausdrücklicher Auftrag hebt es auf, das Anhalten nicht
        JobControl.Skip(u, g, DateTime.UtcNow.AddMinutes(15));
        Assert.NotNull(JobControl.SkippedUntil(u, g));
        JobControl.ClearSkip(u, g);
        Assert.False(JobControl.Blocked(u, g));
        JobControl.Skip(u, g, DateTime.UtcNow.AddSeconds(-1));
        Assert.False(JobControl.Blocked(u, g));

        // Übertragung (ohne Zeitplan): Anhalten wartet an der Prüfstelle bis „Fortsetzen“; Überspringen = Abbrechen
        const string t = Activity.FilesTransfer;
        using (JobControl.Run(u, t))
        {
            JobControl.Pause(u, t);
            var wait = JobControl.CheckAsync(u, t, CancellationToken.None);
            await Task.Delay(1500);
            Assert.False(wait.IsCompleted);
            JobControl.Resume(u, t);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            JobControl.Skip(u, t, DateTime.UtcNow.AddHours(1));
            Assert.Null(JobControl.SkippedUntil(u, t));
            await Assert.ThrowsAsync<JobControl.Stopped>(() => JobControl.CheckAsync(u, t, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Galerie_angehalten_liest_nichts_bis_Fortsetzen_Steuern_ueber_die_Aktivitaet()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var dora = _fx.Space("dora");
        var root = Directory.CreateTempSubdirectory("dmjobsgal").FullName;
        var before = config.Current.ExternalFolders;
        var budget = GalleryScanJob.StageBudget;
        try
        {
            for (var i = 0; i < 3; i++)
                File.WriteAllBytes(Path.Combine(root, $"foto{i}.jpg"), XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "jobs01", Name = "Fotos", Path = root }]);
            var c = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "jobs01", path = "", watch = false })).StatusCode);
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            GalleryScanJob.StageBudget = TimeSpan.Zero;
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            Assert.Equal(3, GalleryFolders.Stages(dora.Db, src.Id).Listed);

            // Über die Aktivität anhalten: Zustand „paused“, nichts wird gelesen
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/activity/control", new { kind = Activity.GalleryScan, action = "pause" })).StatusCode);
            var items = (await c.GetFromJsonAsync<JsonElement>("/api/activity")).GetProperty("items").EnumerateArray().ToList();
            var item = items.Single(i => i.GetProperty("id").GetString() == Activity.GalleryScan);
            Assert.Equal("paused", item.GetProperty("hold").GetString());
            Assert.True(item.GetProperty("control").GetBoolean());
            GalleryScanJob.StageBudget = TimeSpan.FromMinutes(1);
            await job.RunOnceAsync(CancellationToken.None);
            Assert.Equal(3, GalleryFolders.Stages(dora.Db, src.Id).Listed);

            // Suche in Server-Mounts gehört dem Server: nur Admins
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/activity/control", new { kind = Activity.MountIndex, action = "pause" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/activity/control", new { kind = Activity.GalleryScan, action = "kaputt" })).StatusCode);

            // Fortsetzen: liest weiter
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/activity/control", new { kind = Activity.GalleryScan, action = "resume" })).StatusCode);
            await job.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, GalleryFolders.Stages(dora.Db, src.Id).Listed);

            // Überspringen: ruht bis zum nächsten Termin (Überwachen-Abstand)
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/activity/control", new { kind = Activity.GalleryScan, action = "skip" })).StatusCode);
            var until = JobControl.SkippedUntil(dora.User.Id, Activity.GalleryScan);
            Assert.NotNull(until);
            Assert.InRange(until!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            GalleryScanJob.StageBudget = budget;
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
