using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Anhaenge als Freigabelink: aus der Dateiablage und hochgeladen, mit den Link-Vorgaben des Benutzers.</summary>
public class MailLinksTests
{
    [Fact]
    public async Task Stored_and_uploaded_files_become_links_with_the_users_defaults()
    {
        var dir = Directory.CreateTempSubdirectory("dmmaillinks");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t-" + Guid.NewGuid().ToString("N"));
        var space = new UserSpace(new UserRecord { Id = db.UserId, Login = "t" }, db);
        FileStore.EnsureDir(db, "Dokumente");
        FileStore.Put(db, "Dokumente/plan.pdf", new byte[2048], "application/pdf");
        // Vorgaben unter Einstellungen -> Dateien -> Freigabelinks
        FileRetention.Save(db, FileRetention.Load(db) with { LinkDays = 14, LinkMaxDownloads = 10 });

        var index = new LinkIndex(new DataPaths(dir.FullName));
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(index).BuildServiceProvider() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("office.example.org");

        var upload = new FormFile(new MemoryStream(new byte[3000]), 0, 3000, "file", "foto.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };
        var attached = new FormFile(new MemoryStream(new byte[10]), 0, 10, "file", "bleibt.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };
        var files = new List<IFormFile> { upload, attached };
        var draft = new MailCompose.Draft { LinkPaths = { "Dokumente/plan.pdf" }, LinkUploads = { "foto.jpg" } };

        var html = await MailLinks.CreateAsync(http, space, new MailAccount { Language = "de" }, draft, files, CancellationToken.None);

        Assert.Equal(2, html.Split("https://office.example.org/s/").Length - 1);
        Assert.Contains("plan.pdf", html);
        Assert.Contains("foto.jpg", html);
        Assert.Contains("für 10 Abrufe", html);
        // Hochgeladenes liegt jetzt in der Dateiablage und geht nicht mehr als Anhang mit; der andere Anhang bleibt
        Assert.Equal(new[] { "bleibt.txt" }, files.Select(f => f.FileName));
        Assert.Single(FileStore.Search(db, MailLinks.Folder, "foto.jpg"));
        var links = Links.List(db);
        Assert.Equal(2, links.Count);
        Assert.All(links, l => { Assert.Equal(10, l.MaxDownloads); Assert.NotNull(l.Expires); });
    }

    [Fact]
    public async Task Without_link_wishes_nothing_happens()
    {
        var dir = Directory.CreateTempSubdirectory("dmmaillinks2");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t-" + Guid.NewGuid().ToString("N"));
        var space = new UserSpace(new UserRecord { Id = db.UserId, Login = "t" }, db);
        var html = await MailLinks.CreateAsync(new DefaultHttpContext(), space, new MailAccount(), new MailCompose.Draft(), new List<IFormFile>(), CancellationToken.None);
        Assert.Equal("", html);
    }
}
