using DmOffice.Core;

namespace DmOffice.Mail;

// [feature:phishtrust]
/// <summary>
/// „Kein Betrug“ an der Phishing-Warnung: Die Warnung bleibt für diese eine Mail ausgeblendet, auch wenn sich der Absender nicht
/// als sicher eintragen lässt (Herkunft nicht bestätigt, z. B. nach einer Weiterleitung). Gemerkt über die Message-ID wie
/// Virenfunde (MailVirus.KeyOf); ein Virenfund bleibt immer sichtbar.
/// </summary>
public static class PhishDismissed
{
    private static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS mail_phish_ok(message_id TEXT PRIMARY KEY, at TEXT NOT NULL)");

    public static void Mark(UserDb db, string key)
    {
        Ensure(db);
        db.Exec("INSERT OR REPLACE INTO mail_phish_ok(message_id, at) VALUES($k, $t)", ("$k", key), ("$t", DateTime.UtcNow.ToString("o")));
    }

    public static bool Of(UserDb db, string key)
    {
        Ensure(db);
        return db.Scalar("SELECT 1 FROM mail_phish_ok WHERE message_id = $k", ("$k", key)) != null;
    }

    /// <summary>Warnungen ohne die ausgeblendeten; Virenfunde bleiben.</summary>
    public static List<PhishCheck.Finding>? Apply(UserDb db, string key, List<PhishCheck.Finding>? found)
    {
        if (found is not { Count: > 0 } || !Of(db, key)) return found;
        var rest = found.Where(f => f.Kind == "virus").ToList();
        return rest.Count > 0 ? rest : null;
    }
}
// [/feature:phishtrust]
