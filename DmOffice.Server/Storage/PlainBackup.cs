using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;

namespace DmOffice.Storage;

/// <summary>Einstellungen der Datensicherung einer Person (in ihrer verschlüsselten Datenbank, wie eigene WebDAV-Ordner).</summary>
public sealed class PlainBackupSettings
{
    public bool Enabled { get; set; }
    /// <summary>Art des Ziels: „webdav“ (eigene Adresse), „external“ (ein eigener externer Ordner, Zugangsdaten von dort) oder
    /// „onedrive“ (Anmeldung bei Microsoft).</summary>
    public string Target { get; set; } = PlainBackup.External;
    /// <summary>Externer Ordner (Kennung).</summary>
    public string? Mount { get; set; }
    /// <summary>Ordner im externen Ordner bzw. in OneDrive.</summary>
    public string Folder { get; set; } = "";
    /// <summary>OneDrive: Refresh-Token der Anmeldung und das Konto (nur zur Anzeige).</summary>
    public string? OneDriveToken { get; set; }
    public string? OneDriveAccount { get; set; }
    public string Url { get; set; } = "";
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Bestätigt für dieses Ziel (<see cref="PlainBackup.ConfirmKey"/>) - ändert es sich, erneut bestätigen.</summary>
    public string? Confirmed { get; set; }
    /// <summary>Uhrzeit des täglichen Laufs (HH:mm, Zeitzone der Person).</summary>
    public string Time { get; set; } = "03:00";
    /// <summary>Tage, die Gelöschtes und ältere Versionen in .trash bleiben; 0 = nie entfernen.</summary>
    public int TrashDays { get; set; } = 30;
    /// <summary>Ältere Versionen geänderter Dateien nach .trash statt überschreiben.</summary>
    public bool Versions { get; set; } = true;
    public List<string> Areas { get; set; } = [.. PlainBackup.AllAreas];
    /// <summary>Finanzen kam später dazu: Bestehende Sicherungen nehmen den Bereich einmal auf, damit er nicht unbemerkt fehlt. Abwählen bleibt danach bestehen.</summary>
    public bool FinanceOffered { get; set; }
    /// <summary>Abgewählte Ordner: „files:&lt;Pfad&gt;“, „calendar:&lt;Kennung&gt;“, „contacts:…“, „notes:…“, „journal:…“.</summary>
    public List<string> Skip { get; set; } = new();
    /// <summary>
    /// Sprache der Ordner- und Dateinamen im Ziel („de“, „en“): beim ersten Lauf aus der Oberflächensprache festgelegt und danach behalten -
    /// ein späterer Wechsel der Oberflächensprache verschöbe sonst den ganzen Export. Ein neues Ziel legt sie neu fest.
    /// </summary>
    public string? Lang { get; set; }
}

/// <summary>Ordner- und Dateinamen im Ziel je Sprache.</summary>
public sealed record PlainLang(string Files, string Gallery, string Calendar, string Tasks, string Contacts, string Notes, string Dms, string Journal,
    string Coach, string Finance, string Albums, string Archive, string Attachments, string Image, string Note, string Originals, string DmsList,
    string Profile, string Receipts, string EntriesYear, string ReceiptList, string Customers, string Log, string ReadMe)
{
    public static readonly PlainLang De = new("Dateien", "Galerie", "Kalender", "Aufgaben", "Kontakte", "Notizen", "DMS", "Tagebuch",
        "Coach", "Finanzen", "Alben.json", "Archiv", " – Anhänge", "Bild", "Notiz", "Originale", "Dokumente",
        "Profil.json", "Belege", "Einnahmen und Ausgaben {0}.csv", "Belege.csv", "Kunden.csv", "Protokoll.csv", "LIESMICH.txt");
    public static readonly PlainLang En = new("Files", "Gallery", "Calendar", "Tasks", "Contacts", "Notes", "DMS", "Journal",
        "Coach", "Finances", "Albums.json", "Archive", " – Attachments", "Image", "Note", "Originals", "Documents",
        "Profile.json", "Documents", "Income and expenses {0}.csv", "Documents.csv", "Customers.csv", "Log.csv", "README.txt");
    public static PlainLang For(string? lang) => lang == "en" ? En : De;
}

/// <summary>Ergebnis der Läufe (in der Datenbank der Person).</summary>
public sealed class PlainBackupState
{
    public DateTime? LastRun { get; set; }
    public DateTime? LastOk { get; set; }
    public string? LastError { get; set; }
    public double Seconds { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int Written { get; set; }
    public int Moved { get; set; }
    public int Trashed { get; set; }
    public int Purged { get; set; }
    public int Failed { get; set; }
    /// <summary>Fehlgeschlagene Läufe nacheinander.</summary>
    public int Fails { get; set; }
    /// <summary>Wann Pukki zuletzt über Fehler Bescheid gab (bis zum nächsten erfolgreichen Lauf nur einmal).</summary>
    public DateTime? Told { get; set; }
    /// <summary>Zuletzt bediente Uhrzeit (UTC) - der nächste Lauf kommt bei der nächsten.</summary>
    public DateTime? LastSlot { get; set; }
}

/// <summary>Ein Eintrag im Ziel: Pfad, Stempel (null: Prüfsumme des Inhalts) und Inhalt.</summary>
/// <param name="Open">Original als Stream (Dateien, Originale vom DMS)</param>
/// <param name="Write">Erzeugter Inhalt (Kalender, Notizen, Listen)</param>
/// <param name="Collection">Sammeldatei: ältere Versionen nur einmal je Woche nach .trash</param>
/// <param name="Modified">Änderungszeit des Originals - zum Wiedererkennen im Ziel nach einem Zielwechsel</param>
public sealed record PlainItem(string Path, string? Stamp, long Size, Func<Stream>? Open, Func<Stream, Task>? Write, bool Collection = false, DateTime? Modified = null);

public sealed record PlainProgress(DateTime Started, int Done, int Total, long Bytes, string? Current);

/// <summary>
/// Datensicherung (docs/datensicherung-konzept.md): eine lesbare Kopie an ein Ziel der Person (WebDAV). Nur Schreiben,
/// nie zurücklesen. Je Eintrag merkt sich die Datenbank einen Stempel; gleicher Stempel → nichts tun. Gelöschtes und ältere
/// Versionen wandern im Ziel nach .trash und werden erst nach der Frist entfernt. Eine Datei nach der anderen, gestreamt;
/// Erzeugtes über eine Zwischendatei auf der Platte, nie ganz im Speicher.
/// </summary>
public static class PlainBackup
{
    public const string Key = "plainbackup", StateKey = "plainbackup.state", TargetKey = "plainbackup.target";
    public const string Trash = ".trash", ReadMe = "LIESMICH.txt", Part = ".part";
    public const string WebDav = "webdav", External = "external", OneDrive = "onedrive";
    /// <summary>Vorgabe für den Ordner in OneDrive.</summary>
    public const string OneDriveFolder = "Datensicherung";
    public const string Files = "files", Gallery = "gallery", Calendar = "calendar", Tasks = "tasks", Contacts = "contacts", Notes = "notes",
        Dms = "dms", Journal = "journal", Coach = "coach", Finance = "finance";
    /// <summary>Bereiche in der Reihenfolge der Oberfläche. Der Tresor braucht keinen eigenen: als Cryptomator-Ordner in der Dateiablage
    /// (docs/tresor-cryptomator-konzept.md) kommt er mit „Dateien“ so ins Ziel, wie er ist.</summary>
    public static readonly string[] AllAreas = [Files, Gallery, Calendar, Tasks, Contacts, Notes, Dms, Journal, Coach, Finance];
    public static readonly int[] TrashChoices = [7, 30, 90, 0];
    private const int MaxFailsInRow = 5;
    /// <summary>Bis zu dieser Größe direkt an den Platz schreiben (eine Anfrage), darüber über .part und Umbenennen.</summary>
    public const long SmallFile = 4 * 1024 * 1024;

    public static PlainBackupSettings Settings(UserDb db)
    {
        var s = db.GetJson<PlainBackupSettings>(Key);
        if (!s.FinanceOffered)
        {
            s.FinanceOffered = true;
            if (!s.Areas.Contains(Finance)) s.Areas.Add(Finance);
        }
        return s;
    }
    public static void Save(UserDb db, PlainBackupSettings s) => db.SetJson(Key, s);
    public static PlainBackupState State(UserDb db) => db.GetJson<PlainBackupState>(StateKey);
    public static void Save(UserDb db, PlainBackupState s) => db.SetJson(StateKey, s);

    /// <summary>Was bestätigt sein muss: das Ziel (Adresse und Benutzer bzw. externer Ordner).</summary>
    public static string ConfirmKey(PlainBackupSettings s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TargetText(s) + "\nplain")))[..16];

    /// <summary>Ziel ohne Passwort - wechselt es, gelten die gemerkten Stempel nicht mehr.</summary>
    public static string TargetId(PlainBackupSettings s) => TargetOf(s);

    /// <summary>Gleiches Ziel unter neuer Angabe (früheres Ziel wurde ein externes Ziel): gemerkte Stempel gelten weiter.</summary>
    public static void Retarget(UserDb db, string previous, PlainBackupSettings s)
    {
        Ensure(db);
        if (db.Get(TargetKey) == previous) db.Set(TargetKey, TargetOf(s));
    }

    private static string TargetOf(PlainBackupSettings s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TargetText(s))))[..16];

    private static string TargetText(PlainBackupSettings s) => s.Target switch
    {
        External => "external\n" + s.Mount + "\n" + StoragePath.Clean(s.Folder),
        OneDrive => "onedrive\n" + s.OneDriveAccount + "\n" + StoragePath.Clean(s.Folder),
        _ => s.Url.Trim().TrimEnd('/') + "\n" + (s.User ?? ""),
    };

    /// <summary>Ist ein Ziel eingetragen?</summary>
    public static bool HasTarget(PlainBackupSettings s) => s.Target switch
    {
        External => !string.IsNullOrEmpty(s.Mount),
        OneDrive => !string.IsNullOrEmpty(s.OneDriveToken) && StoragePath.Clean(s.Folder).Length > 0,
        _ => s.Url.Length > 0,
    };

    public static bool Ready(PlainBackupSettings s) => s.Enabled && HasTarget(s) && s.Confirmed == ConfirmKey(s);

    public static void Ensure(UserDb db) => db.Exec(@"CREATE TABLE IF NOT EXISTS plain_items (path TEXT PRIMARY KEY, stamp TEXT NOT NULL, size INTEGER NOT NULL,
            written TEXT NOT NULL, kept TEXT NULL);
        CREATE TABLE IF NOT EXISTS plain_trash (path TEXT PRIMARY KEY, at TEXT NOT NULL);");

    // ---------------- Namen ----------------

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Längster Name eines Pfadteils (Grenze der üblichen Dateisysteme). Cryptomator kürzt selbst ab 220 Zeichen (.c9s).</summary>
    public const int MaxName = 255;

    /// <summary>Ein Teil des Pfads, wie Windows, macOS und die WebDAV-Anbieter ihn vertragen: NFC, ohne verbotene Zeichen, nicht
    /// zu lang, ohne Punkt oder Leerzeichen am Ende, kein reservierter Name. Alles andere bleibt, wie es ist - auch versteckte
    /// Dateien und Namen ohne bekannte Endung (etwa die eines Cryptomator-Tresors: vault.cryptomator, *.c9r, *.c9s).</summary>
    public static string Segment(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormC))
            sb.Append(c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c);
        var s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length == 0) s = "_";
        if (Reserved.Contains(System.IO.Path.GetFileNameWithoutExtension(s))) s = "_" + s;
        if (s.Length > MaxName)
        {
            var ext = System.IO.Path.GetExtension(s);
            if (ext.Length > 16) ext = "";
            s = s[..(MaxName - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return s;
    }

    /// <summary>Vergibt eindeutige Pfade: „a.txt“, dann „a (2).txt“. Groß-/Kleinschreibung zählt - Namen, die sich nur darin
    /// unterscheiden (etwa im Cryptomator-Tresor), bleiben unverändert.</summary>
    public sealed class Names(PlainLang? lang = null)
    {
        /// <summary>Ordner- und Dateinamen in der Sprache des Exports.</summary>
        public PlainLang L { get; } = lang ?? PlainLang.De;
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        public string Take(string path)
        {
            if (_used.Add(path)) return path;
            var dir = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
            var leaf = path[dir.Length..];
            var ext = System.IO.Path.GetExtension(leaf);
            var stem = leaf[..^ext.Length];
            for (var i = 2; ; i++)
            {
                var p = dir + stem + " (" + i + ")" + ext;
                if (_used.Add(p)) return p;
            }
        }
        public bool Used(string path) => _used.Contains(path);
    }

    /// <summary>Pfad aus Teilen, jeder Teil bereinigt.</summary>
    public static string Join(params string[] parts) =>
        string.Join('/', parts.SelectMany(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries)).Select(Segment));

    // ---------------- Lauf ----------------

    public sealed record Result(int Files, long Bytes, int Written, int Moved, int Trashed, int Purged, int Failed, string? Error);

    private sealed record Known(string Stamp, long Size, string? Kept);

    /// <summary>Ein Lauf für eine Person gegen das Ziel. items: alles, was im Ziel liegen soll (ohne LIESMICH).</summary>
    public static async Task<Result> RunAsync(UserDb db, PlainBackupSettings s, IStorageDriver d, IReadOnlyList<PlainItem> items, string tmpDir,
        Func<string> readMe, Action<PlainProgress>? progress, CancellationToken ct, Func<DateTime>? clock = null)
    {
        var readMeName = PlainLang.For(s.Lang).ReadMe;
        var now = (clock ?? (() => DateTime.UtcNow))();
        Ensure(db);
        Directory.CreateDirectory(tmpDir);
        // Neues Ziel: die gemerkten Stempel gehören zum alten
        var target = TargetOf(s);
        if (db.Get(TargetKey) != target)
        {
            db.Exec("DELETE FROM plain_items; DELETE FROM plain_trash;");
            db.Set(TargetKey, target);
        }
        var known = db.Query("SELECT path, stamp, size, kept FROM plain_items", r => (r.GetString(0), new Known(r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3))))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        // Erster Lauf an diesem Ziel: was dort schon liegt, nicht noch einmal senden (Größe gleich, nicht älter als das Original)
        var there = known.Count == 0 ? await ListTreeAsync(d, ct) : null;

        var desired = new HashSet<string>(items.Select(i => i.Path), StringComparer.Ordinal);
        // Verschwundene mit gleichem Inhalt (Original umbenannt oder verschoben): im Ziel verschieben statt neu senden
        var vanished = known.Where(k => !desired.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var byStamp = vanished.GroupBy(v => v.Value.Stamp).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(x => x.Key)), StringComparer.Ordinal);

        int files = 0, written = 0, moved = 0, trashed = 0, purged = 0, failed = 0, inRow = 0, done = 0;
        long bytes = 0;
        string? firstError = null;
        var at = now.ToString("o");

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var path = item.Path;
            progress?.Invoke(new PlainProgress(now, done++, items.Count, bytes, item.Path));
            var attempt = 0;
        again:
            try
            {
                var stamp = item.Stamp;
                known.TryGetValue(path, out var old);
                if (stamp != null && old?.Stamp == stamp) { files++; bytes += old.Size; inRow = 0; continue; }
                if (stamp != null && old == null && there != null && item.Open != null && item.Modified is { } mod
                    && there.TryGetValue(path, out var t) && t.Size == item.Size && t.Modified >= mod.AddSeconds(-2))
                {
                    Remember(db, path, stamp, item.Size, at, null);
                    files++; bytes += item.Size; inRow = 0;
                    continue;
                }
                if (stamp != null && old == null && byStamp.TryGetValue(stamp, out var q) && q.Count > 0)
                {
                    var from = q.Dequeue();
                    try
                    {
                        await d.MoveAsync(from, path, ct);
                        db.Exec("DELETE FROM plain_items WHERE path=$p", ("$p", from));
                        vanished.Remove(from);
                        Remember(db, path, stamp, item.Size, at, null);
                        moved++; files++; bytes += item.Size; inRow = 0;
                        continue;
                    }
                    catch (Exception e) when (e is IOException or KeyNotFoundException or UnauthorizedAccessException) { /* dann neu senden */ }
                }
                // Inhalt bereitstellen: Original direkt, Erzeugtes über eine Zwischendatei
                var tmp = new List<string>();
                try
                {
                    Stream data;
                    long size;
                    if (item.Write != null)
                    {
                        var plain = NewTmp(tmpDir, tmp);
                        await using (var f = File.Create(plain)) await item.Write(f);
                        if (stamp == null)
                        {
                            stamp = "h:" + await HashAsync(plain, ct);
                            if (old?.Stamp == stamp) { files++; bytes += old.Size; inRow = 0; continue; }
                        }
                        data = File.OpenRead(plain);
                    }
                    else data = item.Open!();
                    // Was dort lag: ältere Version nach .trash (höchstens einmal am Tag, Sammeldateien einmal je Woche), sonst ersetzen
                    string? kept = old?.Kept;
                    var replace = old != null || there?.ContainsKey(path) == true;
                    var keep = replace && s.Versions && (old?.Kept == null || now - DateTime.Parse(old.Kept, null, DateTimeStyles.RoundtripKind) >= (item.Collection ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1)));
                    await using (data)
                    {
                        size = data.CanSeek ? data.Length : item.Size;
                        if (size < SmallFile)
                        {
                            // Klein (etwa die vielen Dateien eines Cryptomator-Tresors): eine Anfrage, PUT ersetzt
                            if (keep) { if (await ToTrashAsync(db, d, path, now, ct)) trashed++; kept = at; }
                            await d.WriteAsync(path, data, ct);
                        }
                        else
                        {
                            // Groß: erst fertig als .part, dann an seinen Platz - keine halbe Datei im Ziel
                            await d.WriteAsync(path + Part, data, ct);
                            if (keep) { if (await ToTrashAsync(db, d, path, now, ct)) trashed++; kept = at; }
                            else if (replace) await d.DeleteAsync(path, ct);
                            try { await d.MoveAsync(path + Part, path, ct); }
                            catch (IOException)
                            {
                                // Lag doch schon etwas dort (von Hand angelegt): ersetzen
                                await d.DeleteAsync(path, ct);
                                await d.MoveAsync(path + Part, path, ct);
                            }
                        }
                    }
                    Remember(db, path, stamp!, size, at, kept);
                    written++; files++; bytes += size; inRow = 0;
                }
                finally
                {
                    foreach (var f in tmp) try { File.Delete(f); } catch (IOException) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (Transient(e) && attempt < Retries)
            {
                // Verbindung abgebrochen (etwa „ResponseEnded“, wenn das Ziel eine Verbindung schließt): dieselbe Datei noch einmal
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt++), ct);
                goto again;
            }
            catch (Exception e)
            {
                failed++;
                firstError ??= item.Path + ": " + e.GetBaseException().Message;
                // Das Ziel antwortet auch nach erneuten Versuchen nicht oder lehnt ab: aufhören statt jede Datei einzeln scheitern zu lassen
                if (++inRow >= MaxFailsInRow || e is UnauthorizedAccessException or HttpRequestException)
                    return new Result(files, bytes, written, moved, trashed, purged, failed, item.Path + ": " + e.GetBaseException().Message);
            }
        }

        // Nicht mehr in dmOffice: nach .trash
        foreach (var path in vanished.Keys)
        {
            ct.ThrowIfCancellationRequested();
            try { if (await ToTrashAsync(db, d, path, now, ct)) trashed++; }
            catch (KeyNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                failed++;
                firstError ??= path + ": " + e.GetBaseException().Message;
                continue;
            }
            db.Exec("DELETE FROM plain_items WHERE path=$p", ("$p", path));
        }

        // Frist abgelaufen: endgültig entfernen
        if (s.TrashDays > 0)
        {
            var before = now.AddDays(-s.TrashDays).ToString("o");
            foreach (var p in db.Query("SELECT path FROM plain_trash WHERE at < $b", r => r.GetString(0), ("$b", before)))
            {
                try { await d.DeleteAsync(p, ct); purged++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException) { failed++; continue; }
                db.Exec("DELETE FROM plain_trash WHERE path=$p", ("$p", p));
            }
        }

        // LIESMICH zuletzt, mit dem Stand dieses Laufs (nie nach .trash)
        try
        {
            var text = readMe();
            using var ms = new MemoryStream(new UTF8Encoding(true).GetBytes(text.Replace("\n", "\r\n")));
            await d.WriteAsync(readMeName + Part, ms, ct);
            await d.DeleteAsync(readMeName, ct);
            await d.MoveAsync(readMeName + Part, readMeName, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            failed++;
            firstError ??= readMeName + ": " + e.GetBaseException().Message;
        }
        progress?.Invoke(new PlainProgress(now, items.Count, items.Count, bytes, null));
        return new Result(files, bytes, written, moved, trashed, purged, failed, failed > 0 ? firstError : null);
    }

    private static void Remember(UserDb db, string path, string stamp, long size, string at, string? kept) =>
        db.Exec("INSERT INTO plain_items(path, stamp, size, written, kept) VALUES($p,$s,$z,$w,$k) ON CONFLICT(path) DO UPDATE SET stamp=$s, size=$z, written=$w, kept=$k",
            ("$p", path), ("$s", stamp), ("$z", size), ("$w", at), ("$k", kept));

    /// <summary>Nach .trash mit gleicher Ordnerstruktur; ist der Name dort belegt, mit Datum (und notfalls Uhrzeit).</summary>
    private static async Task<bool> ToTrashAsync(UserDb db, IStorageDriver d, string path, DateTime now, CancellationToken ct)
    {
        var target = Trash + "/" + path;
        var dir = target[..(target.LastIndexOf('/') + 1)];
        var leaf = target[dir.Length..];
        var ext = Path.GetExtension(leaf) is { Length: > 0 and <= 16 } e ? e : "";
        var stem = leaf[..^ext.Length];
        bool Taken(string p) => db.Scalar("SELECT 1 FROM plain_trash WHERE path=$p", ("$p", p)) != null;
        if (Taken(target)) target = dir + stem + " (" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")" + ext;
        if (Taken(target)) target = dir + stem + " (" + now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ")" + ext;
        for (var i = 2; Taken(target); i++) target = dir + stem + " (" + now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + " " + i + ")" + ext;
        try { await d.MoveAsync(path, target, ct); }
        catch (KeyNotFoundException) { return false; }
        db.Exec("INSERT OR REPLACE INTO plain_trash(path, at) VALUES($p, $a)", ("$p", target), ("$a", now.ToString("o")));
        return true;
    }

    private static string NewTmp(string dir, List<string> list)
    {
        var p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        list.Add(p);
        return p;
    }

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var f = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct))[..32].ToLowerInvariant();
    }


    /// <summary>Alles im Ziel außer .trash (nur beim ersten Lauf an einem Ziel).</summary>
    /// <summary>Weitere Versuche je Datei bei abgebrochener Verbindung.</summary>
    private const int Retries = 2;

    /// <summary>Vorübergehend: Verbindung abgebrochen oder Zeitüberschreitung, Fehler des Ziels (5xx) - nicht abgelehnt oder fehlend.</summary>
    private static bool Transient(Exception e) => e switch
    {
        HttpRequestException h => h.StatusCode is null || (int)h.StatusCode >= 500,
        OperationCanceledException => true,
        IOException => e.InnerException is System.Net.Sockets.SocketException or HttpRequestException,
        _ => false,
    };

    private static async Task<Dictionary<string, DriverEntry>> ListTreeAsync(IStorageDriver d, CancellationToken ct)
    {
        var result = new Dictionary<string, DriverEntry>(StringComparer.Ordinal);
        var todo = new Stack<string>([""]);
        while (todo.Count > 0)
        {
            var dir = todo.Pop();
            List<DriverEntry> list;
            try { list = await d.ListAsync(dir, ct); }
            catch (KeyNotFoundException) { continue; }
            foreach (var e in list)
            {
                var p = StoragePath.Join(dir, e.Name);
                if (dir.Length == 0 && e.Name == Trash) continue;
                if (e.IsDir) todo.Push(p); else result[p] = e;
            }
        }
        return result;
    }

    /// <summary>Verbindung prüfen: Probe schreiben, lesen, löschen.</summary>
    public static async Task TestAsync(IStorageDriver d, CancellationToken ct)
    {
        var name = ".dmoffice-test-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var text = "dmOffice " + DateTime.UtcNow.ToString("o");
        await d.WriteAsync(name, new MemoryStream(Encoding.UTF8.GetBytes(text)), ct);
        try
        {
            using var r = new StreamReader(await d.OpenReadAsync(name, ct));
            if (await r.ReadToEndAsync(ct) != text) throw new IOException("Die Probe kam verändert zurück.");
        }
        finally { await d.DeleteAsync(name, ct); }
    }
}
