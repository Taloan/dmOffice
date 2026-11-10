using System.Text.RegularExpressions;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Dms;

/// <summary>Ein Mailordner als Quelle (wie die E-Mail-Regeln von paperless, aber mit den Konten, die dmOffice ohnehin hat).</summary>
public sealed class DmsMailSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Account { get; set; } = "";
    public string Folder { get; set; } = "INBOX";
    /// <summary>Nur Mails der letzten so vielen Tage.</summary>
    public int MaxAgeDays { get; set; } = 30;
    public bool Active { get; set; } = true;
    /// <summary>Reihenfolge (kleinere zuerst); Stop: hat diese Quelle eine Mail verarbeitet, kommen die weiteren nicht mehr dran.</summary>
    public int Order { get; set; } = 100;
    public bool Stop { get; set; }
    // Filter: alle angegebenen müssen passen (enthält, ohne Groß-/Kleinschreibung)
    public string? FilterFrom { get; set; }
    public string? FilterTo { get; set; }
    public string? FilterSubject { get; set; }
    public string? FilterBody { get; set; }
    /// <summary>attachments (nur Anhänge), mail (ganze Mail), both.</summary>
    public string Scope { get; set; } = "attachments";
    /// <summary>Auch eingebettete Teile (etwa Bilder im Text) als Anhang nehmen.</summary>
    public bool AllParts { get; set; }
    /// <summary>Dateimuster der Anhänge, durch Komma getrennt, „*“ als Platzhalter (z. B. „*.pdf, *rechnung*“).</summary>
    public string? Include { get; set; }
    public string? Exclude { get; set; }
    /// <summary>Danach mit der Mail: none, seen, flag, move, delete.</summary>
    public string Action { get; set; } = "none";
    public string? ActionFolder { get; set; }
    /// <summary>Titel der Anhänge: filename oder subject (die ganze Mail hat immer den Betreff).</summary>
    public string TitleFrom { get; set; } = "filename";
    /// <summary>Korrespondent: none (einordnen wie sonst), preset (Vorgabe), sender (aus dem Absender; neu angelegt, wenn es ihn nicht gibt).</summary>
    public string CorrespondentMode { get; set; } = "none";
    public string? Correspondent { get; set; }
    public string? Doctype { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Profile { get; set; }
    public int Every { get; set; } = 10;
    public DateTime? LastRun { get; set; }
    public string? Error { get; set; }
    public int LastCount { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Mailordner als Quelle vom DMS (docs/dms-konzept.md, „Quellen“ Punkt 4). Liest Mails über <see cref="IDmsMail"/>, prüft die Filter,
/// legt Anhänge und/oder die ganze Mail ab und führt die Aktion danach aus. Verarbeitete Mails merkt sich die Quelle (dms_mail_seen,
/// Konto + Message-ID); beim Ändern einer Quelle wird das vergessen, damit geänderte Filter wieder greifen.
/// </summary>
public static class DmsMailSources
{
    public const string Key = "dms.mailsources";
    public static int MaxPerRun { get; set; } = 50;
    private static readonly object SaveLock = new();
    private static readonly string[] Scopes = ["attachments", "mail", "both"], Actions = ["none", "seen", "flag", "move", "delete"];

    public static List<DmsMailSource> Of(UserDb db) => db.GetJson<List<DmsMailSource>>(Key);

    private static void Ensure(UserDb db)
    {
        Dms.Ensure(db);
        db.Exec("CREATE TABLE IF NOT EXISTS dms_mail_seen (source TEXT NOT NULL, mail TEXT NOT NULL, PRIMARY KEY(source, mail)) WITHOUT ROWID");
    }

    internal static void Update(UserDb db, string id, Action<DmsMailSource> change)
    {
        lock (SaveLock)
        {
            var list = Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } s) return;
            change(s);
            db.SetJson(Key, list);
        }
    }

    public static DmsMailSource Save(UserSpace space, DmsMailSource s, IDmsMail mail)
    {
        s.Name = (s.Name ?? "").Trim();
        if (s.Name.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (mail.Accounts(space).All(a => a.Id != s.Account)) throw new KeyNotFoundException("Dieses Mailkonto gibt es nicht.");
        if (string.IsNullOrWhiteSpace(s.Folder)) throw new ArgumentException("Bitte einen Ordner wählen.");
        if (!Scopes.Contains(s.Scope)) throw new ArgumentException("Unbekannter Umfang.");
        if (!Actions.Contains(s.Action)) throw new ArgumentException("Unbekannte Aktion.");
        if (s.Action == "move" && string.IsNullOrWhiteSpace(s.ActionFolder)) throw new ArgumentException("Bitte den Ordner angeben, in den die Mail verschoben wird.");
        if (s.Action == "move" && s.ActionFolder == s.Folder) throw new ArgumentException("Der Zielordner ist der überwachte Ordner.");
        if (s.CorrespondentMode is not ("none" or "preset" or "sender")) s.CorrespondentMode = "none";
        s.MaxAgeDays = Math.Clamp(s.MaxAgeDays, 1, 3650);
        s.Every = Math.Clamp(s.Every, 1, 24 * 60);
        s.Profile = (DmsFiling.Find(space, s.Profile) ?? DmsFiling.Default(space)).Id;
        Ensure(space.Db);
        lock (SaveLock)
        {
            var list = Of(space.Db);
            var i = list.FindIndex(x => x.Id == s.Id);
            if (i >= 0) { (s.LastRun, s.Error, s.LastCount, s.Total) = (list[i].LastRun, list[i].Error, list[i].LastCount, list[i].Total); list[i] = s; }
            else list.Add(s);
            space.Db.SetJson(Key, list);
        }
        // Geänderte Filter sollen wieder greifen: Gemerktes dieser Quelle vergessen (schon Abgelegtes erkennt das DMS am Inhalt)
        space.Db.Exec("DELETE FROM dms_mail_seen WHERE source = $s", ("$s", s.Id));
        return s;
    }

    public static void Delete(UserDb db, string id)
    {
        lock (SaveLock)
        {
            var list = Of(db);
            if (list.RemoveAll(s => s.Id == id) == 0) throw new KeyNotFoundException("Quelle nicht gefunden.");
            db.SetJson(Key, list);
        }
        Ensure(db);
        db.Exec("DELETE FROM dms_mail_seen WHERE source = $s", ("$s", id));
    }

    // ---------------- Prüfen ----------------

    /// <summary>„*.pdf, *rechnung*“: passt ein Name auf eines der Muster? Leeres Muster: null (keine Vorgabe).</summary>
    internal static bool? Matches(string? patterns, string name)
    {
        var list = (patterns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0) return null;
        return list.Any(p => Regex.IsMatch(name, "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
    }

    private static bool Contains(string? filter, string value) =>
        string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Passen die Filter der Quelle auf die Mail?</summary>
    internal static bool Fits(DmsMailSource s, MimeMessage m, Func<string, string> htmlToText) =>
        Contains(s.FilterFrom, m.From.ToString())
        && Contains(s.FilterTo, string.Join(", ", m.To.ToString(), m.Cc.ToString()))
        && Contains(s.FilterSubject, m.Subject ?? "")
        && (string.IsNullOrWhiteSpace(s.FilterBody) || Contains(s.FilterBody, m.TextBody ?? (m.HtmlBody is { } h ? htmlToText(h) : "")));

    /// <summary>Die Anhänge, die die Quelle nimmt (Dateimuster einbeziehen bzw. ausschließen).</summary>
    internal static List<(string Name, string? Mime, byte[] Data)> Parts(DmsMailSource s, MimeMessage m)
    {
        IEnumerable<MimePart> parts = s.AllParts
            ? m.BodyParts.OfType<MimePart>().Where(p => !(p is TextPart tp && !p.IsAttachment && (tp.IsHtml ? tp.Text == m.HtmlBody : tp.Text == m.TextBody)))
            : m.Attachments.OfType<MimePart>();
        var result = new List<(string, string?, byte[])>();
        var n = 0;
        foreach (var p in parts)
        {
            n++;
            var name = p.FileName ?? p.ContentType.Name ?? $"Anhang {n}";
            if (Matches(s.Include, name) == false || Matches(s.Exclude, name) == true) continue;
            using var ms = new MemoryStream();
            p.Content?.DecodeTo(ms);
            if (ms.Length == 0) continue;
            result.Add((name, p.ContentType.MimeType, ms.ToArray()));
        }
        return result;
    }

    /// <summary>Korrespondent aus dem Absender: Eintrag mit Zuweisung „Absender“ für die Adresse oder gleichen Namens, sonst neu.</summary>
    internal static string SenderCorrespondent(UserDb db, MailboxAddress from)
    {
        var all = Dms.Entries(db, Dms.Correspondent);
        var address = from.Address.Trim();
        var byRule = all.FirstOrDefault(e => e.Match == Dms.Sender && DmsAssign.Matches(e, "", address));
        if (byRule != null) return byRule.Id;
        var name = string.IsNullOrWhiteSpace(from.Name) ? address : from.Name.Trim();
        var byName = all.FirstOrDefault(e => e.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (byName != null) return byName.Id;
        return Dms.Create(db, Dms.Correspondent, name.Length > 120 ? name[..120] : name, null, null, Dms.Sender, address, false).Id;
    }

    // ---------------- Lauf ----------------

    /// <summary>Alle fälligen (oder angestoßenen) Mailordner-Quellen einer Person, in ihrer Reihenfolge.</summary>
    public static async Task<int> RunDueAsync(UserSpace space, IDmsMail mail, Func<string, string> htmlToText, ISet<string> wanted, CancellationToken ct)
    {
        var db = space.Db;
        var list = Of(db).OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (list.Count == 0) return 0;
        Ensure(db);
        var now = DateTime.UtcNow;
        var stopped = new HashSet<string>();
        var total = 0;
        foreach (var s in list)
        {
            var due = s.Active && (s.LastRun == null || now - s.LastRun.Value >= TimeSpan.FromMinutes(Math.Max(1, s.Every)));
            if (!due && !wanted.Contains(s.Id)) continue;
            total += await RunAsync(space, mail, htmlToText, s, stopped, ct);
        }
        return total;
    }

    /// <summary>Ein Lauf einer Quelle. Gibt die Zahl der abgelegten Dokumente zurück.</summary>
    internal static async Task<int> RunAsync(UserSpace space, IDmsMail mail, Func<string, string> htmlToText, DmsMailSource s, ISet<string> stopped, CancellationToken ct)
    {
        var db = space.Db;
        var now = DateTime.UtcNow;
        int done = 0, handled = 0;
        string? error = null;
        try
        {
            var items = mail.List(space, s.Account, s.Folder, DateTimeOffset.UtcNow.AddDays(-s.MaxAgeDays));
            var seen = db.Query("SELECT mail FROM dms_mail_seen WHERE source = $s", r => r.GetString(0), ("$s", s.Id)).ToHashSet();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var key = s.Account + "|" + (item.MessageId ?? $"{item.Folder}|{item.Uid}");
                if (seen.Contains(key) || stopped.Contains(key)) continue;
                if (handled >= MaxPerRun) break;
                handled++;
                try
                {
                    var raw = await mail.RawAsync(space, s.Account, item.Folder, item.Uid, ct);
                    var message = MimeMessage.Load(new MemoryStream(raw));
                    var (took, added) = Fits(s, message, htmlToText) ? await TakeAsync(space, s, message, raw, item, ct) : (false, 0);
                    db.Exec("INSERT OR IGNORE INTO dms_mail_seen(source, mail) VALUES($s, $m)", ("$s", s.Id), ("$m", key));
                    if (took)
                    {
                        done += added;
                        if (s.Stop) stopped.Add(key);
                        if (s.Action != "none") mail.After(space, s.Account, item.Folder, item.Uid, s.Action, s.ActionFolder);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                // Jeder Fehler steht an der Quelle; die übrigen Mails, Quellen und die Warteschlange laufen weiter
                catch (Exception e)
                {
                    error = e.Message;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            error = e.Message;
        }
        Update(db, s.Id, x => { x.LastRun = now; x.LastCount = done; x.Total += done; x.Error = error; });
        return done;
    }

    /// <summary>Ablegen, was die Quelle von der Mail nimmt. Took: die Mail wurde genommen (auch wenn alles schon da war); Added: neue Dokumente.</summary>
    private static async Task<(bool Took, int Added)> TakeAsync(UserSpace space, DmsMailSource s, MimeMessage m, byte[] raw, DmsMailItem item, CancellationToken ct)
    {
        var db = space.Db;
        var from = m.From.Mailboxes.FirstOrDefault();
        var correspondent = s.CorrespondentMode switch
        {
            "preset" => s.Correspondent,
            "sender" when from != null => SenderCorrespondent(db, from),
            _ => null,
        };
        var presets = new DmsAssign.Presets(correspondent, s.Doctype, s.Tags);
        var reference = s.Account + "|" + (m.MessageId ?? $"{item.Folder}|{item.Uid}");
        var date = m.Date == DateTimeOffset.MinValue ? item.Date : m.Date;
        var count = 0;
        var parts = s.Scope is "attachments" or "both" ? Parts(s, m) : [];
        // Nur Anhänge, aber keiner passt: die Mail zählt als nicht genommen
        if (s.Scope == "attachments" && parts.Count == 0) return (false, 0);
        if (s.Scope is "mail" or "both")
        {
            var name = DmsImport.SafeName(string.IsNullOrWhiteSpace(m.Subject) ? "Mail" : m.Subject) + ".eml";
            var r = await DmsImport.ImportAsync(space, new MemoryStream(raw), name, "message/rfc822",
                new DmsImport.Info("mail", reference, from?.Address, m.Subject, s.Profile, presets, date.UtcDateTime), ct);
            if (!r.Duplicate) count++;
        }
        foreach (var (name, mime, data) in parts)
        {
            var r = await DmsImport.ImportAsync(space, new MemoryStream(data), name, mime,
                new DmsImport.Info("mail", reference, from?.Address, s.TitleFrom == "subject" ? m.Subject : null, s.Profile, presets, date.UtcDateTime), ct);
            if (!r.Duplicate) count++;
        }
        return (true, count);
    }
}
