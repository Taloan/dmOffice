using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Dms;
using DmOffice.Finance;

namespace DmOffice.Tests;

/// <summary>Finanzen und das Merkmal „Fixiert“ im DMS.</summary>
[Collection("server")]
public class FinanceTests : IClassFixture<ChatFixture>, IDisposable
{
    private readonly ChatFixture _fx;
    private readonly List<string> _before;

    public FinanceTests(ChatFixture fx)
    {
        _fx = fx;
        _before = Features.InReleaseOff.ToList();
        Features.Set(_before.Where(x => x != Features.Finance));
    }

    public void Dispose() => Features.Set(_before);

    private static UserDb FreshDb(string name) => new(Path.Combine(Directory.CreateTempSubdirectory(name).FullName, "u.db"), new byte[32], "t");

    private static List<FinEntry> Booked(UserDb db, long series) =>
        db.Query("SELECT id, date FROM fin_entries WHERE recurring = $r ORDER BY date", r => new FinEntry(r.GetInt64(0), "", r.GetString(1), 0, "", "", "", null, null, null), ("$r", series));

    [Fact]
    public void Wiederkehrend_mit_Anzahl_bucht_faellige_Termine_und_haelt_am_Monatsende()
    {
        var db = FreshDb("fin-wk-anzahl");
        FinanceStore.Ensure(db);
        var cat = FinanceStore.Categories(db).First(c => c.Kind == "expense").Id;
        var r = FinanceStore.AddRecurring(db, new FinanceStore.RecurringInput("expense", 9900, cat, "Miete", "Vermieter", "month", "2025-01-31", null, 4), new DateOnly(2025, 3, 15));
        // Bis zum 15.03. fällig: 31.01. und 28.02.; der Beginn am 31. bleibt danach der 31.
        Assert.Equal(["2025-01-31", "2025-02-28"], Booked(db, r.Id).Select(e => e.Date));
        Assert.Equal("2025-03-31", r.Next);
        // Nochmals buchen bucht nichts doppelt
        FinanceStore.BookDue(db, new DateOnly(2025, 3, 15));
        Assert.Equal(2, Booked(db, r.Id).Count);
        // Später: genau vier, dann Schluss
        FinanceStore.BookDue(db, new DateOnly(2026, 1, 1));
        Assert.Equal(["2025-01-31", "2025-02-28", "2025-03-31", "2025-04-30"], Booked(db, r.Id).Select(e => e.Date));
        var done = FinanceStore.Recurring(db, r.Id);
        Assert.Equal(4, done.Done);
        Assert.Null(done.Next);
        var e = FinanceStore.Entries(db, 2025).First(x => x.Recurring == r.Id);
        Assert.Equal(("expense", 9900L, "Miete", "Vermieter"), (e.Kind, e.AmountCents, e.Text, e.Party));
    }

    [Fact]
    public void Wiederkehrend_mit_Enddatum_und_ohne_Ende()
    {
        var db = FreshDb("fin-wk-ende");
        FinanceStore.Ensure(db);
        var income = FinanceStore.Categories(db).First(c => c.Kind == "income").Id;
        var weekly = FinanceStore.AddRecurring(db, new FinanceStore.RecurringInput("income", 5000, income, "Kurs", "", "week", "2025-01-06", "2025-01-27", null), new DateOnly(2025, 12, 31));
        Assert.Equal(["2025-01-06", "2025-01-13", "2025-01-20", "2025-01-27"], Booked(db, weekly.Id).Select(e => e.Date));
        Assert.Null(weekly.Next);

        // Ohne Ende, jährlich: Ein Termin in einem abgeschlossenen Jahr wird übersprungen, die Reihe läuft weiter
        FinanceStore.CloseYear(db, 2024);
        var yearly = FinanceStore.AddRecurring(db, new FinanceStore.RecurringInput("income", 12000, income, "Wartung", "", "year", "2024-06-01", null, null), new DateOnly(2026, 7, 1));
        Assert.Equal(["2025-06-01", "2026-06-01"], Booked(db, yearly.Id).Select(e => e.Date));
        Assert.Equal(3, yearly.Done);
        Assert.Equal("2027-06-01", yearly.Next);
        Assert.Contains(FinanceStore.LogEntries(db), l => l.Action == "recurring.skip" && l.Detail.StartsWith("2024-06-01", StringComparison.Ordinal));

        // Beenden: nichts mehr gebucht, gebuchte Einträge bleiben
        var stopped = FinanceStore.StopRecurring(db, yearly.Id);
        Assert.Null(stopped.Next);
        FinanceStore.BookDue(db, new DateOnly(2028, 1, 1));
        Assert.Equal(2, Booked(db, yearly.Id).Count);
    }

    [Fact]
    public void Wiederkehrend_unzulaessige_Angaben()
    {
        var db = FreshDb("fin-wk-pruefen");
        FinanceStore.Ensure(db);
        var cat = FinanceStore.Categories(db).First(c => c.Kind == "expense").Id;
        FinanceStore.RecurringInput Input(string interval = "month", string start = "2025-01-01", string? end = null, int? count = null) =>
            new("expense", 1000, cat, "Test", "", interval, start, end, count);
        Assert.Throws<ArgumentException>(() => FinanceStore.AddRecurring(db, Input(end: "2025-12-31", count: 3)));
        Assert.Throws<ArgumentException>(() => FinanceStore.AddRecurring(db, Input(end: "2024-12-31")));
        Assert.Throws<ArgumentException>(() => FinanceStore.AddRecurring(db, Input(count: 0)));
        Assert.Throws<ArgumentException>(() => FinanceStore.AddRecurring(db, Input(interval: "daily")));
        // Nach der ersten Buchung stehen erster Termin und Rhythmus fest; Betrag und Ende lassen sich ändern
        var r = FinanceStore.AddRecurring(db, Input(), new DateOnly(2025, 2, 15));
        Assert.Throws<ArgumentException>(() => FinanceStore.UpdateRecurring(db, r.Id, Input(start: "2025-01-02"), new DateOnly(2025, 2, 15)));
        Assert.Throws<ArgumentException>(() => FinanceStore.UpdateRecurring(db, r.Id, Input(interval: "quarter"), new DateOnly(2025, 2, 15)));
        Assert.Throws<ArgumentException>(() => FinanceStore.UpdateRecurring(db, r.Id, Input(count: 1), new DateOnly(2025, 2, 15)));
        var changed = FinanceStore.UpdateRecurring(db, r.Id, Input(count: 3) with { AmountCents = 2000 }, new DateOnly(2025, 2, 15));
        Assert.Equal((2000L, 3, "2025-03-01"), (changed.AmountCents, changed.Count!.Value, changed.Next));
        // Kategorie in Gebrauch: nicht löschbar, auch wenn noch kein Eintrag sie hätte
        var other = FinanceStore.AddCategory(db, "expense", "Nur wiederkehrend");
        FinanceStore.AddRecurring(db, Input(start: "2099-01-01") with { Category = other.Id });
        Assert.Throws<ArgumentException>(() => FinanceStore.DeleteCategory(db, other.Id));
    }

    [Fact]
    public async Task Wiederkehrend_mit_Grundlage_aus_dem_DMS()
    {
        var space = _fx.Space("cleo");
        var db = space.Db;
        FinanceStore.Ensure(db);
        var cat = FinanceStore.Categories(db).First(c => c.Kind == "expense").Id;
        var vertrag = await Import("cleo", "abo.txt", "Abovertrag Software monatlich");
        var r = FinanceStore.AddRecurring(db, new FinanceStore.RecurringInput("expense", 1990, cat, "Abo", "", "month", "2025-01-01", null, 2, vertrag.Id), new DateOnly(2025, 6, 1));
        // Das Dokument hängt an der Wiederholung, wird fixiert und gilt als verknüpft; die Buchungen haben keinen eigenen Beleg
        Assert.Equal((vertrag.Id, false), (r.DmsDoc, r.DmsTresor));
        Assert.True(Dms.Dms.Get(db, vertrag.Id).Fixed);
        Assert.True(FinanceStore.Linked(db, vertrag.Id, false));
        Assert.All(FinanceStore.Entries(db, 2025).Where(e => e.Recurring == r.Id), e => Assert.Null(e.DmsDoc));
        // Ändern ohne Dokument: Die Wiederholung verliert die Grundlage, das Dokument bleibt fixiert
        var changed = FinanceStore.UpdateRecurring(db, r.Id, new FinanceStore.RecurringInput("expense", 1990, cat, "Abo", "", "month", "2025-01-01", null, 2), new DateOnly(2025, 6, 1));
        Assert.Null(changed.DmsDoc);
        Assert.True(Dms.Dms.Get(db, vertrag.Id).Fixed);
    }

    [Fact]
    public async Task Im_DMS_als_Ausgabe_gekennzeichnet_mit_eigenem_Betrag()
    {
        var space = _fx.Space("dora");
        var doc = await Import("dora", "tankquittung.txt", "Tankstelle Quittung 49,90 EUR");
        Dms.Dms.Apply(space.Db, [doc.Id], new Dms.Dms.Change(null, "2026-03-04", null, null, null, null, null, null, null, "4990"));
        // Kennzeichnen: Eintrag mit Platzhalter-Kategorie, Betrag des Dokuments als Vorschlag, Beleg fixiert
        var b = FinanceStore.BookFromDms(space.Db, space.Db, doc.Id, "expense", null, null)!;
        Assert.Equal(("expense", 4990L, "2026-03-04", true), (b.Kind, b.AmountCents, b.Date, b.Unassigned));
        Assert.True(Dms.Dms.Get(space.Db, doc.Id).Fixed);
        var e = FinanceStore.Entry(space.Db, b.Entry);
        Assert.Equal(FinanceStore.UnassignedExpense, e.Category);
        Assert.Equal(doc.Id, e.DmsDoc);
        // Abweichender Betrag in der Buchung: das Dokument behält seinen
        b = FinanceStore.BookFromDms(space.Db, space.Db, doc.Id, "expense", 4500, null)!;
        Assert.Equal(4500L, b.AmountCents);
        Assert.Equal(4990L, Dms.Dms.Get(space.Db, doc.Id).Amount);
        // Kein zweiter Eintrag; andere Art setzt die Kategorie zurück
        b = FinanceStore.BookFromDms(space.Db, space.Db, doc.Id, "income", null, null)!;
        Assert.Equal(e.Id, b.Entry);
        Assert.Equal(FinanceStore.UnassignedIncome, FinanceStore.Entry(space.Db, b.Entry).Category);
        // Platzhalter lässt sich nicht löschen
        Assert.Throws<ArgumentException>(() => FinanceStore.DeleteCategory(space.Db, FinanceStore.UnassignedIncome));
        // Nicht mehr gebucht: Eintrag weg
        Assert.Null(FinanceStore.BookFromDms(space.Db, space.Db, doc.Id, null, null, null));
        Assert.Null(FinanceStore.BookingOf(space.Db, doc.Id, false));
        Assert.Throws<KeyNotFoundException>(() => FinanceStore.Entry(space.Db, e.Id));
    }

    [Fact]
    public void Datenbank_von_vor_den_Vorlagen_wird_ergaenzt_und_Belege_bleiben_lesbar()
    {
        var dir = Directory.CreateTempSubdirectory("fin-alt");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        // fin_docs, wie sie vor den Vorlagen angelegt wurde
        db.Exec(@"CREATE TABLE fin_docs (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL CHECK(kind IN ('offer','invoice','cancel')),
            state TEXT NOT NULL DEFAULT 'draft' CHECK(state IN ('draft','issued')), number TEXT NULL, doc_date TEXT NULL, service_date TEXT NOT NULL DEFAULT '',
            due_date TEXT NULL, subject TEXT NOT NULL DEFAULT '', intro TEXT NOT NULL DEFAULT '', outro TEXT NOT NULL DEFAULT '', recipient TEXT NOT NULL DEFAULT '',
            contact_uid TEXT NULL, customer INTEGER NULL, customer_number TEXT NOT NULL DEFAULT '', total_cents INTEGER NOT NULL DEFAULT 0, ref_id INTEGER NULL, sender TEXT NULL, pdf BLOB NULL, pdf_sha TEXT NULL, issued INTEGER NULL,
            created INTEGER NOT NULL, modified INTEGER NOT NULL);
            INSERT INTO fin_docs(kind, state, number, subject, created, modified) VALUES('invoice', 'issued', 'RE-2026-0001', 'Alt', 1, 1);
            INSERT INTO fin_docs(kind, subject, created, modified) VALUES('offer', 'Entwurf', 1, 1);");

        var docs = FinanceStore.Docs(db, null);

        Assert.Equal(2, docs.Count);
        Assert.All(docs, d => Assert.Null(d.Template));
        Assert.Contains(docs, d => d.Number == "RE-2026-0001");
        Assert.NotEmpty(FinanceStore.Templates(db));
    }

    [Fact]
    public void Datenbank_von_vor_den_Auftraegen_wird_umgebaut_und_bleibt_geschuetzt()
    {
        var dir = Directory.CreateTempSubdirectory("fin-auftrag");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        // fin_docs mit den Belegarten vor den Aufträgen, ausgestellte Rechnung mit Position und dem Schutz-Trigger von damals
        db.Exec(@"CREATE TABLE fin_docs (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL CHECK(kind IN ('offer','invoice','cancel')),
            state TEXT NOT NULL DEFAULT 'draft' CHECK(state IN ('draft','issued')), number TEXT NULL, doc_date TEXT NULL, service_date TEXT NOT NULL DEFAULT '',
            due_date TEXT NULL, subject TEXT NOT NULL DEFAULT '', intro TEXT NOT NULL DEFAULT '', outro TEXT NOT NULL DEFAULT '', recipient TEXT NOT NULL DEFAULT '',
            contact_uid TEXT NULL, template INTEGER NULL, customer INTEGER NULL, customer_number TEXT NOT NULL DEFAULT '', total_cents INTEGER NOT NULL DEFAULT 0, ref_id INTEGER NULL, sender TEXT NULL, pdf BLOB NULL, pdf_sha TEXT NULL, issued INTEGER NULL,
            created INTEGER NOT NULL, modified INTEGER NOT NULL);
            CREATE TABLE fin_lines (id INTEGER PRIMARY KEY AUTOINCREMENT, doc INTEGER NOT NULL, pos INTEGER NOT NULL, description TEXT NOT NULL,
            qty_milli INTEGER NOT NULL, unit TEXT NOT NULL DEFAULT '', price_cents INTEGER NOT NULL, total_cents INTEGER NOT NULL);
            INSERT INTO fin_docs(kind, state, number, subject, total_cents, created, modified) VALUES('invoice', 'issued', 'RE-2026-0001', 'Alt', 5000, 1, 1);
            INSERT INTO fin_lines(doc, pos, description, qty_milli, price_cents, total_cents) VALUES(1, 0, 'Altes', 1000, 5000, 5000);
            CREATE TRIGGER fin_docs_no_update BEFORE UPDATE ON fin_docs WHEN OLD.state = 'issued' BEGIN SELECT RAISE(ABORT, 'gesperrt'); END;");

        var old = FinanceStore.Doc(db, 1);
        Assert.Equal(("RE-2026-0001", "issued", 5000L, ""), (old.Number, old.State, old.TotalCents, old.InvoiceType));
        Assert.Single(old.Lines);
        Assert.ThrowsAny<Exception>(() => db.Exec("UPDATE fin_docs SET subject = 'neu' WHERE id = 1"));
        Assert.ThrowsAny<Exception>(() => db.Exec("DELETE FROM fin_lines WHERE doc = 1"));
        var order = FinanceStore.CreateDraft(db, new FinanceStore.DocInput("order", "2026-03-10", "", null, "Auftrag", "", "", "Kunde", null, []));
        Assert.Equal("order", order.Kind);
        // Neue Belege zählen nach der höchsten alten Kennung weiter
        Assert.True(order.Id > 1);
    }

    [Fact]
    public async Task Angebot_Auftrag_Abschlaege_und_Schlussrechnung()
    {
        var c = await Session("dora");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var offer = await Json(await c.PostAsJsonAsync("/api/finance/docs", Draft("offer")));
        var offerId = offer.GetProperty("id").GetInt64();
        await Json(await c.PostAsync($"/api/finance/docs/{offerId}/issue", null));

        // Auftrag aus dem Angebot, überarbeitet und ausgestellt (Auftragsbestätigung mit eigener Nummer)
        var order = await Json(await c.PostAsJsonAsync($"/api/finance/docs/{offerId}/copy", new { kind = "order" }));
        var orderId = order.GetProperty("id").GetInt64();
        Assert.Equal("offer", order.GetProperty("refKind").GetString());
        await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{orderId}/partial", new { percent = 30 }), HttpStatusCode.BadRequest);
        var changed = new { kind = "order", docDate = "2026-03-12", serviceDate = "März bis April 2026", subject = "Beratung", recipient = "Erika Beispiel\nTestgasse 2\n54321 Testdorf",
            lines = new object[] { new { description = "Beratung", qty = 10m, unit = "Std.", priceCents = 10000 } } };
        Assert.True((await c.PutAsJsonAsync($"/api/finance/docs/{orderId}", changed)).IsSuccessStatusCode);
        var confirmed = (await Json(await c.PostAsync($"/api/finance/docs/{orderId}/issue", null))).GetProperty("doc");
        Assert.StartsWith("AU-2026-", confirmed.GetProperty("number").GetString());
        Assert.Contains(PdfTitle("Auftragsbestätigung"), Encoding.Latin1.GetString(await c.GetByteArrayAsync($"/api/finance/docs/{orderId}/pdf")));

        // Erster Abschlag 30 % = 300 €, zweiter fest 200 €; zusammen nicht über 1.000 €
        var p1 = await Json(await c.PostAsJsonAsync($"/api/finance/docs/{orderId}/partial", new { percent = 30 }));
        Assert.Equal(("partial", 30000L), (p1.GetProperty("invoiceType").GetString(), p1.GetProperty("totalCents").GetInt64()));
        await Json(await c.PostAsync($"/api/finance/docs/{p1.GetProperty("id").GetInt64()}/issue", null));
        await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{orderId}/partial", new { amountCents = 80000 }), HttpStatusCode.BadRequest);
        var p2 = await Json(await c.PostAsJsonAsync($"/api/finance/docs/{orderId}/partial", new { amountCents = 20000 }));
        // Schlussrechnung erst, wenn kein Abschlag mehr Entwurf ist
        await Refused(await c.PostAsync($"/api/finance/docs/{orderId}/final", null), HttpStatusCode.BadRequest);
        var p2issued = (await Json(await c.PostAsync($"/api/finance/docs/{p2.GetProperty("id").GetInt64()}/issue", null))).GetProperty("doc");

        var fin = await Json(await c.PostAsync($"/api/finance/docs/{orderId}/final", null));
        var finId = fin.GetProperty("id").GetInt64();
        Assert.Equal(("final", 50000L), (fin.GetProperty("invoiceType").GetString(), fin.GetProperty("totalCents").GetInt64()));
        Assert.Contains(fin.GetProperty("lines").EnumerateArray(), l => l.GetProperty("description").GetString()!.StartsWith(FinanceStore.DeductPrefix + p2issued.GetProperty("number").GetString()));
        await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{orderId}/partial", new { amountCents = 100 }), HttpStatusCode.BadRequest);

        // Abgezogene Abschläge im Entwurf verändert: Ausstellen wird abgelehnt
        var tampered = new { kind = "invoice", docDate = "2026-03-20", serviceDate = "März 2026", subject = "Beratung", recipient = "Erika Beispiel\nTestgasse 2\n54321 Testdorf",
            lines = new object[] { new { description = "Beratung", qty = 10m, unit = "Std.", priceCents = 10000 }, new { description = FinanceStore.DeductPrefix + "RE-x vom 1.1.2026", qty = 1m, unit = "", priceCents = -30000 } } };
        Assert.True((await c.PutAsJsonAsync($"/api/finance/docs/{finId}", tampered)).IsSuccessStatusCode);
        await Refused(await c.PostAsync($"/api/finance/docs/{finId}/issue", null), HttpStatusCode.BadRequest);
        Assert.True((await c.DeleteAsync($"/api/finance/docs/{finId}")).IsSuccessStatusCode);

        fin = await Json(await c.PostAsync($"/api/finance/docs/{orderId}/final", null));
        finId = fin.GetProperty("id").GetInt64();
        var finDraft = new { kind = "invoice", docDate = "2026-03-20", serviceDate = "März 2026", subject = "Beratung", recipient = "Erika Beispiel\nTestgasse 2\n54321 Testdorf",
            lines = fin.GetProperty("lines").EnumerateArray().Select(l => new { description = l.GetProperty("description").GetString(), qty = l.GetProperty("qtyMilli").GetInt64() / 1000m, unit = l.GetProperty("unit").GetString(), priceCents = l.GetProperty("priceCents").GetInt64() }).ToArray() };
        Assert.True((await c.PutAsJsonAsync($"/api/finance/docs/{finId}", finDraft)).IsSuccessStatusCode);
        var finIssued = (await Json(await c.PostAsync($"/api/finance/docs/{finId}/issue", null))).GetProperty("doc");
        Assert.Equal(50000L, finIssued.GetProperty("totalCents").GetInt64());
        Assert.Contains(PdfTitle("Schlussrechnung"), Encoding.Latin1.GetString(await c.GetByteArrayAsync($"/api/finance/docs/{finId}/pdf")));

        // Auftrag: vollständig berechnet; Abschlag nicht stornierbar, solange die Schlussrechnung gilt
        var orderNow = await Json(await c.GetAsync($"/api/finance/docs/{orderId}"));
        Assert.Equal(100000L, orderNow.GetProperty("billedCents").GetInt64());
        await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{p1.GetProperty("id").GetInt64()}/cancel", new { date = "2026-03-25" }), HttpStatusCode.BadRequest);
        // Zahlung zur Schlussrechnung wie bei jeder Rechnung
        Assert.True((await c.PostAsJsonAsync($"/api/finance/docs/{finId}/pay", new { date = "2026-03-28" })).IsSuccessStatusCode);
    }

    /// <summary>Titel in den PDF-Metadaten, wie Skia ihn schreibt: reines ASCII als Klartext, sonst UTF-16BE als Hex.</summary>
    private static string PdfTitle(string text) => text.All(char.IsAscii) ? "/Title (" + text : "/Title <FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    private async Task<HttpClient> Session(string login)
    {
        var c = _fx.Factory.CreateClient(new() { HandleCookies = true });
        Assert.True((await c.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).IsSuccessStatusCode);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> Refused(HttpResponseMessage r, HttpStatusCode status)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"{r.StatusCode}: {text}");
        return text;
    }

    private async Task<DmsDoc> Import(string login, string name, string text)
    {
        var space = _fx.Space(login);
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes(text)), name, "text/plain", new DmsImport.Info("upload"), default);
        await DmsImport.ProcessQueueAsync(space, DmsHost.Current!, 100, default);
        return Dms.Dms.Get(space.Db, r.Id);
    }

    private static object Company() => new
    {
        settings = new FinanceSettings { Name = "Muster Technik", Address = "Beispielweg 1\n12345 Musterstadt", TaxNumber = "12/345/67890", Iban = "DE00 0000 0000 0000 0000 00", Bank = "Testbank" },
    };

    [Fact]
    public async Task Beleg_per_Mail_Platzhalter_Kunde_und_Rolle_je_Vorgang()
    {
        var c = await Session("ben");
        var space = _fx.Space("ben");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        // Rolle „AGB“ mit einem ersten Dokument
        var role = (await Json(await c.PostAsJsonAsync("/api/dms/lists", new { kind = "role", name = "AGB Mailtest" }))).GetProperty("id").GetString()!;
        var agb1 = await Import("ben", "agb-alt.txt", "Allgemeine Geschaeftsbedingungen Fassung eins");
        var agb2 = await Import("ben", "agb-neu.txt", "Allgemeine Geschaeftsbedingungen Fassung zwei");
        Dms.Dms.Apply(space.Db, [agb1.Id], new Dms.Dms.Change(Role: role));
        var s = FinanceStore.Settings(space.Db);
        s.MailTemplates["offer"] = new FinMailTemplate { Subject = "Angebot {Nummer} für {Kunde}", Body = "Betrag {Betrag}\nGruß {Firma}", Roles = [role] };
        s.MailTemplates["order"] = new FinMailTemplate { Subject = "AB {Nummer}", Body = "x", Roles = [role] };
        s.MailTemplates["invoice"] = new FinMailTemplate { Subject = "RE {Nummer}", Body = "x", Roles = [role], Docs = [agb1.Id] };
        FinanceStore.SaveSettings(space.Db, s);
        var customer = (await Json(await c.PostAsJsonAsync("/api/finance/customers", new { name = "Erika Beispiel", address = "Testgasse 2", email = "erika@example.org" }))).GetProperty("id").GetInt64();
        await Refused(await c.PostAsJsonAsync("/api/finance/customers", new { name = "Falsch", email = "keine-adresse" }), HttpStatusCode.BadRequest);

        var offer = (await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "offer", docDate = "2026-03-10", serviceDate = "März 2026", subject = "Beratung",
            recipient = "Erika Beispiel\nTestgasse 2", customer, lines = new object[] { new { description = "Beratung", qty = 1m, unit = "", priceCents = 10000 } } }))).GetProperty("id").GetInt64();
        await Refused(await c.GetAsync($"/api/finance/docs/{offer}/mail"), HttpStatusCode.BadRequest);
        await Json(await c.PostAsync($"/api/finance/docs/{offer}/issue", null));
        var m = await Json(await c.GetAsync($"/api/finance/docs/{offer}/mail"));
        Assert.Equal("erika@example.org", m.GetProperty("to")[0].GetString());
        Assert.StartsWith("Angebot AN-", m.GetProperty("subject").GetString());
        Assert.EndsWith("für Erika Beispiel", m.GetProperty("subject").GetString());
        Assert.Contains("Muster Technik", m.GetProperty("html").GetString());
        var r = m.GetProperty("roles")[0];
        // Erster Beleg des Vorgangs: nichts festgehalten, die Rolle hat agb1
        Assert.Equal(JsonValueKind.Null, r.GetProperty("original").ValueKind);
        Assert.Equal(agb1.Id, r.GetProperty("current").GetProperty("id").GetInt64());
        Assert.True((await c.PutAsJsonAsync($"/api/finance/docs/{offer}/mail", new { roles = new[] { new { role, doc = agb1.Id, tresor = false } } })).IsSuccessStatusCode);

        // AGB geändert: der Auftrag erbt agb1 vom Angebot, die Rolle hat jetzt agb2
        Dms.Dms.Apply(space.Db, [agb2.Id], new Dms.Dms.Change(Role: role));
        var order = (await Json(await c.PostAsJsonAsync($"/api/finance/docs/{offer}/copy", new { kind = "order" }))).GetProperty("id").GetInt64();
        object Body(string kind) => new { kind, docDate = "2026-03-12", serviceDate = "März 2026", dueDate = "2026-03-26", subject = "Beratung", recipient = "Erika Beispiel\nTestgasse 2", customer,
            lines = new object[] { new { description = "Beratung", qty = 1m, unit = "", priceCents = 10000 } } };
        await Json(await c.PutAsJsonAsync($"/api/finance/docs/{order}", Body("order")));
        await Json(await c.PostAsync($"/api/finance/docs/{order}/issue", null));
        r = (await Json(await c.GetAsync($"/api/finance/docs/{order}/mail"))).GetProperty("roles")[0];
        Assert.Equal(agb1.Id, r.GetProperty("original").GetProperty("id").GetInt64());
        Assert.Equal(agb2.Id, r.GetProperty("current").GetProperty("id").GetInt64());
        // Aktualisierte gewählt: die Rechnung vergleicht mit dem Auftrag, nicht mit dem Angebot
        Assert.True((await c.PutAsJsonAsync($"/api/finance/docs/{order}/mail", new { roles = new[] { new { role, doc = agb2.Id, tresor = false } } })).IsSuccessStatusCode);
        var invoice = (await Json(await c.PostAsJsonAsync($"/api/finance/docs/{order}/copy", new { kind = "invoice" }))).GetProperty("id").GetInt64();
        await Json(await c.PutAsJsonAsync($"/api/finance/docs/{invoice}", Body("invoice")));
        await Json(await c.PostAsync($"/api/finance/docs/{invoice}/issue", null));
        var mi = await Json(await c.GetAsync($"/api/finance/docs/{invoice}/mail"));
        r = mi.GetProperty("roles")[0];
        Assert.Equal(agb2.Id, r.GetProperty("original").GetProperty("id").GetInt64());
        Assert.Equal(agb2.Id, r.GetProperty("current").GetProperty("id").GetInt64());
        Assert.Equal(agb1.Id, mi.GetProperty("docs")[0].GetProperty("id").GetInt64());
    }

    private static object Draft(string kind) => new
    {
        kind, docDate = "2026-03-10", serviceDate = "März 2026", dueDate = "2026-03-24", subject = "Beratung", intro = "Vielen Dank.", outro = "Mit freundlichen Grüßen",
        recipient = "Erika Beispiel\nTestgasse 2\n54321 Testdorf",
        lines = new object[] { new { description = "Beratung", qty = 2.5m, unit = "Std.", priceCents = 8000 }, new { description = "Fahrtkosten", qty = 1m, unit = "", priceCents = 1999 } },
    };

    [Fact]
    public async Task Fixiertes_Dokument_laesst_sich_auf_keinem_Weg_aendern_oder_loeschen_aber_verschieben()
    {
        var c = await Session("anna");
        var doc = await Import("anna", "fix-a.txt", "Fixiert Testinhalt Alpha");
        var db = _fx.Space("anna").Db;
        Assert.False(doc.Fixed);
        Assert.True((await Json(await c.PostAsJsonAsync($"/api/dms/docs/{doc.Id}/fixed", new { @fixed = true }))).GetProperty("fixed").GetBoolean());
        var path = Dms.Dms.Get(db, doc.Id).Path!;

        // DMS
        await Refused(await c.PostAsJsonAsync("/api/dms/docs/delete", new { ids = new[] { doc.Id } }), HttpStatusCode.Forbidden);
        // Dateiablage und damit WebDAV, Office und alle anderen Wege
        Assert.Throws<DavForbiddenException>(() => FileStore.Delete(db, path));
        await Assert.ThrowsAsync<DavForbiddenException>(() => FileStore.PutAsync(db, path, new MemoryStream("neu"u8.ToArray()), "text/plain"));
        // Ordner darüber
        var folder = FileStore.Parent(path)!;
        if (folder.Length > 0) Assert.Throws<DavForbiddenException>(() => FileStore.Delete(db, folder));
        // Überschreiben per Kopie
        await FileStore.PutAsync(db, "fix-andere.txt", new MemoryStream("x"u8.ToArray()), "text/plain");
        Assert.Throws<DavForbiddenException>(() => FileStore.MoveOrCopy(db, "fix-andere.txt", path, overwrite: true, copy: true));
        Assert.Equal(doc.Size, FileStore.Get(db, path)!.Size);

        // Verschieben bleibt möglich, die Sperre zieht mit
        FileStore.MoveOrCopy(db, path, "fix-verschoben.txt", overwrite: false, copy: false);
        Assert.Throws<DavForbiddenException>(() => FileStore.Delete(db, "fix-verschoben.txt"));

        // Angaben und Betrag bleiben änderbar
        var changed = await Json(await c.PutAsJsonAsync($"/api/dms/docs/{doc.Id}", new { title = "Neuer Titel", amount = "12345" }));
        Assert.Equal(12345, changed.GetProperty("amount").GetInt64());

        // Bewusst aufgehoben: wieder löschbar
        await Json(await c.PostAsJsonAsync($"/api/dms/docs/{doc.Id}/fixed", new { @fixed = false }));
        Assert.True((await c.PostAsJsonAsync("/api/dms/docs/delete", new { ids = new[] { doc.Id } })).IsSuccessStatusCode);
        Assert.Null(FileStore.Get(db, "fix-verschoben.txt"));
    }

    [Fact]
    public async Task Auswahl_mit_fixiertem_Dokument_loescht_nichts()
    {
        var c = await Session("anna");
        var a = await Import("anna", "fix-b1.txt", "Fixiert Testinhalt Beta eins");
        var b = await Import("anna", "fix-b2.txt", "Fixiert Testinhalt Beta zwei");
        await Json(await c.PostAsJsonAsync($"/api/dms/docs/{b.Id}/fixed", new { @fixed = true }));
        await Refused(await c.PostAsJsonAsync("/api/dms/docs/delete", new { ids = new[] { a.Id, b.Id } }), HttpStatusCode.Forbidden);
        Assert.False(Dms.Dms.Get(_fx.Space("anna").Db, a.Id).Trashed);
    }

    [Fact]
    public async Task Einnahmen_Ausgaben_Ueberschuss_und_Beleg_bleibt_fixiert()
    {
        var c = await Session("ben");
        var cats = await Json(await c.GetAsync("/api/finance/categories"));
        string Cat(string kind) => cats.EnumerateArray().First(x => x.GetProperty("kind").GetString() == kind).GetProperty("id").GetString()!;
        var beleg = await Import("ben", "beleg.txt", "Beleg Testinhalt Gamma");

        await Json(await c.PostAsJsonAsync("/api/finance/entries", new { kind = "income", date = "2025-02-01", amountCents = 100000, category = Cat("income"), text = "Einnahme" }));
        var ausgabe = await Json(await c.PostAsJsonAsync("/api/finance/entries", new { kind = "expense", date = "2025-03-01", amountCents = 2599, category = Cat("expense"), text = "Papier", dmsDoc = beleg.Id }));

        // Verknüpfung fixiert; Aufheben geht nicht, solange verknüpft
        var db = _fx.Space("ben").Db;
        Assert.True(Dms.Dms.Get(db, beleg.Id).Fixed);
        await Refused(await c.PostAsJsonAsync($"/api/dms/docs/{beleg.Id}/fixed", new { @fixed = false }), HttpStatusCode.BadRequest);
        await Refused(await c.PostAsJsonAsync("/api/dms/docs/delete", new { ids = new[] { beleg.Id } }), HttpStatusCode.Forbidden);

        var sum = await Json(await c.GetAsync("/api/finance/summary?year=2025"));
        Assert.Equal(100000, sum.GetProperty("income").GetInt64());
        Assert.Equal(2599, sum.GetProperty("expense").GetInt64());
        Assert.Equal(97401, sum.GetProperty("surplus").GetInt64());
        var pdf = await c.GetByteArrayAsync("/api/finance/summary.pdf?year=2025");
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));

        // Verknüpfung gelöst: Die Fixierung bleibt, bis sie bewusst aufgehoben wird
        var id = ausgabe.GetProperty("id").GetInt64();
        await Json(await c.PutAsJsonAsync($"/api/finance/entries/{id}", new { kind = "expense", date = "2025-03-01", amountCents = 2599, category = Cat("expense"), text = "Papier" }));
        Assert.True(Dms.Dms.Get(db, beleg.Id).Fixed);
        await Json(await c.PostAsJsonAsync($"/api/dms/docs/{beleg.Id}/fixed", new { @fixed = false }));

        // Abgeschlossenes Jahr: nichts mehr änderbar, Korrektur im offenen Jahr
        Assert.True((await c.PostAsync("/api/finance/years/2025/close", null)).IsSuccessStatusCode);
        await Refused(await c.DeleteAsync($"/api/finance/entries/{id}"), HttpStatusCode.BadRequest);
        Assert.ThrowsAny<Exception>(() => db.Exec("DELETE FROM fin_entries WHERE id = $i", ("$i", id)));
        await Refused(await c.PostAsJsonAsync("/api/finance/entries", new { kind = "income", date = "2025-12-01", amountCents = 5, category = Cat("income") }), HttpStatusCode.BadRequest);
        var korrektur = await Json(await c.PostAsJsonAsync($"/api/finance/entries/{id}/correct", new { date = "2026-01-05" }));
        Assert.Equal(-2599, korrektur.GetProperty("amountCents").GetInt64());
        await Refused(await c.PostAsJsonAsync($"/api/finance/entries/{id}/correct", new { date = "2026-01-06" }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rechnung_erst_mit_Pflichtangaben_dann_unveraenderlich_mit_fortlaufender_Nummer()
    {
        var c = await Session("cleo");
        var draft = await Json(await c.PostAsJsonAsync("/api/finance/docs", Draft("invoice")));
        var id = draft.GetProperty("id").GetInt64();
        Assert.Equal(21999, draft.GetProperty("totalCents").GetInt64()); // 2,5 × 80,00 + 19,99
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("number").ValueKind);

        // Ohne eigene Angaben geht es nicht, kein Nummernverbrauch
        Assert.Contains("Einstellungen", await Refused(await c.PostAsync($"/api/finance/docs/{id}/issue", null), HttpStatusCode.BadRequest));
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);

        var ohneDatum = await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "invoice", docDate = "2026-03-10", recipient = "X\nY", lines = new[] { new { description = "A", qty = 1m, unit = "", priceCents = 100 } } }));
        Assert.Contains("Leistung", await Refused(await c.PostAsync($"/api/finance/docs/{ohneDatum.GetProperty("id").GetInt64()}/issue", null), HttpStatusCode.BadRequest));

        // Entwurf als Vorschau
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await c.GetByteArrayAsync($"/api/finance/docs/{id}/pdf"))[..4]));

        var issued = (await Json(await c.PostAsync($"/api/finance/docs/{id}/issue", null))).GetProperty("doc");
        Assert.Equal("RE-2026-0001", issued.GetProperty("number").GetString());
        var db = _fx.Space("cleo").Db;
        Assert.True(FinanceStore.PdfIntact(db, id));

        // Kopie im DMS: fixiert
        var copy = issued.GetProperty("dmsDoc").GetInt64();
        Assert.True(Dms.Dms.Get(db, copy).Fixed);
        await DmsImport.ProcessQueueAsync(_fx.Space("cleo"), DmsHost.Current!, 100, default);
        Assert.NotEqual(0, Dms.Dms.Get(db, copy).FileId);
        Assert.True(Dms.Dms.Get(db, copy).Fixed);
        await Refused(await c.PostAsJsonAsync("/api/dms/docs/delete", new { ids = new[] { copy } }), HttpStatusCode.Forbidden);

        // Unveränderlich: über die Schnittstelle und unmittelbar in der Datenbank
        await Refused(await c.PutAsJsonAsync($"/api/finance/docs/{id}", Draft("invoice")), HttpStatusCode.BadRequest);
        await Refused(await c.DeleteAsync($"/api/finance/docs/{id}"), HttpStatusCode.BadRequest);
        Assert.ThrowsAny<Exception>(() => db.Exec("UPDATE fin_docs SET total_cents = 1 WHERE id = $i", ("$i", id)));
        Assert.ThrowsAny<Exception>(() => db.Exec("DELETE FROM fin_docs WHERE id = $i", ("$i", id)));
        Assert.ThrowsAny<Exception>(() => db.Exec("UPDATE fin_lines SET total_cents = 1 WHERE doc = $i", ("$i", id)));

        // Zahlung legt die Einnahme an, zum Tag der Zahlung
        var pay = await Json(await c.PostAsJsonAsync($"/api/finance/docs/{id}/pay", new { date = "2026-04-02" }));
        Assert.Equal(21999, pay.GetProperty("amountCents").GetInt64());
        Assert.Equal(21999, (await Json(await c.GetAsync("/api/finance/summary?year=2026"))).GetProperty("income").GetInt64());
        await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{id}/pay", new { date = "2026-04-03" }), HttpStatusCode.BadRequest);
        // Bezahlte Rechnung: Storno erst nach Korrektur der Einnahme
        Assert.Contains("Zahlung", await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{id}/cancel", new { date = "2026-05-01" }), HttpStatusCode.BadRequest));
        await Json(await c.PostAsJsonAsync($"/api/finance/entries/{pay.GetProperty("id").GetInt64()}/correct", new { date = "2026-04-30" }));

        var cancel = (await Json(await c.PostAsJsonAsync($"/api/finance/docs/{id}/cancel", new { date = "2026-05-01" }))).GetProperty("doc");
        Assert.Equal("RE-2026-0002", cancel.GetProperty("number").GetString());
        Assert.Equal(-21999, cancel.GetProperty("totalCents").GetInt64());
        Assert.Equal("RE-2026-0001", cancel.GetProperty("refNumber").GetString());
        Assert.True((await Json(await c.GetAsync($"/api/finance/docs/{id}"))).GetProperty("cancelled").GetBoolean());
        await Refused(await c.PostAsJsonAsync($"/api/finance/docs/{id}/cancel", new { date = "2026-05-01" }), HttpStatusCode.BadRequest);

        // Das Protokoll hält alles fest, ist nicht änderbar, die Kette stimmt
        var log = await Json(await c.GetAsync("/api/finance/log"));
        Assert.True(log.GetProperty("intact").GetBoolean());
        Assert.ThrowsAny<Exception>(() => db.Exec("DELETE FROM fin_log"));
    }

    [Fact]
    public async Task Angebot_eigener_Nummernkreis_neue_Version_und_Rechnung_daraus()
    {
        var c = await Session("dora");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var offer = await Json(await c.PostAsJsonAsync("/api/finance/docs", Draft("offer")));
        var id = offer.GetProperty("id").GetInt64();
        var issued = (await Json(await c.PostAsync($"/api/finance/docs/{id}/issue", null))).GetProperty("doc");
        Assert.StartsWith("AN-2026-", issued.GetProperty("number").GetString());

        var version = await Json(await c.PostAsJsonAsync($"/api/finance/docs/{id}/copy", new { kind = "offer" }));
        Assert.Equal("draft", version.GetProperty("state").GetString());
        Assert.Equal(id, version.GetProperty("refId").GetInt64());
        var invoice = await Json(await c.PostAsJsonAsync($"/api/finance/docs/{id}/copy", new { kind = "invoice" }));
        Assert.Equal(21999, invoice.GetProperty("totalCents").GetInt64());
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("number").ValueKind);

        // Nummer beim Umstieg vorgeben, zurück geht es nicht
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", new { settings = new FinanceSettings { Name = "Muster Technik", Address = "Beispielweg 1", TaxNumber = "1" }, nextInvoice = 40L })).IsSuccessStatusCode);
        var put = await c.PutAsJsonAsync($"/api/finance/docs/{invoice.GetProperty("id").GetInt64()}", Draft("invoice"));
        Assert.True(put.IsSuccessStatusCode);
        var numbered = (await Json(await c.PostAsync($"/api/finance/docs/{invoice.GetProperty("id").GetInt64()}/issue", null))).GetProperty("doc");
        Assert.Equal("RE-2026-0040", numbered.GetProperty("number").GetString());
        await Refused(await c.PutAsJsonAsync("/api/finance/settings", new { settings = new FinanceSettings { Name = "M", Address = "A", TaxNumber = "1" }, nextInvoice = 5L }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Gleichzeitig_ausgestellt_bekommt_jede_Rechnung_eine_eigene_Nummer()
    {
        var c = await Session("anna");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var ids = new List<long>();
        for (var i = 0; i < 8; i++) ids.Add((await Json(await c.PostAsJsonAsync("/api/finance/docs", Draft("offer")))).GetProperty("id").GetInt64());
        var db = _fx.Space("anna").Db;
        var docs = await Task.WhenAll(ids.Select(id => Task.Run(() => FinanceStore.Issue(db, id))));
        Assert.Equal(8, docs.Select(d => d.Number).Distinct().Count());
    }

    [Fact]
    public async Task Fremde_kommen_nicht_an_Belege_und_App_Passwort_kommt_nicht_an_Finanzen()
    {
        var anna = await Session("anna");
        var doc = await Json(await anna.PostAsJsonAsync("/api/finance/docs", Draft("offer")));
        var ben = await Session("ben");
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/finance/docs/{doc.GetProperty("id").GetInt64()}")).StatusCode);
        var app = _fx.As("anna");
        Assert.False((await app.GetAsync("/api/finance/docs")).IsSuccessStatusCode);
    }

    /// <summary>Im offenen Tresor gilt die Sperre für Ändern und Löschen, der Umzug nimmt Fixierung und Betrag mit.</summary>
    [Fact]
    public async Task Fixiert_im_Tresor_gesperrt_und_beim_Umzug_erhalten()
    {
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024; Tresor.ArgonIterations = 1;
        var space = _fx.Space("dora");
        Tresor.Delete(space.User.Id);
        var c = _fx.As("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));
        await Json(await c.PostAsJsonAsync("/api/dms/profiles", new { name = "Privat", target = "tresor", folder = "Dokumente", folders = "{doc_year}", names = "{title}", unknown = "Unbekannt", follow = true, language = "de" }));
        var doc = await Import("dora", "fix-tresor.txt", "Fixiert Testinhalt Tresor");
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{doc.Id}", new { amount = "777" }));
        await Json(await c.PostAsJsonAsync($"/api/dms/docs/{doc.Id}/fixed", new { @fixed = true }));

        var moved = await Json(await c.PostAsJsonAsync("/api/dms/docs/move-area", new { ids = new[] { doc.Id }, to = "tresor" }));
        Assert.Equal(1, moved.GetProperty("moved").GetInt32() + moved.GetProperty("merged").GetInt32());
        var list = await Json(await c.GetAsync("/api/dms/docs?area=tresor&limit=50"));
        var inside = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("originalName").GetString() == "fix-tresor.txt");
        Assert.True(inside.GetProperty("fixed").GetBoolean());
        Assert.Equal(777, inside.GetProperty("amount").GetInt64());
        var id = inside.GetProperty("id").GetInt64();
        await Refused(await c.PostAsJsonAsync("/api/dms/docs/delete?area=tresor", new { ids = new[] { id } }), HttpStatusCode.Forbidden);

        var tresor = Tresor.DbOf(space.User.Id)!;
        var fileId = inside.GetProperty("fileId").GetInt64();
        Assert.Throws<DavForbiddenException>(() => tresor.Files.Delete(fileId));
        await Assert.ThrowsAsync<DavForbiddenException>(() => tresor.Files.ReplaceAsync(fileId, new MemoryStream("neu"u8.ToArray()), "text/plain", null, default));

        // Aufgehoben: wieder löschbar
        await Json(await c.PostAsJsonAsync($"/api/dms/docs/{id}/fixed?area=tresor", new { @fixed = false }));
        Assert.True((await c.PostAsJsonAsync("/api/dms/docs/delete?area=tresor", new { ids = new[] { id } })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Kunde_mit_Nummer_steht_fest_im_Beleg()
    {
        var c = await Session("ben");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var kunde = await Json(await c.PostAsJsonAsync("/api/finance/customers", new { name = "Beispiel GmbH", address = "Testgasse 2\n54321 Testdorf" }));
        Assert.StartsWith("K-", kunde.GetProperty("number").GetString());
        var id = kunde.GetProperty("id").GetInt64();
        var doubled = await c.PostAsJsonAsync("/api/finance/customers", new { name = "Zweite", number = kunde.GetProperty("number").GetString() });
        await Refused(doubled, HttpStatusCode.BadRequest);

        var draft = await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "invoice", docDate = "2026-03-10", serviceDate = "März 2026", recipient = "Beispiel GmbH\nTestgasse 2", customer = id,
            lines = new[] { new { description = "A", qty = 1m, unit = "", priceCents = 1000 } } }));
        var doc = draft.GetProperty("id").GetInt64();
        Assert.Equal(kunde.GetProperty("number").GetString(), draft.GetProperty("customerNumber").GetString());
        await Json(await c.PostAsync($"/api/finance/docs/{doc}/issue", null));

        // Kunde ändern berührt den ausgestellten Beleg nicht; löschen geht nicht, solange es Belege gibt
        var changed = await Json(await c.PutAsJsonAsync($"/api/finance/customers/{id}", new { name = "Neu GmbH", number = "K-9000", address = "Anderswo" }));
        Assert.Equal(1000, changed.GetProperty("invoicedCents").GetInt64());
        Assert.Equal(kunde.GetProperty("number").GetString(), (await Json(await c.GetAsync($"/api/finance/docs/{doc}"))).GetProperty("customerNumber").GetString());
        await Refused(await c.DeleteAsync($"/api/finance/customers/{id}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Leistungen_anlegen_aendern_loeschen_und_ins_Protokoll()
    {
        var c = await Session("ben");
        await Refused(await c.PostAsJsonAsync("/api/finance/services", new { description = "  ", unit = "", priceCents = 0 }), HttpStatusCode.BadRequest);
        var x = await Json(await c.PostAsJsonAsync("/api/finance/services", new { description = "Beratung\nvor Ort", unit = "Stunde", priceCents = 8500 }));
        var id = x.GetProperty("id").GetInt64();
        Assert.Equal("Beratung\nvor Ort", x.GetProperty("description").GetString());
        var changed = await Json(await c.PutAsJsonAsync($"/api/finance/services/{id}", new { description = "Beratung", unit = "Std.", priceCents = 9000 }));
        Assert.Equal(9000, changed.GetProperty("priceCents").GetInt64());
        Assert.Contains((await Json(await c.GetAsync("/api/finance/services"))).EnumerateArray(), s => s.GetProperty("id").GetInt64() == id && s.GetProperty("unit").GetString() == "Std.");
        Assert.True((await c.DeleteAsync($"/api/finance/services/{id}")).IsSuccessStatusCode);
        await Refused(await c.PutAsJsonAsync($"/api/finance/services/{id}", new { description = "Weg", unit = "", priceCents = 1 }), HttpStatusCode.NotFound);
        var log = (await Json(await c.GetAsync("/api/finance/log"))).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("service.add", log); Assert.Contains("service.change", log); Assert.Contains("service.delete", log);
    }

    [Fact]
    public async Task Nach_Wiederherstellung_erst_Nummern_bestaetigen()
    {
        var c = await Session("cleo");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var before = FinanceStore.RestoreStamp;
        try
        {
            FinanceStore.RestoreStamp = () => "stand-1";
            Assert.True((await Json(await c.GetAsync("/api/finance/settings"))).GetProperty("numbersToConfirm").GetBoolean());
            var id = (await Json(await c.PostAsJsonAsync("/api/finance/docs", Draft("offer")))).GetProperty("id").GetInt64();
            Assert.Contains("Wiederherstellung", await Refused(await c.PostAsync($"/api/finance/docs/{id}/issue", null), HttpStatusCode.BadRequest));
            Assert.Equal("draft", (await Json(await c.GetAsync($"/api/finance/docs/{id}"))).GetProperty("state").GetString());
            Assert.True((await c.PostAsync("/api/finance/numbers/confirm", null)).IsSuccessStatusCode);
            Assert.False((await Json(await c.GetAsync("/api/finance/settings"))).GetProperty("numbersToConfirm").GetBoolean());
            Assert.Equal("issued", (await Json(await c.PostAsync($"/api/finance/docs/{id}/issue", null))).GetProperty("doc").GetProperty("state").GetString());
            // Die nächste Wiederherstellung sperrt wieder
            FinanceStore.RestoreStamp = () => "stand-2";
            Assert.True(FinanceStore.NumbersToConfirm(_fx.Space("cleo").Db));
        }
        finally { FinanceStore.RestoreStamp = before; }
    }

    [Fact]
    public async Task Datensicherung_enthaelt_Belege_und_Tabellen()
    {
        var c = await Session("anna");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var id = (await Json(await c.PostAsJsonAsync("/api/finance/docs", Draft("offer")))).GetProperty("id").GetInt64();
        var number = (await Json(await c.PostAsync($"/api/finance/docs/{id}/issue", null))).GetProperty("doc").GetProperty("number").GetString();
        var cats = await Json(await c.GetAsync("/api/finance/categories"));
        await Json(await c.PostAsJsonAsync("/api/finance/entries", new { kind = "income", date = "2024-02-01", amountCents = 500, category = cats.EnumerateArray().First(x => x.GetProperty("kind").GetString() == "income").GetProperty("id").GetString(), text = "=FORMEL" }));

        var space = _fx.Space("anna");
        var settings = new DmOffice.Storage.PlainBackupSettings { Areas = [DmOffice.Storage.PlainBackup.Finance] };
        var items = DmOffice.Storage.PlainBackupAreas.Items(space, settings, null);
        var pdf = items.Single(i => i.Path == $"Finanzen/Belege/{number}.pdf");
        await using (var s = pdf.Open!()) { var b = new byte[4]; await s.ReadExactlyAsync(b); Assert.Equal("%PDF", Encoding.ASCII.GetString(b)); }
        Assert.NotEqual(0, pdf.Size);
        foreach (var name in new[] { "Finanzen/Belege.csv", "Finanzen/Kunden.csv", "Finanzen/Protokoll.csv" }) Assert.Contains(items, i => i.Path == name);
        var csv = new MemoryStream();
        await items.Single(i => i.Path == "Finanzen/Einnahmen und Ausgaben 2024.csv").Write!(csv);
        var text = Encoding.UTF8.GetString(csv.ToArray());
        Assert.Contains("5,00", text);
        Assert.Contains("'=FORMEL", text);

        // Bestehende Sicherung ohne den Bereich: wird einmal aufgenommen, Abwählen bleibt danach
        var db = _fx.Space("ben").Db;
        db.SetJson(DmOffice.Storage.PlainBackup.Key, new DmOffice.Storage.PlainBackupSettings { Areas = ["files"] });
        var offered = DmOffice.Storage.PlainBackup.Settings(db);
        Assert.Contains(DmOffice.Storage.PlainBackup.Finance, offered.Areas);
        offered.Areas.Remove(DmOffice.Storage.PlainBackup.Finance);
        db.SetJson(DmOffice.Storage.PlainBackup.Key, offered);
        Assert.DoesNotContain(DmOffice.Storage.PlainBackup.Finance, DmOffice.Storage.PlainBackup.Settings(db).Areas);
    }

    private static Task<HttpResponseMessage> UploadTo(HttpClient c, string profile, string name, string text) =>
        c.PostAsync("/api/dms/upload?profile=" + profile, new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } });

    /// <summary>Beleg im Tresor: fixiert dort, Kennung gilt nur im Tresor, Zahlen bleiben bei geschlossenem Tresor nutzbar.</summary>
    [Fact]
    public async Task Beleg_im_Tresor_verknuepfen_und_bei_geschlossenem_Tresor_weiterarbeiten()
    {
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024; Tresor.ArgonIterations = 1;
        var space = _fx.Space("cleo");
        Tresor.Delete(space.User.Id);
        var c = await Session("cleo");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));
        var profile = (await Json(await c.PostAsJsonAsync("/api/dms/profiles", new { name = "Privat", target = "tresor", folder = "Dokumente", folders = "{doc_year}", names = "{title}", unknown = "Unbekannt", follow = true, language = "de" }))).GetProperty("id").GetString()!;
        await Json(await UploadTo(c, profile, "Geheim.txt", "Quittung im Tresor Delta"));
        var store = DmsStore.ForTresor(space.Db, Tresor.DbOf(space.User.Id)!);
        await DmsImport.ProcessQueueAsync(space, DmsHost.Current!, 100, default, store);
        var inside = (await Json(await c.GetAsync("/api/dms/docs?area=tresor"))).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("originalName").GetString() == "Geheim.txt").GetProperty("id").GetInt64();
        var cat = (await Json(await c.GetAsync("/api/finance/categories"))).EnumerateArray().First(x => x.GetProperty("kind").GetString() == "expense").GetProperty("id").GetString();

        // Gleiche Kennung im offenen Bereich darf davon nicht berührt werden
        var outside = space.Db.Scalar("SELECT 1 FROM dms_docs WHERE id = $i AND fixed = 1", ("$i", inside));
        var entry = await Json(await c.PostAsJsonAsync("/api/finance/entries", new { kind = "expense", date = "2026-02-01", amountCents = 1500, category = cat, text = "Tresorbeleg", dmsDoc = inside, dmsTresor = true }));
        Assert.True(entry.GetProperty("dmsTresor").GetBoolean());
        Assert.True(Dms.Dms.Get(store, inside).Fixed);
        Assert.Equal(outside, space.Db.Scalar("SELECT 1 FROM dms_docs WHERE id = $i AND fixed = 1", ("$i", inside)));
        await Refused(await c.PostAsJsonAsync($"/api/dms/docs/{inside}/fixed?area=tresor", new { @fixed = false }), HttpStatusCode.BadRequest);
        await Refused(await c.PostAsJsonAsync("/api/dms/docs/delete?area=tresor", new { ids = new[] { inside } }), HttpStatusCode.Forbidden);
        Assert.Equal(1, (await Json(await c.GetAsync("/api/finance/tresor-links"))).GetProperty("count").GetInt64());
        // Verknüpft lässt es sich nicht aus dem Tresor holen: Die Kennung änderte sich sonst
        var move = await Json(await c.PostAsJsonAsync("/api/dms/docs/move-area", new { ids = new[] { inside }, to = "files" }));
        Assert.Equal(1, move.GetProperty("failed").GetArrayLength());

        // Tresor zu: Zahlen und unveränderte Verknüpfung gehen, eine neue nicht
        Assert.True((await c.PostAsync("/api/tresor/close", null)).IsSuccessStatusCode);
        Assert.Equal(1500, (await Json(await c.GetAsync("/api/finance/summary?year=2026"))).GetProperty("expense").GetInt64());
        var id = entry.GetProperty("id").GetInt64();
        await Json(await c.PutAsJsonAsync($"/api/finance/entries/{id}", new { kind = "expense", date = "2026-02-02", amountCents = 1500, category = cat, text = "Geändert", dmsDoc = inside, dmsTresor = true }));
        await Refused(await c.PostAsJsonAsync("/api/finance/entries", new { kind = "expense", date = "2026-02-03", amountCents = 1, category = cat, dmsDoc = inside, dmsTresor = true }), (HttpStatusCode)423);
        await c.DeleteAsync($"/api/dms/profiles/{profile}");
    }

    /// <summary>Standardprofil auf den Tresor: Die Kopie liegt und ist dort fixiert; bei geschlossenem Tresor meldet die Antwort, was wirklich geschah.</summary>
    [Fact]
    public async Task Kopie_einer_Rechnung_folgt_dem_Standardprofil_in_den_Tresor()
    {
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024; Tresor.ArgonIterations = 1;
        var space = _fx.Space("dora");
        Tresor.Delete(space.User.Id);
        var c = await Session("dora");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));
        await Json(await c.GetAsync("/api/dms/profiles"));
        var profile = (await Json(await c.PostAsJsonAsync("/api/dms/profiles", new { name = "Standard Tresor", target = "tresor", folder = "Dokumente", folders = "{doc_year}", names = "{title}", unknown = "Unbekannt", follow = true, language = "de", @default = true }))).GetProperty("id").GetString()!;
        try
        {
            Assert.Equal("tresor", DmsFiling.Default(space).Target);
            var open = await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "offer", docDate = "2026-03-10", recipient = "X\nY", subject = "Tresorkopie", lines = new[] { new { description = "Tresor A", qty = 1m, unit = "", priceCents = 700 } } }));
            var r = await Json(await c.PostAsync($"/api/finance/docs/{open.GetProperty("id").GetInt64()}/issue", null));
            Assert.True(r.GetProperty("dmsCopy").GetBoolean());
            var doc = r.GetProperty("doc");
            Assert.True(doc.GetProperty("dmsTresor").GetBoolean());
            var store = DmsStore.ForTresor(space.Db, Tresor.DbOf(space.User.Id)!);
            Assert.True(Dms.Dms.Get(store, doc.GetProperty("dmsDoc").GetInt64()).Fixed);

            Assert.True((await c.PostAsync("/api/tresor/close", null)).IsSuccessStatusCode);
            var closed = await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "offer", docDate = "2026-03-11", recipient = "X\nY", subject = "Tresorkopie zu", lines = new[] { new { description = "Tresor B", qty = 1m, unit = "", priceCents = 800 } } }));
            var z = await Json(await c.PostAsync($"/api/finance/docs/{closed.GetProperty("id").GetInt64()}/issue", null));
            Assert.False(z.GetProperty("dmsCopy").GetBoolean());
            Assert.True(z.GetProperty("dmsPending").GetBoolean());
            Assert.Equal("issued", z.GetProperty("doc").GetProperty("state").GetString());
        }
        finally { await c.DeleteAsync($"/api/dms/profiles/{profile}"); Tresor.Delete(space.User.Id); }
    }

    private static List<string> PdfPages(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => p.Text).ToList();
    }

    private static readonly FinanceSettings Firma = new() { Name = "Muster Technik", Address = "Beispielweg 1\n12345 Musterstadt", TaxNumber = "12/345/67890", Bank = "Testbank", Iban = "DE00" };

    [Fact]
    public void Standardvorlage_enthaelt_die_Pflichtangaben()
    {
        var layout = FinanceTemplates.Default();
        FinanceTemplates.Check(layout);
        var text = string.Join(" ", PdfPages(FinanceTemplates.Pdf(FinanceTemplates.Sample("invoice", 3), Firma, layout, false)));
        foreach (var part in new[] { "Muster Technik", "Erika Beispiel", "RE-2026-0001", "K-0001", "März 2026", "Beratung", "Gesamtbetrag", "§ 19 UStG", "12/345/67890", "Testbank", "Seite 1 von 1" })
            Assert.Contains(part, text);
    }

    [Fact]
    public void Viele_Positionen_gehen_ueber_mehrere_Seiten_mit_Kopf_und_Seitenzahl()
    {
        var doc = FinanceTemplates.Sample("invoice", 90);
        var pages = PdfPages(FinanceTemplates.Pdf(doc, Firma, FinanceTemplates.Default(), false));
        Assert.True(pages.Count >= 3, pages.Count.ToString());
        for (var i = 0; i < pages.Count; i++)
        {
            Assert.Contains($"Seite {i + 1} von {pages.Count}", pages[i]);
            Assert.Contains("Einzelpreis", pages[i].Length > 0 && i < pages.Count - 1 ? pages[i] : "Einzelpreis");
        }
        // Jede Position steht genau einmal da, keine geht verloren
        var all = string.Concat(pages);
        foreach (var n in new[] { 2, 45, 90 }) Assert.Contains($"Leistung {n}", all);
        Assert.Contains(FinancePdf.Money(doc.TotalCents), pages[^1]);
        Assert.Contains("§ 19 UStG", pages[^1]);
    }

    [Fact]
    public void Eigene_Vorlage_Spalten_Bild_und_leere_Abschnitte()
    {
        using var bmp = new SkiaSharp.SKBitmap(20, 10);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        var logo = "data:image/png;base64," + Convert.ToBase64String(img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray());
        var layout = FinanceTemplates.Default();
        layout.Header.Add(new TplBlock { Type = "columns", Cells = [[new TplBlock { Type = "text", Text = "Kopf {firma}" }], [new TplBlock { Type = "image", Image = logo, ImageWidth = 40, Align = "right" }]] });
        layout.Footer.Add(new TplBlock { Type = "text", Text = "[USt-IdNr.: {ust_id}]Fest" });
        FinanceTemplates.Check(layout);
        var text = PdfPages(FinanceTemplates.Pdf(FinanceTemplates.Sample("offer", 2), Firma, layout, true))[0];
        Assert.Contains("Kopf Muster Technik", text);
        Assert.Contains("Fest", text);
        Assert.DoesNotContain("USt-IdNr", text);
        Assert.Contains("Entwurf", text);
        Assert.Contains("Gültig bis", text);
        Assert.DoesNotContain("Leistungsdatum", text);
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("table")]
    [InlineData("total")]
    [InlineData("note")]
    public void Vorlage_ohne_Pflichtbaustein_wird_abgelehnt(string type)
    {
        var layout = FinanceTemplates.Default();
        layout.Body.RemoveAll(b => b.Type == type);
        Assert.Throws<ArgumentException>(() => FinanceTemplates.Check(layout));
    }

    [Fact]
    public void Vorlage_mit_unbekanntem_Platzhalter_oder_ohne_Mengenspalte_wird_abgelehnt()
    {
        var a = FinanceTemplates.Default();
        a.Body.Add(new TplBlock { Type = "text", Text = "{gibtsnicht}" });
        Assert.Contains("gibtsnicht", Assert.Throws<ArgumentException>(() => FinanceTemplates.Check(a)).Message);
        var b = FinanceTemplates.Default();
        b.Body.Single(x => x.Type == "table").Columns!.RemoveAll(c => c.Field == "qty");
        Assert.Throws<ArgumentException>(() => FinanceTemplates.Check(b));
        var c = FinanceTemplates.Default();
        c.Body.Add(new TplBlock { Type = "image", Image = "data:text/html;base64,AAAA" });
        Assert.Throws<ArgumentException>(() => FinanceTemplates.Check(c));
    }

    [Fact]
    public async Task Vorlage_aendern_beruehrt_ausgestellte_Belege_nicht()
    {
        var c = await Session("ben");
        Assert.True((await c.PutAsJsonAsync("/api/finance/settings", Company())).IsSuccessStatusCode);
        var list = await Json(await c.GetAsync("/api/finance/templates"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("isDefault").GetBoolean());
        var layout = FinanceTemplates.Default();
        layout.Body.Insert(0, new TplBlock { Type = "text", Text = "Meine Vorlage eins" });
        var created = await Json(await c.PostAsJsonAsync("/api/finance/templates", new { name = "Eigene", layout }));
        var id = created.GetProperty("id").GetInt64();

        var preview = await Json(await c.PostAsJsonAsync("/api/finance/templates/preview", new { layout, kind = "invoice", lines = 90, page = 2 }));
        Assert.True(preview.GetProperty("pages").GetInt32() >= 3);
        Assert.StartsWith("data:image/png;base64,", preview.GetProperty("image").GetString());
        await Refused(await c.PostAsJsonAsync("/api/finance/templates/preview", new { layout = new TplLayout() }), HttpStatusCode.BadRequest);

        var draft = await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "offer", docDate = "2026-03-10", recipient = "X\nY", template = id, lines = new[] { new { description = "Vorlagenprobe", qty = 1m, unit = "", priceCents = 100 } } }));
        var doc = draft.GetProperty("id").GetInt64();
        await Json(await c.PostAsync($"/api/finance/docs/{doc}/issue", null));
        var before = await c.GetByteArrayAsync($"/api/finance/docs/{doc}/pdf");
        Assert.Contains("Meine Vorlage eins", string.Concat(PdfPages(before)));

        layout.Body[0].Text = "Meine Vorlage zwei";
        await Json(await c.PutAsJsonAsync($"/api/finance/templates/{id}", new { name = "Eigene", layout }));
        Assert.Equal(before, await c.GetByteArrayAsync($"/api/finance/docs/{doc}/pdf"));

        // Neue Belege nehmen den neuen Stand; gelöscht fällt der Entwurf auf die Standardvorlage zurück
        var next = (await Json(await c.PostAsJsonAsync("/api/finance/docs", new { kind = "offer", docDate = "2026-03-11", recipient = "X\nY", template = id, lines = new[] { new { description = "Vorlagenprobe", qty = 1m, unit = "", priceCents = 100 } } }))).GetProperty("id").GetInt64();
        Assert.Contains("Meine Vorlage zwei", string.Concat(PdfPages(await c.GetByteArrayAsync($"/api/finance/docs/{next}/pdf"))));
        Assert.True((await c.DeleteAsync($"/api/finance/templates/{id}")).IsSuccessStatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await c.GetAsync($"/api/finance/docs/{next}"))).GetProperty("template").ValueKind);
        Assert.DoesNotContain("Meine Vorlage", string.Concat(PdfPages(await c.GetByteArrayAsync($"/api/finance/docs/{next}/pdf"))));
        Assert.Equal(before, await c.GetByteArrayAsync($"/api/finance/docs/{doc}/pdf"));

        var standard = (await Json(await c.GetAsync("/api/finance/templates"))).EnumerateArray().Single(x => x.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetInt64();
        await Refused(await c.DeleteAsync($"/api/finance/templates/{standard}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Betrag_kaufmaennisch_gerundet()
    {
        Assert.Equal(21, FinanceStore.LineTotal(1500, 14)); // 1,5 × 0,14 = 0,21
        Assert.Equal(1, FinanceStore.LineTotal(500, 1));    // 0,005 → 0,01
        Assert.Equal(-1, FinanceStore.LineTotal(500, -1));
    }
}
