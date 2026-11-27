using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>Grenze der angefangenen Uploads: liegen gebliebene Sitzungen (Seite neu geladen) sperren niemanden aus.</summary>
public class UploadSessionLimitTests
{
    private static int? Status(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode;

    private static string? IdOf(IResult r) =>
        JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize((r as IValueHttpResult)?.Value))?["id"]?.GetValue<string>();

    /// <summary>Sitzung so tun lassen, als kaeme seit <paramref name="ago"/> nichts mehr.</summary>
    private static void Age(UserDb db, string id, TimeSpan ago)
    {
        var json = JsonNode.Parse(db.Get("upload.session." + id)!)!;
        var t = DateTime.UtcNow - ago;
        json["created"] = t;
        json["touched"] = t;
        db.Set("upload.session." + id, json.ToJsonString());
    }

    [Fact]
    public void Liegen_gebliebene_Sitzungen_machen_Platz()
    {
        var dir = Directory.CreateTempSubdirectory("dmupl");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var ids = Enumerable.Range(0, FilesApi.MaxOpenPerUser)
            .Select(i => IdOf(FilesApi.StartSession(db, $"f{i}.bin", 100, null))!).ToList();

        // Alle laufen gerade: die naechste wartet (429)
        Assert.Equal(429, Status(FilesApi.StartSession(db, "zuviel.bin", 100, null)));

        // Nach Neuladen nie fortgesetzt: die alten weichen, der neue Upload startet sofort
        foreach (var id in ids.Take(3)) Age(db, id, TimeSpan.FromMinutes(30));
        Assert.Equal(200, Status(FilesApi.StartSession(db, "neu.bin", 100, null)));
        Assert.Null(FilesApi.GetSession(db, ids[0]));
        Assert.NotNull(FilesApi.GetSession(db, ids[1]));   // nur so viele wie noetig

        // Nach Stunden ohne Teil verfallen sie ganz
        Age(db, ids[5], TimeSpan.FromHours(7));
        FilesApi.SweepSessions(db);
        Assert.Null(FilesApi.GetSession(db, ids[5]));
        Assert.NotNull(FilesApi.GetSession(db, ids[6]));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
