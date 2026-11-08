using System.Globalization;
using System.Text;
using DmOffice.Core;
using DmOffice.Dms;

namespace DmOffice.Finance;

/// <summary>Schnittstelle von Finanzen (/api/finance). Nur in der angemeldeten Sitzung: App-Passwörter und Geräte kommen nicht heran (Auth.AppPasswordAllowed).</summary>
public static class FinanceApi
{
    public record SettingsBody(FinanceSettings Settings, long? NextInvoice, long? NextOffer, long? NextOrder = null);
    public record CategoryBody(string? Kind, string? Name);
    public record DateBody(string? Date);
    public record PayBody(string? Date, long? AmountCents, string? Category);
    public record CopyBody(string? Kind);
    public record PartialBody(decimal? Percent, long? AmountCents);
    public record PreviewBody(TplLayout Layout, string? Kind, int? Lines, int? Page);

    /// <summary>Datei im Datenordner: Kennung der letzten Wiederherstellung (geschrieben beim Einspielen eines Core-Backups).</summary>
    public const string RestoreStampFile = "finance-restored.txt";

    /// <summary>Einträge eines Jahres als CSV, für Export und Datensicherung.</summary>
    public static byte[] EntriesCsv(UserDb db, int year)
    {
        var names = FinanceStore.Categories(db).ToDictionary(c => c.Id, c => c.Name);
        var csv = new StringBuilder("Datum;Art;Betrag;Kategorie;Text;Geschäftspartner\r\n");
        foreach (var e in FinanceStore.Entries(db, year).AsEnumerable().Reverse())
            csv.Append(e.Date).Append(';').Append(e.Kind == FinanceStore.Income ? "Einnahme" : "Ausgabe").Append(';')
                .Append((e.AmountCents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("de-DE"))).Append(';')
                .Append(CsvCell(names.GetValueOrDefault(e.Category) ?? "")).Append(';').Append(CsvCell(e.Text)).Append(';').Append(CsvCell(e.Party)).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    /// <summary>Liste der Belege als CSV, für die Datensicherung.</summary>
    public static byte[] DocsCsv(UserDb db)
    {
        var csv = new StringBuilder("Nummer;Art;Stand;Datum;Leistungsdatum;Fällig;Kundennummer;Empfänger;Betreff;Betrag;Bezahlt;Storniert;Bezug\r\n");
        var de = CultureInfo.GetCultureInfo("de-DE");
        foreach (var d in FinanceStore.Docs(db, null).AsEnumerable().Reverse())
            csv.Append(string.Join(';', CsvCell(d.Number ?? ""), d.Kind == FinanceStore.Order ? "Auftrag" : FinanceStore.Heading(d),
                d.State == FinanceStore.Draft ? "Entwurf" : "Ausgestellt", d.DocDate ?? "", CsvCell(d.ServiceDate), d.DueDate ?? "", CsvCell(d.CustomerNumber), CsvCell(d.Recipient.Replace('\n', ' ')), CsvCell(d.Subject),
                (d.TotalCents / 100m).ToString("0.00", de), (d.PaidCents / 100m).ToString("0.00", de), d.Cancelled ? "ja" : "nein", CsvCell(d.RefNumber ?? ""))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    /// <summary>Kunden als CSV, für die Datensicherung.</summary>
    public static byte[] CustomersCsv(UserDb db)
    {
        var csv = new StringBuilder("Kundennummer;Name;Anschrift;Notiz\r\n");
        foreach (var c in FinanceStore.Customers(db))
            csv.Append(string.Join(';', CsvCell(c.Number), CsvCell(c.Name), CsvCell(c.Address.Replace('\n', ' ')), CsvCell(c.Note.Replace('\n', ' ')))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    /// <summary>Protokoll als CSV, für die Datensicherung.</summary>
    public static byte[] LogCsv(UserDb db)
    {
        var csv = new StringBuilder("Zeit;Vorgang;Angaben\r\n");
        foreach (var e in FinanceStore.LogEntries(db, int.MaxValue).AsEnumerable().Reverse())
            csv.Append(string.Join(';', e.At.ToString("o", CultureInfo.InvariantCulture), CsvCell(e.Action), CsvCell(e.Detail))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    private static string CsvCell(string text)
    {
        // Tabellenprogramme führten sonst Texte als Formel aus
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static void Map(WebApplication app)
    {
        // Fixierung bleibt, solange Finanzen auf das Dokument verweist
        var paths = app.Services.GetRequiredService<DataPaths>();
        var stampFile = Path.Combine(paths.Root, RestoreStampFile);
        FinanceStore.RestoreStamp = () => File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : null;
        Dms.Dms.FixedReason = (store, id) => Features.On(Features.Finance) && FinanceStore.Linked(store.User, id, store.Tresor) ? "Das Dokument ist in Finanzen verknüpft. Die Fixierung lässt sich nicht aufheben." : null;

        var api = app.MapGroup("/api/finance").RequireAuthorization();

        api.MapGet("/settings", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var year = DateTime.Now.Year;
            return new { settings = FinanceStore.Settings(db), nextInvoice = FinanceStore.NextNumber(db, FinanceStore.Invoice, year), nextOffer = FinanceStore.NextNumber(db, FinanceStore.Offer, year), nextOrder = FinanceStore.NextNumber(db, FinanceStore.Order, year), year, numbersToConfirm = FinanceStore.NumbersToConfirm(db) };
        });
        api.MapPut("/settings", (SettingsBody b, HttpContext http) =>
        {
            var db = http.Space().Db;
            FinanceStore.SaveSettings(db, b.Settings);
            var year = DateTime.Now.Year;
            if (b.NextInvoice is { } i && i != FinanceStore.NextNumber(db, FinanceStore.Invoice, year)) FinanceStore.SetNextNumber(db, FinanceStore.Invoice, year, i);
            if (b.NextOffer is { } o && o != FinanceStore.NextNumber(db, FinanceStore.Offer, year)) FinanceStore.SetNextNumber(db, FinanceStore.Offer, year, o);
            if (b.NextOrder is { } a && a != FinanceStore.NextNumber(db, FinanceStore.Order, year)) FinanceStore.SetNextNumber(db, FinanceStore.Order, year, a);
            return Results.NoContent();
        });

        api.MapPost("/numbers/confirm", (HttpContext http) => { FinanceStore.ConfirmNumbers(http.Space().Db); return Results.NoContent(); });

        api.MapGet("/customers", (HttpContext http) => FinanceStore.Customers(http.Space().Db));
        api.MapPost("/customers", (FinanceStore.CustomerInput b, HttpContext http) => FinanceStore.AddCustomer(http.Space().Db, b));
        api.MapPut("/customers/{id:long}", (long id, FinanceStore.CustomerInput b, HttpContext http) => FinanceStore.UpdateCustomer(http.Space().Db, id, b));
        api.MapDelete("/customers/{id:long}", (long id, HttpContext http) => { FinanceStore.DeleteCustomer(http.Space().Db, id); return Results.NoContent(); });

        api.MapGet("/services", (HttpContext http) => FinanceStore.Services(http.Space().Db));
        api.MapPost("/services", (FinanceStore.ServiceInput b, HttpContext http) => FinanceStore.SaveService(http.Space().Db, null, b));
        api.MapPut("/services/{id:long}", (long id, FinanceStore.ServiceInput b, HttpContext http) => FinanceStore.SaveService(http.Space().Db, id, b));
        api.MapDelete("/services/{id:long}", (long id, HttpContext http) => { FinanceStore.DeleteService(http.Space().Db, id); return Results.NoContent(); });

        api.MapGet("/templates", (HttpContext http) => FinanceStore.Templates(http.Space().Db));
        api.MapGet("/templates/keys", () => FinanceTemplates.Keys);
        api.MapGet("/templates/default-layout", () => FinanceTemplates.Default());
        api.MapPost("/templates", (FinanceStore.TemplateInput b, HttpContext http) => FinanceStore.SaveTemplate(http.Space().Db, null, b));
        api.MapPut("/templates/{id:long}", (long id, FinanceStore.TemplateInput b, HttpContext http) => FinanceStore.SaveTemplate(http.Space().Db, id, b));
        api.MapDelete("/templates/{id:long}", (long id, HttpContext http) => { FinanceStore.DeleteTemplate(http.Space().Db, id); return Results.NoContent(); });
        api.MapPost("/templates/{id:long}/default", (long id, HttpContext http) => { FinanceStore.SetDefaultTemplate(http.Space().Db, id); return Results.NoContent(); });
        // Vorschau aus demselben Satz wie das PDF, mit den eigenen Angaben und einem erfundenen Beispiel
        api.MapPost("/templates/preview", (PreviewBody b, HttpContext http) =>
        {
            FinanceTemplates.Check(b.Layout);
            var kind = b.Kind is FinanceStore.Offer or FinanceStore.Order or FinanceStore.Cancel ? b.Kind : FinanceStore.Invoice;
            var (pages, png) = FinanceTemplates.Preview(FinanceTemplates.Sample(kind, b.Lines ?? 3), FinanceStore.Settings(http.Space().Db), b.Layout, b.Page ?? 1);
            return new { pages, image = "data:image/png;base64," + Convert.ToBase64String(png) };
        });

        api.MapGet("/categories", (HttpContext http) => FinanceStore.Categories(http.Space().Db));
        api.MapPost("/categories", (CategoryBody b, HttpContext http) => FinanceStore.AddCategory(http.Space().Db, b.Kind, b.Name));
        api.MapPut("/categories/{id}", (string id, CategoryBody b, HttpContext http) => { FinanceStore.RenameCategory(http.Space().Db, id, b.Name); return Results.NoContent(); });
        api.MapDelete("/categories/{id}", (string id, HttpContext http) => { FinanceStore.DeleteCategory(http.Space().Db, id); return Results.NoContent(); });

        api.MapGet("/years", (HttpContext http) => FinanceStore.Years(http.Space().Db));
        api.MapPost("/years/{year:int}/close", (int year, HttpContext http) => { FinanceStore.CloseYear(http.Space().Db, year); return Results.NoContent(); });

        api.MapGet("/entries", (int year, HttpContext http) => FinanceStore.Entries(http.Space().Db, year));
        api.MapPost("/entries", (FinanceStore.EntryInput b, HttpContext http) => FinanceStore.AddEntry(http.Space().Db, b, tresor: TresorOf(http)));
        api.MapPut("/entries/{id:long}", (long id, FinanceStore.EntryInput b, HttpContext http) => FinanceStore.UpdateEntry(http.Space().Db, id, b, TresorOf(http)));
        api.MapGet("/tresor-links", (HttpContext http) => new { count = FinanceStore.TresorLinks(http.Space().Db) });
        api.MapDelete("/entries/{id:long}", (long id, HttpContext http) => { FinanceStore.DeleteEntry(http.Space().Db, id); return Results.NoContent(); });
        // Buchung eines DMS-Dokuments (Feld „In Finanzen“ der DMS-Bearbeitung)
        api.MapGet("/dms/{id:long}", (long id, string? area, HttpContext http) =>
            Results.Ok(new { booking = FinanceStore.BookingOf(http.Space().Db, id, area == "tresor") }));
        api.MapPut("/dms/{id:long}", (long id, string? area, DmsBookingBody b, HttpContext http) =>
        {
            var store = area == "tresor" ? TresorOf(http)() : (Dms.DmsStore)http.Space().Db;
            return Results.Ok(new { booking = FinanceStore.BookFromDms(http.Space().Db, store, id, b.Kind, b.AmountCents, TresorOf(http)) });
        });
        api.MapGet("/recurring", (HttpContext http) => FinanceStore.RecurringList(http.Space().Db));
        api.MapPost("/recurring", (FinanceStore.RecurringInput b, HttpContext http) => FinanceStore.AddRecurring(http.Space().Db, b, tresor: TresorOf(http)));
        api.MapPut("/recurring/{id:long}", (long id, FinanceStore.RecurringInput b, HttpContext http) => FinanceStore.UpdateRecurring(http.Space().Db, id, b, tresor: TresorOf(http)));
        api.MapPost("/recurring/{id:long}/stop", (long id, HttpContext http) => FinanceStore.StopRecurring(http.Space().Db, id));
        api.MapDelete("/recurring/{id:long}", (long id, HttpContext http) => { FinanceStore.DeleteRecurring(http.Space().Db, id); return Results.NoContent(); });
        api.MapPost("/entries/{id:long}/correct", (long id, DateBody b, HttpContext http) => FinanceStore.Correct(http.Space().Db, id, b.Date));

        api.MapGet("/summary", (int year, HttpContext http) => FinanceStore.SummaryOf(http.Space().Db, year));
        api.MapGet("/summary.pdf", (int year, HttpContext http) =>
        {
            var db = http.Space().Db;
            return Results.File(FinancePdf.Summary(FinanceStore.SummaryOf(db, year), FinanceStore.Settings(db)), "application/pdf", $"Einnahmen und Ausgaben {year}.pdf");
        });
        api.MapGet("/entries.csv", (int year, HttpContext http) =>
        {
            return Results.File(EntriesCsv(http.Space().Db, year), "text/csv; charset=utf-8", $"Einnahmen und Ausgaben {year}.csv");
        });

        api.MapGet("/docs", (string? kind, HttpContext http) => FinanceStore.Docs(http.Space().Db, kind));
        api.MapGet("/docs/{id:long}", (long id, HttpContext http) => FinanceStore.Doc(http.Space().Db, id));
        api.MapPost("/docs", (FinanceStore.DocInput b, HttpContext http) => FinanceStore.CreateDraft(http.Space().Db, b));
        api.MapPut("/docs/{id:long}", (long id, FinanceStore.DocInput b, HttpContext http) => FinanceStore.UpdateDraft(http.Space().Db, id, b));
        api.MapDelete("/docs/{id:long}", (long id, HttpContext http) => { FinanceStore.DeleteDraft(http.Space().Db, id); return Results.NoContent(); });
        api.MapPost("/docs/{id:long}/copy", (long id, CopyBody b, HttpContext http) => FinanceStore.CopyToDraft(http.Space().Db, id, b.Kind));
        api.MapPost("/docs/{id:long}/partial", (long id, PartialBody b, HttpContext http) => FinanceStore.PartialInvoice(http.Space().Db, id, b.Percent, b.AmountCents));
        api.MapPost("/docs/{id:long}/final", (long id, HttpContext http) => FinanceStore.FinalInvoice(http.Space().Db, id));
        api.MapPost("/docs/{id:long}/pay", (long id, PayBody b, HttpContext http) => FinanceStore.Pay(http.Space().Db, id, b.Date, b.AmountCents, b.Category));
        api.MapPost("/docs/{id:long}/issue", async (long id, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var doc = FinanceStore.Issue(space.Db, id);
            var copy = await CopyToDms(space, doc, ct);
            return new { doc = FinanceStore.Doc(space.Db, doc.Id), dmsCopy = copy == DmsCopy.Done, dmsPending = copy == DmsCopy.Pending };
        });
        api.MapPost("/docs/{id:long}/cancel", async (long id, DateBody b, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var doc = FinanceStore.CancelInvoice(space.Db, id, b.Date);
            var copy = await CopyToDms(space, doc, ct);
            return new { doc = FinanceStore.Doc(space.Db, doc.Id), dmsCopy = copy == DmsCopy.Done, dmsPending = copy == DmsCopy.Pending };
        });
        // Belege per E-Mail: Vorgaben der Vorlagen, Entwurf zu einem Beleg, festhalten, welche Dokumente je Rolle mitgingen
        api.MapGet("/mail/defaults", () => new
        {
            placeholders = FinanceMail.Placeholders,
            templates = new[] { FinanceStore.Offer, FinanceStore.Order, FinanceStore.Invoice }.ToDictionary(k => k, FinanceMail.Default),
        });
        api.MapGet("/docs/{id:long}/mail", (long id, HttpContext http) => FinanceMail.DraftOf(http.Space(), id, TresorOf(http)));
        api.MapPut("/docs/{id:long}/mail", (long id, MailKeepBody b, HttpContext http) =>
        {
            FinanceMail.Keep(http.Space().Db, id, b.Roles ?? []);
            return Results.NoContent();
        });
        api.MapGet("/docs/{id:long}/pdf", (long id, HttpContext http) =>
        {
            var db = http.Space().Db;
            var doc = FinanceStore.Doc(db, id);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(FinanceStore.Pdf(db, id), "application/pdf", (doc.Number ?? "Entwurf") + ".pdf");
        });

        api.MapGet("/log", (HttpContext http) =>
        {
            var db = http.Space().Db;
            return new { intact = FinanceStore.LogIntact(db), entries = FinanceStore.LogEntries(db) };
        });
    }

    public sealed record MailKeepBody(List<FinanceMail.KeptRole>? Roles);
    public sealed record DmsBookingBody(string? Kind, long? AmountCents);

    private static Func<DmsStore> TresorOf(HttpContext http) => () => DmsStore.ForTresor(http.Space().Db, Tresor.Db(http));

    private enum DmsCopy { None, Done, Pending }

    /// <summary>
    /// Kopie des ausgestellten Belegs ins DMS, dort fixiert. Maßgeblich bleibt das PDF in Finanzen; scheitert die Kopie, ist der Beleg trotzdem
    /// gültig ausgestellt und die Antwort sagt, was geschehen ist. Ziel ist das Standardprofil: Bei offenem Tresor als Ziel liegt das Dokument dort, die Kennung
    /// gilt also im Tresor. Bei geschlossenem liegt die Datei verschlüsselt bereit und wird erst beim Öffnen ein Dokument, noch ohne Fixierung.
    /// </summary>
    private static async Task<DmsCopy> CopyToDms(UserSpace space, FinDoc doc, CancellationToken ct)
    {
        if (!Features.On(Features.Dms) || Dav.FileStore.FilesOff(space.Db)) return DmsCopy.None;
        try
        {
            var title = $"{FinanceStore.Heading(doc)} {doc.Number}";
            var date = DateTime.ParseExact(doc.DocDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var r = await DmsImport.ImportAsync(space, new MemoryStream(FinanceStore.Pdf(space.Db, doc.Id)), DmsImport.SafeName(title) + ".pdf", "application/pdf",
                new DmsImport.Info("finance", doc.Id.ToString(CultureInfo.InvariantCulture), Subject: title, FileDate: date), ct);
            if (r.Dropped) return DmsCopy.Pending;
            var inTresor = DmsFiling.Default(space).Target == "tresor";
            DmsStore store = inTresor ? DmsStore.ForTresor(space.Db, Tresor.DbOf(space.User.Id) ?? throw new TresorClosedException()) : space.Db;
            Dms.Dms.Fix(store, r.Id);
            FinanceStore.RememberDmsCopy(space.Db, doc.Id, r.Id, inTresor);
            return DmsCopy.Done;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SystemLog.Add(LogLevel.Warning, "Finanzen", $"Beleg {doc.Number} nicht ins DMS kopiert: {e.GetType().Name}");
            return DmsCopy.None;
        }
    }
}
