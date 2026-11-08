using System.Globalization;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Search;

namespace DmOffice.Dms;

/// <summary>Einstellungen vom DMS je Person.</summary>
public sealed class DmsSettings
{
    /// <summary>Beim Import mit KI einordnen (nur, wenn eingerichtet und für die Person freigegeben).</summary>
    public bool Ai { get; set; }
    /// <summary>Warteschlange angehalten: Quellen nehmen weiter an, verarbeitet wird erst nach dem Fortsetzen.</summary>
    public bool Paused { get; set; }
}

/// <summary>
/// Import und Verarbeitung (docs/dms-konzept.md, „Verarbeitung“): Das Original kommt sofort verschlüsselt in den Blob-Bereich von
/// DMS und wird als „queued“ eingetragen; der Hintergrunddienst liest den Text (bei Scans Texterkennung mit Archivfassung als
/// PDF/A), ordnet ein und legt die Datei nach dem Ablageprofil in der Dateiablage ab. Das Original bleibt nur, wenn es eine
/// Archivfassung gibt - sonst ist die Datei in der Ablage das Original. Im externen Ordner liegt das Original neben der Archivfassung im
/// Unterordner „Original/“; der Blob-Bereich wird dann wieder frei.
/// </summary>
public static class DmsImport
{
    public const string SettingsKey = "dms.settings";
    /// <summary>Größere Dateien nimmt das DMS nicht an.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;
    private const int MaxTries = 3;

    public static DmsSettings Settings(UserDb db) => db.GetJson<DmsSettings>(SettingsKey);
    public static void SaveSettings(UserDb db, DmsSettings s) => db.SetJson(SettingsKey, s);

    /// <summary>Herkunft eines neuen Dokuments.</summary>
    /// <param name="Source">upload, folder, files, mail …</param>
    /// <param name="SourceRef">etwa die Kennung der Quelle oder Konto + Message-ID</param>
    /// <param name="FileDate">Datum der Datei bzw. Mail - Dokumentdatum, wenn im Text keines steht</param>
    public sealed record Info(string Source, string? SourceRef = null, string? Sender = null, string? Subject = null, string? Profile = null,
        DmsAssign.Presets? Presets = null, DateTime? FileDate = null, Given? Given = null, Stream? Archive = null, long Freed = 0);

    /// <summary>
    /// Schon bekannt (Übernahme aus paperless): Text und Archivfassung, Titel, Datum, Vorschlag vom DMS (als „automatisch“, noch
    /// nicht bestätigt) und die Angaben aus paperless zum Vergleich. Dann keine Texterkennung und keine eigene Einordnung.
    /// </summary>
    public sealed record Given(string Title, string? DocDate, DateTime? Imported, string Text,
        string? Correspondent, string? Doctype, List<string> Tags, string? PlCorrespondent, string? PlDoctype, List<string> PlTags, bool Compare = true);

    /// <summary>Dropped: Ziel ist der geschlossene Tresor - verschlüsselt abgelegt, übernommen beim nächsten Öffnen.</summary>
    public sealed record Result(long Id, bool Duplicate, bool Dropped = false);

    /// <summary>Kopf einer bei geschlossenem Tresor abgelegten Datei (Tresor.Drop, Art „dms“).</summary>
    private sealed record DropHeader(string UserId, string Name, string? Mime, string Source, string? SourceRef, string? Sender, string? Subject,
        string? Profile, DmsAssign.Presets? Presets, DateTime? FileDate);

    /// <summary>Spaces der Personen - für die Übernahme beim Öffnen des Tresors (gesetzt in <see cref="DmsApi.Map"/>).</summary>
    public static Func<string, UserSpace?>? SpaceOf { get; set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> RoomGates = new();

    /// <summary>Passt das noch ins Kontingent? freed: was dafür gleich aus der Dateiablage verschwindet (Aufnehmen mit Verschieben).</summary>
    private static void CheckRoom(UserDb db, long adding, long freed)
    {
        var quota = Dav.FileStore.Quota(db);
        if (quota > 0 && UserStorage.Counted(db) - freed + adding > quota) throw new Dav.QuotaExceededException(quota);
    }

    /// <summary>Ziel Tresor bei geschlossenem Tresor: mit dem öffentlichen Schlüssel des Tresors ablegen.</summary>
    private static async Task DropAsync(UserSpace space, Stream data, string name, string? mime, Info info, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct);
        if (ms.Length > MaxBytes) throw new ArgumentException($"Die Datei ist zu groß (höchstens {MaxBytes / 1024 / 1024} MB).");
        CheckRoom(space.Db, ms.Length, info.Freed);
        var head = JsonSerializer.SerializeToUtf8Bytes(new DropHeader(space.User.Id, name, mime, info.Source, info.SourceRef, info.Sender, info.Subject,
            info.Profile, info.Presets, info.FileDate));
        Core.Tresor.Drop(space.User.Id, "dms", [.. head, 0, .. ms.ToArray()]);
    }

    /// <summary>Übernahme beim Öffnen des Tresors (Tresor.Absorbers["dms"]): wie ein neues Dokument im Tresor.</summary>
    public static void Absorb(TresorDb tresor, byte[] payload)
    {
        var zero = Array.IndexOf(payload, (byte)0);
        var head = JsonSerializer.Deserialize<DropHeader>(payload.AsSpan(0, zero)) ?? throw new InvalidDataException("Kopf fehlt.");
        var space = SpaceOf?.Invoke(head.UserId) ?? throw new InvalidOperationException("Person nicht gefunden.");
        var store = DmsStore.ForTresor(space.Db, tresor);
        ImportAsync(space, new MemoryStream(payload, zero + 1, payload.Length - zero - 1), head.Name, head.Mime,
            new Info(head.Source, head.SourceRef, head.Sender, head.Subject, head.Profile, head.Presets, head.FileDate), CancellationToken.None, store)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Ein neues Dokument annehmen. Gleicher Inhalt schon vorhanden und abrufbar: nichts Neues, die Kennung des vorhandenen. Ein Dokument
    /// im Papierkorb zählt nicht: Es verfällt dort mit der Frist, die neue Datei wird ein eigenes Dokument.
    /// </summary>
    public static async Task<Result> ImportAsync(UserSpace space, Stream data, string name, string? mime, Info info, CancellationToken ct, DmsStore? store = null)
    {
        name = Dms.CleanText(Path.GetFileName((name ?? "").Replace('\\', '/')));
        if (name.Length == 0) name = "Dokument";
        if (info.Subject != null) info = info with { Subject = Dms.CleanText(info.Subject) };
        if (info.Given != null) info = info with { Given = info.Given with { Title = Dms.CleanText(info.Given.Title) } };
        // Beim Übernehmen abgelegter Inhalte aus dem Tresor (store gesetzt) ist der Platz schon belegt
        var checkRoom = store == null;
        // Ziel aus dem Ablageprofil: Tresor offen → dorthin, zu → verschlüsselt ablegen und beim Öffnen übernehmen
        if (store == null)
        {
            var target = DmsFiling.Find(space, info.Profile) ?? DmsFiling.Default(space);
            if (target.Target == "tresor")
            {
                if (Core.Tresor.DbOf(space.User.Id) is { } tresor) store = DmsStore.ForTresor(space.Db, tresor);
                else
                {
                    if (info.Given != null) throw new ArgumentException("Für die Übernahme in den Tresor muss der Tresor offen sein.");
                    // Virenscanner an: vor dem verschlüsselten Ablegen prüfen (danach kann ihn niemand mehr lesen, bis der Tresor offen ist)
                    if (Core.VirusScan.Enabled)
                    {
                        var copy = new MemoryStream();
                        await data.CopyToAsync(copy, ct);
                        Core.VirusScan.CheckBytes(copy.ToArray(), space.User.Id, "DMS/" + name);
                        copy.Position = 0;
                        data = copy;
                    }
                    await DropAsync(space, data, name, mime, info, ct);
                    return new Result(0, false, true);
                }
            }
        }
        var db = store ?? space.Db;
        Dms.Ensure(db);
        var blob = await db.Blobs.WriteAsync(data, MaxBytes, () => new ArgumentException($"Die Datei ist zu groß (höchstens {MaxBytes / 1024 / 1024} MB)."), ct);
        // Virenscanner an (Verwaltung → Sicherheit): vor dem Eintragen prüfen; Fund → nicht aufgenommen, die Quelle meldet den Grund
        try { Core.VirusScan.CheckBlob(db.Blobs, space.User.Id, blob, "DMS/" + name); }
        catch { if (blob.Id != null) db.Blobs.Delete(blob.Id); throw; }
        var sha = Dav.FileStore.Hex(blob.Sha256);
        if (await ExistingAsync(space, db, sha, ct) is { } existing)
        {
            if (blob.Id != null) db.Blobs.Delete(blob.Id);
            return new Result(existing, true);
        }
        var profile = DmsFiling.Find(space, info.Profile)?.Id ?? DmsFiling.Default(space).Id;
        string? archiveBlob = null;
        long archiveSize = 0;
        if (info.Given != null && info.Archive is { } archive)
        {
            var written = await db.Blobs.WriteAsync(archive, MaxBytes, () => new ArgumentException("Die Archivfassung ist zu groß."), ct);
            (archiveBlob, archiveSize) = (written.Id, written.Size);
        }
        var given = info.Given;
        long id;
        // Prüfen und Eintragen in einem Zug: Sonst kämen viele gleichzeitige Uploads gemeinsam über die Grenze
        lock (RoomGates.GetOrAdd(space.User.Id, _ => new object()))
        {
            if (checkRoom)
                try { CheckRoom(space.Db, blob.Size + archiveSize, info.Freed); }
                catch (Dav.QuotaExceededException)
                {
                    if (blob.Id != null) db.Blobs.Delete(blob.Id);
                    if (archiveBlob != null) db.Blobs.Delete(archiveBlob);
                    throw;
                }
            db.Exec(@"INSERT INTO dms_docs(state, title, imported, original_name, mime, size, original_blob, sha256, source, source_ref, sender, profile, presets, doc_date)
                      VALUES('queued', $t, $i, $n, $m, $z, $b, $s, $src, $ref, $snd, $p, $pr, $fd)",
                ("$t", info.Subject ?? ""), ("$i", Dms.Unix()), ("$n", name), ("$m", mime), ("$z", blob.Size), ("$b", blob.Id), ("$s", sha), ("$src", info.Source),
                ("$ref", info.SourceRef), ("$snd", info.Sender), ("$p", profile), ("$pr", JsonSerializer.Serialize(new Pending(info.Presets, info.Subject, info.FileDate,
                    given == null ? null : given with { Text = "" }, archiveBlob))),
                ("$fd", null));
            id = (long)db.Scalar("SELECT last_insert_rowid()")!;
            UserStorage.Refresh(space.Db);
        }
        if (given != null) Dms.WriteText(db, id, given.Title, given.Text, given.Compare ? "paperless" : "moved");
        if (given is { Compare: true })
        {
            db.Exec("UPDATE dms_docs SET pl = 1, pl_correspondent = $c, pl_doctype = $t, pl_tags = $g, imported = COALESCE($i, imported) WHERE id = $d",
                ("$c", given.PlCorrespondent), ("$t", given.PlDoctype), ("$g", string.Join(",", given.PlTags.Distinct().OrderBy(x => x, StringComparer.Ordinal))),
                ("$i", given.Imported is { } im ? new DateTimeOffset(DateTime.SpecifyKind(im, DateTimeKind.Utc)).ToUnixTimeSeconds() : null), ("$d", id));
        }
        DmsJob.Kick();
        return new Result(id, false);
    }

    /// <summary>
    /// Das Dokument mit diesem Inhalt, dessen Inhalt noch abrufbar ist: Datei in der Ablage, im Tresor oder im externen Ordner, oder noch in
    /// Arbeit. Ein externer Ordner, der gerade nicht antwortet, gilt als vorhanden; nur eine dort sicher fehlende Datei zählt nicht.
    /// </summary>
    private static async Task<long?> ExistingAsync(UserSpace space, DmsStore db, string sha, CancellationToken ct)
    {
        var rows = db.Query($"SELECT id, ext_mount, ext_path FROM dms_docs WHERE sha256 = $s AND {db.Visible("dms_docs")} ORDER BY id",
            r => (Id: r.GetInt64(0), Mount: r.IsDBNull(1) ? null : r.GetString(1), Path: r.IsDBNull(2) ? null : r.GetString(2)), ("$s", sha));
        foreach (var (id, mount, path) in rows)
        {
            if (mount == null || path == null || DmsHost.Current is not { } host) return id;
            try { if (await host.StatAsync(space, mount, path, ct) != null) return id; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return id; }
        }
        return null;
    }

    /// <summary>Was bis zur Verarbeitung am Dokument hängt.</summary>
    private sealed record Pending(DmsAssign.Presets? Presets, string? Subject, DateTime? FileDate, Given? Given = null, string? ArchiveBlob = null);

    /// <summary>Wartende Dokumente einer Person verarbeiten (höchstens <paramref name="max"/>).</summary>
    public static async Task<int> ProcessQueueAsync(UserSpace space, DmsHost host, int max, CancellationToken ct, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        Dms.Ensure(db);
        // Nach mehreren abgebrochenen Versuchen (etwa Absturz eines Werkzeugs): mit Fehler in den Posteingang
        db.Exec("UPDATE dms_docs SET state = 'error', error = COALESCE(error, 'Abgebrochen nach mehreren Versuchen.') WHERE state = 'queued' AND tries >= $m", ("$m", MaxTries));
        var ids = db.Query("SELECT id FROM dms_docs WHERE state = 'queued' ORDER BY id LIMIT $n", r => r.GetInt64(0), ("$n", max));
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessAsync(space, host, id, ct, db);
        }
        return ids.Count;
    }

    /// <summary>Ein Dokument: Text, Archivfassung, Einordnung, Ablage.</summary>
    public static async Task ProcessAsync(UserSpace space, DmsHost host, long id, CancellationToken ct, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        var row = db.Query("SELECT original_blob, original_name, mime, sender, profile, presets, tries, source FROM dms_docs WHERE id = $d AND state = 'queued'",
            r => (Blob: r.IsDBNull(0) ? null : r.GetString(0), Name: r.GetString(1), Mime: r.IsDBNull(2) ? null : r.GetString(2), Sender: r.IsDBNull(3) ? null : r.GetString(3),
                Profile: r.IsDBNull(4) ? null : r.GetString(4), Pending: r.IsDBNull(5) ? null : r.GetString(5), Tries: r.GetInt32(6), Source: r.GetString(7)), ("$d", id))
            .FirstOrDefault();
        if (row.Name == null) return;
        // Den Versuch vor der Arbeit zählen: bringt ein Dokument den Prozess zum Absturz, kommt es nicht endlos wieder
        db.Exec("UPDATE dms_docs SET tries = tries + 1 WHERE id = $d", ("$d", id));
        var pending = row.Pending != null ? JsonSerializer.Deserialize<Pending>(row.Pending) : null;
        byte[] data = row.Blob != null ? db.Blobs.ReadAll(row.Blob) : [];
        var ext = TextExtract.Ext(row.Name, row.Mime);

        // Text, bei Scans und Bildern mit Archivfassung
        string text = "", method = "none";
        string? note = null;
        byte[]? archive = null;
        var pdfa = false;
        if (pending?.Given is { } known)
        {
            await PlaceKnownAsync(space, host, db, id, row.Blob, row.Name, row.Mime, row.Profile, row.Source, data, known, pending.ArchiveBlob, ct);
            return;
        }
        // Hochgeladene Mail: Betreff, Datum und Absender aus der Mail, wie beim Mailordner als Quelle
        var subject = pending?.Subject;
        var fileDate = pending?.FileDate;
        var sender = row.Sender;
        if (ext == ".eml" && row.Source != "mail" && MailFacts(data) is { } mailFacts)
        {
            subject ??= mailFacts.Subject;
            fileDate = mailFacts.Date ?? fileDate;
            sender ??= mailFacts.Sender;
        }
        try
        {
            var ex = host.Extract(data, row.Name, row.Mime, ct);
            text = ex.Text;
            method = ex.Method;
            if (ex.Status == "error") note = Noted(id, TextNotRead, ex.Note);
            if (ext == ".eml")
            {
                // Ganze Mail: PDF mit Kopf, Text und Anhangliste, PDF-Anhänge hinten angefügt; das .eml bleibt Original
                archive = host.MailPdf(data, ct);
                if (archive == null) note = "Die Mail ließ sich nicht als PDF setzen; abgelegt ist das Original.";
            }
            // Ohne Texterkennung auf dem Server gibt es nichts zu melden: das sagt die Zeile oben in der Ansicht für alle Dokumente gleich
            else if (ex.WantsOcr && DmsArchive.CanMakeSearchable(ext) && host.OcrAvailable)
            {
                var s = host.MakeSearchable(data, row.Name, row.Mime, ct);
                if (s.Pdf != null)
                {
                    archive = s.Pdf;
                    text = (text + "\n" + s.Text).Trim();
                    method = "ocr";
                }
                else
                {
                    note = Noted(id, OcrFailed, s.Note);
                    // Zu viele Seiten: keine Archivfassung, aber der erkannte Text bleibt suchbar
                    if (s.Text.Length > 0) { text = (text + "\n" + s.Text).Trim(); method = "ocr"; }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { note = Noted(id, TextNotRead, e.Message); }

        // Einordnen: Vorgaben, Regeln, KI, Gelerntes
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone));
        var decided = DmsAssign.Decide(db, text + "\n" + Path.GetFileNameWithoutExtension(row.Name), row.Name, sender, pending?.Presets);
        DmsAssign.Result? ai = null;
        // Nie für den Tresor
        if (!db.Tresor && Settings(space.Db).Ai && host.Allowed(space) && text.Length > 0)
            try { ai = await DmsAssign.AiAsync(space.Db, host, text, row.Name, sender, subject, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ai = null; }
        var correspondent = decided.FixedCorrespondent ? decided.Correspondent : ai?.Correspondent ?? decided.Correspondent;
        // Neuer Korrespondent laut KI, den es schon gibt (anders geschrieben): gleich zuordnen statt vorschlagen
        var suggestion = ai?.NewCorrespondent;
        if (suggestion != null && Dms.FindByName(Dms.Entries(db.User, Dms.Correspondent), Dms.Correspondent, suggestion) is { } existing)
        {
            suggestion = null;
            if (correspondent == null && !decided.FixedCorrespondent) correspondent = existing.Id;
        }
        var doctype = decided.FixedDoctype ? decided.Doctype : ai?.Doctype ?? decided.Doctype;
        var tags = decided.Tags.Union(ai?.Tags ?? []).ToList();
        var fixedTags = (decided.FixedTags ?? []).ToHashSet();
        var byAi = ai != null && ((!decided.FixedCorrespondent && ai.Correspondent != null) || (!decided.FixedDoctype && ai.Doctype != null) || ai.Tags.Count > 0);
        var date = ai?.DocDate ?? DmsAssign.FindDate(text, today)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ?? (fileDate is { } fd ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fd, DateTimeKind.Utc), space.Zone)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
        var title = Dms.CleanText(ai?.Title ?? DmsAssign.Title(row.Name, text, subject));

        // Ablegen
        var profile = DmsFiling.Find(space, row.Profile) ?? DmsFiling.Default(space);
        var fileExt = archive != null ? ".pdf" : Path.GetExtension(row.Name) is { Length: > 0 } e0 ? e0.ToLowerInvariant() : ext;
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            UserDb.Exec(conn, @"UPDATE dms_docs SET title = $t, doc_date = $dd, correspondent = $c, doctype = $y, auto_correspondent = $ac, auto_doctype = $ay,
                by_ai = $ai, ai_suggestion = $as, sender = $snd WHERE id = $d",
                ("$t", title), ("$dd", date), ("$c", correspondent), ("$y", doctype), ("$ac", correspondent != null && !decided.FixedCorrespondent ? 1 : 0),
                ("$ay", doctype != null && !decided.FixedDoctype ? 1 : 0), ("$ai", byAi ? 1 : 0), ("$as", suggestion), ("$snd", sender), ("$d", id));
            UserDb.Exec(conn, "DELETE FROM dms_doc_tags WHERE doc = $d", ("$d", id));
            foreach (var t in tags)
                UserDb.Exec(conn, "INSERT OR IGNORE INTO dms_doc_tags(doc, tag, auto) VALUES($d, $t, $a)", ("$d", id), ("$t", t), ("$a", fixedTags.Contains(t) ? 0 : 1));
            tx.Commit();
        }
        var doc = Dms.Get(db, id);
        // Archivfassung als PDF/A, mit Titel, Korrespondent, Dokumenttyp, Datum und Tags in den Metadaten
        if (archive != null && host.PdfAAvailable)
        {
            try
            {
                if (host.ToPdfA(archive, PdfInfoOf(space, doc), ct) is { } a) { archive = a; pdfa = true; }
                else note = "Umwandlung in PDF/A fehlgeschlagen; abgelegt ist die durchsuchbare PDF.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { note = Noted(id, "Umwandlung in PDF/A fehlgeschlagen; abgelegt ist die durchsuchbare PDF.", e.Message); }
        }
        var facts = DmsFiling.FactsOf(space, doc, SourceName(space, row.Source, doc, db));
        try
        {
            var content = archive ?? data;
            Placed placed;
            using (UserStorage.Placing(db.Tresor ? 0 : data.LongLength))
                placed = await PlaceAsync(space, host, db, profile, DmsFiling.PathFor(profile, facts, fileExt, doc.Inbox), content,
                    archive != null ? "application/pdf" : row.Mime, archive != null ? data : null, row.Name, ct);
            if (await RemovedMeanwhileAsync(space, host, db, id, placed, row.Blob, ct)) return;
            if (row.Blob != null && !(archive != null && placed.Mount == null)) db.Blobs.Delete(row.Blob);
            db.Exec(@"UPDATE dms_docs SET state = 'done', fileid = $f, placed_path = $p, etag = $e, archive = $a, pdfa = $pa, original_blob = $b,
                      mime = $m, size = $z, error = $err, presets = NULL, profile = $pr, ext_mount = $xm, ext_path = $xp, ext_original = $xo, ext_sha = $xs
                      WHERE id = $d",
                ("$f", placed.FileId == 0 ? null : placed.FileId), ("$p", placed.Path), ("$e", placed.Etag), ("$a", archive != null ? 1 : 0), ("$pa", pdfa ? 1 : 0),
                ("$b", archive != null && placed.Mount == null ? row.Blob : null), ("$m", archive != null ? "application/pdf" : row.Mime), ("$z", content.LongLength),
                ("$err", note), ("$pr", profile.Id), ("$xm", placed.Mount), ("$xp", placed.Mount != null ? placed.Path : null), ("$xo", placed.Original),
                ("$xs", placed.Sha), ("$d", id));
            UserStorage.Refresh(space.Db);
            Dms.WriteText(db, id, title, text, method);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or Dav.QuotaExceededException or Dav.DavConflictException
            or Pim.PreconditionFailedException or KeyNotFoundException or HttpRequestException or UnauthorizedAccessException)
        {
            // Nicht abgelegt (Speicher voll, Ordner belegt …): später erneut, nach drei Versuchen mit Fehler im Posteingang
            db.Exec("UPDATE dms_docs SET error = $e, state = CASE WHEN tries >= $m THEN 'error' ELSE 'queued' END WHERE id = $d",
                ("$e", e.Message), ("$m", MaxTries), ("$d", id));
        }
    }

    /// <summary>Bekannte Angaben (Übernahme): eintragen und ablegen, ohne Texterkennung und eigene Einordnung.</summary>
    private static async Task PlaceKnownAsync(UserSpace space, DmsHost host, DmsStore db, long id, string? blob, string name, string? mime, string? profileId, string source,
        byte[] data, Given g, string? archiveBlob, CancellationToken ct)
    {
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            UserDb.Exec(conn, @"UPDATE dms_docs SET title = $t, doc_date = $dd, correspondent = $c, doctype = $y, auto_correspondent = $ac, auto_doctype = $ay WHERE id = $d",
                ("$t", g.Title), ("$dd", g.DocDate), ("$c", g.Correspondent), ("$y", g.Doctype), ("$ac", g.Correspondent != null ? 1 : 0),
                ("$ay", g.Doctype != null ? 1 : 0), ("$d", id));
            UserDb.Exec(conn, "DELETE FROM dms_doc_tags WHERE doc = $d", ("$d", id));
            foreach (var t in g.Tags.Distinct())
                UserDb.Exec(conn, "INSERT OR IGNORE INTO dms_doc_tags(doc, tag, auto) VALUES($d, $t, 1)", ("$d", id), ("$t", t));
            tx.Commit();
        }
        var doc = Dms.Get(db, id);
        var profile = DmsFiling.Find(space, profileId) ?? DmsFiling.Default(space);
        var archive = archiveBlob != null ? db.Blobs.ReadAll(archiveBlob) : null;
        var ext = archive != null ? ".pdf" : Path.GetExtension(name) is { Length: > 0 } e0 ? e0.ToLowerInvariant() : TextExtract.Ext(name, mime);
        try
        {
            var content = archive ?? data;
            Placed placed;
            using (UserStorage.Placing(db.Tresor ? 0 : data.LongLength))
                placed = await PlaceAsync(space, host, db, profile, DmsFiling.PathFor(profile, DmsFiling.FactsOf(space, doc, SourceName(space, source, doc, db)), ext, doc.Inbox),
                    content, archive != null ? "application/pdf" : mime, archive != null ? data : null, name, ct);
            if (await RemovedMeanwhileAsync(space, host, db, id, placed, blob, ct)) { if (archiveBlob != null) db.Blobs.Delete(archiveBlob); return; }
            if (blob != null && !(archive != null && placed.Mount == null)) db.Blobs.Delete(blob);
            if (archiveBlob != null) db.Blobs.Delete(archiveBlob);
            db.Exec(@"UPDATE dms_docs SET state = 'done', fileid = $f, placed_path = $p, etag = $e, archive = $a, original_blob = $b, mime = $m, size = $z,
                      presets = NULL, profile = $pr, ext_mount = $xm, ext_path = $xp, ext_original = $xo, ext_sha = $xs WHERE id = $d",
                ("$f", placed.FileId == 0 ? null : placed.FileId), ("$p", placed.Path), ("$e", placed.Etag), ("$a", archive != null ? 1 : 0),
                ("$b", archive != null && placed.Mount == null ? blob : null), ("$m", archive != null ? "application/pdf" : mime), ("$z", content.LongLength),
                ("$pr", profile.Id), ("$xm", placed.Mount), ("$xp", placed.Mount != null ? placed.Path : null), ("$xo", placed.Original), ("$xs", placed.Sha),
                ("$d", id));
            UserStorage.Refresh(space.Db);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or Dav.QuotaExceededException or Dav.DavConflictException
            or Pim.PreconditionFailedException or KeyNotFoundException or HttpRequestException or UnauthorizedAccessException)
        {
            db.Exec("UPDATE dms_docs SET error = $e, state = CASE WHEN tries >= $m THEN 'error' ELSE 'queued' END WHERE id = $d",
                ("$e", e.Message), ("$m", MaxTries), ("$d", id));
        }
    }

    private const string TextNotRead = "Der Text dieser Datei ließ sich nicht lesen. Die Datei ist trotzdem abgelegt.";
    private const string OcrFailed = "Die Texterkennung ist bei dieser Datei fehlgeschlagen. Die Datei ist trotzdem abgelegt.";

    /// <summary>Hinweis am Dokument in Worten der Person; der technische Grund steht im Systemprotokoll.</summary>
    private static string Noted(long id, string note, string? detail)
    {
        SystemLog.Add(Microsoft.Extensions.Logging.LogLevel.Warning, "DMS", $"Dokument {id}: {detail}");
        return note;
    }

    /// <summary>Betreff, Datum und Absender einer Mail; null, wenn sie sich nicht lesen lässt.</summary>
    private static (string? Subject, DateTime? Date, string? Sender)? MailFacts(byte[] eml)
    {
        try
        {
            var m = MimeKit.MimeMessage.Load(new MemoryStream(eml));
            var subject = string.IsNullOrWhiteSpace(m.Subject) ? null : Dms.CleanText(m.Subject);
            return (subject, m.Date == default ? null : m.Date.UtcDateTime, m.From.Mailboxes.FirstOrDefault()?.Address);
        }
        catch (Exception e) when (e is FormatException or IOException) { return null; }
    }

    /// <summary>
    /// Während des Ablegens gelöscht (aus der Warteschlange entfernt): die eben abgelegte Datei nicht verwaist liegen lassen; aus der
    /// Dateiablage kommt sie in den Papierkorb.
    /// </summary>
    private static async Task<bool> RemovedMeanwhileAsync(UserSpace space, DmsHost host, DmsStore db, long id, Placed placed, string? blob, CancellationToken ct)
    {
        if (db.Scalar("SELECT 1 FROM dms_docs WHERE id = $d", ("$d", id)) != null) return false;
        if (placed.Mount != null)
        {
            await host.RemoveAsync(space, placed.Mount, placed.Path, ct);
            if (placed.Original != null) await host.RemoveAsync(space, placed.Mount, placed.Original, ct);
        }
        else if (placed.FileId != 0) host.Delete(space, db, placed.FileId);
        if (blob != null) db.Blobs.Delete(blob);
        return true;
    }

    /// <summary>Ein noch nicht abgelegtes Dokument (wartend oder mit Fehler) samt Original und bereitliegender Archivfassung entfernen.</summary>
    internal static void ForgetUnplaced(DmsStore db, long id)
    {
        var (blob, presets) = db.Query("SELECT original_blob, presets FROM dms_docs WHERE id = $d",
            r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), ("$d", id)).FirstOrDefault();
        var archive = presets != null ? JsonSerializer.Deserialize<Pending>(presets)?.ArchiveBlob : null;
        Forget(db, id, blob);
        if (archive != null) db.Blobs.Delete(archive);
    }

    /// <summary>Dokumente vergessen, deren Datei endgültig gelöscht ist (nicht in der Ablage, nicht im Papierkorb). Läuft beim Leeren des Papierkorbs mit.</summary>
    public static int ForgetGone(UserDb db)
    {
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'dms_docs'") == null) return 0;
        var gone = db.Query(@"SELECT id, original_blob FROM dms_docs WHERE state <> 'queued' AND fileid IS NOT NULL
                AND fileid NOT IN (SELECT fileid FROM files WHERE fileid IS NOT NULL) AND fileid NOT IN (SELECT fileid FROM file_trash_items WHERE fileid IS NOT NULL)",
            r => (Id: r.GetInt64(0), Blob: r.IsDBNull(1) ? null : r.GetString(1)));
        foreach (var (id, blob) in gone) Forget(db, id, blob);
        return gone.Count;
    }

    /// <summary>Wohin abgelegt: Datei der Ablage bzw. des Tresors (FileId) oder externer Ordner (Mount, Pfad, Original, Prüfsumme).</summary>
    private sealed record Placed(long FileId, string Path, string Etag, string? Mount = null, string? Original = null, string? Sha = null);

    /// <summary>
    /// Ablegen nach dem Profil. Dateiablage und Tresor: nur die Datei (das Original einer Archivfassung bleibt im Blob-Bereich). Externer
    /// Ordner: die Datei und - bei einer Archivfassung - das Original unter gleichem Namen mit seiner Endung im Unterordner „Original/“.
    /// </summary>
    private static async Task<Placed> PlaceAsync(UserSpace space, DmsHost host, DmsStore db, DmsProfile profile, string path, byte[] content, string? mime,
        byte[]? original, string originalName, CancellationToken ct)
    {
        if (profile.Target != "external" || db.Tresor)
        {
            // Auch ein älteres Ablageprofil legt nichts in einem Systemordner ab
            if (!db.Tresor && host.Protected(space, path)) throw new ArgumentException("In diesem Ordner kann das DMS nichts ablegen.");
            var (p, fileId, etag) = await host.CreateAsync(space, db, path, new MemoryStream(content), mime, ct);
            return new Placed(fileId, p, etag);
        }
        var mount = profile.Mount ?? throw new InvalidOperationException("Kein externer Ordner im Ablageprofil.");
        var (at, tag) = await host.CreateAsync(space, mount, path, content, ct);
        string? orig = null;
        if (original != null)
            (orig, _) = await host.CreateAsync(space, mount, OriginalPath(at, originalName), original, ct);
        return new Placed(0, at, tag, mount, orig, Dav.FileStore.Hex(System.Security.Cryptography.SHA256.HashData(content)));
    }

    /// <summary>Original zu einer Datei im externen Ordner: „Original/“ daneben, gleicher Name, Endung des Originals.</summary>
    internal static string OriginalPath(string file, string originalName) =>
        Storage.StoragePath.Join(Storage.StoragePath.Join(Storage.StoragePath.Parent(file), "Original"),
            Path.GetFileNameWithoutExtension(file) + Path.GetExtension(originalName).ToLowerInvariant());

    private static string? ExtOriginal(DmsStore db, long id) => db.Scalar("SELECT ext_original FROM dms_docs WHERE id = $d", ("$d", id)) as string;

    /// <summary>Ist das Dokument abgelegt (Datei in Ablage, Tresor oder externem Ordner)?</summary>
    internal static bool IsPlaced(DmsDoc d) => d.State == "done" && (d.FileId != 0 || d.Mount != null);

    /// <summary>Die abgelegte Datei.</summary>
    internal static async Task<byte[]> ReadFileAsync(UserSpace space, DmsHost host, DmsStore db, DmsDoc doc, CancellationToken ct)
    {
        if (doc.Mount != null) return await host.ReadAsync(space, doc.Mount, doc.Path!, ct);
        using var s = host.OpenRead(space, db, doc.FileId);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Das Original, wenn es neben der Datei eines gibt (Archivfassung); sonst null - dann ist die Datei das Original.</summary>
    private static async Task<byte[]?> ReadOriginalAsync(UserSpace space, DmsHost host, DmsStore db, DmsDoc doc, CancellationToken ct)
    {
        if (doc.Mount != null) return ExtOriginal(db, doc.Id) is { } o ? await host.ReadAsync(space, doc.Mount, o, ct) : null;
        return db.Scalar("SELECT original_blob FROM dms_docs WHERE id = $d", ("$d", doc.Id)) is string b ? db.Blobs.ReadAll(b) : null;
    }

    /// <summary>
    /// Datei entfernen: Dateiablage in den Papierkorb (purge: endgültig), Tresor endgültig, externer Ordner endgültig samt Original. Gibt
    /// zurück, ob das Dokument gleich mit vergessen werden kann (alles außer dem Papierkorb der Ablage).
    /// </summary>
    internal static async Task<bool> RemoveFileAsync(UserSpace space, DmsHost host, DmsStore db, DmsDoc doc, bool purge, CancellationToken ct)
    {
        if (doc.Mount != null)
        {
            await host.RemoveAsync(space, doc.Mount, doc.Path!, ct);
            if (ExtOriginal(db, doc.Id) is { } o) await host.RemoveAsync(space, doc.Mount, o, ct);
            return true;
        }
        if (doc.FileId != 0) host.Delete(space, db, doc.FileId, purge);
        return doc.FileId == 0 || db.Tresor || purge;
    }

    /// <summary>Angaben eines Dokuments für die Metadaten der PDF.</summary>
    internal static DmsArchive.PdfInfo PdfInfoOf(UserSpace space, DmsDoc doc)
    {
        var all = Dms.Entries(space.Db).ToDictionary(e => e.Id);
        string? Name(string? id) => id != null && all.TryGetValue(id, out var e) ? e.Name : null;
        var subject = string.Join(", ", new[] { Name(doc.Doctype), doc.DocDate }.Where(x => !string.IsNullOrEmpty(x)));
        var tags = string.Join(", ", doc.Tags.Select(t => Name(t.Id)).Where(n => n != null).Order(StringComparer.CurrentCultureIgnoreCase));
        return new DmsArchive.PdfInfo(doc.Title, Name(doc.Correspondent), subject.Length > 0 ? subject : null, tags.Length > 0 ? tags : null);
    }

    /// <summary>
    /// Ein Dokument zwischen offenem Bereich und Tresor verschieben: im Ziel mit Angaben, Text und Original neu anlegen und nach dem
    /// gewählten Ablageprofil (Vorgabe: das als Standard markierte mit diesem Ziel) ablegen, dann im Ursprung löschen - aus dem offenen Bereich endgültig (nicht in den
    /// Papierkorb), damit nichts unverschlüsselt zurückbleibt.
    /// </summary>
    /// <summary>Merged: die gleiche Datei lag schon abgelegt im Ziel (etwa ein unterbrochenes Verschieben) - die Kopie im Ursprung ist gelöscht.</summary>
    public enum Moved { Done, NotPlaced, AlreadyThere, Merged }

    public static async Task<Moved> MoveAreaAsync(UserSpace space, DmsHost host, DmsStore from, DmsStore to, long id, string? profileId, CancellationToken ct)
    {
        var doc = Dms.Get(from, id);
        if (!IsPlaced(doc)) return Moved.NotPlaced;
        // Eine Verknüpfung zeigt auf das Dokument dieses Bereichs; im anderen Bereich bekäme es eine neue Kennung
        if (doc.Fixed && Dms.FixedReason?.Invoke(from, id) is { } linked) throw new InvalidOperationException(linked);
        var target = to.Tresor ? "tresor" : "files";
        // Aus dem Tresor: Dateiablage oder externer Ordner (beides offener Bereich); in den Tresor: nur Ziel Tresor
        var profile = DmsFiling.Find(space, profileId) is { } p && (p.Target == target || (target == "files" && p.Target == "external")) ? p
            : DmsFiling.Of(space).FirstOrDefault(x => x.Target == target && x.Default) ?? DmsFiling.Of(space).FirstOrDefault(x => x.Target == target)
            ?? throw new ArgumentException(to.Tresor ? "Es gibt kein Ablageprofil mit Ziel Tresor." : "Es gibt kein Ablageprofil mit Ziel Dateiablage.");
        var file = await ReadFileAsync(space, host, from, doc, ct);
        var originalBlob = from.Scalar("SELECT original_blob FROM dms_docs WHERE id = $d", ("$d", id)) as string;
        var separate = await ReadOriginalAsync(space, host, from, doc, ct);
        var original = separate ?? file;
        var given = new Given(doc.Title, doc.DocDate, doc.Imported, Dms.Text(from, id) ?? "", doc.Correspondent, doc.Doctype, doc.Tags.Select(t => t.Id).ToList(),
            null, null, [], Compare: false);
        var r = await ImportAsync(space, new MemoryStream(original), doc.OriginalName, separate != null ? null : doc.Mime,
            new Info(doc.Source, null, doc.Sender, null, profile.Id, Given: given, Archive: separate != null ? new MemoryStream(file) : null), ct, to);
        if (r.Duplicate)
        {
            // Schon im Ziel: war ein früheres Verschieben unterbrochen (Löschen im Ursprung gescheitert), hier zu Ende bringen -
            // nur, wenn das Dokument dort fertig abgelegt ist. Leere Angaben ergänzt der Ursprung, sonst gilt das Ziel
            if (!IsPlaced(Dms.Get(to, r.Id))) return Moved.AlreadyThere;
            to.Exec("UPDATE dms_docs SET correspondent = COALESCE(correspondent, $c), doctype = COALESCE(doctype, $y) WHERE id = $d",
                ("$c", doc.Correspondent), ("$y", doc.Doctype), ("$d", r.Id));
            foreach (var t in doc.Tags) to.Exec("INSERT OR IGNORE INTO dms_doc_tags(doc, tag, auto) VALUES($d, $t, $a)", ("$d", r.Id), ("$t", t.Id), ("$a", t.Auto ? 1 : 0));
            // Im Ursprung bestätigt: im Ziel auch, und aus dem Inbox-Ordner nach dem Ordnermuster (der unterbrochene Versuch kam oft nicht mehr dazu)
            if (!doc.Inbox)
            {
                to.Exec("UPDATE dms_docs SET inbox = 0 WHERE id = $d", ("$d", r.Id));
                Refile(space, host, r.Id, false, to);
            }
            await RemoveFixedAsync(space, host, from, to, doc, r.Id, ct);
            Forget(from, id, originalBlob);
            return Moved.Merged;
        }
        await ProcessAsync(space, host, r.Id, ct, to);
        // Stand übernehmen: Posteingang, bestätigte Angaben, PDF/A
        to.Exec("UPDATE dms_docs SET inbox = $i, auto_correspondent = $ac, auto_doctype = $ay, pdfa = $pa, source_ref = $ref WHERE id = $d",
            ("$i", doc.Inbox ? 1 : 0), ("$ac", doc.AutoCorrespondent ? 1 : 0), ("$ay", doc.AutoDoctype ? 1 : 0), ("$pa", doc.PdfA ? 1 : 0),
            ("$ref", from.Scalar("SELECT source_ref FROM dms_docs WHERE id = $d", ("$d", id))), ("$d", r.Id));
        foreach (var t in doc.Tags) to.Exec("UPDATE dms_doc_tags SET auto = $a WHERE doc = $d AND tag = $t", ("$a", t.Auto ? 1 : 0), ("$d", r.Id), ("$t", t.Id));
        // Rolle zieht mit; hatte das Dokument sie inne, hat es sie danach im anderen Bereich inne
        if (doc.Role != null)
        {
            to.Exec("UPDATE dms_docs SET role = $r, role_since = $s WHERE id = $d",
                ("$r", doc.Role), ("$s", from.Scalar("SELECT role_since FROM dms_docs WHERE id = $d", ("$d", id))), ("$d", r.Id));
            if (doc.RoleCurrent)
                to.User.Exec("UPDATE dms_roles SET tresor = $t, doc = $n WHERE role = $r", ("$t", to.Tresor ? 1 : 0), ("$n", r.Id), ("$r", doc.Role));
        }
        // Schon bestätigt: aus dem Inbox-Ordner nach dem Ordnermuster
        if (!doc.Inbox) Refile(space, host, r.Id, false, to);
        await RemoveFixedAsync(space, host, from, to, doc, r.Id, ct);
        Forget(from, id, originalBlob);
        return Moved.Done;
    }

    /// <summary>Betrag und Fixierung ziehen mit um. Die Fixierung gilt im Ziel, bevor die Datei im Ursprung entfernt wird; scheitert das, gilt sie auch dort wieder.</summary>
    private static async Task RemoveFixedAsync(UserSpace space, DmsHost host, DmsStore from, DmsStore to, DmsDoc doc, long target, CancellationToken ct)
    {
        to.Exec("UPDATE dms_docs SET amount = COALESCE(amount, $a), fixed = MAX(fixed, $f) WHERE id = $d", ("$a", doc.Amount), ("$f", doc.Fixed ? 1 : 0), ("$d", target));
        if (doc.Fixed) Dms.Fix(from, doc.Id, false);
        try { await RemoveFileAsync(space, host, from, doc, purge: true, ct); }
        catch { if (doc.Fixed) Dms.Fix(from, doc.Id); throw; }
    }

    /// <summary>Dateiname aus einem Text (Betreff): ohne unzulässige Zeichen, höchstens 100 Zeichen.</summary>
    public static string SafeName(string text)
    {
        var s = Gallery.ImportProfiles.Safe(System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim());
        return s.Length > 100 ? s[..100].TrimEnd() : s;
    }

    /// <summary>Name der Quelle für den Platzhalter {source}.</summary>
    private static string SourceName(UserSpace space, string source, DmsDoc doc, DmsStore db) => source switch
    {
        "folder" => DmsSources.Of(space.Db).FirstOrDefault(s => s.Id == SourceRef(db, doc.Id))?.Name ?? "Ordner",
        "mail" => "Mail",
        "files" => "Dateien",
        "paperless" => "paperless",
        _ => "Hochladen",
    };

    private static string? SourceRef(DmsStore db, long id) => db.Scalar("SELECT source_ref FROM dms_docs WHERE id = $d", ("$d", id)) as string;

    // ---------------- Neu ablegen, Original, Aufräumen ----------------

    /// <summary>
    /// Nach dem Muster neu ablegen, wenn das Profil „Ablage folgt den Angaben“ hat (oder <paramref name="force"/>). Liegt die Datei nicht
    /// mehr dort, wo das DMS sie abgelegt hat, gilt sie als von Hand abgelegt - dann nur mit <paramref name="force"/>. Zwischen Inbox-Ordner
    /// und Ordnermuster (Bestätigen) wird immer verschoben, auch ohne „Ablage folgt den Angaben“.
    /// </summary>
    public static string? Refile(UserSpace space, IDmsFiles files, long id, bool force = false, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        var doc = Dms.Get(db, id);
        if (doc.Mount != null && files is DmsHost h) return RefileExternalAsync(space, h, db, doc, force, CancellationToken.None).GetAwaiter().GetResult();
        if (doc.FileId == 0 || doc.State == "queued") return null;
        var now = files.Find(space, db, doc.FileId);
        if (now == null) return null;
        var placed = db.Scalar("SELECT placed_path FROM dms_docs WHERE id = $d", ("$d", id)) as string;
        if (!force && (doc.Manual || (placed != null && placed != now.Value.Path)))
        {
            if (!doc.Manual) db.Exec("UPDATE dms_docs SET manual = 1 WHERE id = $d", ("$d", id));
            return null;
        }
        var profile = DmsFiling.Find(space, doc.Profile) ?? DmsFiling.Default(space);
        var inboxChange = DmsFiling.InInbox(profile, now.Value.Path) != doc.Inbox;
        if (!force && !profile.Follow && !inboxChange) return null;
        var ext = Path.GetExtension(now.Value.Path);
        var wanted = DmsFiling.PathFor(profile, DmsFiling.FactsOf(space, doc, SourceName(space, doc.Source, doc, db)), ext, doc.Inbox);
        var path = DmsFiling.SamePlace(now.Value.Path, wanted) ? now.Value.Path : files.Move(space, db, doc.FileId, wanted);
        db.Exec("UPDATE dms_docs SET placed_path = $p, manual = 0 WHERE id = $d", ("$p", path), ("$d", id));
        return path;
    }

    /// <summary>
    /// Neu ablegen im externen Ordner - nur nach einem Profil mit demselben Ordner; das Original im Unterordner wandert mit. Von Hand
    /// draußen Verschobenes (der Pfad weicht vom zuletzt abgelegten ab) bleibt, wo es ist.
    /// </summary>
    private static async Task<string?> RefileExternalAsync(UserSpace space, DmsHost host, DmsStore db, DmsDoc doc, bool force, CancellationToken ct)
    {
        if (doc.State == "queued" || doc.Path == null) return null;
        var placed = db.Scalar("SELECT placed_path FROM dms_docs WHERE id = $d", ("$d", doc.Id)) as string;
        if (!force && (doc.Manual || (placed != null && placed != doc.Path)))
        {
            if (!doc.Manual) db.Exec("UPDATE dms_docs SET manual = 1 WHERE id = $d", ("$d", doc.Id));
            return null;
        }
        var profile = DmsFiling.Find(space, doc.Profile) ?? DmsFiling.Default(space);
        if (profile.Target != "external" || profile.Mount != doc.Mount) return null;
        if (!force && !profile.Follow && DmsFiling.InInbox(profile, doc.Path) == doc.Inbox) return null;
        var wanted = DmsFiling.PathFor(profile, DmsFiling.FactsOf(space, doc, SourceName(space, doc.Source, doc, db)), Path.GetExtension(doc.Path), doc.Inbox);
        if (DmsFiling.SamePlace(doc.Path, wanted)) return doc.Path;
        var path = await host.MoveAsync(space, doc.Mount, doc.Path, wanted, ct);
        var original = ExtOriginal(db, doc.Id);
        if (original != null)
            try { original = await host.MoveAsync(space, doc.Mount, original, OriginalPath(path, original), ct); }
            catch (Exception e) when (e is IOException or FileNotFoundException or DirectoryNotFoundException) { original = null; }
        var etag = (await host.StatAsync(space, doc.Mount, path, ct))?.Etag;
        db.Exec("UPDATE dms_docs SET placed_path = $p, ext_path = $p, ext_original = $o, etag = COALESCE($e, etag), manual = 0 WHERE id = $d",
            ("$p", path), ("$o", original), ("$e", etag), ("$d", doc.Id));
        return path;
    }

    /// <summary>Das Original (bei Archivfassung aus dem Blob-Bereich bzw. dem Unterordner „Original/“, sonst die abgelegte Datei).</summary>
    public static Stream OpenOriginal(UserSpace space, IDmsFiles files, long id, out string name, out string? mime, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        var (blob, n, m, fileId, mount, extPath, extOriginal) = db.Query(
            "SELECT original_blob, original_name, mime, COALESCE(fileid, 0), ext_mount, ext_path, ext_original FROM dms_docs WHERE id = $d",
            r => (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)), ("$d", id)).FirstOrDefault();
        if (n == null) throw new KeyNotFoundException("Dokument nicht gefunden.");
        name = n;
        if (mount != null && files is DmsHost h)
        {
            mime = extOriginal != null ? Dav.DavServer.Mime(n) : m;
            return new MemoryStream(h.ReadAsync(space, mount, extOriginal ?? extPath!, CancellationToken.None).GetAwaiter().GetResult());
        }
        if (blob != null)
        {
            mime = Dav.DavServer.Mime(n);
            return db.Blobs.OpenRead(blob);
        }
        mime = m;
        return files.OpenRead(space, db, fileId);
    }

    /// <summary>
    /// Aufräumen: Dokumente, deren Datei endgültig gelöscht ist (nicht in der Ablage, nicht im Papierkorb), samt Original und Text
    /// entfernen; Dateien mit neuem Inhalt (neue Version) neu lesen.
    /// </summary>
    public static void Cleanup(UserSpace space, DmsHost host, CancellationToken ct, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        Dms.Ensure(db);
        // Kachel-Vorschauen gelöschter Dokumente
        db.Exec("DELETE FROM dms_tiles WHERE doc NOT IN (SELECT id FROM dms_docs)");
        foreach (var (id, fileId, blob, etag) in db.Query("SELECT id, fileid, original_blob, etag FROM dms_docs WHERE state <> 'queued' AND fileid IS NOT NULL",
                     r => (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3))))
        {
            ct.ThrowIfCancellationRequested();
            var now = host.Find(space, db, fileId);
            if (now == null)
            {
                if (host.InTrash(space, db, fileId)) continue;
                Forget(db, id, blob);
                continue;
            }
            if (etag != null && now.Value.Etag != etag)
            {
                try
                {
                    using var s = host.OpenRead(space, db, fileId);
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var name = Path.GetFileName(now.Value.Path);
                    var ex = host.Extract(ms.ToArray(), name, null, ct);
                    var text = ex.Text;
                    if (ex.WantsOcr && DmsArchive.CanMakeSearchable(TextExtract.Ext(name, null)))
                        text = (text + "\n" + host.MakeSearchable(ms.ToArray(), name, null, ct).Text).Trim();
                    var doc = Dms.Get(db, id);
                    Dms.WriteText(db, id, doc.Title, text, ex.WantsOcr ? "ocr" : ex.Method);
                    db.Exec("UPDATE dms_docs SET size = $z WHERE id = $d", ("$z", ms.Length), ("$d", id));
                }
                catch (Exception e) when (e is IOException or KeyNotFoundException or InvalidOperationException) { }
                db.Exec("UPDATE dms_docs SET etag = $e WHERE id = $d", ("$e", now.Value.Etag), ("$d", id));
            }
        }
    }

    /// <summary>
    /// Externe Dokumente einlesen (docs/dms-konzept.md, „Einlesen und Wiedererkennen“): je externem Ordner die Zielordner der Profile auflisten.
    /// Liegt die Datei noch am Pfad, nur bei geändertem Inhalt den Text neu lesen. Fehlt sie, wird sie unter den unbekannten Dateien gleicher
    /// Größe über die Prüfsumme gesucht (draußen verschoben oder umbenannt - dann gilt sie als von Hand abgelegt); sonst ist sie draußen
    /// gelöscht und das Dokument verschwindet. Ein fehlendes Original wird nur ausgetragen. Ist ein Ordner nicht erreichbar oder fehlt ein
    /// Zielordner ganz, bleibt alles, wie es ist.
    /// </summary>
    public static async Task ScanExternalAsync(UserSpace space, DmsHost host, CancellationToken ct)
    {
        var db = space.Db;
        Dms.Ensure(db);
        var docs = db.Query("SELECT id, ext_mount, ext_path, ext_original, etag, ext_sha, size FROM dms_docs WHERE ext_mount IS NOT NULL AND state <> 'queued'",
            r => (Id: r.GetInt64(0), Mount: r.GetString(1), Path: r.GetString(2), Original: r.IsDBNull(3) ? null : r.GetString(3),
                Etag: r.IsDBNull(4) ? null : r.GetString(4), Sha: r.IsDBNull(5) ? null : r.GetString(5), Size: r.GetInt64(6)));
        var profiles = DmsFiling.Of(space).Where(p => p.Target == "external" && p.Mount != null).ToList();
        foreach (var group in docs.GroupBy(d => d.Mount))
        {
            ct.ThrowIfCancellationRequested();
            var mount = group.Key;
            // Die Zielordner der Profile auf diesem Ordner auflisten; Dokumente außerhalb (Profil gelöscht oder geändert) einzeln nachsehen
            var roots = profiles.Where(p => p.Mount == mount).Select(p => p.Folder).Distinct().ToList();
            var files = new Dictionary<string, DmsSourceFile>(StringComparer.Ordinal);
            try
            {
                if (host.MountInfo(space, mount) == null) continue;
                foreach (var root in roots)
                {
                    if (!await host.FolderExistsAsync(space, mount, root, ct)) throw new DirectoryNotFoundException(root);
                    foreach (var f in (await host.ListAsync(space, mount, root, ct)).Files) files[f.Path] = f;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { continue; }
            var known = group.Select(d => d.Path).Concat(group.Where(d => d.Original != null).Select(d => d.Original!)).ToHashSet(StringComparer.Ordinal);
            foreach (var d in group)
            {
                ct.ThrowIfCancellationRequested();
                // Unter keinem Zielordner (Profil gelöscht oder geändert): einzeln nachsehen
                var under = roots.Any(r => d.Path.StartsWith(r + "/", StringComparison.Ordinal));
                (long Size, string Etag)? now = files.TryGetValue(d.Path, out var hit) ? (hit.Size, hit.Etag)
                    : under ? null : await host.StatAsync(space, mount, d.Path, ct);
                if (now == null)
                {
                    // Draußen verschoben oder umbenannt? Gleiche Größe, gleiche Prüfsumme
                    string? moved = null;
                    if (d.Sha != null)
                        foreach (var c in files.Values.Where(f => f.Size == d.Size && !known.Contains(f.Path) && !f.Path.Contains("/Original/", StringComparison.Ordinal)))
                        {
                            var bytes = await host.ReadAsync(space, mount, c.Path, ct);
                            if (Dav.FileStore.Hex(System.Security.Cryptography.SHA256.HashData(bytes)) != d.Sha) continue;
                            moved = c.Path;
                            known.Add(c.Path);
                            db.Exec("UPDATE dms_docs SET ext_path = $p, etag = $e, manual = 1 WHERE id = $d", ("$p", c.Path), ("$e", c.Etag), ("$d", d.Id));
                            break;
                        }
                    if (moved == null) { Forget(db, d.Id, null); continue; }
                }
                else if (d.Etag != null && now.Value.Etag != d.Etag)
                {
                    // Neuer Inhalt: Text neu lesen
                    try
                    {
                        var bytes = await host.ReadAsync(space, mount, d.Path, ct);
                        var name = Path.GetFileName(d.Path);
                        var ex = host.Extract(bytes, name, null, ct);
                        var text = ex.Text;
                        if (ex.WantsOcr && DmsArchive.CanMakeSearchable(TextExtract.Ext(name, null)))
                            text = (text + "\n" + host.MakeSearchable(bytes, name, null, ct).Text).Trim();
                        Dms.WriteText(db, d.Id, Dms.Get(db, d.Id).Title, text, ex.WantsOcr ? "ocr" : ex.Method);
                        db.Exec("UPDATE dms_docs SET etag = $e, ext_sha = $s, size = $z WHERE id = $d", ("$e", now.Value.Etag),
                            ("$s", Dav.FileStore.Hex(System.Security.Cryptography.SHA256.HashData(bytes))), ("$z", bytes.LongLength), ("$d", d.Id));
                    }
                    catch (Exception e) when (e is IOException or KeyNotFoundException or InvalidOperationException) { }
                }
                // Original draußen gelöscht: nur austragen
                if (d.Original != null && !files.ContainsKey(d.Original)
                    && (roots.Any(r => d.Original.StartsWith(r + "/", StringComparison.Ordinal)) || await host.StatAsync(space, mount, d.Original, ct) == null))
                    db.Exec("UPDATE dms_docs SET ext_original = NULL WHERE id = $d", ("$d", d.Id));
            }
        }
    }

    /// <summary>
    /// Geänderte Angaben in die PDF im externen Ordner schreiben (Profil „Angaben in die PDF schreiben“): nur Archivfassungen vom DMS
    /// (PDF/A), die Ghostscript neu schreibt; andere Dateien bleiben unverändert.
    /// </summary>
    public static async Task WriteMetaAsync(UserSpace space, DmsHost host, CancellationToken ct)
    {
        var db = space.Db;
        Dms.Ensure(db);
        foreach (var id in db.Query("SELECT id FROM dms_docs WHERE meta_dirty = 1 AND ext_mount IS NOT NULL LIMIT 20", r => r.GetInt64(0)))
        {
            ct.ThrowIfCancellationRequested();
            db.Exec("UPDATE dms_docs SET meta_dirty = 0 WHERE id = $d", ("$d", id));
            var doc = Dms.Get(db, id);
            if (!doc.PdfA || doc.Mount == null || doc.Path == null) continue;
            try
            {
                var bytes = await host.ReadAsync(space, doc.Mount, doc.Path, ct);
                if (host.ToPdfA(bytes, PdfInfoOf(space, doc), ct) is not { } pdf) continue;
                var etag = await host.ReplaceAsync(space, doc.Mount, doc.Path, pdf, ct);
                db.Exec("UPDATE dms_docs SET etag = $e, ext_sha = $s, size = $z WHERE id = $d", ("$e", etag),
                    ("$s", Dav.FileStore.Hex(System.Security.Cryptography.SHA256.HashData(pdf))), ("$z", pdf.LongLength), ("$d", id));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Ordner nicht erreichbar: beim nächsten Ändern */ }
        }
    }

    /// <summary>
    /// Einmalig je Bereich: Dokumente, die schon vor dem Inbox-Ordner in der Inbox lagen, dorthin verschieben (von Hand abgelegte bleiben).
    /// </summary>
    public static void MoveInboxOnce(UserSpace space, DmsHost host, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        var key = "dms.inbox-folder" + (db.Tresor ? ".tresor" : "");
        if (space.Db.GetJson<Dictionary<string, bool>>(key).ContainsKey("done")) return;
        Dms.Ensure(db);
        foreach (var id in db.Query("SELECT id FROM dms_docs WHERE inbox = 1 AND state = 'done' AND manual = 0", r => r.GetInt64(0)))
            try { Refile(space, host, id, false, db); }
            catch (Exception e) when (e is IOException or InvalidOperationException or KeyNotFoundException or ArgumentException or HttpRequestException) { }
        space.Db.SetJson(key, new Dictionary<string, bool> { ["done"] = true });
    }

    /// <summary>Ein Dokument ganz vergessen (Datei endgültig weg).</summary>
    internal static void Forget(DmsStore db, long id, string? blob)
    {
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var sql in new[] { "DELETE FROM dms_doc_tags WHERE doc = $d", "DELETE FROM dms_text WHERE doc = $d", "DELETE FROM dms_fts WHERE rowid = $d", "DELETE FROM dms_tiles WHERE doc = $d", "DELETE FROM dms_docs WHERE id = $d" })
                UserDb.Exec(conn, sql, ("$d", id));
            tx.Commit();
        }
        if (blob != null) db.Blobs.Delete(blob);
        // Hatte es eine Rolle inne: die hat danach keines mehr (frühere Versionen bleiben frühere)
        db.User.Exec("DELETE FROM dms_roles WHERE tresor = $t AND doc = $d", ("$t", db.Tresor ? 1 : 0), ("$d", id));
    }
}
