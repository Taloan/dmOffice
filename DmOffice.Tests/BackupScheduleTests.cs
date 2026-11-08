using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Core-Backup: wann es fällig ist (stündlich bis wöchentlich, ab der eingestellten Uhrzeit).</summary>
public class BackupScheduleTests
{
    private static ServerSettings Every(int hours) => new() { S3BackupAt = "03:00", S3BackupEveryHours = hours };
    private static DateTime Local(int day, int hour, int minute = 0) => new(2030, 5, day, hour, minute, 0, DateTimeKind.Local);
    private static DateTime? Ran(int day, int hour, int minute = 0) => Local(day, hour, minute).ToUniversalTime();

    [Fact]
    public void Taeglich_nach_der_Uhrzeit_einmal()
    {
        Assert.True(S3Backup.Due(Every(24), Local(6, 3, 5), null));
        Assert.True(S3Backup.Due(Every(24), Local(6, 3, 5), Ran(5, 3, 1)));
        Assert.False(S3Backup.Due(Every(24), Local(6, 2, 59), Ran(5, 3, 1)));
        Assert.False(S3Backup.Due(Every(24), Local(6, 20, 0), Ran(6, 3, 1)));
    }

    [Fact]
    public void Alle_sechs_Stunden_ab_drei_Uhr()
    {
        Assert.True(S3Backup.Due(Every(6), Local(6, 9, 1), Ran(6, 3, 2)));
        Assert.False(S3Backup.Due(Every(6), Local(6, 8, 59), Ran(6, 3, 2)));
        Assert.False(S3Backup.Due(Every(6), Local(6, 10, 0), Ran(6, 9, 2)));
        // Nach Mitternacht vor drei Uhr: der Zeitpunkt 21:00 vom Vortag zählt
        Assert.True(S3Backup.Due(Every(6), Local(7, 1, 0), Ran(6, 15, 2)));
    }

    [Fact]
    public void Woechentlich()
    {
        Assert.False(S3Backup.Due(Every(168), Local(9, 4, 0), Ran(6, 3, 1)));
        Assert.True(S3Backup.Due(Every(168), Local(13, 3, 5), Ran(6, 3, 1)));
    }
}
