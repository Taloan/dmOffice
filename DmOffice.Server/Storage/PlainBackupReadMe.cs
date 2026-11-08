using System.Globalization;
using System.Text;
using DmOffice.Core;

namespace DmOffice.Storage;

/// <summary>Die LIESMICH.txt (README.txt) im Ziel des Datenexports: was das ist, wie es aufgebaut ist, womit man was öffnet, was fehlt.
/// Deutsch und Englisch in einer Datei - im Notfall soll sie jeder ohne dmOffice verstehen.</summary>
public static class PlainBackupReadMe
{
    public static string Text(UserSpace space, PlainBackupSettings s, DateTime startedUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(startedUtc, space.Zone);
        var L = PlainLang.For(s.Lang);
        var on = PlainBackupAreas.Available(space).Where(s.Areas.Contains).ToHashSet();
        string Area(string key, string de, string en) => on.Contains(key) ? $"  {de} / {en}\n" : "";
        var keep = s.TrashDays == 0 ? "nie entfernt" : $"nach {s.TrashDays} Tagen entfernt";
        var keepEn = s.TrashDays == 0 ? "never removed" : $"removed after {s.TrashDays} days";
        var sb = new StringBuilder();
        sb.Append($"""
            Datenexport aus dmOffice
            ================================

            Person: {space.User.DisplayName} ({space.User.Login})
            Zuletzt exportiert: {local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} ({space.Zone.Id})

            Diese Ordner sind eine lesbare Kopie der Daten aus dmOffice. Sie lassen sich ohne dmOffice öffnen.
            Die Kopie wird nur geschrieben: Änderungen hier kommen nicht nach dmOffice zurück.

            Enthalten:

            """);
        sb.Append(Area(PlainBackup.Files, $"{L.Files}/ – Dateien der Ablage im Original, in ihrer Ordnerstruktur (auch Fotos der Galerie)", $"{L.Files}/ – stored files as originals, in their folders (including gallery photos)"));
        sb.Append(Area(PlainBackup.Gallery, $"{L.Gallery}/{L.Albums} – Alben mit den Pfaden der Fotos", $"{L.Gallery}/{L.Albums} – albums with the paths of their photos"));
        sb.Append(Area(PlainBackup.Calendar, $"{L.Calendar}/*.ics – je Kalender eine Datei, in jedes Kalenderprogramm importierbar", $"{L.Calendar}/*.ics – one file per calendar, importable into any calendar app"));
        sb.Append(Area(PlainBackup.Tasks, $"{L.Tasks}/*.ics – je Aufgabenliste eine Datei", $"{L.Tasks}/*.ics – one file per task list"));
        sb.Append(Area(PlainBackup.Contacts, $"{L.Contacts}/*.vcf – je Adressbuch eine Datei mit allen Kontakten", $"{L.Contacts}/*.vcf – one file per address book"));
        sb.Append(Area(PlainBackup.Notes, $"{L.Notes}/*.md – je Notiz eine Markdown-Datei, Bilder und Anhänge im Ordner „…{L.Attachments}“", $"{L.Notes}/*.md – one Markdown file per note, images and attachments in the folder \"…{L.Attachments}\""));
        sb.Append(Area(PlainBackup.Dms, $"{L.Dms}/{L.DmsList}.csv und .json – Titel, Datum, Korrespondent, Typ, Tags je Dokument; die Dokumente selbst unter {L.Files}/, getrennt gehaltene Originale unter {L.Dms}/{L.Originals}/", $"{L.Dms}/{L.DmsList}.csv and .json – metadata per document; the documents are under {L.Files}/, separately kept originals under {L.Dms}/{L.Originals}/"));
        sb.Append(Area(PlainBackup.Journal, $"{L.Journal}/<Tagebuch>/<Jahr>/*.md – je Eintrag eine Markdown-Datei; Fotos, Videos und Sprachaufnahmen daneben im Ordner Media/", $"{L.Journal}/<journal>/<year>/*.md – one Markdown file per entry; photos, videos and voice recordings next to it in Media/"));
        sb.Append(Area(PlainBackup.Coach, $"{L.Coach}/*.csv – Mahlzeiten, Gewicht, Aktivität usw. als Tabellen; {L.Profile}", $"{L.Coach}/*.csv – meals, weight, activity etc. as tables; {L.Profile}"));
        sb.Append(Area(PlainBackup.Finance, $"{L.Finance}/ – ausgestellte Belege als PDF; Einnahmen und Ausgaben, Belege, Kunden, Protokoll als Tabellen", $"{L.Finance}/ – issued documents as PDF; income and expenses, documents, customers, log as tables"));
        sb.Append($"""

            Öffnen:
              .ics → Kalenderprogramm (Outlook, Thunderbird, Google Kalender, Apple Kalender)
              .vcf → Adressbuch (Kontakte-App, Outlook, Thunderbird)
              .md  → Texteditor oder Markdown-Programm (z. B. Obsidian)
              .csv → Tabellenkalkulation (Excel, LibreOffice); Trennzeichen Semikolon
              .json → Texteditor

            """);
        sb.Append($"""
            Der versteckte Ordner .trash:
              Was in dmOffice gelöscht wurde, bleibt hier zunächst erhalten – mit derselben Ordnerstruktur.
              {(s.Versions ? "Auch ältere Versionen geänderter Dateien liegen dort (höchstens eine je Tag, bei Kalendern, Kontakten und Listen eine je Woche).\n  " : "")}Liegt dort schon etwas mit gleichem Namen, steht das Datum im Namen, z. B. „Rechnung (2026-10-31).pdf“.
              Einträge in .trash werden {keep}.
              Versteckte Ordner muss man im Dateimanager eventuell erst einblenden.

            Nicht enthalten: E-Mails (liegen beim Mailanbieter), externe Ordner, frühere Versionen und Papierkorb aus
            dmOffice, Chat, Passwörter und Schlüssel. Der Tresor ist nur enthalten, wenn er als Cryptomator-Ordner unter
            {L.Files}/ liegt (dann verschlüsselt, zu öffnen mit Cryptomator und dem Tresor-Passwort).

            Alles auf dem eigenen Rechner: diesen Zielordner mit dem Programm des Anbieters (z. B. pCloud Drive) oder einem
            WebDAV-Programm auf den PC synchronisieren.

            ---

            Data export from dmOffice. Last export: {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.
            These folders are a readable copy of the data in dmOffice and can be opened without dmOffice. The copy is
            write-only: changes made here do not go back to dmOffice. The hidden folder .trash keeps deleted items
            {(s.Versions ? "and older versions of changed files " : "")}with the same folder structure; names get the date if needed. Items in .trash are {keepEn}.
            Not included: e-mail, external folders, previous versions and trash in dmOffice, chat, passwords and keys. The vault is only
            included if it lies under {L.Files}/ as a Cryptomator folder (encrypted; open it with Cryptomator and the vault password).
            To have everything on your own computer, sync this target folder to your PC.

            """);
        return sb.ToString();
    }
}
