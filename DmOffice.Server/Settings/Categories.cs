using System.Text;
using DmOffice.Core;

namespace DmOffice.Settings;

/// <summary>Kategorien fuer Mails: Name und Farbe hier, auf dem Server ein IMAP-Schlagwort.</summary>
public static class Categories
{
    public static readonly List<MailCategory> Defaults = new()
    {
        new() { Key = "dm_wichtig", Name = "Wichtig", Color = "#D13438" },
        new() { Key = "dm_rechnung", Name = "Rechnung", Color = "#CA5010" },
        new() { Key = "dm_spaeter", Name = "Später", Color = "#C19C00" },
        new() { Key = "dm_privat", Name = "Privat", Color = "#13A10E" },
        new() { Key = "dm_arbeit", Name = "Arbeit", Color = "#0F6CBD" },
        new() { Key = "dm_familie", Name = "Familie", Color = "#8764B8" },
    };

    public static List<MailCategory> Of(UserSettings s) => s.Categories ?? Defaults.Select(c => new MailCategory { Key = c.Key, Name = c.Name, Color = c.Color }).ToList();

    public static bool ValidKey(string key) =>
        key.Length is > 3 and <= 60 && (key.StartsWith("dm_") || key.StartsWith("$label")) && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '$');

    /// <summary>Schlagwort aus dem Namen: klein, Umlaute umschrieben, nur a-z0-9_.</summary>
    public static string KeyFor(string name, IEnumerable<string> taken)
    {
        var sb = new StringBuilder("dm_");
        foreach (var ch in name.Trim().ToLowerInvariant())
            sb.Append(ch switch
            {
                'ä' => "ae", 'ö' => "oe", 'ü' => "ue", 'ß' => "ss",
                >= 'a' and <= 'z' or >= '0' and <= '9' => ch.ToString(),
                _ => "_",
            });
        var key = sb.ToString().Trim('_');
        if (key.Length > 40) key = key[..40];
        if (key == "dm") key = "dm_kategorie";
        var set = taken.ToHashSet();
        var result = key;
        for (int i = 2; set.Contains(result); i++) result = key + "_" + i;
        return result;
    }
}
