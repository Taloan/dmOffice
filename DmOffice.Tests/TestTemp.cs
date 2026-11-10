using System.Runtime.CompilerServices;

namespace DmOffice.Tests;

/// <summary>
/// Temporaere Ordner der Tests: auf Wunsch auf einem anderen Laufwerk (DMOFFICE_TEST_TMP, z. B. D:\DevTemp\dmoffice-tests)
/// und beim Start werden Reste frueherer Laeufe weggeraeumt - viele Fixtures legen Ordner an, ohne sie zu loeschen,
/// und tausende davon fuellten sonst die Platte.
/// </summary>
internal static class TestTemp
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Release-Liste: Tests prüfen alle Funktionen, auch die erst in späteren Versionen freigeschalteten (eigener Test: FeaturesTests)
        Environment.SetEnvironmentVariable("DMOFFICE_FEATURES", "all");
        // Audiothek: in Tests ohne Pause zwischen den Stücken
        DmOffice.Audio.AudioFolders.TagPause = TimeSpan.Zero;
        if (Environment.GetEnvironmentVariable("DMOFFICE_TEST_TMP") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("TMP", dir);
            Environment.SetEnvironmentVariable("TEMP", dir);
            Environment.SetEnvironmentVariable("TMPDIR", dir);
        }
        // Reste aelter als zwei Stunden: kein Lauf dauert so lange, laufende Tests (auch anderer Sitzungen) bleiben unberuehrt
        var cut = DateTime.Now.AddHours(-2);
        try
        {
            foreach (var d in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("dm*"))
                if (d.LastWriteTime < cut && d.Name.Length > 2 && char.IsAsciiLetterLower(d.Name[2]))
                    try { d.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }
}
