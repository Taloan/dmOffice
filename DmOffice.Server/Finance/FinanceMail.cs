using System.Globalization;
using System.Text;
using DmOffice.Core;

namespace DmOffice.Finance;

/// <summary>Betreff und Text für eine Belegart, dazu Anhänge aus dem DMS: Rollen (etwa „AGB“) und feste Dokumente.</summary>
public sealed class FinMailTemplate
{
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Kennungen von DMS-Rollen: angehängt wird das Dokument, das die Rolle innehat (je Vorgang festgehalten).</summary>
    public List<string> Roles { get; set; } = [];
    /// <summary>Feste Dokumente aus der Ablage des DMS.</summary>
    public List<long> Docs { get; set; } = [];
}

/// <summary>
/// Belege per E-Mail: Aus Vorlage, Kunde und Beleg entsteht ein Mail-Entwurf (Empfänger, Betreff, Text mit gefüllten Platzhaltern,
/// Anhänge), den die Oberfläche im Fenster „Neue E-Mail“ öffnet. Gesendet wird erst dort.
/// Je Beleg wird festgehalten, welches Dokument für jede Rolle mitging; ein Folgebeleg (Auftrag aus Angebot, Rechnung aus Auftrag)
/// übernimmt das seines direkten Vorgängers. Hat die Rolle inzwischen ein anderes Dokument, entscheidet die Person.
/// </summary>
public static class FinanceMail
{
    public static readonly string[] Placeholders = ["{Nummer}", "{Datum}", "{Fällig}", "{Betrag}", "{Kunde}", "{Betreff}", "{Firma}"];

    /// <summary>Vorgaben, solange in den Einstellungen nichts steht.</summary>
    public static FinMailTemplate Default(string kind) => kind switch
    {
        FinanceStore.Offer => new() { Subject = "Angebot {Nummer}", Body = "Guten Tag,\n\nanbei erhalten Sie unser Angebot {Nummer} vom {Datum} über {Betrag}.\n\nMit freundlichen Grüßen\n{Firma}" },
        FinanceStore.Order => new() { Subject = "Auftragsbestätigung {Nummer}", Body = "Guten Tag,\n\nvielen Dank für Ihren Auftrag. Anbei erhalten Sie die Auftragsbestätigung {Nummer} vom {Datum}.\n\nMit freundlichen Grüßen\n{Firma}" },
        _ => new() { Subject = "Rechnung {Nummer}", Body = "Guten Tag,\n\nanbei erhalten Sie die Rechnung {Nummer} vom {Datum} über {Betrag}, zahlbar bis {Fällig}.\n\nMit freundlichen Grüßen\n{Firma}" },
    };

    public static FinMailTemplate TemplateOf(FinanceSettings s, string kind) =>
        s.MailTemplates.TryGetValue(kind is FinanceStore.Offer or FinanceStore.Order ? kind : FinanceStore.Invoice, out var t) ? t : Default(kind);

    public sealed record DocRef(long Id, bool Tresor, string Title, string Name, string? Mime);
    /// <summary>Anhang für eine Rolle: das Dokument aus dem Vorgang (Original) und das, das die Rolle jetzt hat.</summary>
    public sealed record RoleAttachment(string Role, string RoleName, DocRef? Original, DocRef? Current);
    public sealed record Draft(string? Account, string? ReplyTo, List<string> To, string Subject, string Html, string PdfName, List<RoleAttachment> Roles, List<DocRef> Docs);

    private static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS fin_doc_attach (doc INTEGER NOT NULL, role TEXT NOT NULL, dms_doc INTEGER NOT NULL, tresor INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(doc, role)) WITHOUT ROWID");

    /// <summary>Platzhalter füllen; unbekannte bleiben stehen.</summary>
    public static string Fill(string text, FinDoc d, FinanceSettings s)
    {
        static string Day(string? iso) => DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x)
            ? x.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
        return text.Replace("{Nummer}", d.Number ?? "").Replace("{Datum}", Day(d.DocDate)).Replace("{Fällig}", Day(d.DueDate))
            .Replace("{Betrag}", FinancePdf.Money(d.TotalCents)).Replace("{Kunde}", d.Recipient.Split('\n')[0].Trim())
            .Replace("{Betreff}", d.Subject).Replace("{Firma}", s.Name);
    }

    /// <summary>Text der Vorlage als HTML für den Editor: jede Zeile bleibt, auch Leerzeilen; Umlaute bleiben lesbar.</summary>
    public static string ToHtml(string text) => string.Join("<br>", text.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")));

    private static DocRef? Ref(UserSpace space, Func<Dms.DmsStore>? tresor, long id, bool inTresor)
    {
        try
        {
            Dms.DmsStore store = inTresor ? (tresor ?? throw new InvalidOperationException())() : space.Db;
            var d = Dms.Dms.Get(store, id);
            if (d.Trashed) return null;
            return new DocRef(id, inTresor, d.Title, d.OriginalName, d.Mime);
        }
        // Tresor zu: Kennung bleibt, Titel unbekannt
        catch (Exception e) when (inTresor && e is not KeyNotFoundException) { return new DocRef(id, true, "", "", null); }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>Für diese Rolle festgehaltenes Dokument: am Beleg selbst, sonst beim nächsten Vorgänger, der eins hat.</summary>
    private static (long Doc, bool Tresor)? Kept(UserDb db, FinDoc d, string role)
    {
        var seen = new HashSet<long>();
        for (FinDoc? x = d; x != null && seen.Add(x.Id); x = x.RefId is { } r ? TryDoc(db, r) : null)
        {
            var hit = db.Query("SELECT dms_doc, tresor FROM fin_doc_attach WHERE doc = $d AND role = $r", r => (r.GetInt64(0), r.GetInt64(1) != 0),
                ("$d", x.Id), ("$r", role)).FirstOrDefault();
            if (hit != default) return hit;
        }
        return null;
    }

    private static FinDoc? TryDoc(UserDb db, long id) { try { return FinanceStore.Doc(db, id); } catch (KeyNotFoundException) { return null; } }

    public static Draft DraftOf(UserSpace space, long id, Func<Dms.DmsStore>? tresor)
    {
        var db = space.Db;
        FinanceStore.Ensure(db);
        Ensure(db);
        var d = FinanceStore.Doc(db, id);
        if (d.State != FinanceStore.IssuedState) throw new ArgumentException("Per E-Mail senden lässt sich nur ein ausgestellter Beleg.");
        var s = FinanceStore.Settings(db);
        var tpl = TemplateOf(s, d.Kind);
        var accounts = space.Settings.MailAccounts;
        var account = accounts.FirstOrDefault(a => a.Id == s.MailAccount) ?? accounts.FirstOrDefault();
        var to = d.Customer is { } c && FinanceStore.Customer(db, c).Email is { Length: > 0 } mail ? [mail] : new List<string>();
        var holders = Dms.Dms.Roles(db);
        var roleNames = Dms.Dms.Entries(db, Dms.Dms.Role).ToDictionary(e => e.Id, e => e.Name);
        var roles = new List<RoleAttachment>();
        foreach (var role in tpl.Roles.Distinct())
        {
            if (!roleNames.TryGetValue(role, out var name)) continue;
            var current = holders.TryGetValue(role, out var h) ? Ref(space, tresor, h.Doc, h.Tresor) : null;
            var kept = Kept(db, d, role);
            var original = kept is { } k ? Ref(space, tresor, k.Doc, k.Tresor) : null;
            roles.Add(new RoleAttachment(role, name, original, current));
        }
        var docs = tpl.Docs.Distinct().Select(x => Ref(space, tresor, x, false)).OfType<DocRef>().ToList();
        return new Draft(account?.Id, string.IsNullOrWhiteSpace(s.MailReplyTo) ? null : s.MailReplyTo.Trim(), to, Fill(tpl.Subject, d, s), ToHtml(Fill(tpl.Body, d, s)),
            (d.Number ?? "Beleg") + ".pdf", roles, docs);
    }

    public sealed record KeptRole(string Role, long Doc, bool Tresor);

    /// <summary>Festhalten, welches Dokument je Rolle mit diesem Beleg ging (beim Öffnen des Entwurfs).</summary>
    public static void Keep(UserDb db, long id, List<KeptRole> roles)
    {
        FinanceStore.Ensure(db);
        Ensure(db);
        FinanceStore.Doc(db, id);
        foreach (var r in roles)
            db.Exec("INSERT INTO fin_doc_attach(doc, role, dms_doc, tresor) VALUES($d,$r,$m,$t) ON CONFLICT(doc, role) DO UPDATE SET dms_doc = $m, tresor = $t",
                ("$d", id), ("$r", r.Role), ("$m", r.Doc), ("$t", r.Tresor ? 1 : 0));
    }
}
