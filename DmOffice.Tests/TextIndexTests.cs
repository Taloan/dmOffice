using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Search;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MimeKit;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using MailStore = DmOffice.Mail.MailStore;
using NotesDb = DmOffice.Notes.NotesDb;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace DmOffice.Tests;

/// <summary>Nur mit Tesseract und pdftoppm (im Docker-Image) - sonst uebersprungen.</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (!Ocr.Available) Skip = "Tesseract fehlt: " + Ocr.Current.Reason;
        else if (Ocr.Current.PdfToPpm == null) Skip = "pdftoppm fehlt";
    }
}

/// <summary>Testdateien, im Test erzeugt.</summary>
internal static class Samples
{
    public static byte[] Pdf(params string[] lines) => Pdf(12, lines);

    public static byte[] Pdf(double size, params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        double y = 760;
        foreach (var line in lines)
        {
            page.AddText(line, size, new PdfPoint(40, y), font);
            y -= size * 1.8;
        }
        return builder.Build();
    }

    /// <summary>PDF ohne Textebene: nur eine Linie (bzw. ein Bild, wenn angegeben) - wie ein Scan.</summary>
    public static byte[] ScannedPdf(byte[]? png = null)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        if (png != null) page.AddPng(png, new PdfRectangle(0, 0, 595, 842));
        else page.DrawLine(new PdfPoint(40, 400), new PdfPoint(500, 400));
        return builder.Build();
    }

    public static byte[] Docx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(paragraphs.Select(p => new W.Paragraph(new W.Run(new W.Text(p))))));
        }
        return ms.ToArray();
    }

    public static byte[] Xlsx(string shared, string inline, string number)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new X.Workbook();
            var sst = wb.AddNewPart<SharedStringTablePart>();
            sst.SharedStringTable = new X.SharedStringTable(new X.SharedStringItem(new X.Text(shared)));
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = new X.Worksheet(new X.SheetData(new X.Row(
                new X.Cell { DataType = X.CellValues.SharedString, CellValue = new X.CellValue("0") },
                new X.Cell { DataType = X.CellValues.InlineString, InlineString = new X.InlineString(new X.Text(inline)) },
                new X.Cell { CellValue = new X.CellValue(number) })));
            wb.Workbook.AppendChild(new X.Sheets(new X.Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Blatt" }));
        }
        return ms.ToArray();
    }

    /// <summary>PDF-Seite als PNG (pdftoppm) - ein "Foto" mit echtem Text fuer die OCR.</summary>
    public static byte[] Png(byte[] pdf)
    {
        var dir = Directory.CreateTempSubdirectory("dmocr");
        File.WriteAllBytes(Path.Combine(dir.FullName, "a.pdf"), pdf);
        var (code, _, err) = Ocr.Run(Ocr.Current.PdfToPpm!, new[] { "-r", "150", "-png", "-singlefile", Path.Combine(dir.FullName, "a.pdf"), Path.Combine(dir.FullName, "a") },
            null, TimeSpan.FromMinutes(1));
        Assert.True(code == 0, err);
        return File.ReadAllBytes(Path.Combine(dir.FullName, "a.png"));
    }
}

public class TextExtractTests
{
    [Fact]
    public void Pdf_mit_Textebene()
    {
        var r = TextExtract.Extract(Samples.Pdf("Stromrechnung Nummer 4711", "Zaehlerstand 98765 kWh"), "rechnung.pdf", null, ocr: false);
        Assert.Equal("ok", r.Status);
        Assert.Equal("text", r.Method);
        Assert.Contains("Stromrechnung", r.Text);
        Assert.Contains("98765", r.Text);
    }

    [Fact]
    public void Word_und_Excel()
    {
        var w = TextExtract.Extract(Samples.Docx("Mietvertrag fuer die Wohnung", "Kaution 1500 Euro"), "vertrag.docx", null, ocr: false);
        Assert.Equal("ok", w.Status);
        Assert.Contains("Mietvertrag", w.Text);
        Assert.Contains("Kaution 1500", w.Text);

        var x = TextExtract.Extract(Samples.Xlsx("Kontoauszug", "Girokonto", "1234.5"), "konto.xlsx", null, ocr: false);
        Assert.Equal("ok", x.Status);
        Assert.Contains("Kontoauszug", x.Text);
        Assert.Contains("Girokonto", x.Text);
        Assert.Contains("1234.5", x.Text);
    }

    [Fact]
    public void Text_Html_und_angehaengte_Mail()
    {
        Assert.Contains("Grüße", TextExtract.Extract(Encoding.UTF8.GetBytes("Viele Grüße"), "a.txt", null, false).Text);
        // Alte Windows-Datei (nicht UTF-8)
        Assert.Contains("Grüße", TextExtract.Extract(Encoding.Latin1.GetBytes("Viele Grüße"), "a.csv", null, false).Text);
        var html = TextExtract.Extract(Encoding.UTF8.GetBytes("<html><body><p>Absatz <b>fett</b></p><script>x</script></body></html>"), "a.html", null, false);
        Assert.Contains("Absatz fett", html.Text);

        var inner = new MimeMessage { Subject = "Angebot Dachdecker" };
        inner.From.Add(new MailboxAddress("Firma", "info@dach.de"));
        var body = new Multipart("mixed") { new TextPart("plain") { Text = "Siehe Anhang" } };
        body.Add(new MimePart("application", "pdf") { FileName = "angebot.pdf", Content = new MimeContent(new MemoryStream(Samples.Pdf("Ziegel Eindeckung 9900 Euro"))), ContentTransferEncoding = ContentEncoding.Base64 });
        inner.Body = body;
        using var ms = new MemoryStream();
        inner.WriteTo(ms);
        var eml = TextExtract.Extract(ms.ToArray(), "weitergeleitet.eml", null, false);
        Assert.Equal("ok", eml.Status);
        Assert.Contains("Angebot Dachdecker", eml.Text);
        Assert.Contains("Ziegel", eml.Text);
    }

    [Fact]
    public void Angehaengte_eml_wird_nur_eine_Ebene_tief_gelesen()
    {
        // Eine .eml, die als Anhang wieder eine .eml enthaelt, die wieder eine .eml enthaelt.
        // Frueher begann jede Ebene ueber Extract -> Eml wieder bei Tiefe 0 (Stack-Ueberlauf bei vielen Ebenen).
        static byte[] Eml(string subject, params MimeEntity[] attachments)
        {
            var m = new MimeMessage { Subject = subject };
            m.From.Add(new MailboxAddress("A", "a@b.de"));
            var mp = new Multipart("mixed") { new TextPart("plain") { Text = "Rumpf " + subject } };
            foreach (var a in attachments) mp.Add(a);
            m.Body = mp;
            using var s = new MemoryStream(); m.WriteTo(s); return s.ToArray();
        }
        MimePart AsEml(string name, byte[] data) =>
            new("application", "octet-stream") { FileName = name, Content = new MimeContent(new MemoryStream(data)), ContentTransferEncoding = ContentEncoding.Base64 };

        var tief = Eml("INNEN-GEHEIMNIS");
        var mitte = Eml("MITTE", AsEml("tief.eml", tief));
        var oben = Eml("OBEN", AsEml("mitte.eml", mitte));

        var r = TextExtract.Extract(oben, "oben.eml", null, false);
        Assert.Equal("ok", r.Status);
        Assert.Contains("OBEN", r.Text);
        Assert.Contains("MITTE", r.Text);              // eine Ebene tief: Kopf und Rumpf
        Assert.DoesNotContain("INNEN-GEHEIMNIS", r.Text); // tiefer nicht mehr
    }

    [Fact]
    public void Grenzen_je_Datei()
    {
        var pdf = Samples.Pdf("Alpha Beta Gamma Delta Epsilon Zeta Eta Theta Iota Kappa Lambda");
        Assert.Equal("skipped", TextExtract.Extract(pdf, "a.pdf", null, false, new ExtractLimits { MaxBytes = 100 }).Status);
        var cut = TextExtract.Extract(pdf, "a.pdf", null, false, new ExtractLimits { MaxChars = 20 });
        Assert.True(cut.Text.Length <= 20);
        Assert.Equal("skipped", TextExtract.Extract(new byte[10], "a.zip", null, false).Status);
        // Kaputte Datei: Fehler im Ergebnis, keine Ausnahme
        Assert.Equal("error", TextExtract.Extract(Encoding.ASCII.GetBytes("kein pdf"), "a.docx", null, false).Status);
    }

    [Fact]
    public void Bild_und_Scan_ohne_OCR_werden_vorgemerkt()
    {
        var image = TextExtract.Extract(new byte[20_000], "foto.jpg", null, ocr: false);
        Assert.Equal("skipped", image.Status);
        Assert.True(image.WantsOcr);
        // Kleine Bilder (Logos) gar nicht
        Assert.False(TextExtract.Extract(new byte[500], "logo.png", null, ocr: true).WantsOcr);

        var scan = TextExtract.Extract(Samples.ScannedPdf(), "scan.pdf", null, ocr: false);
        Assert.Equal("skipped", scan.Status);
        Assert.True(scan.WantsOcr);
    }

    [OcrFact]
    public void OCR_fuer_Bild_und_gescanntes_PDF()
    {
        var png = Samples.Png(Samples.Pdf(28, "RECHNUNG Zahnarzt", "Betrag 4711 Euro"));
        var image = TextExtract.Extract(png, "foto.png", "image/png", ocr: true);
        Assert.Equal("ocr", image.Method);
        Assert.Contains("Zahnarzt", image.Text);

        var scan = TextExtract.Extract(Samples.ScannedPdf(png), "scan.pdf", null, ocr: true);
        Assert.Equal("ocr", scan.Method);
        Assert.Contains("Zahnarzt", scan.Text);
        Assert.Contains("4711", scan.Text);
    }
}

public class TextIndexTests
{
    private static UserSpace NewSpace(bool enabled = true)
    {
        var dir = Directory.CreateTempSubdirectory("dmtext");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "a", Email = "ich@x.de" }));
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true), ("Archiv", "Archiv", "/", null, true) });
        if (enabled) Enable(space);
        return space;
    }

    private static void Enable(UserSpace space, bool ocr = false)
    {
        var before = space.Settings.TextIndex;
        space.UpdateSettings(s => s.TextIndex = new TextIndexSettings { Enabled = true, Ocr = ocr });
        TextIndex.Apply(space, before);
    }

    private static int Run(UserSpace space) => TextIndexer.RunAsync(space, 1000, CancellationToken.None).GetAwaiter().GetResult();

    private static long Queued(UserDb db) => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM text_queue"));

    private static List<string> Found(UserSpace space, string q) => TextIndex.SearchFiles(space, q).Select(h => h.Path).ToList();

    [Fact]
    public void Neue_Datei_wird_eingereiht_und_gefunden()
    {
        var space = NewSpace();
        FileStore.EnsureDir(space.Db, "Belege");
        FileStore.Put(space.Db, "Belege/strom.pdf", Samples.Pdf("Stromrechnung Stadtwerke", "Abschlag 87 Euro"), "application/pdf");
        Assert.Equal(1, Queued(space.Db));
        Run(space);
        Assert.Equal(0, Queued(space.Db));

        var hit = Assert.Single(TextIndex.SearchFiles(space, "stadtwerk"));
        Assert.Equal("Belege/strom.pdf", hit.Path);
        Assert.Equal("strom.pdf", hit.Name);
        Assert.Contains(TextIndex.MarkStart + "Stadtwerke" + TextIndex.MarkEnd, hit.Snippet);
        // Unterhalb eines Ordners suchen
        Assert.Single(TextIndex.SearchFiles(space, "stadtwerke", "Belege"));
        Assert.Empty(TextIndex.SearchFiles(space, "stadtwerke", "Anderes"));

        var state = TextIndex.State(space);
        Assert.Equal(1, state.WithText);
        Assert.Equal(0, state.Pending);
        Assert.NotNull(state.LastRun);
    }

    [Fact]
    public void Aendern_Verschieben_Umbenennen_und_Loeschen_ziehen_nach()
    {
        var space = NewSpace();
        var db = space.Db;
        FileStore.EnsureDir(db, "a");
        FileStore.EnsureDir(db, "b");
        FileStore.Put(db, "a/vertrag.docx", Samples.Docx("Handyvertrag Laufzeit 24 Monate"), null);
        Run(space);
        Assert.Equal(new[] { "a/vertrag.docx" }, Found(space, "handyvertrag"));

        // Inhalt geaendert: neu gelesen, alter Text weg
        FileStore.Put(db, "a/vertrag.docx", Samples.Docx("Stromvertrag Laufzeit 12 Monate"), null);
        Assert.Equal(1, Queued(db));
        Run(space);
        Assert.Empty(Found(space, "handyvertrag"));
        Assert.Equal(new[] { "a/vertrag.docx" }, Found(space, "stromvertrag"));

        // Umbenennen und Verschieben (ganzer Ordner): Pfad und Name folgen ohne neues Lesen
        FileStore.MoveOrCopy(db, "a/vertrag.docx", "a/strom.docx", false, copy: false);
        Assert.Equal(0, Queued(db));
        Assert.Equal(new[] { "a/strom.docx" }, Found(space, "stromvertrag"));
        Assert.Equal(new[] { "a/strom.docx" }, Found(space, "strom"));   // auch der neue Name ist durchsuchbar
        FileStore.MoveOrCopy(db, "a", "b/alt", false, copy: false);
        Assert.Equal(new[] { "b/alt/strom.docx" }, Found(space, "stromvertrag"));

        // Kopie: gleicher Inhalt, Text wird uebernommen
        FileStore.MoveOrCopy(db, "b/alt/strom.docx", "b/kopie.docx", false, copy: true);
        Run(space);
        Assert.Equal(2, Found(space, "stromvertrag").Count);

        // Loeschen: sofort aus dem Index
        FileStore.Delete(db, "b/alt");
        Assert.Equal(new[] { "b/kopie.docx" }, Found(space, "stromvertrag"));
        FileStore.Delete(db, "b/kopie.docx");
        Assert.Empty(Found(space, "stromvertrag"));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM text_docs")));
    }

    [Fact]
    public void Ausgeschaltet_wird_nichts_eingereiht_und_Einschalten_holt_den_Bestand()
    {
        var space = NewSpace(enabled: false);
        FileStore.Put(space.Db, "alt.txt", Encoding.UTF8.GetBytes("Kfz Versicherung Beitrag"), null);
        Assert.Equal(0, Queued(space.Db));
        Enable(space);
        Assert.Equal(1, Queued(space.Db));
        Run(space);
        Assert.Single(Found(space, "versicherung"));

        // Ausschalten verwirft den Index
        var before = space.Settings.TextIndex;
        space.UpdateSettings(s => s.TextIndex = new TextIndexSettings());
        TextIndex.Apply(space, before);
        Assert.Empty(Found(space, "versicherung"));
        FileStore.Put(space.Db, "neu.txt", Encoding.UTF8.GetBytes("Kfz"), null);
        Assert.Equal(0, Queued(space.Db));
    }

    [Fact]
    public void Neu_aufbauen_liest_alles_noch_einmal()
    {
        var space = NewSpace();
        FileStore.Put(space.Db, "a.txt", Encoding.UTF8.GetBytes("Steuerbescheid Finanzamt"), null);
        Run(space);
        TextIndex.Rebuild(space);
        Assert.Empty(Found(space, "finanzamt"));
        Assert.Equal(1, Queued(space.Db));
        Run(space);
        Assert.Single(Found(space, "finanzamt"));
    }

    [Fact]
    public void Der_notizi_Ordner_wird_nie_indiziert()
    {
        var space = NewSpace(enabled: false);
        var db = space.Db;
        FileStore.EnsureDir(db, "notizisync/db");
        FileStore.Put(db, "notizisync/db/notiz.txt", Encoding.UTF8.GetBytes("Geheimnotiz Tresorcode"), null);
        FileStore.Put(db, "notizisync2.txt", Encoding.UTF8.GetBytes("Tresorcode daneben"), null);
        Enable(space);
        // Bestand: der Ordner kommt gar nicht erst in die Warteschlange
        Assert.Equal(1, Queued(db));
        // Neue Datei dort: der Trigger reiht ein, die Warteschlange liest sie aber nicht
        FileStore.Put(db, "notizisync/db/neu.txt", Encoding.UTF8.GetBytes("Tresorcode neu"), null);
        Run(space);
        Assert.Equal(new[] { "notizisync2.txt" }, Found(space, "tresorcode"));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM text_docs WHERE path LIKE 'notizisync/%'")));
        // Auch unter dem Ordner gesucht: nichts
        Assert.Empty(TextIndex.SearchFiles(space, "tresorcode", "notizisync"));
    }

    [Fact]
    public void Fehler_beim_Lesen_stehen_im_Zustand()
    {
        var space = NewSpace();
        FileStore.Put(space.Db, "kaputt.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 kaputt"), "application/pdf");
        FileStore.Put(space.Db, "foto.jpg", new byte[20_000], "image/jpeg");
        Run(space);
        var state = TextIndex.State(space);
        Assert.Equal(1, state.Errors);
        Assert.Equal("kaputt.pdf", Assert.Single(state.RecentErrors).Name);
        // Bild ohne OCR: vorgemerkt, nicht als Fehler
        Assert.Equal(1L, Convert.ToInt64(space.Db.Scalar("SELECT want_ocr FROM text_docs WHERE path='foto.jpg'")));
    }

    // ---------------- Mail-Anhaenge ----------------

    private static MimeMessage Mail(string subject, string messageId)
    {
        var m = new MimeMessage { Subject = subject, MessageId = messageId };
        m.From.Add(new MailboxAddress("Stadtwerke", "rechnung@stadtwerke.de"));
        m.To.Add(new MailboxAddress("Ich", "ich@x.de"));
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "Ihre Rechnung finden Sie im Anhang." } };
        mixed.Add(new MimePart("application", "pdf")
        {
            FileName = "Rechnung-2026-10.pdf", Content = new MimeContent(new MemoryStream(Samples.Pdf("Jahresabrechnung Fernwaerme", "Zaehlernummer 556677"))),
            ContentTransferEncoding = ContentEncoding.Base64,
        });
        mixed.Add(new MimePart("application", "vnd.openxmlformats-officedocument.wordprocessingml.document")
        {
            FileName = "AGB.docx", Content = new MimeContent(new MemoryStream(Samples.Docx("Allgemeine Geschaeftsbedingungen Kuendigungsfrist"))),
            ContentTransferEncoding = ContentEncoding.Base64,
        });
        // Eingebettetes Logo: kein Anhang, keine OCR
        mixed.Add(new MimePart("image", "png") { ContentId = "logo@x", Content = new MimeContent(new MemoryStream(new byte[30_000])), ContentTransferEncoding = ContentEncoding.Base64 });
        m.Body = mixed;
        return m;
    }

    private static void Store(UserDb db, string folder, uint uid, MimeMessage m, bool raw = true)
    {
        db.Exec("INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,date,size,has_att) VALUES('a',$f,$u,$mid,$s,'Stadtwerke','rechnung@stadtwerke.de',$d,1000,1)",
            ("$f", folder), ("$u", (long)uid), ("$mid", m.MessageId), ("$s", m.Subject), ("$d", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE folder=$f AND uid=$u", ("$f", folder), ("$u", (long)uid)));
        db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,'Stadtwerke rechnung@stadtwerke.de','','')", ("$i", id), ("$s", m.Subject));
        if (!raw) return;
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        MailStore.StoreRaw(db, "a", folder, uid, ms.ToArray(), m);
    }

    [Fact]
    public void Mail_wird_ueber_den_Inhalt_ihrer_Anhaenge_gefunden()
    {
        var space = NewSpace();
        var db = space.Db;
        Store(db, "INBOX", 1, Mail("Ihre Rechnung", "r1@stadtwerke.de"));
        Assert.Equal(1, Queued(db));
        Run(space);

        var hit = Assert.Single(MailStore.Search(db, "fernwaerme", null, null));
        Assert.Equal("Rechnung-2026-10.pdf", hit.Match?.Name);
        Assert.Equal(0, hit.Match?.Part);
        Assert.Contains(TextIndex.MarkStart + "Fernwaerme" + TextIndex.MarkEnd, hit.Match?.Snippet);
        Assert.Equal("AGB.docx", Assert.Single(MailStore.Search(db, "kuendigungsfrist", null, null)).Match?.Name);
        // Feldangaben gelten der Mail, freie Woerter dem Anhang
        Assert.Single(MailStore.Search(db, "von:stadtwerke fernwaerme", null, null));
        Assert.Empty(MailStore.Search(db, "von:finanzamt fernwaerme", null, null));
        // Treffer im Text der Mail selbst bleiben ohne Anhang-Angabe
        Assert.Null(Assert.Single(MailStore.Search(db, "rechnung", null, null)).Match);
        // Logo: nicht im Index
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM text_docs WHERE name LIKE 'Teil%'")));
    }

    [Fact]
    public void Verschieben_Schlank_und_Loeschen_einer_Mail()
    {
        var space = NewSpace();
        var db = space.Db;
        var m = Mail("Ihre Rechnung", "r2@stadtwerke.de");
        Store(db, "INBOX", 7, m);
        Run(space);

        // Verschieben: lokal geloescht, im Ziel neu (noch ohne Inhalt) - der Text bleibt an der Message-ID
        MailStore.Delete(db, "a", "INBOX", new uint[] { 7 });
        Assert.Empty(MailStore.Search(db, "fernwaerme", null, null));
        Store(db, "Archiv", 3, m, raw: false);
        var hit = Assert.Single(MailStore.Search(db, "fernwaerme", "a", "Archiv"));
        Assert.Equal(3u, hit.Uid);
        Assert.Empty(MailStore.Search(db, "fernwaerme", "a", "INBOX"));

        // Schlank: Inhalt verworfen, Text bleibt
        Store(db, "INBOX", 8, Mail("Zweite", "r3@stadtwerke.de"));
        Run(space);
        db.Exec("DELETE FROM mail_raw; UPDATE mail SET cached=0;");
        Assert.Equal(2, MailStore.Search(db, "fernwaerme", null, null).Count);

        // Endgueltig geloescht: nach der Frist auch der Text
        MailStore.Delete(db, "a", "Archiv", new uint[] { 3 });
        Assert.Equal(0, TextIndex.Prune(space));
        db.Exec("UPDATE text_docs SET gone = gone - 864000 WHERE gone IS NOT NULL");
        Assert.True(TextIndex.Prune(space) > 0);
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM text_docs WHERE msgid='r2@stadtwerke.de'")));
        Assert.Single(MailStore.Search(db, "fernwaerme", null, null));
    }

    // ---------------- Notizen ----------------

    private static string Blob(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void Notizen_ueber_Titel_Text_und_Anhaenge_finden_und_nachziehen()
    {
        var space = NewSpace();
        var notes = NotesDb.Of(space);
        var id = notes.CreateNote(null);
        notes.SaveNote(id, "<p>Einkaufsliste Baumarkt</p>", "Einkaufsliste Baumarkt");
        var pdf = Samples.Pdf("Garantieschein Bohrmaschine", "Seriennummer XK-2207");
        notes.PutBlob(Blob(pdf), pdf);
        notes.AddAttachment(id, "garantie.pdf", "application/pdf", pdf, Blob(pdf));
        // Eingebettetes Bild ohne Namen: als Bild erkannt und (ohne OCR) fuer spaeter vorgemerkt
        var png = new byte[20_000];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        notes.PutBlob(Blob(png), png);
        notes.SaveNote(id, $"<p>Einkaufsliste Baumarkt</p><img src=\"blob:{Blob(png)}\">", "Einkaufsliste Baumarkt");
        Assert.True(Queued(space.Db) >= 1);
        Run(space);

        // Titel und Text wie bisher, ohne Anhang-Hinweis
        Assert.Null(Assert.Single(notes.Search("baumarkt")).Match);
        // Inhalt des Anhangs
        var hit = Assert.Single(notes.Search("seriennummer"));
        Assert.Equal(id, hit.Id);
        Assert.Equal("garantie.pdf", hit.Match?.Name);
        Assert.Equal(Blob(pdf), hit.Match?.Blob);
        Assert.Contains(TextIndex.MarkStart + "Seriennummer" + TextIndex.MarkEnd, hit.Match?.Snippet);
        Assert.Equal(1L, Convert.ToInt64(space.Db.Scalar("SELECT want_ocr FROM text_docs WHERE kind='note' AND name='Bild 1'")));
        // Sonderzeichen brechen die Suche nicht mehr
        Assert.Empty(notes.Search("\"(*"));

        // Anhang entfernt: neu gelesen, Text weg
        notes.RemoveAttachment(id, "garantie.pdf");
        Run(space);
        Assert.Empty(notes.Search("seriennummer"));

        // Papierkorb: nicht gefunden; endgueltig geloescht: alles aus dem Index
        notes.AddAttachment(id, "garantie.pdf", "application/pdf", pdf, Blob(pdf));
        Run(space);
        Assert.Single(notes.Search("seriennummer"));
        notes.TrashNote(id);
        Assert.Empty(notes.Search("seriennummer"));
        notes.DeleteNote(id);
        Assert.Equal(0L, Convert.ToInt64(space.Db.Scalar("SELECT COUNT(*) FROM text_docs WHERE kind='note'")));
    }

    [Fact]
    public void Notizen_aus_dem_Abgleich_kommen_in_den_Bestand()
    {
        var space = NewSpace(enabled: false);
        var notes = NotesDb.Of(space);
        var id = notes.CreateNote(null);
        var docx = Samples.Docx("Rezept Apfelkuchen mit Zimt");
        notes.PutBlob(Blob(docx), docx);
        notes.AddAttachment(id, "rezept.docx", null, docx, Blob(docx));
        Assert.Equal(0, Queued(space.Db));
        Enable(space);
        Assert.Equal(1, Queued(space.Db));
        Run(space);
        Assert.Equal("rezept.docx", Assert.Single(notes.Search("apfelkuchen")).Match?.Name);
        // Abgleich vergisst alles (Trennen, anderer Ordner): Index folgt
        notes.Clear();
        Assert.Equal(0L, Convert.ToInt64(space.Db.Scalar("SELECT COUNT(*) FROM text_docs")));
    }

    [Fact]
    public void Bestand_an_Mails_wird_eingereiht_und_bereits_Gelesenes_nicht_doppelt()
    {
        var space = NewSpace(enabled: false);
        var db = space.Db;
        Store(db, "INBOX", 1, Mail("A", "a@x"));
        Store(db, "INBOX", 2, Mail("B", "b@x"));
        Assert.Equal(0, Queued(db));
        Enable(space);
        Assert.Equal(2, Queued(db));
        Run(space);
        Assert.Equal(2, MailStore.Search(db, "zaehlernummer", null, null).Count);
        // Erneut geholt (Schlank: beim Oeffnen nachgeladen): kein zweites Lesen noetig
        using (var ms = new MemoryStream())
        {
            var m = Mail("A", "a@x");
            m.WriteTo(ms);
            MailStore.StoreRaw(db, "a", "INBOX", 1, ms.ToArray(), m);
        }
        var before = Convert.ToInt64(db.Scalar("SELECT MAX(id) FROM text_docs"));
        Run(space);
        Assert.Equal(before, Convert.ToInt64(db.Scalar("SELECT MAX(id) FROM text_docs")));
    }
}

/// <summary>Die Endpunkte: Schalter und Zustand, Inhaltssuche der Dateiablage, Mail-Suche.</summary>
[Collection("server")]
public class TextIndexApiTests : IClassFixture<NextcloudServerFixture>
{
    private readonly NextcloudServerFixture _fx;
    public TextIndexApiTests(NextcloudServerFixture fx) => _fx = fx;

    private async Task<HttpClient> Web()
    {
        var web = _fx.Factory.CreateClient();
        Assert.True((await web.PostAsJsonAsync("/api/auth/login", new { login = NextcloudServerFixture.Login, password = NextcloudServerFixture.Password })).IsSuccessStatusCode);
        return web;
    }

    [Fact]
    public async Task Einschalten_hochladen_und_im_Inhalt_finden()
    {
        var web = await Web();
        var state = await web.GetFromJsonAsync<JsonElement>("/api/settings/textindex");
        Assert.False(state.GetProperty("enabled").GetBoolean());

        // OCR laesst sich nur einschalten, wenn der Server sie kann
        var put = await web.PutAsJsonAsync("/api/settings/textindex", new { enabled = true, ocr = true });
        state = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(Ocr.Available, state.GetProperty("ocr").GetBoolean());
        Assert.Equal(Ocr.Available, state.GetProperty("ocrAvailable").GetBoolean());

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Samples.Pdf("Kaufvertrag Gebrauchtwagen", "Fahrgestellnummer WVW123")), "file", "kaufvertrag.pdf");
        Assert.True((await web.PostAsync("/api/files/upload?path=", form)).IsSuccessStatusCode);
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Factory.Services.GetRequiredService<UserStore>().ByLogin(NextcloudServerFixture.Login)!.Id);
        FileStore.EnsureDir(space.Db, "notizisync");
        FileStore.Put(space.Db, "notizisync/x.txt", Encoding.UTF8.GetBytes("Gebrauchtwagen geheim"), null);
        await TextIndexer.RunAsync(space, 100, CancellationToken.None);

        var result = await web.GetFromJsonAsync<JsonElement>("/api/files/search/content?q=gebrauchtwagen");
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal("kaufvertrag.pdf", item.GetProperty("path").GetString());
        Assert.Contains("Gebrauchtwagen", item.GetProperty("snippet").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("/api/files/search/content?q=geheim&path=notizisync")).StatusCode);

        // Auch fuer die Handy-App (App-Passwort)
        var app = _fx.Client(_fx.AppPassword);
        Assert.Contains("kaufvertrag.pdf", await app.GetStringAsync("/api/files/search/content?q=fahrgestellnummer"));

        state = await web.GetFromJsonAsync<JsonElement>("/api/settings/textindex");
        Assert.Equal(1, state.GetProperty("withText").GetInt32());
        Assert.Equal(0, state.GetProperty("pending").GetInt32());

        state = await (await web.PostAsync("/api/settings/textindex/rebuild", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, state.GetProperty("pending").GetInt32());
        Assert.Equal(0, state.GetProperty("withText").GetInt32());
    }
}
