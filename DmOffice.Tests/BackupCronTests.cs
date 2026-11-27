using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Neuer Zeitplan der Sicherungen (Kennung backupcron): alle N Stunden/Tage/Wochen und eigene Cron-Regel.</summary>
public class BackupCronTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Erster_Montag_im_Monat()
    {
        Assert.True(Cron.TryParse("0 3 * * MON#1", out var c, out var e), e);
        Assert.Equal([new DateTime(2026, 12, 7, 3, 0, 0), new DateTime(2027, 1, 4, 3, 0, 0)], c!.Next(Now, 2));
        // Tag und Wochentag zusammen: auch so „erster Montag“
        Assert.True(Cron.TryParse("0 3 1-7 * 1", out var d, out _));
        Assert.Equal(new DateTime(2026, 12, 7, 3, 0, 0), d!.Next(Now, 1)[0]);
        Assert.Equal(new DateTime(2026, 11, 2, 3, 0, 0), d.Previous(Now));
    }

    [Theory]
    [InlineData("0 3 * *")]
    [InlineData("61 3 * * *")]
    [InlineData("0 3 * * MON#6")]
    [InlineData("0 5-3 * * *")]
    public void Ungueltige_Regeln_mit_Grund(string text)
    {
        Assert.False(Cron.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Alle_zwei_Tage_und_jede_zweite_Woche()
    {
        var everyTwoDays = new ServerSettings { S3BackupScheduleKind = BackupSchedule.KindEvery, S3BackupEveryCount = 2, S3BackupEveryUnit = "d", S3BackupAt = "03:00" };
        var next = BackupSchedule.Next(everyTwoDays, Now, 3);
        Assert.Equal(TimeSpan.FromDays(2), next[1] - next[0]);
        Assert.All(next, t => Assert.Equal(new TimeSpan(3, 0, 0), t.TimeOfDay));

        var everyOtherFriday = new ServerSettings { S3BackupScheduleKind = BackupSchedule.KindEvery, S3BackupEveryCount = 2, S3BackupEveryUnit = "w", S3BackupWeekday = 5, S3BackupAt = "22:30" };
        var fridays = BackupSchedule.Next(everyOtherFriday, Now, 3);
        Assert.All(fridays, t => Assert.Equal(DayOfWeek.Friday, t.DayOfWeek));
        Assert.Equal(TimeSpan.FromDays(14), fridays[1] - fridays[0]);
    }

    [Fact]
    public void Faellig_nur_wenn_seit_dem_letzten_Zeitpunkt_nicht_gesichert()
    {
        var daily = new ServerSettings { S3BackupScheduleKind = BackupSchedule.KindCron, S3BackupCron = "30 2 * * *" };
        var slot = new DateTime(2026, 11, 13, 2, 30, 0, DateTimeKind.Local);
        Assert.True(BackupSchedule.Due(daily, Now, slot.AddHours(-1).ToUniversalTime()));
        Assert.False(BackupSchedule.Due(daily, Now, slot.AddMinutes(5).ToUniversalTime()));
        Assert.True(BackupSchedule.Due(daily, Now, null));
    }

    [Fact]
    public void Alle_sechs_Stunden_ab_Uhrzeit()
    {
        var s = new ServerSettings { S3BackupScheduleKind = BackupSchedule.KindEvery, S3BackupEveryCount = 6, S3BackupEveryUnit = "h", S3BackupAt = "03:00" };
        Assert.Equal(new DateTime(2026, 11, 13, 9, 0, 0), BackupSchedule.Previous(s, Now));
        Assert.Equal(new DateTime(2026, 11, 13, 15, 0, 0), BackupSchedule.Next(s, Now, 1)[0]);
    }
}
