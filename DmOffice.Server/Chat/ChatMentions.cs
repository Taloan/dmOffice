namespace DmOffice.Chat;

/// <summary>
/// @Erwaehnungen (Chat-Komfort): wer in einem Text gemeint ist - „@Anzeigename“, „@Vorname“, „@login“ oder „@alle“.
/// Erwaehnte bekommen auch in stumm geschalteten Unterhaltungen Bescheid.
/// </summary>
public static class ChatMentions
{
    private static readonly string[] All = ["alle", "all", "everyone"];

    /// <param name="members">Mitglieder mit ihren Namen (Anzeigename, Vorname, Anmeldename)</param>
    public static List<string> Find(string? text, IEnumerable<(string Id, string?[] Names)> members)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@')) return [];
        var list = members.ToList();
        if (All.Any(a => Has(text, a))) return list.Select(m => m.Id).ToList();
        return list.Where(m => m.Names.Any(n => !string.IsNullOrWhiteSpace(n) && Has(text, n!.Trim()))).Select(m => m.Id).ToList();
    }

    /// <summary>„@name“ als eigenes Wort: davor kein Buchstabe (keine Mail-Adresse), danach auch keiner.</summary>
    public static bool Has(string text, string name)
    {
        for (int i = text.IndexOf('@'); i >= 0; i = text.IndexOf('@', i + 1))
        {
            if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue;
            var end = i + 1 + name.Length;
            if (end > text.Length || string.Compare(text, i + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            if (end == text.Length || !char.IsLetterOrDigit(text[end])) return true;
        }
        return false;
    }
}
