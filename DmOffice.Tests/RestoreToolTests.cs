using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Bedienung von „restore“: benutzer:/pfad wie rclone und Wahl des Stands nach Datum.</summary>
public class RestoreToolTests
{
    [Theory]
    [InlineData("anna:/Verträge/x.pdf", "anna", "/Verträge/x.pdf")]
    [InlineData("anna:", "anna", "")]
    [InlineData("anna:Verträge", "anna", "Verträge")]
    public void Benutzer_und_Pfad(string arg, string user, string path) => Assert.Equal((user, path), RestoreTool.UserPath(arg));

    [Theory]
    [InlineData("werkzeug/probe.txt")]
    [InlineData(@"C:\out")]
    [InlineData("C:/out")]
    [InlineData(":ohne")]
    public void Kein_Benutzer(string arg) => Assert.Null(RestoreTool.UserPath(arg));

    [Fact]
    public void Stand_nach_Datum()
    {
        var a = new S3Backup.Stand("k1", "a", new DateTime(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc), 1);
        var b = new S3Backup.Stand("k2", "b", new DateTime(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc), 1);
        var c = new S3Backup.Stand("k3", "c", new DateTime(2026, 11, 6, 2, 0, 0, DateTimeKind.Utc), 1);
        List<S3Backup.Stand> all = [a, b, c];
        Assert.Equal("c", RestoreTool.Pick(all, null)!.Name);
        Assert.Equal("b", RestoreTool.Pick(all, "2026-11-05")!.Name);
        Assert.Equal("b", RestoreTool.Pick(all, "2026-11-04")!.Name);
        Assert.Null(RestoreTool.Pick(all, "2026-11-01"));
        Assert.Throws<ArgumentException>(() => RestoreTool.Pick(all, "gestern"));
    }
}
