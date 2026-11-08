using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Finance;

/// <summary>Angaben zum eigenen Unternehmen und Vorgaben (kv „finance.settings“).</summary>
public sealed class FinanceSettings
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string TaxNumber { get; set; } = "";
    public string VatId { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Iban { get; set; } = "";
    public string Bic { get; set; } = "";
    public int PaymentDays { get; set; } = 14;
    public int OfferDays { get; set; } = 30;
    public string InvoicePattern { get; set; } = "RE-{jahr}-{nr}";
    public string OfferPattern { get; set; } = "AN-{jahr}-{nr}";
    public string OrderPattern { get; set; } = "AU-{jahr}-{nr}";
    /// <summary>Postfach für Belege per E-Mail (Kennung eines Mailkontos); leer: das erste.</summary>
    public string MailAccount { get; set; } = "";
    /// <summary>Antwortadresse in Mails mit Belegen; leer: keine eigene.</summary>
    public string MailReplyTo { get; set; } = "";
    /// <summary>Vorlagen je Belegart („offer“, „order“, „invoice“); fehlt eine, gilt die Vorgabe.</summary>
    public Dictionary<string, FinMailTemplate> MailTemplates { get; set; } = new();
}

public sealed record FinLine(string Description, long QtyMilli, string Unit, long PriceCents, long TotalCents);

/// <summary>
/// Beleg: Angebot, Auftrag (ausgestellt: Auftragsbestätigung), Rechnung oder Stornorechnung. Bei Rechnungen sagt <paramref name="InvoiceType"/>, ob es eine
/// Abschlagsrechnung („partial“) oder Schlussrechnung („final“) ist; leer: gewöhnliche Rechnung. <paramref name="BilledCents"/>: Summe der ausgestellten,
/// nicht stornierten Rechnungen, die sich auf diesen Beleg beziehen.
/// </summary>
public sealed record FinDoc(long Id, string Kind, string State, string? Number, string? DocDate, string ServiceDate, string? DueDate, string Subject, string Intro,
    string Outro, string Recipient, string? ContactUid, long TotalCents, long? RefId, string? RefNumber, string? Sender, string? PdfSha, DateTime? Issued,
    long PaidCents, bool Cancelled, long? DmsDoc, bool DmsTresor, long? Customer, string CustomerNumber, long? Template, List<FinLine> Lines,
    string InvoiceType = "", string? RefKind = null, long BilledCents = 0);

public sealed record FinTemplate(long Id, string Name, bool IsDefault, DateTime Modified, TplLayout Layout);

/// <summary>Vorab erfasste Leistung: Text, Einheit und Einzelpreis, die in eine Position übernommen werden.</summary>
public sealed record FinService(long Id, string Description, string Unit, long PriceCents);
public sealed record FinCustomer(long Id, string Number, string Name, string Address, string Note, string? ContactUid, int Docs, long InvoicedCents, string Email = "");

public sealed record FinEntry(long Id, string Kind, string Date, long AmountCents, string Category, string Text, string Party, long? DmsDoc, long? Invoice, long? Corrects, bool DmsTresor = false, long? Recurring = null);

/// <summary>
/// Wiederkehrende Einnahme oder Ausgabe. Gebucht wird je Termin ein gewöhnlicher Eintrag, sobald der Tag erreicht ist. Ende: ohne, an einem Tag
/// (End) oder nach einer Anzahl von Buchungen (Count). Done zählt die erledigten Termine, Next ist der nächste (null: beendet).
/// </summary>
public sealed record FinRecurring(long Id, string Kind, long AmountCents, string Category, string Text, string Party, string Interval, string Start, string? End, int? Count,
    int Done, string? Next, bool Stopped, long? DmsDoc = null, bool DmsTresor = false);

public sealed record FinCategory(string Id, string Kind, string Name, int Count);

/// <summary>
/// Finanzen (docs/finanzen-konzept.md, erste Version): Einnahmen und Ausgaben, Angebote und Rechnungen für Kleinunternehmer, in der
/// Benutzerdatenbank. Ausgestellte Belege und das Protokoll ändert die Datenbank selbst nicht mehr (Trigger), ein abgeschlossenes Jahr ebenso.
/// Auf das DMS greift Finanzen nur über dessen öffentliche Methoden zu.
/// </summary>
public static class FinanceStore
{
    public const string Offer = "offer", Order = "order", Invoice = "invoice", Cancel = "cancel", Income = "income", Expense = "expense", Draft = "draft", IssuedState = "issued";
    public const string Partial = "partial", Final = "final";

    /// <summary>Anfang der Positionen, mit denen eine Schlussrechnung die Abschlagsrechnungen abzieht.</summary>
    public const string DeductPrefix = "abzüglich Abschlagsrechnung ";

    /// <summary>Überschrift des Belegs, wie sie auf dem PDF, im DMS und im Protokoll steht.</summary>
    public static string Heading(FinDoc d) => d.Kind switch
    {
        Offer => "Angebot",
        Order => "Auftragsbestätigung",
        Cancel => "Stornorechnung",
        _ => d.InvoiceType switch { Partial => "Abschlagsrechnung", Final => "Schlussrechnung", _ => "Rechnung" },
    };

    /// <summary>Name der Belegart als Quelle, etwa in „zu Auftrag AU-2026-0001“.</summary>
    public static string SourceName(string? kind) => kind switch { Offer => "Angebot", Order => "Auftrag", _ => "Rechnung" };
    private const string SettingsKey = "finance.settings";

    /// <summary>Hinweis auf die Befreiung, steht auf jedem Beleg.</summary>
    public const string SmallBusinessNote = "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.";

    private static readonly ConditionalWeakTable<object, object> Ready = new();

    private const string DocsColumns = @"id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL CHECK(kind IN ('offer','order','invoice','cancel')),
    state TEXT NOT NULL DEFAULT 'draft' CHECK(state IN ('draft','issued')), number TEXT NULL, doc_date TEXT NULL, service_date TEXT NOT NULL DEFAULT '',
    due_date TEXT NULL, subject TEXT NOT NULL DEFAULT '', intro TEXT NOT NULL DEFAULT '', outro TEXT NOT NULL DEFAULT '', recipient TEXT NOT NULL DEFAULT '',
    contact_uid TEXT NULL, template INTEGER NULL, customer INTEGER NULL, customer_number TEXT NOT NULL DEFAULT '', total_cents INTEGER NOT NULL DEFAULT 0, ref_id INTEGER NULL, sender TEXT NULL, pdf BLOB NULL, pdf_sha TEXT NULL, issued INTEGER NULL,
    created INTEGER NOT NULL, modified INTEGER NOT NULL, invoice_type TEXT NOT NULL DEFAULT ''";
    private const string DocsTable = "CREATE TABLE IF NOT EXISTS fin_docs (" + DocsColumns + ");";

    private const string Schema = @"
" + DocsTable + @"
CREATE UNIQUE INDEX IF NOT EXISTS fin_docs_number ON fin_docs(number) WHERE number IS NOT NULL;
CREATE TABLE IF NOT EXISTS fin_lines (id INTEGER PRIMARY KEY AUTOINCREMENT, doc INTEGER NOT NULL, pos INTEGER NOT NULL, description TEXT NOT NULL,
    qty_milli INTEGER NOT NULL, unit TEXT NOT NULL DEFAULT '', price_cents INTEGER NOT NULL, total_cents INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS fin_lines_doc ON fin_lines(doc);
CREATE TABLE IF NOT EXISTS fin_series (series TEXT NOT NULL, year INTEGER NOT NULL, last INTEGER NOT NULL, PRIMARY KEY(series, year)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS fin_customers (id INTEGER PRIMARY KEY AUTOINCREMENT, number TEXT NOT NULL UNIQUE, name TEXT NOT NULL, address TEXT NOT NULL DEFAULT '',
    note TEXT NOT NULL DEFAULT '', contact_uid TEXT NULL, created INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS fin_services (id INTEGER PRIMARY KEY AUTOINCREMENT, description TEXT NOT NULL, unit TEXT NOT NULL DEFAULT '', price_cents INTEGER NOT NULL DEFAULT 0, modified INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS fin_templates (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, layout TEXT NOT NULL, is_default INTEGER NOT NULL DEFAULT 0, modified INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS fin_doc_dms (doc INTEGER PRIMARY KEY, dms_doc INTEGER NOT NULL, tresor INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS fin_categories (id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('income','expense')), name TEXT NOT NULL, pos INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS fin_entries (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL CHECK(kind IN ('income','expense')), date TEXT NOT NULL,
    amount_cents INTEGER NOT NULL, category TEXT NOT NULL, text TEXT NOT NULL DEFAULT '', party TEXT NOT NULL DEFAULT '', dms_doc INTEGER NULL, dms_tresor INTEGER NOT NULL DEFAULT 0, invoice INTEGER NULL,
    corrects INTEGER NULL, created INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS fin_entries_date ON fin_entries(date);
CREATE TABLE IF NOT EXISTS fin_recurring (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL CHECK(kind IN ('income','expense')), amount_cents INTEGER NOT NULL,
    category TEXT NOT NULL, text TEXT NOT NULL DEFAULT '', party TEXT NOT NULL DEFAULT '', interval TEXT NOT NULL, start TEXT NOT NULL, end_date TEXT NULL, count INTEGER NULL,
    done INTEGER NOT NULL DEFAULT 0, stopped INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL, modified INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS fin_years (year INTEGER PRIMARY KEY, closed INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS fin_log (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, action TEXT NOT NULL, ref TEXT NOT NULL, detail TEXT NOT NULL, prev TEXT NOT NULL, hash TEXT NOT NULL);
CREATE TRIGGER IF NOT EXISTS fin_docs_no_update BEFORE UPDATE ON fin_docs WHEN OLD.state = 'issued' BEGIN SELECT RAISE(ABORT, 'Ausgestellte Belege lassen sich nicht ändern.'); END;
CREATE TRIGGER IF NOT EXISTS fin_docs_no_delete BEFORE DELETE ON fin_docs WHEN OLD.state = 'issued' BEGIN SELECT RAISE(ABORT, 'Ausgestellte Belege lassen sich nicht löschen.'); END;
CREATE TRIGGER IF NOT EXISTS fin_lines_no_insert BEFORE INSERT ON fin_lines WHEN (SELECT state FROM fin_docs WHERE id = NEW.doc) = 'issued' BEGIN SELECT RAISE(ABORT, 'Ausgestellte Belege lassen sich nicht ändern.'); END;
CREATE TRIGGER IF NOT EXISTS fin_lines_no_update BEFORE UPDATE ON fin_lines WHEN (SELECT state FROM fin_docs WHERE id = OLD.doc) = 'issued' BEGIN SELECT RAISE(ABORT, 'Ausgestellte Belege lassen sich nicht ändern.'); END;
CREATE TRIGGER IF NOT EXISTS fin_lines_no_delete BEFORE DELETE ON fin_lines WHEN (SELECT state FROM fin_docs WHERE id = OLD.doc) = 'issued' BEGIN SELECT RAISE(ABORT, 'Ausgestellte Belege lassen sich nicht ändern.'); END;
CREATE TRIGGER IF NOT EXISTS fin_entries_closed_insert BEFORE INSERT ON fin_entries WHEN EXISTS (SELECT 1 FROM fin_years WHERE year = CAST(substr(NEW.date, 1, 4) AS INTEGER)) BEGIN SELECT RAISE(ABORT, 'Dieses Jahr ist abgeschlossen.'); END;
CREATE TRIGGER IF NOT EXISTS fin_entries_closed_update BEFORE UPDATE ON fin_entries WHEN EXISTS (SELECT 1 FROM fin_years WHERE year IN (CAST(substr(NEW.date, 1, 4) AS INTEGER), CAST(substr(OLD.date, 1, 4) AS INTEGER))) BEGIN SELECT RAISE(ABORT, 'Dieses Jahr ist abgeschlossen.'); END;
CREATE TRIGGER IF NOT EXISTS fin_entries_closed_delete BEFORE DELETE ON fin_entries WHEN EXISTS (SELECT 1 FROM fin_years WHERE year = CAST(substr(OLD.date, 1, 4) AS INTEGER)) BEGIN SELECT RAISE(ABORT, 'Dieses Jahr ist abgeschlossen.'); END;
CREATE TRIGGER IF NOT EXISTS fin_years_no_change BEFORE UPDATE ON fin_years BEGIN SELECT RAISE(ABORT, 'Ein abgeschlossenes Jahr bleibt abgeschlossen.'); END;
CREATE TRIGGER IF NOT EXISTS fin_years_no_delete BEFORE DELETE ON fin_years BEGIN SELECT RAISE(ABORT, 'Ein abgeschlossenes Jahr bleibt abgeschlossen.'); END;
CREATE TRIGGER IF NOT EXISTS fin_log_no_update BEFORE UPDATE ON fin_log BEGIN SELECT RAISE(ABORT, 'Das Protokoll lässt sich nicht ändern.'); END;
CREATE TRIGGER IF NOT EXISTS fin_log_no_delete BEFORE DELETE ON fin_log BEGIN SELECT RAISE(ABORT, 'Das Protokoll lässt sich nicht ändern.'); END;";

    private static readonly (string Kind, string Name)[] DefaultCategories =
    [
        (Income, "Betriebseinnahmen"), (Income, "Sonstige Einnahmen"),
        (Expense, "Waren und Material"), (Expense, "Fremdleistungen"), (Expense, "Bürobedarf"), (Expense, "Software und Lizenzen"), (Expense, "Telefon und Internet"),
        (Expense, "Reisekosten"), (Expense, "Versicherungen und Beiträge"), (Expense, "Werbung"), (Expense, "Bank und Gebühren"), (Expense, "Sonstige Ausgaben"),
    ];

    public static void Ensure(UserDb db)
    {
        if (Ready.TryGetValue(db, out _)) return;
        // Einer zugleich: gleichzeitige erste Anfragen legten sonst dasselbe zweimal an
        lock (db)
        {
            if (Ready.TryGetValue(db, out _)) return;
            db.Exec(Schema);
            // Datenbanken von vor den Vorlagen haben die Spalte noch nicht
            if (db.Scalar("SELECT 1 FROM pragma_table_info('fin_docs') WHERE name = 'template'") == null)
                db.Exec("ALTER TABLE fin_docs ADD COLUMN template INTEGER NULL");
            // Datenbanken von vor dem Versand per E-Mail
            if (db.Scalar("SELECT 1 FROM pragma_table_info('fin_customers') WHERE name = 'email'") == null)
                db.Exec("ALTER TABLE fin_customers ADD COLUMN email TEXT NOT NULL DEFAULT ''");
            // Datenbanken von vor den wiederkehrenden Einträgen
            if (db.Scalar("SELECT 1 FROM pragma_table_info('fin_entries') WHERE name = 'recurring'") == null)
                db.Exec("ALTER TABLE fin_entries ADD COLUMN recurring INTEGER NULL");
            // Datenbanken von vor dem Dokument an der Wiederholung (Vertrag, Abo)
            if (db.Scalar("SELECT 1 FROM pragma_table_info('fin_recurring') WHERE name = 'dms_doc'") == null)
            {
                db.Exec("ALTER TABLE fin_recurring ADD COLUMN dms_doc INTEGER NULL");
                db.Exec("ALTER TABLE fin_recurring ADD COLUMN dms_tresor INTEGER NOT NULL DEFAULT 0");
            }
            // Datenbanken von vor den Aufträgen: Die erlaubten Belegarten stehen in einer CHECK-Regel, die SQLite nicht ändern kann
            if (db.Scalar("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'fin_docs'") is string sql && !sql.Contains("'order'", StringComparison.Ordinal))
                RebuildDocs(db);
            // Platzhalter für Einträge aus dem DMS, deren Kategorie noch zugeordnet werden muss
            db.Exec("INSERT OR IGNORE INTO fin_categories(id, kind, name, pos) VALUES($i, 'income', 'Noch nicht zugeordnet', -1)", ("$i", UnassignedIncome));
            db.Exec("INSERT OR IGNORE INTO fin_categories(id, kind, name, pos) VALUES($i, 'expense', 'Noch nicht zugeordnet', -1)", ("$i", UnassignedExpense));
            if (Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM fin_categories WHERE id NOT IN ($a, $b)", ("$a", UnassignedIncome), ("$b", UnassignedExpense))) == 0)
            {
                var pos = 0;
                foreach (var (kind, name) in DefaultCategories)
                    db.Exec("INSERT INTO fin_categories(id, kind, name, pos) VALUES($i,$k,$n,$p)", ("$i", NewId()), ("$k", kind), ("$n", name), ("$p", pos++));
            }
            if (Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM fin_templates")) == 0)
                db.Exec("INSERT INTO fin_templates(name, layout, is_default, modified) VALUES('Standard', $l, 1, $t)", ("$l", FinanceTemplates.Serialize(FinanceTemplates.Default())), ("$t", Unix()));
            Ready.AddOrUpdate(db, new object());
        }
    }

    /// <summary>
    /// Tabelle der Belege neu anlegen und alle Zeilen übernehmen, in einer Transaktion. Die Schutz-Trigger werden vorher entfernt und danach aus dem
    /// Schema neu angelegt; Inhalte ausgestellter Belege ändern sich dabei nicht.
    /// </summary>
    private static void RebuildDocs(UserDb db)
    {
        var old = db.Query("SELECT name FROM pragma_table_info('fin_docs')", r => r.GetString(0)).ToHashSet();
        var cols = string.Join(", ", new[] { "id", "kind", "state", "number", "doc_date", "service_date", "due_date", "subject", "intro", "outro", "recipient", "contact_uid",
            "template", "customer", "customer_number", "total_cents", "ref_id", "sender", "pdf", "pdf_sha", "issued", "created", "modified" }.Where(old.Contains));
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var t in new[] { "fin_docs_no_update", "fin_docs_no_delete", "fin_lines_no_insert", "fin_lines_no_update", "fin_lines_no_delete" })
            UserDb.Exec(conn, $"DROP TRIGGER IF EXISTS {t}");
        UserDb.Exec(conn, "CREATE TABLE fin_docs_new (" + DocsColumns + ")");
        UserDb.Exec(conn, $"INSERT INTO fin_docs_new({cols}) SELECT {cols} FROM fin_docs");
        UserDb.Exec(conn, "DROP TABLE fin_docs");
        UserDb.Exec(conn, "ALTER TABLE fin_docs_new RENAME TO fin_docs");
        UserDb.Exec(conn, Schema);
        tx.Commit();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------------- Einstellungen und Nummern ----------------

    public static FinanceSettings Settings(UserDb db) => db.GetJson<FinanceSettings>(SettingsKey);

    public static void SaveSettings(UserDb db, FinanceSettings s)
    {
        Ensure(db);
        foreach (var pattern in new[] { s.InvoicePattern, s.OfferPattern, s.OrderPattern })
            if (!pattern.Contains("{nr}", StringComparison.Ordinal)) throw new ArgumentException("Das Muster für die Nummern braucht den Platzhalter {nr}.");
        if (s.PaymentDays is < 0 or > 365 || s.OfferDays is < 0 or > 365) throw new ArgumentException("Bitte höchstens 365 Tage angeben.");
        db.SetJson(SettingsKey, s);
        Log(db, "settings", "", "Einstellungen geändert");
    }

    private static string SeriesOf(string kind) => kind is Offer or Order ? kind : Invoice;

    private static string PatternOf(FinanceSettings s, string series) => series switch { Offer => s.OfferPattern, Order => s.OrderPattern, _ => s.InvoicePattern };

    public static long NextNumber(UserDb db, string series, int year)
    {
        Ensure(db);
        return Convert.ToInt64(db.Scalar("SELECT COALESCE((SELECT last FROM fin_series WHERE series = $s AND year = $y), 0) + 1", ("$s", series), ("$y", year)));
    }

    /// <summary>Beim Umstieg: mit dieser Nummer weiterzählen. Zurück geht es nicht, sonst entstünde eine Nummer zweimal.</summary>
    public static void SetNextNumber(UserDb db, string series, int year, long next)
    {
        if (series is not (Offer or Order or Invoice)) throw new ArgumentException("Unbekannter Nummernkreis.");
        if (next < NextNumber(db, series, year)) throw new ArgumentException("Die nächste Nummer darf nicht kleiner werden.");
        db.Exec("INSERT INTO fin_series(series, year, last) VALUES($s,$y,$l) ON CONFLICT(series, year) DO UPDATE SET last = $l", ("$s", series), ("$y", year), ("$l", next - 1));
        Log(db, "number", series + "/" + year, "Nächste Nummer " + next);
    }

    private const string RestoreSeenKey = "finance.restore_seen";

    /// <summary>Kennung der letzten Wiederherstellung des Servers, null: keine (gesetzt in <see cref="FinanceApi.Map"/>).</summary>
    public static Func<string?>? RestoreStamp { get; set; }

    /// <summary>
    /// Ein älterer Stand kennt später vergebene Nummern nicht mehr. Darum wird nach jeder Wiederherstellung nichts ausgestellt, bis die Person die
    /// nächsten Nummern geprüft hat.
    /// </summary>
    public static bool NumbersToConfirm(UserDb db) => RestoreStamp?.Invoke() is { } stamp && db.Get(RestoreSeenKey) != stamp;

    public static void ConfirmNumbers(UserDb db)
    {
        if (RestoreStamp?.Invoke() is not { } stamp) return;
        Ensure(db);
        db.Set(RestoreSeenKey, stamp);
        Log(db, "number.confirm", "", "Nummern nach einer Wiederherstellung bestätigt");
    }

    private static string Format(string pattern, int year, long nr) =>
        pattern.Replace("{jahr}", year.ToString(CultureInfo.InvariantCulture)).Replace("{nr}", nr.ToString("D4", CultureInfo.InvariantCulture));

    // ---------------- Vorlagen ----------------

    private static FinTemplate ReadTemplate(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetInt64(3) != 0, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).UtcDateTime, FinanceTemplates.Parse(r.GetString(2)));

    public static List<FinTemplate> Templates(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, name, layout, is_default, modified FROM fin_templates ORDER BY is_default DESC, name COLLATE NOCASE", ReadTemplate);
    }

    public static FinTemplate Template(UserDb db, long id)
    {
        Ensure(db);
        return db.Query("SELECT id, name, layout, is_default, modified FROM fin_templates WHERE id = $i", ReadTemplate, ("$i", id)).FirstOrDefault() ?? throw new KeyNotFoundException("Vorlage nicht gefunden.");
    }

    /// <summary>Aufbau für einen Beleg: die gewählte Vorlage; gibt es sie nicht mehr, die Standardvorlage.</summary>
    public static TplLayout LayoutOf(UserDb db, long? id)
    {
        Ensure(db);
        var json = (id is { } t ? db.Scalar("SELECT layout FROM fin_templates WHERE id = $i", ("$i", t)) as string : null)
            ?? db.Scalar("SELECT layout FROM fin_templates ORDER BY is_default DESC, id LIMIT 1") as string;
        return json == null ? FinanceTemplates.Default() : FinanceTemplates.Parse(json);
    }

    public sealed record TemplateInput(string? Name, TplLayout? Layout);

    public static FinTemplate SaveTemplate(UserDb db, long? id, TemplateInput b)
    {
        Ensure(db);
        var name = RequireName(b.Name);
        var layout = b.Layout ?? throw new ArgumentException("Die Vorlage hat keinen Aufbau.");
        FinanceTemplates.Check(layout);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (id is { } t)
        {
            if (UserDb.Exec(conn, "UPDATE fin_templates SET name=$n, layout=$l, modified=$t WHERE id=$i", ("$n", name), ("$l", FinanceTemplates.Serialize(layout)), ("$t", Unix()), ("$i", t)) == 0)
                throw new KeyNotFoundException("Vorlage nicht gefunden.");
        }
        else
        {
            UserDb.Exec(conn, "INSERT INTO fin_templates(name, layout, modified) VALUES($n,$l,$t)", ("$n", name), ("$l", FinanceTemplates.Serialize(layout)), ("$t", Unix()));
            id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
        }
        Log(conn, "template.save", id.Value.ToString(CultureInfo.InvariantCulture), name);
        tx.Commit();
        return Template(db, id.Value);
    }

    public static void SetDefaultTemplate(UserDb db, long id)
    {
        var t = Template(db, id);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE fin_templates SET is_default = CASE WHEN id = $i THEN 1 ELSE 0 END", ("$i", id));
        Log(conn, "template.default", id.ToString(CultureInfo.InvariantCulture), t.Name);
        tx.Commit();
    }

    /// <summary>Ausgestellte Belege behalten ihr PDF. Entwürfe mit dieser Vorlage nehmen danach die Standardvorlage.</summary>
    public static void DeleteTemplate(UserDb db, long id)
    {
        var t = Template(db, id);
        if (t.IsDefault) throw new ArgumentException("Die Standardvorlage lässt sich nicht löschen.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE fin_docs SET template = NULL WHERE template = $i AND state = 'draft'", ("$i", id));
        UserDb.Exec(conn, "DELETE FROM fin_templates WHERE id = $i", ("$i", id));
        Log(conn, "template.delete", id.ToString(CultureInfo.InvariantCulture), t.Name);
        tx.Commit();
    }

    // ---------------- Kunden ----------------

    private const string CustomerCols = @"c.id, c.number, c.name, c.address, c.note, c.contact_uid,
        (SELECT COUNT(*) FROM fin_docs d WHERE d.customer = c.id),
        (SELECT COALESCE(SUM(d.total_cents), 0) FROM fin_docs d WHERE d.customer = c.id AND d.state = 'issued' AND d.kind IN ('invoice', 'cancel')), c.email";

    private static FinCustomer ReadCustomer(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), (int)r.GetInt64(6), r.GetInt64(7), r.GetString(8));

    public static List<FinCustomer> Customers(UserDb db)
    {
        Ensure(db);
        return db.Query($"SELECT {CustomerCols} FROM fin_customers c ORDER BY c.name COLLATE NOCASE", ReadCustomer);
    }

    public static FinCustomer Customer(UserDb db, long id)
    {
        Ensure(db);
        return db.Query($"SELECT {CustomerCols} FROM fin_customers c WHERE c.id = $i", ReadCustomer, ("$i", id)).FirstOrDefault() ?? throw new KeyNotFoundException("Kunde nicht gefunden.");
    }

    public sealed record CustomerInput(string? Number, string? Name, string? Address, string? Note, string? ContactUid, string? Email = null);

    /// <summary>E-Mail-Adresse des Kunden: leer oder eine Adresse mit @ und Punkt in der Domain.</summary>
    private static string CleanEmail(string? email)
    {
        var e = (email ?? "").Trim();
        if (e.Length == 0) return "";
        if (e.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new ArgumentException("Bitte eine gültige E-Mail-Adresse angeben.");
        return e;
    }

    private static string CleanNumber(string? number)
    {
        var n = Dms.Dms.CleanText(number ?? "");
        if (n.Length > 40) throw new ArgumentException("Die Kundennummer ist zu lang (höchstens 40 Zeichen).");
        return n;
    }

    private static string RequireName(string? name)
    {
        var n = Dms.Dms.CleanText(name ?? "");
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        return n.Length > 200 ? n[..200] : n;
    }

    /// <summary>Ohne Angabe bekommt der Kunde die nächste freie Nummer.</summary>
    public static FinCustomer AddCustomer(UserDb db, CustomerInput b)
    {
        Ensure(db);
        var name = RequireName(b.Name);
        var number = CleanNumber(b.Number);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (number.Length == 0)
        {
            var nr = UserDb.Query(conn, "SELECT COALESCE((SELECT last FROM fin_series WHERE series = 'customer' AND year = 0), 0)", r => r.GetInt64(0)).First();
            do number = "K-" + (++nr).ToString("D4", CultureInfo.InvariantCulture);
            while (UserDb.Query(conn, "SELECT 1 FROM fin_customers WHERE number = $n", r => r.GetInt64(0), ("$n", number)).Count > 0);
            UserDb.Exec(conn, "INSERT INTO fin_series(series, year, last) VALUES('customer', 0, $l) ON CONFLICT(series, year) DO UPDATE SET last = $l", ("$l", nr));
        }
        else if (UserDb.Query(conn, "SELECT 1 FROM fin_customers WHERE number = $n", r => r.GetInt64(0), ("$n", number)).Count > 0)
            throw new ArgumentException($"Die Kundennummer {number} gibt es schon.");
        UserDb.Exec(conn, "INSERT INTO fin_customers(number, name, address, note, contact_uid, email, created) VALUES($n,$m,$a,$o,$c,$e,$t)",
            ("$n", number), ("$m", name), ("$a", MultiLine(b.Address, 1000)), ("$o", MultiLine(b.Note, 2000)), ("$c", b.ContactUid), ("$e", CleanEmail(b.Email)), ("$t", Unix()));
        var id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
        Log(conn, "customer.add", id.ToString(CultureInfo.InvariantCulture), $"{number} {name}");
        tx.Commit();
        return Customer(db, id);
    }

    public static FinCustomer UpdateCustomer(UserDb db, long id, CustomerInput b)
    {
        var old = Customer(db, id);
        var number = CleanNumber(b.Number);
        if (number.Length == 0) number = old.Number;
        var name = RequireName(b.Name);
        if (number != old.Number && db.Scalar("SELECT 1 FROM fin_customers WHERE number = $n", ("$n", number)) != null) throw new ArgumentException($"Die Kundennummer {number} gibt es schon.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE fin_customers SET number=$n, name=$m, address=$a, note=$o, contact_uid=$c, email=$e WHERE id=$i",
            ("$n", number), ("$m", name), ("$a", MultiLine(b.Address, 1000)), ("$o", MultiLine(b.Note, 2000)), ("$c", b.ContactUid), ("$e", CleanEmail(b.Email)), ("$i", id));
        Log(conn, "customer.change", id.ToString(CultureInfo.InvariantCulture), $"{old.Number} {old.Name} → {number} {name}");
        tx.Commit();
        return Customer(db, id);
    }

    public static void DeleteCustomer(UserDb db, long id)
    {
        var c = Customer(db, id);
        if (c.Docs > 0) throw new ArgumentException("Zu diesem Kunden gibt es Belege. Er lässt sich nicht löschen.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM fin_customers WHERE id = $i", ("$i", id));
        Log(conn, "customer.delete", id.ToString(CultureInfo.InvariantCulture), $"{c.Number} {c.Name}");
        tx.Commit();
    }

    // ---------------- Leistungen ----------------

    public sealed record ServiceInput(string? Description, string? Unit, long PriceCents);

    private static FinService ReadService(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3));

    public static List<FinService> Services(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, description, unit, price_cents FROM fin_services ORDER BY description COLLATE NOCASE", ReadService);
    }

    public static FinService Service(UserDb db, long id)
    {
        Ensure(db);
        return db.Query("SELECT id, description, unit, price_cents FROM fin_services WHERE id = $i", ReadService, ("$i", id)).FirstOrDefault()
            ?? throw new KeyNotFoundException("Leistung nicht gefunden.");
    }

    /// <summary>Neu anlegen (<paramref name="id"/> null) oder ändern. Belege mit dieser Leistung bleiben unverändert: Die Position ist eine Kopie.</summary>
    public static FinService SaveService(UserDb db, long? id, ServiceInput b)
    {
        Ensure(db);
        var text = MultiLine(b.Description, 2000);
        if (text.Length == 0) throw new ArgumentException("Bitte die Leistung beschreiben.");
        var unit = Dms.Dms.CleanText(b.Unit ?? "");
        if (unit.Length > 40) throw new ArgumentException("Die Einheit ist zu lang (höchstens 40 Zeichen).");
        var old = id is { } x ? Service(db, x) : null;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (old == null)
        {
            UserDb.Exec(conn, "INSERT INTO fin_services(description, unit, price_cents, modified) VALUES($d,$u,$p,$t)", ("$d", text), ("$u", unit), ("$p", b.PriceCents), ("$t", Unix()));
            id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
            Log(conn, "service.add", id.Value.ToString(CultureInfo.InvariantCulture), $"{FirstLine(text)} {unit} {b.PriceCents}");
        }
        else
        {
            UserDb.Exec(conn, "UPDATE fin_services SET description=$d, unit=$u, price_cents=$p, modified=$t WHERE id=$i", ("$d", text), ("$u", unit), ("$p", b.PriceCents), ("$t", Unix()), ("$i", old.Id));
            Log(conn, "service.change", old.Id.ToString(CultureInfo.InvariantCulture), $"{FirstLine(old.Description)} {old.Unit} {old.PriceCents} → {FirstLine(text)} {unit} {b.PriceCents}");
        }
        tx.Commit();
        return Service(db, id!.Value);
    }

    public static void DeleteService(UserDb db, long id)
    {
        var s = Service(db, id);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM fin_services WHERE id = $i", ("$i", id));
        Log(conn, "service.delete", id.ToString(CultureInfo.InvariantCulture), FirstLine(s.Description));
        tx.Commit();
    }

    private static string FirstLine(string text) => text.Split('\n')[0] is var f && f.Length > 80 ? f[..80] : text.Split('\n')[0];

    /// <summary>Anzahl ausgestellter Belege, ohne die Tabellen anzulegen (Warnung beim Konto löschen).</summary>
    public static long IssuedCount(UserDb db) =>
        db.Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fin_docs'") == null ? 0
            : Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM fin_docs WHERE state = 'issued'"));

    // ---------------- Protokoll ----------------

    private static void Log(UserDb db, string action, string reference, string detail)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Log(conn, action, reference, detail);
        tx.Commit();
    }

    /// <summary>Eintrag mit der Prüfsumme des vorigen: Eine Lücke oder Änderung im Protokoll fällt beim Nachrechnen auf.</summary>
    private static void Log(SqliteConnection conn, string action, string reference, string detail)
    {
        var prev = UserDb.Query(conn, "SELECT hash FROM fin_log ORDER BY id DESC LIMIT 1", r => r.GetString(0)).FirstOrDefault() ?? "";
        var at = Unix();
        var hash = LogHash(prev, at, action, reference, detail);
        UserDb.Exec(conn, "INSERT INTO fin_log(at, action, ref, detail, prev, hash) VALUES($a,$c,$r,$d,$p,$h)",
            ("$a", at), ("$c", action), ("$r", reference), ("$d", detail), ("$p", prev), ("$h", hash));
    }

    private static string LogHash(string prev, long at, string action, string reference, string detail) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', prev, at.ToString(CultureInfo.InvariantCulture), action, reference, detail)))).ToLowerInvariant();

    public sealed record LogEntry(long Id, DateTime At, string Action, string Ref, string Detail);

    public static List<LogEntry> LogEntries(UserDb db, int limit = 500)
    {
        Ensure(db);
        return db.Query("SELECT id, at, action, ref, detail FROM fin_log ORDER BY id DESC LIMIT $l",
            r => new LogEntry(r.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).UtcDateTime, r.GetString(2), r.GetString(3), r.GetString(4)), ("$l", limit));
    }

    /// <summary>Stimmt die Kette der Prüfsummen?</summary>
    public static bool LogIntact(UserDb db)
    {
        Ensure(db);
        var prev = "";
        foreach (var (at, action, reference, detail, p, hash) in db.Query("SELECT at, action, ref, detail, prev, hash FROM fin_log ORDER BY id",
                     r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5))))
        {
            if (p != prev || hash != LogHash(prev, at, action, reference, detail)) return false;
            prev = hash;
        }
        return true;
    }

    // ---------------- Kategorien ----------------

    public static List<FinCategory> Categories(UserDb db)
    {
        Ensure(db);
        return db.Query(@"SELECT c.id, c.kind, c.name, (SELECT COUNT(*) FROM fin_entries e WHERE e.category = c.id) FROM fin_categories c ORDER BY c.kind, c.pos, c.name COLLATE NOCASE",
            r => new FinCategory(r.GetString(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3)));
    }

    private static string CleanName(string? name)
    {
        var n = Dms.Dms.CleanText(name ?? "");
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (n.Length > 120) throw new ArgumentException("Der Name ist zu lang (höchstens 120 Zeichen).");
        return n;
    }

    public static FinCategory AddCategory(UserDb db, string? kind, string? name)
    {
        Ensure(db);
        if (kind is not (Income or Expense)) throw new ArgumentException("Bitte Einnahme oder Ausgabe wählen.");
        var n = CleanName(name);
        if (Categories(db).Any(c => c.Kind == kind && string.Equals(c.Name, n, StringComparison.CurrentCultureIgnoreCase))) throw new ArgumentException($"„{n}“ gibt es hier schon.");
        var id = NewId();
        db.Exec("INSERT INTO fin_categories(id, kind, name, pos) VALUES($i,$k,$n,(SELECT COALESCE(MAX(pos),0)+1 FROM fin_categories))", ("$i", id), ("$k", kind), ("$n", n));
        return new FinCategory(id, kind, n, 0);
    }

    public static void RenameCategory(UserDb db, string id, string? name)
    {
        Ensure(db);
        if (db.Exec("UPDATE fin_categories SET name = $n WHERE id = $i", ("$n", CleanName(name)), ("$i", id)) == 0) throw new KeyNotFoundException("Kategorie nicht gefunden.");
    }

    public static void DeleteCategory(UserDb db, string id)
    {
        if (id is UnassignedIncome or UnassignedExpense) throw new ArgumentException("Diese Kategorie gehört zu dmOffice und lässt sich nicht löschen.");
        var c = Categories(db).FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Kategorie nicht gefunden.");
        if (c.Count > 0) throw new ArgumentException("Diese Kategorie wird noch verwendet.");
        if (db.Scalar("SELECT 1 FROM fin_recurring WHERE category = $i AND stopped = 0", ("$i", id)) != null) throw new ArgumentException("Diese Kategorie wird von einem wiederkehrenden Eintrag verwendet.");
        db.Exec("DELETE FROM fin_categories WHERE id = $i", ("$i", id));
    }

    // ---------------- Einnahmen und Ausgaben ----------------

    private const string EntryCols = "id, kind, date, amount_cents, category, text, party, dms_doc, invoice, corrects, dms_tresor, recurring";

    private static FinEntry ReadEntry(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.GetInt64(10) != 0, r.IsDBNull(11) ? null : r.GetInt64(11));

    public static List<FinEntry> Entries(UserDb db, int year)
    {
        Ensure(db);
        BookDue(db);
        return db.Query($"SELECT {EntryCols} FROM fin_entries WHERE date >= $a AND date < $b ORDER BY date DESC, id DESC", ReadEntry,
            ("$a", $"{year:D4}-01-01"), ("$b", $"{year + 1:D4}-01-01"));
    }

    public static FinEntry Entry(UserDb db, long id)
    {
        Ensure(db);
        return db.Query($"SELECT {EntryCols} FROM fin_entries WHERE id = $i", ReadEntry, ("$i", id)).FirstOrDefault() ?? throw new KeyNotFoundException("Eintrag nicht gefunden.");
    }

    public sealed record EntryInput(string? Kind, string? Date, long AmountCents, string? Category, string? Text, string? Party, long? DmsDoc, bool DmsTresor = false);

    private static string CheckDate(string? date, string message = "Bitte ein gültiges Datum angeben.")
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Year < 2000) throw new ArgumentException(message);
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static void CheckEntry(UserDb db, EntryInput e)
    {
        if (e.Kind is not (Income or Expense)) throw new ArgumentException("Bitte Einnahme oder Ausgabe wählen.");
        if (e.AmountCents == 0 || Math.Abs(e.AmountCents) >= 100_000_000_000) throw new ArgumentException("Bitte einen Betrag angeben.");
        if (Categories(db).All(c => c.Id != e.Category || c.Kind != e.Kind)) throw new ArgumentException("Bitte eine passende Kategorie wählen.");
    }

    /// <summary>Beleg verknüpfen: Das Dokument wird fixiert. Für einen Beleg im Tresor muss der Tresor dabei offen sein (tresor liefert den Zugang oder meldet, dass er zu ist).</summary>
    private static void LinkDocument(UserDb db, EntryInput e, Func<Dms.DmsStore>? tresor)
    {
        if (e.DmsDoc is not { } id) return;
        Dms.DmsStore store = e.DmsTresor ? (tresor ?? throw new ArgumentException("Der Tresor ist hier nicht verfügbar."))() : db;
        var doc = Dms.Dms.Get(store, id);
        if (doc.Trashed) throw new ArgumentException("Dieses Dokument liegt im Papierkorb.");
        Dms.Dms.Fix(store, id);
    }

    /// <summary>Verständliche Meldung; die Trigger sichern dasselbe in der Datenbank ab.</summary>
    private static void RequireOpenYear(UserDb db, string date)
    {
        if (db.Scalar("SELECT 1 FROM fin_years WHERE year = $y", ("$y", int.Parse(date.AsSpan(0, 4), CultureInfo.InvariantCulture))) != null)
            throw new ArgumentException($"Das Jahr {date[..4]} ist abgeschlossen. Bitte die Korrektur in einem offenen Jahr eintragen.");
    }

    public static FinEntry AddEntry(UserDb db, EntryInput e, long? invoice = null, long? corrects = null, Func<Dms.DmsStore>? tresor = null) => AddEntry(db, e, invoice, corrects, tresor, null);

    private static FinEntry AddEntry(UserDb db, EntryInput e, long? invoice, long? corrects, Func<Dms.DmsStore>? tresor, long? recurring)
    {
        CheckEntry(db, e);
        var date = CheckDate(e.Date);
        RequireOpenYear(db, date);
        // Die Kopie einer Rechnung ist schon fixiert; eine Zahlung darf nicht daran scheitern, dass der Tresor gerade zu ist
        if (invoice == null) LinkDocument(db, e, tresor);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "INSERT INTO fin_entries(kind, date, amount_cents, category, text, party, dms_doc, dms_tresor, invoice, corrects, recurring, created) VALUES($k,$d,$a,$c,$t,$p,$m,$mt,$i,$o,$r,$n)",
            ("$k", e.Kind), ("$d", date), ("$a", e.AmountCents), ("$c", e.Category), ("$t", Dms.Dms.CleanText(e.Text ?? "")), ("$p", Dms.Dms.CleanText(e.Party ?? "")),
            ("$m", e.DmsDoc), ("$mt", e.DmsTresor ? 1 : 0), ("$i", invoice), ("$o", corrects), ("$r", recurring), ("$n", Unix()));
        var id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
        Log(conn, "entry.add", id.ToString(CultureInfo.InvariantCulture), Describe(e.Kind!, date, e.AmountCents) + (recurring is { } rid ? $" (wiederkehrend {rid})" : ""));
        // Buchung und Zähler in einer Transaktion: Ein Termin wird nie doppelt gebucht
        if (recurring is { } done) UserDb.Exec(conn, "UPDATE fin_recurring SET done = done + 1 WHERE id = $i", ("$i", done));
        tx.Commit();
        return Entry(db, id);
    }

    /// <summary>Lesbar fürs Protokoll, auf Deutsch wie die Belege.</summary>
    private static string Describe(string kind, string date, long cents) =>
        $"{(kind == Income ? "Einnahme" : "Ausgabe")} {DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd.MM.yyyy} {FinancePdf.Money(cents)}";

    public static FinEntry UpdateEntry(UserDb db, long id, EntryInput e, Func<Dms.DmsStore>? tresor = null)
    {
        var old = Entry(db, id);
        if (old.Invoice != null) throw new ArgumentException("Dieser Eintrag gehört zu einer Rechnung. Bitte stattdessen korrigieren.");
        CheckEntry(db, e);
        var date = CheckDate(e.Date);
        RequireOpenYear(db, old.Date);
        RequireOpenYear(db, date);
        // Unverändert verknüpft: geht auch bei geschlossenem Tresor
        if (old.DmsDoc != e.DmsDoc || old.DmsTresor != e.DmsTresor) LinkDocument(db, e, tresor);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE fin_entries SET kind=$k, date=$d, amount_cents=$a, category=$c, text=$t, party=$p, dms_doc=$m, dms_tresor=$mt WHERE id=$i",
            ("$k", e.Kind), ("$d", date), ("$a", e.AmountCents), ("$c", e.Category), ("$t", Dms.Dms.CleanText(e.Text ?? "")), ("$p", Dms.Dms.CleanText(e.Party ?? "")), ("$m", e.DmsDoc), ("$mt", e.DmsTresor ? 1 : 0), ("$i", id));
        Log(conn, "entry.change", id.ToString(CultureInfo.InvariantCulture), $"{Describe(old.Kind, old.Date, old.AmountCents)} → {Describe(e.Kind!, date, e.AmountCents)}");
        tx.Commit();
        return Entry(db, id);
    }

    public static void DeleteEntry(UserDb db, long id)
    {
        var old = Entry(db, id);
        if (old.Invoice != null) throw new ArgumentException("Dieser Eintrag gehört zu einer Rechnung. Bitte stattdessen korrigieren.");
        RequireOpenYear(db, old.Date);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM fin_entries WHERE id = $i", ("$i", id));
        Log(conn, "entry.delete", id.ToString(CultureInfo.InvariantCulture), Describe(old.Kind, old.Date, old.AmountCents));
        tx.Commit();
    }

    /// <summary>Gegeneintrag: hebt den Betrag des Eintrags auf, am angegebenen Tag (auch nach dem Jahresabschluss möglich, dann in einem offenen Jahr).</summary>
    public static FinEntry Correct(UserDb db, long id, string? date)
    {
        var old = Entry(db, id);
        var sum = Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(amount_cents), 0) FROM fin_entries WHERE corrects = $i", ("$i", id)));
        if (old.AmountCents + sum == 0) throw new ArgumentException("Dieser Eintrag ist schon korrigiert.");
        return AddEntry(db, new EntryInput(old.Kind, date, -(old.AmountCents + sum), old.Category, "Korrektur: " + old.Text, old.Party, null), old.Invoice, id);
    }

    // ---------------- Wiederkehrende Einnahmen und Ausgaben ----------------

    public static readonly string[] Intervals = ["week", "month", "quarter", "halfyear", "year"];

    public sealed record RecurringInput(string? Kind, long AmountCents, string? Category, string? Text, string? Party, string? Interval, string? Start, string? End, int? Count,
        long? DmsDoc = null, bool DmsTresor = false);

    private const string RecurringCols = "id, kind, amount_cents, category, text, party, interval, start, end_date, count, done, stopped, dms_doc, dms_tresor";

    /// <summary>Termin Nummer n (ab 0). Monate werden vom Beginn aus gezählt: Ein Beginn am 31. fällt im Februar auf den letzten Tag, im März wieder auf den 31.</summary>
    public static DateOnly Occurrence(string interval, DateOnly start, int n) => interval switch
    {
        "week" => start.AddDays(7 * n),
        "month" => start.AddMonths(n),
        "quarter" => start.AddMonths(3 * n),
        "halfyear" => start.AddMonths(6 * n),
        _ => start.AddMonths(12 * n),
    };

    private static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NextOf(string interval, string start, string? end, int? count, int done, bool stopped)
    {
        if (stopped || (count is { } c && done >= c)) return null;
        var next = Occurrence(interval, Day(start), done);
        return end != null && next > Day(end) ? null : Iso(next);
    }

    private static FinRecurring ReadRecurring(SqliteDataReader r)
    {
        var interval = r.GetString(6);
        var start = r.GetString(7);
        var end = r.IsDBNull(8) ? null : r.GetString(8);
        int? count = r.IsDBNull(9) ? null : r.GetInt32(9);
        var done = r.GetInt32(10);
        var stopped = r.GetInt64(11) != 0;
        return new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), interval, start, end, count, done,
            NextOf(interval, start, end, count, done, stopped), stopped, r.IsDBNull(12) ? null : r.GetInt64(12), r.GetInt64(13) != 0);
    }

    public static List<FinRecurring> RecurringList(UserDb db)
    {
        Ensure(db);
        BookDue(db);
        return db.Query($"SELECT {RecurringCols} FROM fin_recurring ORDER BY kind DESC, text COLLATE NOCASE, id", ReadRecurring);
    }

    public static FinRecurring Recurring(UserDb db, long id)
    {
        Ensure(db);
        return db.Query($"SELECT {RecurringCols} FROM fin_recurring WHERE id = $i", ReadRecurring, ("$i", id)).FirstOrDefault()
            ?? throw new KeyNotFoundException("Wiederkehrender Eintrag nicht gefunden.");
    }

    private static (string Start, string? End, int? Count) CheckRecurring(UserDb db, RecurringInput b)
    {
        CheckEntry(db, new EntryInput(b.Kind, null, b.AmountCents, b.Category, b.Text, b.Party, null));
        if (!Intervals.Contains(b.Interval)) throw new ArgumentException("Bitte wählen, wie oft der Eintrag wiederkehrt.");
        var start = CheckDate(b.Start, "Bitte den ersten Termin angeben.");
        if (b.End != null && b.Count != null) throw new ArgumentException("Bitte entweder ein Enddatum oder eine Anzahl angeben.");
        string? end = null;
        if (b.End != null)
        {
            end = CheckDate(b.End, "Bitte ein gültiges Enddatum angeben.");
            if (string.CompareOrdinal(end, start) < 0) throw new ArgumentException("Das Enddatum liegt vor dem ersten Termin.");
        }
        if (b.Count is < 1 or > 1000) throw new ArgumentException("Bitte eine Anzahl von 1 bis 1000 angeben.");
        return (start, end, b.Count);
    }

    /// <summary>Neu anlegen. Termine bis heute werden gleich gebucht.</summary>
    public static FinRecurring AddRecurring(UserDb db, RecurringInput b, DateOnly? today = null, Func<Dms.DmsStore>? tresor = null)
    {
        Ensure(db);
        var (start, end, count) = CheckRecurring(db, b);
        LinkDocument(db, new EntryInput(b.Kind, null, b.AmountCents, b.Category, null, null, b.DmsDoc, b.DmsTresor), tresor);
        long id;
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            UserDb.Exec(conn, "INSERT INTO fin_recurring(kind, amount_cents, category, text, party, interval, start, end_date, count, dms_doc, dms_tresor, created, modified) VALUES($k,$a,$c,$t,$p,$v,$s,$e,$n,$m,$mt,$u,$u)",
                ("$k", b.Kind), ("$a", b.AmountCents), ("$c", b.Category), ("$t", Dms.Dms.CleanText(b.Text ?? "")), ("$p", Dms.Dms.CleanText(b.Party ?? "")),
                ("$v", b.Interval), ("$s", start), ("$e", end), ("$n", count), ("$m", b.DmsDoc), ("$mt", b.DmsDoc != null && b.DmsTresor ? 1 : 0), ("$u", Unix()));
            id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
            Log(conn, "recurring.add", id.ToString(CultureInfo.InvariantCulture), $"{(b.Kind == Income ? "Einnahme" : "Ausgabe")} {FinancePdf.Money(b.AmountCents)} {b.Interval} ab {start}");
            tx.Commit();
        }
        BookDue(db, today);
        return Recurring(db, id);
    }

    /// <summary>
    /// Ändern wirkt auf die künftigen Buchungen; gebuchte Einträge bleiben, wie sie sind. Sobald gebucht wurde, stehen Beginn und Rhythmus fest,
    /// sonst verschöben sich die Termine.
    /// </summary>
    public static FinRecurring UpdateRecurring(UserDb db, long id, RecurringInput b, DateOnly? today = null, Func<Dms.DmsStore>? tresor = null)
    {
        var old = Recurring(db, id);
        var (start, end, count) = CheckRecurring(db, b);
        if (b.Kind != old.Kind) throw new ArgumentException("Aus einer Einnahme lässt sich keine Ausgabe machen.");
        if (old.Done > 0 && (start != old.Start || b.Interval != old.Interval))
            throw new ArgumentException("Nach der ersten Buchung lassen sich erster Termin und Rhythmus nicht mehr ändern. Bitte den Eintrag beenden und neu anlegen.");
        if (count is { } c && c < old.Done) throw new ArgumentException($"Es sind schon {old.Done} Termine gebucht.");
        // Unverändert verknüpft: geht auch bei geschlossenem Tresor
        if (old.DmsDoc != b.DmsDoc || old.DmsTresor != b.DmsTresor) LinkDocument(db, new EntryInput(b.Kind, null, b.AmountCents, b.Category, null, null, b.DmsDoc, b.DmsTresor), tresor);
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            UserDb.Exec(conn, "UPDATE fin_recurring SET amount_cents=$a, category=$c, text=$t, party=$p, interval=$v, start=$s, end_date=$e, count=$n, dms_doc=$m, dms_tresor=$mt, modified=$u WHERE id=$i",
                ("$a", b.AmountCents), ("$c", b.Category), ("$t", Dms.Dms.CleanText(b.Text ?? "")), ("$p", Dms.Dms.CleanText(b.Party ?? "")),
                ("$v", b.Interval), ("$s", start), ("$e", end), ("$n", count), ("$m", b.DmsDoc), ("$mt", b.DmsDoc != null && b.DmsTresor ? 1 : 0), ("$u", Unix()), ("$i", id));
            Log(conn, "recurring.change", id.ToString(CultureInfo.InvariantCulture), $"{FinancePdf.Money(old.AmountCents)} → {FinancePdf.Money(b.AmountCents)}");
            tx.Commit();
        }
        BookDue(db, today);
        return Recurring(db, id);
    }

    /// <summary>Beenden: Es wird nichts mehr gebucht; der Eintrag bleibt in der Liste.</summary>
    public static FinRecurring StopRecurring(UserDb db, long id)
    {
        Recurring(db, id);
        db.Exec("UPDATE fin_recurring SET stopped = 1, modified = $u WHERE id = $i", ("$u", Unix()), ("$i", id));
        Log(db, "recurring.stop", id.ToString(CultureInfo.InvariantCulture), "beendet");
        return Recurring(db, id);
    }

    /// <summary>Löschen entfernt nur die Wiederholung; gebuchte Einträge bleiben.</summary>
    public static void DeleteRecurring(UserDb db, long id)
    {
        Recurring(db, id);
        db.Exec("DELETE FROM fin_recurring WHERE id = $i", ("$i", id));
        Log(db, "recurring.delete", id.ToString(CultureInfo.InvariantCulture), "gelöscht");
    }

    /// <summary>
    /// Fällige Termine buchen, bis einschließlich heute. Läuft bei jedem Abruf der Einträge; je Datenbank einer zugleich, damit kein Termin doppelt
    /// gebucht wird. Ein Termin in einem abgeschlossenen Jahr wird übersprungen und im Protokoll vermerkt.
    /// </summary>
    public static void BookDue(UserDb db, DateOnly? today = null)
    {
        var now = Iso(today ?? DateOnly.FromDateTime(DateTime.Now));
        lock (db)
        {
            foreach (var r in db.Query($"SELECT {RecurringCols} FROM fin_recurring WHERE stopped = 0", ReadRecurring))
            {
                var next = r.Next;
                for (var guard = 0; next != null && string.CompareOrdinal(next, now) <= 0 && guard < 1000; guard++)
                {
                    if (db.Scalar("SELECT 1 FROM fin_years WHERE year = $y", ("$y", int.Parse(next.AsSpan(0, 4), CultureInfo.InvariantCulture))) != null)
                    {
                        using var conn = db.Open();
                        using var tx = conn.BeginTransaction();
                        UserDb.Exec(conn, "UPDATE fin_recurring SET done = done + 1 WHERE id = $i", ("$i", r.Id));
                        Log(conn, "recurring.skip", r.Id.ToString(CultureInfo.InvariantCulture), $"{next}: Jahr abgeschlossen, nicht gebucht");
                        tx.Commit();
                    }
                    else AddEntry(db, new EntryInput(r.Kind, next, r.AmountCents, r.Category, r.Text, r.Party, null), null, null, null, r.Id);
                    next = Recurring(db, r.Id).Next;
                }
            }
        }
    }

    // ---------------- Aus dem DMS gebucht ----------------

    /// <summary>Kategorie „Noch nicht zugeordnet“: Einträge, die im DMS als Einnahme oder Ausgabe gekennzeichnet wurden.</summary>
    public const string UnassignedIncome = "unassigned-income", UnassignedExpense = "unassigned-expense";

    public static bool IsUnassigned(string category) => category is UnassignedIncome or UnassignedExpense;

    /// <summary>Buchung eines DMS-Dokuments, wie das DMS sie zeigt. Der Betrag gehört der Buchung, nicht dem Dokument.</summary>
    public sealed record DmsBooking(long Entry, string Kind, long AmountCents, string Date, bool Unassigned, bool Closed, bool Invoice);

    /// <summary>Der Eintrag, der auf dieses Dokument verweist (kein Gegeneintrag; bei mehreren der jüngste).</summary>
    public static DmsBooking? BookingOf(UserDb db, long dmsDoc, bool tresor)
    {
        Ensure(db);
        var e = db.Query($"SELECT {EntryCols} FROM fin_entries WHERE dms_doc = $d AND dms_tresor = $t AND corrects IS NULL ORDER BY id DESC LIMIT 1", ReadEntry,
            ("$d", dmsDoc), ("$t", tresor ? 1 : 0)).FirstOrDefault();
        if (e == null) return null;
        var closed = db.Scalar("SELECT 1 FROM fin_years WHERE year = $y", ("$y", int.Parse(e.Date.AsSpan(0, 4), CultureInfo.InvariantCulture))) != null;
        return new DmsBooking(e.Id, e.Kind, e.AmountCents, e.Date, IsUnassigned(e.Category), closed, e.Invoice != null);
    }

    /// <summary>
    /// Im DMS als Einnahme oder Ausgabe kennzeichnen (kind null: nicht mehr). Neu: ein Eintrag mit Datum, Titel und Korrespondent des
    /// Dokuments und der Kategorie „Noch nicht zugeordnet“; der Beleg wird verknüpft und damit fixiert. Vorhanden: Art und Betrag ändern -
    /// eine andere Art setzt die Kategorie zurück auf den Platzhalter. Der Betrag des Dokuments bleibt unberührt.
    /// </summary>
    public static DmsBooking? BookFromDms(UserDb db, Dms.DmsStore store, long dmsDoc, string? kind, long? amountCents, Func<Dms.DmsStore>? tresor)
    {
        Ensure(db);
        var doc = Dms.Dms.Get(store, dmsDoc);
        var old = BookingOf(db, dmsDoc, store.Tresor);
        if (old is { Invoice: true }) throw new ArgumentException("Dieses Dokument gehört zu einer Rechnung. Die Buchung lässt sich nur in Finanzen ändern.");
        if (kind == null)
        {
            if (old != null) DeleteEntry(db, old.Entry);
            return null;
        }
        if (kind is not (Income or Expense)) throw new ArgumentException("Bitte Einnahme oder Ausgabe wählen.");
        var cents = Math.Abs(amountCents ?? old?.AmountCents ?? doc.Amount ?? 0);
        if (cents == 0) throw new ArgumentException("Bitte zuerst einen Betrag eintragen.");
        if (old != null)
        {
            var e = Entry(db, old.Entry);
            var category = e.Kind == kind ? e.Category : kind == Income ? UnassignedIncome : UnassignedExpense;
            UpdateEntry(db, e.Id, new EntryInput(kind, e.Date, cents, category, e.Text, e.Party, e.DmsDoc, e.DmsTresor), tresor);
        }
        else
        {
            var party = doc.Correspondent is { } c ? Dms.Dms.Entries(store.User).FirstOrDefault(x => x.Id == c)?.Name ?? "" : "";
            var date = doc.DocDate ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            AddEntry(db, new EntryInput(kind, date, cents, kind == Income ? UnassignedIncome : UnassignedExpense, doc.Title, party, dmsDoc, store.Tresor), tresor: tresor);
        }
        return BookingOf(db, dmsDoc, store.Tresor);
    }

    public sealed record YearState(int Year, bool Closed);

    public static List<YearState> Years(UserDb db)
    {
        Ensure(db);
        var closed = db.Query("SELECT year FROM fin_years", r => (int)r.GetInt64(0)).ToHashSet();
        var used = db.Query("SELECT DISTINCT CAST(substr(date, 1, 4) AS INTEGER) FROM fin_entries", r => (int)r.GetInt64(0));
        return used.Concat(closed).Append(DateTime.Now.Year).Distinct().OrderDescending().Select(y => new YearState(y, closed.Contains(y))).ToList();
    }

    public static void CloseYear(UserDb db, int year)
    {
        Ensure(db);
        // Was bis heute fällig war, steht vor dem Abschluss im Jahr
        BookDue(db);
        if (year >= DateTime.Now.Year) throw new ArgumentException("Abschließen lässt sich nur ein vergangenes Jahr.");
        if (db.Scalar("SELECT 1 FROM fin_years WHERE year = $y", ("$y", year)) != null) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "INSERT INTO fin_years(year, closed) VALUES($y,$n)", ("$y", year), ("$n", Unix()));
        Log(conn, "year.close", year.ToString(CultureInfo.InvariantCulture), "Jahr abgeschlossen");
        tx.Commit();
    }

    public sealed record CategorySum(string Id, string Name, string Kind, long Cents);
    public sealed record MonthSum(int Month, long Income, long Expense);
    public sealed record Summary(int Year, bool Closed, long Income, long Expense, long Surplus, List<CategorySum> Categories, List<MonthSum> Months);

    public static Summary SummaryOf(UserDb db, int year)
    {
        var entries = Entries(db, year);
        var cats = Categories(db).ToDictionary(c => c.Id);
        long Sum(string kind) => entries.Where(e => e.Kind == kind).Sum(e => e.AmountCents);
        var months = Enumerable.Range(1, 12).Select(m =>
        {
            var part = entries.Where(e => int.Parse(e.Date.AsSpan(5, 2), CultureInfo.InvariantCulture) == m).ToList();
            return new MonthSum(m, part.Where(e => e.Kind == Income).Sum(e => e.AmountCents), part.Where(e => e.Kind == Expense).Sum(e => e.AmountCents));
        }).ToList();
        var byCat = entries.GroupBy(e => e.Category).Select(g => new CategorySum(g.Key, cats.GetValueOrDefault(g.Key)?.Name ?? "?", g.First().Kind, g.Sum(e => e.AmountCents)))
            .OrderBy(c => c.Kind == Income ? 0 : 1).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new Summary(year, Years(db).Any(y => y.Year == year && y.Closed), Sum(Income), Sum(Expense), Sum(Income) - Sum(Expense), byCat, months);
    }

    // ---------------- Belege ----------------

    public sealed record LineInput(string? Description, decimal Qty, string? Unit, long PriceCents);
    public sealed record DocInput(string? Kind, string? DocDate, string? ServiceDate, string? DueDate, string? Subject, string? Intro, string? Outro, string? Recipient, string? ContactUid, List<LineInput>? Lines, long? Customer = null, long? Template = null);

    private const string DocCols = @"d.id, d.kind, d.state, d.number, d.doc_date, d.service_date, d.due_date, d.subject, d.intro, d.outro, d.recipient, d.contact_uid, d.total_cents, d.ref_id,
        (SELECT r.number FROM fin_docs r WHERE r.id = d.ref_id), d.sender, d.pdf_sha, d.issued,
        (SELECT COALESCE(SUM(e.amount_cents), 0) FROM fin_entries e WHERE e.invoice = d.id),
        EXISTS (SELECT 1 FROM fin_docs c WHERE c.ref_id = d.id AND c.kind = 'cancel' AND c.state = 'issued'), (SELECT m.dms_doc FROM fin_doc_dms m WHERE m.doc = d.id), COALESCE((SELECT m.tresor FROM fin_doc_dms m WHERE m.doc = d.id), 0), d.customer, d.customer_number, d.template,
        d.invoice_type, (SELECT r.kind FROM fin_docs r WHERE r.id = d.ref_id),
        (SELECT COALESCE(SUM(i.total_cents), 0) FROM fin_docs i WHERE i.ref_id = d.id AND i.kind = 'invoice' AND i.state = 'issued'
            AND NOT EXISTS (SELECT 1 FROM fin_docs c WHERE c.ref_id = i.id AND c.kind = 'cancel' AND c.state = 'issued'))";

    private static FinDoc ReadDoc(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt64(12),
        r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(17)).UtcDateTime, r.GetInt64(18), r.GetInt64(19) != 0, r.IsDBNull(20) ? null : r.GetInt64(20), r.GetInt64(21) != 0, r.IsDBNull(22) ? null : r.GetInt64(22), r.GetString(23), r.IsDBNull(24) ? null : r.GetInt64(24), [],
        r.GetString(25), r.IsDBNull(26) ? null : r.GetString(26), r.GetInt64(27));

    /// <summary>Liste ohne Positionen.</summary>
    public static List<FinDoc> Docs(UserDb db, string? kind)
    {
        Ensure(db);
        // Stornorechnungen stehen bei den Rechnungen
        return db.Query($"SELECT {DocCols} FROM fin_docs d WHERE $k IS NULL OR d.kind = $k OR ($k = 'invoice' AND d.kind = 'cancel') ORDER BY COALESCE(d.doc_date, '9999') DESC, d.id DESC", ReadDoc, ("$k", kind));
    }

    public static FinDoc Doc(UserDb db, long id)
    {
        Ensure(db);
        var doc = db.Query($"SELECT {DocCols} FROM fin_docs d WHERE d.id = $i", ReadDoc, ("$i", id)).FirstOrDefault() ?? throw new KeyNotFoundException("Beleg nicht gefunden.");
        return doc with
        {
            Lines = db.Query("SELECT description, qty_milli, unit, price_cents, total_cents FROM fin_lines WHERE doc = $d ORDER BY pos",
                r => new FinLine(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)), ("$d", id))
        };
    }

    private static string MultiLine(string? text, int max) => (text ?? "").Replace("\r\n", "\n").Trim() is var t && t.Length > max ? t[..max] : (text ?? "").Replace("\r\n", "\n").Trim();

    /// <summary>Betrag einer Position: kaufmännisch auf Cent gerundet.</summary>
    public static long LineTotal(long qtyMilli, long priceCents) =>
        (long)Math.Round(qtyMilli * (decimal)priceCents / 1000m, 0, MidpointRounding.AwayFromZero);

    private static void WriteDraft(SqliteConnection conn, long id, DocInput b)
    {
        var lines = (b.Lines ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.PriceCents != 0).ToList();
        if (lines.Count > 500) throw new ArgumentException("Zu viele Positionen (höchstens 500).");
        UserDb.Exec(conn, "DELETE FROM fin_lines WHERE doc = $d", ("$d", id));
        long total = 0;
        var pos = 0;
        foreach (var l in lines)
        {
            if (l.Qty is < 0 or > 10_000_000) throw new ArgumentException("Ungültige Menge.");
            if (Math.Abs(l.PriceCents) >= 100_000_000_000) throw new ArgumentException("Ungültiger Preis.");
            var qty = (long)Math.Round(l.Qty * 1000m, 0, MidpointRounding.AwayFromZero);
            var sum = LineTotal(qty, l.PriceCents);
            total += sum;
            UserDb.Exec(conn, "INSERT INTO fin_lines(doc, pos, description, qty_milli, unit, price_cents, total_cents) VALUES($d,$p,$t,$q,$u,$c,$s)",
                ("$d", id), ("$p", pos++), ("$t", MultiLine(l.Description, 2000)), ("$q", qty), ("$u", Dms.Dms.CleanText(l.Unit ?? "")), ("$c", l.PriceCents), ("$s", sum));
        }
        string? Day(string? v) => string.IsNullOrEmpty(v) ? null : CheckDate(v);
        // Die Kundennummer steht fest im Beleg, spätere Änderungen am Kunden berühren ihn nicht
        var customerNumber = b.Customer is { } cu
            ? UserDb.Query(conn, "SELECT number FROM fin_customers WHERE id = $c", r => r.GetString(0), ("$c", cu)).FirstOrDefault() ?? throw new KeyNotFoundException("Kunde nicht gefunden.") : "";
        UserDb.Exec(conn, "UPDATE fin_docs SET customer=$c, customer_number=$n, template=$tp WHERE id=$id", ("$c", b.Customer), ("$n", customerNumber), ("$tp", b.Template), ("$id", id));
        UserDb.Exec(conn, @"UPDATE fin_docs SET doc_date=$dd, service_date=$sd, due_date=$du, subject=$s, intro=$i, outro=$o, recipient=$r, contact_uid=$c, total_cents=$t, modified=$n WHERE id=$id",
            ("$dd", Day(b.DocDate)), ("$sd", Dms.Dms.CleanText(b.ServiceDate ?? "")), ("$du", Day(b.DueDate)), ("$s", Dms.Dms.CleanText(b.Subject ?? "")), ("$i", MultiLine(b.Intro, 4000)),
            ("$o", MultiLine(b.Outro, 4000)), ("$r", MultiLine(b.Recipient, 1000)), ("$c", b.ContactUid), ("$t", total), ("$n", Unix()), ("$id", id));
    }

    public static FinDoc CreateDraft(UserDb db, DocInput b)
    {
        Ensure(db);
        if (b.Kind is not (Offer or Order or Invoice)) throw new ArgumentException("Unbekannte Belegart.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "INSERT INTO fin_docs(kind, created, modified) VALUES($k,$n,$n)", ("$k", b.Kind), ("$n", Unix()));
        var id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
        WriteDraft(conn, id, b);
        tx.Commit();
        return Doc(db, id);
    }

    private static FinDoc RequireDraft(UserDb db, long id)
    {
        var doc = Doc(db, id);
        if (doc.State != Draft) throw new ArgumentException("Ausgestellte Belege lassen sich nicht ändern.");
        return doc;
    }

    public static FinDoc UpdateDraft(UserDb db, long id, DocInput b)
    {
        RequireDraft(db, id);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        WriteDraft(conn, id, b);
        tx.Commit();
        return Doc(db, id);
    }

    public static void DeleteDraft(UserDb db, long id)
    {
        RequireDraft(db, id);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM fin_lines WHERE doc = $d", ("$d", id));
        UserDb.Exec(conn, "DELETE FROM fin_docs WHERE id = $d", ("$d", id));
        tx.Commit();
    }

    /// <summary>
    /// Neuer Entwurf aus einem Beleg, mit Verweis auf die Quelle: neue Version eines Angebots oder Auftrags, Auftrag aus einem Angebot, Rechnung aus
    /// einem Angebot oder Auftrag.
    /// </summary>
    public static FinDoc CopyToDraft(UserDb db, long id, string? kind)
    {
        var src = Doc(db, id);
        if (kind is not (Offer or Order or Invoice)) throw new ArgumentException("Unbekannte Belegart.");
        if (src.Kind == Cancel) throw new ArgumentException("Eine Stornorechnung lässt sich nicht kopieren.");
        if (kind == Offer && src.Kind != Offer) throw new ArgumentException("Ein Angebot entsteht nur aus einem Angebot.");
        if (kind == Order && src.Kind is not (Offer or Order)) throw new ArgumentException("Ein Auftrag entsteht aus einem Angebot oder Auftrag.");
        if (src.InvoiceType != "") throw new ArgumentException("Abschlags- und Schlussrechnungen lassen sich nicht kopieren.");
        var draft = CreateDraft(db, new DocInput(kind, null, kind == Invoice ? src.ServiceDate : "", null, src.Subject, src.Intro, src.Outro, src.Recipient, src.ContactUid,
            src.Lines.Select(l => new LineInput(l.Description, l.QtyMilli / 1000m, l.Unit, l.PriceCents)).ToList(), src.Customer, src.Template));
        db.Exec("UPDATE fin_docs SET ref_id = $r WHERE id = $d", ("$r", id), ("$d", draft.Id));
        return Doc(db, draft.Id);
    }

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Rechnungen zu einer Quelle: Entwürfe und ausgestellte, ohne stornierte.</summary>
    private static List<FinDoc> InvoicesOf(UserDb db, long source) =>
        Docs(db, Invoice).Where(d => d.Kind == Invoice && d.RefId == source && !d.Cancelled).ToList();

    private static FinDoc RequireSource(UserDb db, long id)
    {
        var src = Doc(db, id);
        if (src.Kind is not (Offer or Order)) throw new ArgumentException("Abschlags- und Schlussrechnungen beziehen sich auf ein Angebot oder einen Auftrag.");
        if (src.State != IssuedState) throw new ArgumentException(src.Kind == Order ? "Bitte den Auftrag zuerst ausstellen (Auftragsbestätigung)." : "Bitte das Angebot zuerst ausstellen.");
        return src;
    }

    /// <summary>
    /// Abschlagsrechnung zu einem Angebot oder Auftrag: eine Position mit dem Abschlag, als Prozent der Summe oder fester Betrag. Alle Abschläge
    /// zusammen dürfen die Summe nicht übersteigen; nach einer Schlussrechnung gibt es keine Abschläge mehr.
    /// </summary>
    public static FinDoc PartialInvoice(UserDb db, long sourceId, decimal? percent, long? amountCents)
    {
        var src = RequireSource(db, sourceId);
        var others = InvoicesOf(db, sourceId);
        if (others.Any(d => d.InvoiceType == Final)) throw new ArgumentException("Zu diesem Beleg gibt es schon eine Schlussrechnung.");
        long amount;
        string text;
        var label = $"{SourceName(src.Kind)} {src.Number}";
        var nr = others.Count(d => d.InvoiceType == Partial) + 1;
        if (percent is { } p)
        {
            if (p is <= 0 or > 100) throw new ArgumentException("Bitte einen Prozentsatz über 0 und bis 100 angeben.");
            amount = (long)Math.Round(src.TotalCents * p / 100m, 0, MidpointRounding.AwayFromZero);
            text = $"{nr}. Abschlag: {p.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"))} % von {FinancePdf.Money(src.TotalCents)} gemäß {label}";
        }
        else if (amountCents is { } a)
        {
            amount = a;
            text = $"{nr}. Abschlag gemäß {label}";
        }
        else throw new ArgumentException("Bitte einen Prozentsatz oder Betrag angeben.");
        if (amount <= 0) throw new ArgumentException("Der Abschlag muss größer als 0 sein.");
        var before = others.Where(d => d.InvoiceType == Partial).Sum(d => d.TotalCents);
        if (before + amount > src.TotalCents)
            throw new ArgumentException($"Alle Abschläge zusammen wären höher als die Summe von {FinancePdf.Money(src.TotalCents)}. Schon abgerechnet oder als Entwurf: {FinancePdf.Money(before)}.");
        var draft = CreateDraft(db, new DocInput(Invoice, Today(), src.ServiceDate, null, src.Subject, "", "", src.Recipient, src.ContactUid,
            [new LineInput(text, 1, "", amount)], src.Customer, src.Template));
        db.Exec("UPDATE fin_docs SET ref_id = $r, invoice_type = 'partial' WHERE id = $d", ("$r", sourceId), ("$d", draft.Id));
        return Doc(db, draft.Id);
    }

    /// <summary>
    /// Schlussrechnung: alle Positionen der Quelle, darunter je ausgestellter Abschlagsrechnung eine Position, die ihren Betrag abzieht. Offene Entwürfe
    /// von Abschlagsrechnungen müssen vorher ausgestellt oder gelöscht sein.
    /// </summary>
    public static FinDoc FinalInvoice(UserDb db, long sourceId)
    {
        var src = RequireSource(db, sourceId);
        var others = InvoicesOf(db, sourceId);
        if (others.Any(d => d.InvoiceType == Final)) throw new ArgumentException("Zu diesem Beleg gibt es schon eine Schlussrechnung.");
        if (others.Any(d => d.InvoiceType == Partial && d.State == Draft))
            throw new ArgumentException("Es gibt noch einen Entwurf einer Abschlagsrechnung zu diesem Beleg. Bitte zuerst ausstellen oder löschen.");
        var lines = src.Lines.Select(l => new LineInput(l.Description, l.QtyMilli / 1000m, l.Unit, l.PriceCents)).ToList();
        foreach (var p in others.Where(d => d.InvoiceType == Partial).OrderBy(d => d.DocDate).ThenBy(d => d.Id))
            lines.Add(new LineInput($"{DeductPrefix}{p.Number} vom {FinancePdf.Day(p.DocDate)}", 1, "", -p.TotalCents));
        var draft = CreateDraft(db, new DocInput(Invoice, Today(), src.ServiceDate, null, src.Subject, src.Intro, src.Outro, src.Recipient, src.ContactUid, lines, src.Customer, src.Template));
        db.Exec("UPDATE fin_docs SET ref_id = $r, invoice_type = 'final' WHERE id = $d", ("$r", sourceId), ("$d", draft.Id));
        return Doc(db, draft.Id);
    }

    /// <summary>Beim Ausstellen von Abschlags- und Schlussrechnungen: Stimmt der Bezug zu den anderen Rechnungen noch?</summary>
    private static void CheckSeries(UserDb db, FinDoc doc)
    {
        if (doc.Kind != Invoice || doc.InvoiceType == "" || doc.RefId is not { } source) return;
        var src = Doc(db, source);
        var issued = InvoicesOf(db, source).Where(d => d.State == IssuedState && d.Id != doc.Id).ToList();
        if (issued.Any(d => d.InvoiceType == Final)) throw new ArgumentException("Zu diesem Beleg ist schon eine Schlussrechnung ausgestellt.");
        var partials = issued.Where(d => d.InvoiceType == Partial).ToList();
        if (doc.InvoiceType == Partial)
        {
            if (doc.TotalCents <= 0) throw new ArgumentException("Der Abschlag muss größer als 0 sein.");
            if (partials.Sum(d => d.TotalCents) + doc.TotalCents > src.TotalCents) throw new ArgumentException("Alle Abschläge zusammen wären höher als die Summe des Belegs.");
            return;
        }
        // Schlussrechnung: genau die ausgestellten Abschläge abgezogen, sonst neu erzeugen lassen
        var deducted = doc.Lines.Where(l => l.Description.StartsWith(DeductPrefix, StringComparison.Ordinal)).ToList();
        if (deducted.Count != partials.Count || deducted.Sum(l => l.TotalCents) != -partials.Sum(d => d.TotalCents)
            || partials.Any(p => !deducted.Any(l => l.Description.StartsWith(DeductPrefix + p.Number + " ", StringComparison.Ordinal))))
            throw new ArgumentException("Die abgezogenen Abschlagsrechnungen stimmen nicht mit den ausgestellten überein. Bitte den Entwurf löschen und die Schlussrechnung neu erzeugen.");
        if (InvoicesOf(db, source).Any(d => d.InvoiceType == Partial && d.State == Draft))
            throw new ArgumentException("Es gibt noch einen Entwurf einer Abschlagsrechnung zu diesem Beleg. Bitte zuerst ausstellen oder löschen.");
        if (doc.TotalCents < 0) throw new ArgumentException("Die Schlussrechnung ergäbe einen negativen Betrag.");
    }

    /// <summary>Was vor dem Ausstellen da sein muss; Meldungen in Worten der Oberfläche.</summary>
    private static void CheckIssue(FinDoc doc, FinanceSettings s)
    {
        if (s.Name.Trim().Length == 0 || s.Address.Trim().Length == 0) throw new ArgumentException("Bitte zuerst unter Einstellungen Name und Anschrift des Unternehmens eintragen.");
        if (doc.Kind is Invoice or Cancel && s.TaxNumber.Trim().Length == 0 && s.VatId.Trim().Length == 0) throw new ArgumentException("Bitte zuerst unter Einstellungen die Steuernummer oder USt-IdNr. eintragen.");
        if (doc.Recipient.Trim().Length == 0) throw new ArgumentException("Bitte den Empfänger mit Anschrift eintragen.");
        if (doc.Lines.Count == 0) throw new ArgumentException("Bitte mindestens eine Position eintragen.");
        if (doc.Lines.Any(l => l.Description.Trim().Length == 0)) throw new ArgumentException("Bitte bei jeder Position die Leistung beschreiben.");
        if (doc.DocDate == null) throw new ArgumentException("Bitte das Belegdatum eintragen.");
        if (doc.Kind == Invoice && doc.ServiceDate.Trim().Length == 0) throw new ArgumentException("Bitte das Datum oder den Zeitraum der Leistung eintragen.");
    }

    /// <summary>Ausstellen in einem Zug: prüfen, Nummer vergeben, Absender einfrieren, PDF erzeugen, Prüfsumme, Protokoll. Danach ist der Beleg unveränderlich.</summary>
    public static FinDoc Issue(UserDb db, long id)
    {
        var doc = RequireDraft(db, id);
        var s = Settings(db);
        CheckIssue(doc, s);
        CheckSeries(db, doc);
        IssueCore(db, doc, s);
        return Doc(db, id);
    }

    private static void IssueCore(UserDb db, FinDoc doc, FinanceSettings s)
    {
        if (NumbersToConfirm(db)) throw new ArgumentException("Nach einer Wiederherstellung können Nummern doppelt vergeben werden. Bitte zuerst unter Einstellungen die nächsten Nummern prüfen und bestätigen.");
        var year = int.Parse(doc.DocDate!.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var series = SeriesOf(doc.Kind);
        using var conn = db.Open();
        // Eine Schreibtransaktion je Benutzerdatenbank: Zwei gleichzeitige Ausstellungen bekommen verschiedene Nummern
        using var tx = conn.BeginTransaction();
        var nr = UserDb.Query(conn, "SELECT COALESCE((SELECT last FROM fin_series WHERE series = $s AND year = $y), 0) + 1", r => r.GetInt64(0), ("$s", series), ("$y", year)).First();
        UserDb.Exec(conn, "INSERT INTO fin_series(series, year, last) VALUES($s,$y,$l) ON CONFLICT(series, year) DO UPDATE SET last = $l", ("$s", series), ("$y", year), ("$l", nr));
        var number = Format(PatternOf(s, series), year, nr);
        var sender = System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var issued = doc with { Number = number, Sender = sender };
        var pdf = FinanceTemplates.Pdf(issued, s, LayoutOf(db, doc.Template), draft: false);
        var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
        UserDb.Exec(conn, "UPDATE fin_docs SET state = 'issued', number = $n, sender = $s, pdf = $p, pdf_sha = $h, issued = $t, modified = $t WHERE id = $i AND state = 'draft'",
            ("$n", number), ("$s", sender), ("$p", pdf), ("$h", sha), ("$t", Unix()), ("$i", doc.Id));
        Log(conn, "doc.issue", doc.Id.ToString(CultureInfo.InvariantCulture), $"{Heading(doc)} {number} {FinancePdf.Money(doc.TotalCents)}, Prüfsumme {sha}");
        tx.Commit();
    }

    /// <summary>Stornorechnung: eigener Beleg mit eigener Nummer und Bezug, die Rechnung bleibt unverändert.</summary>
    public static FinDoc CancelInvoice(UserDb db, long id, string? date)
    {
        var inv = Doc(db, id);
        if (inv.Kind != Invoice || inv.State != IssuedState) throw new ArgumentException("Stornieren lässt sich nur eine ausgestellte Rechnung.");
        if (inv.Cancelled) throw new ArgumentException("Diese Rechnung ist schon storniert.");
        if (inv.PaidCents != 0) throw new ArgumentException("Für diese Rechnung ist eine Zahlung erfasst. Bitte zuerst den Eintrag korrigieren.");
        // Eine Schlussrechnung hat diesen Abschlag abgezogen; ohne ihn stimmte sie nicht mehr
        if (inv.InvoiceType == Partial && inv.RefId is { } src && InvoicesOf(db, src).Any(d => d.InvoiceType == Final && d.State == IssuedState))
            throw new ArgumentException("Zu diesem Beleg ist eine Schlussrechnung ausgestellt, die diesen Abschlag abzieht. Bitte zuerst die Schlussrechnung stornieren.");
        var day = CheckDate(date);
        var s = Settings(db);
        CheckIssue(inv with { Kind = Cancel, DocDate = day }, s);
        long cancelId;
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            UserDb.Exec(conn, @"INSERT INTO fin_docs(kind, doc_date, service_date, subject, recipient, contact_uid, template, customer, customer_number, total_cents, ref_id, created, modified)
                VALUES('cancel', $d, $sd, $s, $r, $c, $tp, $cu, $cn, $t, $ref, $n, $n)", ("$tp", inv.Template), ("$cu", inv.Customer), ("$cn", inv.CustomerNumber), ("$d", day), ("$sd", inv.ServiceDate), ("$s", inv.Subject), ("$r", inv.Recipient), ("$c", inv.ContactUid),
                ("$t", -inv.TotalCents), ("$ref", id), ("$n", Unix()));
            cancelId = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
            var pos = 0;
            foreach (var l in inv.Lines)
                UserDb.Exec(conn, "INSERT INTO fin_lines(doc, pos, description, qty_milli, unit, price_cents, total_cents) VALUES($d,$p,$t,$q,$u,$c,$s)",
                    ("$d", cancelId), ("$p", pos++), ("$t", l.Description), ("$q", l.QtyMilli), ("$u", l.Unit), ("$c", -l.PriceCents), ("$s", -l.TotalCents));
            tx.Commit();
        }
        IssueCore(db, Doc(db, cancelId), s);
        return Doc(db, cancelId);
    }

    /// <summary>Zahlungseingang: legt den Eintrag bei den Einnahmen an (zählt zum Tag der Zahlung).</summary>
    public static FinEntry Pay(UserDb db, long id, string? date, long? amountCents, string? category)
    {
        var inv = Doc(db, id);
        if (inv.Kind != Invoice || inv.State != IssuedState) throw new ArgumentException("Eine Zahlung lässt sich nur zu einer ausgestellten Rechnung erfassen.");
        if (inv.Cancelled) throw new ArgumentException("Diese Rechnung ist storniert.");
        var open = inv.TotalCents - inv.PaidCents;
        if (open <= 0) throw new ArgumentException("Diese Rechnung ist schon bezahlt.");
        var cat = category ?? Categories(db).First(c => c.Kind == Income && !IsUnassigned(c.Id)).Id;
        var entry = new EntryInput(Income, date, amountCents ?? open, cat, "Rechnung " + inv.Number, inv.Recipient.Split('\n')[0], inv.DmsDoc, inv.DmsTresor);
        return AddEntry(db, entry, invoice: id);
    }

    public static byte[] Pdf(UserDb db, long id)
    {
        var doc = Doc(db, id);
        if (doc.State == Draft) return FinanceTemplates.Pdf(doc, Settings(db), LayoutOf(db, doc.Template), draft: true);
        return (byte[])db.Scalar("SELECT pdf FROM fin_docs WHERE id = $i", ("$i", id))!;
    }

    /// <summary>Stimmt das gespeicherte PDF noch mit der Prüfsumme vom Ausstellen überein?</summary>
    public static bool PdfIntact(UserDb db, long id)
    {
        var doc = Doc(db, id);
        return doc.PdfSha != null && Convert.ToHexString(SHA256.HashData(Pdf(db, id))).Equals(doc.PdfSha, StringComparison.OrdinalIgnoreCase);
    }

    public static void RememberDmsCopy(UserDb db, long doc, long dmsDoc, bool tresor) =>
        db.Exec("INSERT OR REPLACE INTO fin_doc_dms(doc, dms_doc, tresor) VALUES($d,$m,$t)", ("$d", doc), ("$m", dmsDoc), ("$t", tresor ? 1 : 0));

    /// <summary>Ist dieses Dokument als Beleg oder Kopie verknüpft? Kennungen gelten je Bereich.</summary>
    public static bool Linked(UserDb db, long dmsDoc, bool tresor)
    {
        Ensure(db);
        return db.Scalar("SELECT 1 FROM fin_entries WHERE dms_doc = $d AND dms_tresor = $t UNION ALL SELECT 1 FROM fin_doc_dms WHERE dms_doc = $d AND tresor = $t UNION ALL SELECT 1 FROM fin_recurring WHERE dms_doc = $d AND dms_tresor = $t LIMIT 1", ("$d", dmsDoc), ("$t", tresor ? 1 : 0)) != null;
    }

    /// <summary>Wie viele Einträge und Belege auf Dokumente im Tresor zeigen (Warnung beim Tresor löschen).</summary>
    public static long TresorLinks(UserDb db)
    {
        Ensure(db);
        return Convert.ToInt64(db.Scalar("SELECT (SELECT COUNT(*) FROM fin_entries WHERE dms_doc IS NOT NULL AND dms_tresor = 1) + (SELECT COUNT(*) FROM fin_doc_dms WHERE tresor = 1) + (SELECT COUNT(*) FROM fin_recurring WHERE dms_doc IS NOT NULL AND dms_tresor = 1)"));
    }
}
