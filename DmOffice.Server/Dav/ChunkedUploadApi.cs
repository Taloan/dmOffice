using System.Collections.Concurrent;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Hochladen in Stuecken (Version 2): grosse Dateien gehen in Teilen hoch (8 MB empfohlen, hoechstens 16 MB je Teil) und
/// lassen sich nach einem Abbruch beim Stand des Servers fortsetzen - ohne Groessengrenze je Datei (nur das Kontingent).
/// Jedes Teil liegt verschluesselt als eigener Blob; die Sitzung (Name, Groesse, Teile) im Schluessel/Wert-Speicher der
/// Benutzerdatenbank - kein neues Schema. Am Ende setzt der Server die Teile zusammen und legt die Datei wie gewohnt ab
/// (Dateiablage ueber <see cref="StoreOne"/>, Chat ueber /api/chat/uploads/{id}/send). Sitzungen verfallen nach einem Tag.
/// <list type="bullet">
/// <item>POST /api/files/uploads { name, size, mime } -> { id, chunkSize }</item>
/// <item>PUT /api/files/uploads/{id}?offset=N (Teil roh im Koerper) -> { received }; passt offset nicht: 409 mit received</item>
/// <item>GET /api/files/uploads/{id} -> { received, size } (zum Fortsetzen)</item>
/// <item>POST /api/files/uploads/{id}/finish?path&amp;conflict&amp;mtime -> wie /api/files/upload</item>
/// <item>DELETE /api/files/uploads/{id} (abbrechen)</item>
/// </list>
/// </summary>
public static partial class FilesApi
{
    public const int ChunkSize = 8 * 1024 * 1024, MaxChunk = 16 * 1024 * 1024;
    /// <summary>Hoechstens so viele Teile je Datei (bei 8 MB: 32 GB) und je Link hoechstens so viele offene Sitzungen.</summary>
    public const int MaxChunks = 4096, MaxOpenPerLink = 50;
    /// <summary>Offene Sitzungen je Benutzer (ohne Link).</summary>
    public const int MaxOpenPerUser = 10;
    private static readonly TimeSpan SessionLife = TimeSpan.FromDays(1);
    private const string SessionPrefix = "upload.session.";
    private static readonly JsonSerializerOptions SessionJson = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionLocks = new();

    public sealed class UploadSession
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string? Mime { get; set; }
        public DateTime Created { get; set; }
        /// <summary>Teile der Reihe nach: Blob (null bei leerem Teil) und Laenge.</summary>
        public List<UploadChunk> Chunks { get; set; } = new();
        /// <summary>Ueber einen Upload-Link (Kennung des Links): nur dieser Link darf weitermachen.</summary>
        public string? Link { get; set; }
        public long Received => Chunks.Sum(c => c.Length);
    }

    public sealed record UploadChunk(string? Blob, long Length);

    private sealed record NewUpload(string Name, long Size, string? Mime);

    public static UploadSession? GetSession(UserDb db, string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{32}$") && db.Get(SessionPrefix + id) is { } json
            ? JsonSerializer.Deserialize<UploadSession>(json, SessionJson) : null;

    /// <summary>Sitzung, die zu diesem Zugang gehoert: angemeldet (link null) oder genau dieser Upload-Link.</summary>
    public static UploadSession? GetSession(UserDb db, string id, string? link) => GetSession(db, id) is { } s && s.Link == link ? s : null;

    private static void SaveSession(UserDb db, UploadSession s) => db.Set(SessionPrefix + s.Id, JsonSerializer.Serialize(s, SessionJson));

    /// <summary>Sitzung samt Teilen loeschen (fertig, abgebrochen oder verfallen).</summary>
    public static void DropSession(UserDb db, UploadSession s)
    {
        SessionLocks.TryRemove(s.Id, out _);
        foreach (var c in s.Chunks) if (c.Blob != null) try { db.Blobs.Delete(c.Blob); } catch (IOException) { }
        db.Set(SessionPrefix + s.Id, null);
    }

    /// <summary>Verfallene Sitzungen dieses Benutzers wegraeumen (auch einmal am Tag im Hintergrund).</summary>
    public static void SweepSessions(UserDb db)
    {
        foreach (var s in OpenSessions(db).ToList())
            if (DateTime.UtcNow - s.Created > SessionLife) DropSession(db, s);
    }

    /// <summary>Alles fertig hochgeladen? Sonst Fehler mit dem Stand.</summary>
    public static UploadSession RequireComplete(UserDb db, string id, string? link = null)
    {
        var s = GetSession(db, id, link) ?? throw new KeyNotFoundException("Diesen Upload gibt es nicht (mehr).");
        if (s.Received != s.Size) throw new InvalidOperationException($"Der Upload ist noch nicht vollständig ({s.Received} von {s.Size} Bytes).");
        return s;
    }

    /// <summary>Die Teile als ein Strom, der Reihe nach gelesen.</summary>
    public static Stream OpenSession(UserDb db, UploadSession s) => OpenBlobs(db, s.Chunks.Where(c => c.Blob != null).Select(c => c.Blob!).ToList());

    /// <summary>Blobs der Reihe nach als ein Strom (auch fuer Nextcloud-Uploads in Stuecken).</summary>
    public static Stream OpenBlobs(UserDb db, List<string> ids) => new ChunkStream(db.Blobs, ids);

    private static readonly IResult SessionGone = Results.NotFound(new { error = "Diesen Upload gibt es nicht (mehr)." });

    /// <summary>Neue Sitzung - angemeldet oder ueber einen Upload-Link (link: dessen Kennung, hoechstens 50 offene je Link).</summary>
    public static IResult StartSession(UserDb db, string? name, long size, string? mime, string? link = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Dateiname fehlt.");
        if (size < 0) throw new ArgumentException("Ungültige Größe.");
        // Keine tiefen Pfade, keine endlosen Namen - vor allem aus anonymen Link-Anfragen
        if (name.Count(c => c == '/') > 16 || name.Length > 255) throw new ArgumentException("Dateiname oder Pfad zu lang.");
        if (size > (long)MaxChunks * ChunkSize) throw new ArgumentException("Die Datei ist zu groß für das Hochladen in Stücken.");
        SweepSessions(db);
        var open = OpenSessions(db).ToList();
        if (link != null && open.Count(x => x.Link == link) >= MaxOpenPerLink)
            return Results.Json(new { error = "Zu viele Uploads auf einmal. Bitte später noch einmal." }, statusCode: 429);
        if (link == null && open.Count(x => x.Link == null) >= MaxOpenPerUser)
            return Results.Json(new { error = "Zu viele angefangene Uploads. Warte, bis einer fertig ist, oder brich einen ab." }, statusCode: 429);
        // Passt es ueberhaupt? Was in allen offenen Sitzungen schon angekuendigt ist, zaehlt mit - sonst liessen sich mit
        // vielen Sitzungen zugleich beliebig viele Teile ablegen. (In geteilte Ordner zaehlt am Ende das Kontingent des Besitzers.)
        var quota = FileStore.Quota(db);
        var pending = open.Sum(x => x.Size - x.Received) + open.Sum(x => x.Received);
        if (quota > 0 && size + pending > quota - FileStore.Usage(db))
            return Results.Json(new { error = new QuotaExceededException(quota).Message }, statusCode: 507);
        var s = new UploadSession { Id = Guid.NewGuid().ToString("N"), Name = name, Size = size, Mime = mime, Created = DateTime.UtcNow, Link = link };
        SaveSession(db, s);
        return Results.Ok(new { id = s.Id, chunkSize = ChunkSize });
    }

    private static IEnumerable<UploadSession> OpenSessions(UserDb db) =>
        db.Query("SELECT key FROM kv WHERE key LIKE $p", r => r.GetString(0), ("$p", SessionPrefix + "%"))
            .Select(k => GetSession(db, k[SessionPrefix.Length..])).OfType<UploadSession>();

    public static IResult SessionStatus(UserDb db, string id, string? link = null) =>
        GetSession(db, id, link) is { } s ? Results.Ok(new { received = s.Received, size = s.Size }) : SessionGone;

    /// <summary>Ein Teil: nur genau dort, wo der Server steht - so geht nichts doppelt, und ein Wiederholen nach Abbruch ist harmlos.</summary>
    public static async Task<IResult> PutChunk(UserDb db, string id, string? link, long offset, Stream body, CancellationToken ct)
    {
        var gate = SessionLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var s = GetSession(db, id, link);
            if (s == null) return SessionGone;
            if (offset != s.Received) return Results.Json(new { error = "Falsche Stelle.", received = s.Received }, statusCode: 409);
            if (s.Chunks.Count >= MaxChunks) throw new ArgumentException("Zu viele Teile.");
            var left = s.Size - s.Received;
            var blob = await db.Blobs.WriteAsync(body, Math.Min(MaxChunk, left), () => new ArgumentException("Das Teil ist zu groß."), ct);
            // Nur das letzte Teil darf kleiner als ein Stueck sein - sonst entstehen Tausende winziger Blobs
            if (blob.Size < Math.Min(ChunkSize, left) && blob.Size < left)
            {
                try { db.Blobs.Delete(blob.Id); } catch (IOException) { }
                throw new ArgumentException("Das Teil ist zu klein – nur das letzte darf kleiner als ein Stück sein.");
            }
            s.Chunks.Add(new UploadChunk(blob.Id, blob.Size));
            SaveSession(db, s);
            return Results.Ok(new { received = s.Received });
        }
        finally { gate.Release(); }
    }

    public static IResult CancelSession(UserDb db, string id, string? link = null)
    {
        if (GetSession(db, id, link) is { } s) DropSession(db, s);
        return Results.Ok();
    }

    private static void MapChunked(RouteGroupBuilder api)
    {
        api.MapPost("/uploads", (NewUpload body, HttpContext http) => StartSession(http.Space().Db, body.Name, body.Size, body.Mime));

        api.MapGet("/uploads/{id}", (string id, HttpContext http) => SessionStatus(http.Space().Db, id));

        api.MapPut("/uploads/{id}", (string id, long offset, HttpRequest request, HttpContext http, CancellationToken ct) =>
            PutChunk(http.Space().Db, id, null, offset, request.Body, ct));

        api.MapPost("/uploads/{id}/finish", async (string id, string? path, string? conflict, long? mtime, HttpContext http, CancellationToken ct) =>
        {
            var db = http.Space().Db;
            var s = RequireComplete(db, id);
            DateTime? modified = mtime is > 0 and < 253402300800000 ? DateTime.UnixEpoch.AddMilliseconds(mtime.Value) : null;
            (object? Entry, IResult? Error) result;
            await using (var body = OpenSession(db, s))
                result = CryptomatorPaths.Is(path)
                    ? await CryptomatorPaths.StoreOne(http, path!, s.Name, body, conflict, ct)
                    : TresorPaths.Is(path)
                    // In den Tresor: die zusammengesetzten Teile verschlüsselt dorthin, danach werden sie verworfen
                    ? await TresorPaths.StoreOne(http, path!, s.Name, body, s.Mime, conflict, ct)
                    : await StoreOne(http, FileStore.Normalize(path ?? ""), s.Name, body, s.Mime, conflict, modified, ct);
            if (TresorPaths.Is(path) || CryptomatorPaths.Is(path)) UserStorage.Refresh(db);
            if (result.Error != null) return result.Error;   // Konflikt oder kein Platz: die Teile bleiben, ein zweiter Versuch geht
            DropSession(db, s);
            return Results.Ok(new { files = new[] { result.Entry } });
        });

        api.MapDelete("/uploads/{id}", (string id, HttpContext http) => CancelSession(http.Space().Db, id));
    }

    /// <summary>Liest mehrere Blobs nacheinander wie einen Strom.</summary>
    private sealed class ChunkStream(BlobStore blobs, List<string> ids) : Stream
    {
        private int _next;
        private Stream? _current;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (true)
            {
                if (_current == null)
                {
                    if (_next >= ids.Count) return 0;
                    _current = blobs.OpenRead(ids[_next++]);
                }
                var n = _current.Read(buffer);
                if (n > 0) return n;
                _current.Dispose();
                _current = null;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        protected override void Dispose(bool disposing) { if (disposing) _current?.Dispose(); base.Dispose(disposing); }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Liegengebliebene Uploads in Stuecken wegraeumen, die seit einem Tag liegen (sonst erst beim naechsten Upload desselben Benutzers) - einmal am Tag.</summary>
public sealed class UploadJanitor(IServiceProvider services, ILogger<UploadJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in services.GetRequiredService<Spaces>().All())
                try { FilesApi.SweepSessions(space.Db); DavServer.SweepUploads(space.Db); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Uploads aufräumen für {User}", space.User.Login); }
            try { await Task.Delay(TimeSpan.FromHours(24), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
