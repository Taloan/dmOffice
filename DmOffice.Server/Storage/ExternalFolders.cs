using System.Net;
using System.Net.Sockets;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Storage;

/// <summary>Ein Ordner auf dem Server, den der Admin als externen Ordner freigibt.</summary>
public sealed class ExternalFolderDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>Voller Pfad im Container (eingebundene Freigabe, USB-Platte).</summary>
    public string Path { get; set; } = "";
    /// <summary>Wer ihn sieht (Benutzer-Kennungen); leer: alle.</summary>
    public List<string> Users { get; set; } = new();
    public bool ReadOnly { get; set; }
    /// <summary>Nur diese Ordner der obersten Ebene; leer: alles.</summary>
    public List<string> Include { get; set; } = new();
    /// <summary>Frühere Einstellung, wird noch gelesen: durchsuchbar. Heute <see cref="SearchLevel"/>.</summary>
    public bool Search { get; set; }
    /// <summary>Frühere Einstellung, wird noch gelesen: mit Texterkennung. Heute <see cref="SearchGroups"/>.</summary>
    public bool SearchOcr { get; set; }

    public const string LevelNone = "none", LevelNames = "names", LevelContents = "contents";
    public const string GroupText = "text", GroupDocs = "docs", GroupImages = "images";
    public const string CheckDaily = "daily", CheckSixHours = "6h", CheckHourly = "1h", CheckWeekly = "weekly", CheckManual = "manual";
    public const string ReadContinuous = "continuous", ReadNight = "night";
    /// <summary>In dieser Reihenfolge wird ausgewertet: vom geringsten Aufwand zum größten.</summary>
    public static readonly string[] AllGroups = [GroupText, GroupDocs, GroupImages];

    /// <summary>Stufe der Suche (<see cref="ExternalIndex"/>): nur bereitstellen, Dateien und Ordner, auch Inhalte. Neue Mounts: Dateien und Ordner.</summary>
    public string? SearchLevel { get; set; }
    /// <summary>Inhalte dieser Dateigruppen.</summary>
    public List<string>? SearchGroups { get; set; }
    /// <summary>Inhalte nur aus diesen Ordnern der obersten Ebene; leer: aus allen.</summary>
    public List<string> ContentFolders { get; set; } = new();
    /// <summary>Größere Dateien nicht auswerten.</summary>
    public int SearchMaxMb { get; set; } = 50;
    public string SearchRead { get; set; } = ReadContinuous;
    public string SearchCheck { get; set; } = CheckDaily;

    [System.Text.Json.Serialization.JsonIgnore] public string Level => SearchLevel ?? (Search ? LevelContents : LevelNone);
    [System.Text.Json.Serialization.JsonIgnore] public List<string> Groups => SearchGroups ?? (Search && SearchOcr ? [.. AllGroups] : [GroupText, GroupDocs]);
    [System.Text.Json.Serialization.JsonIgnore] public long MaxBytes => Math.Max(1, SearchMaxMb) * 1024L * 1024;
}

/// <summary>
/// Eigener externer Speicher eines Benutzers (in seiner verschluesselten Datenbank), zwei Arten von Einträgen:
/// ein externes Ziel (Access leer: Zugang zu WebDAV, OneDrive oder pCloud mit Zugangsdaten, angelegt unter Einstellungen →
/// Externe Ziele) oder eine Einbindung in Dateien (Access = Ziel, Path = Ordner darin, ohne eigene Zugangsdaten).
/// </summary>
public sealed class UserMount
{
    public const string WebDav = "webdav", OneDrive = "onedrive", PCloud = "pcloud";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>webdav (auch null - so gespeichert, bevor es OneDrive gab), onedrive oder pcloud.</summary>
    public string? Kind { get; set; }
    public string Url { get; set; } = "";
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>OneDrive: Refresh-Token der Anmeldung (Microsoft tauscht es beim Erneuern aus).</summary>
    public string? RefreshToken { get; set; }
    /// <summary>OneDrive: Laufwerk und gewählter Ordner darin ("root": das ganze Laufwerk).</summary>
    public string? Drive { get; set; }
    public string? Item { get; set; }
    /// <summary>OneDrive: Konto und Ort zum Anzeigen, etwa „Anna Muster · Dokumente/Steuer“.</summary>
    public string? Account { get; set; }
    /// <summary>pCloud: Token der Anmeldung (läuft nicht ab), Server der Region (api.pcloud.com, eapi.pcloud.com) und
    /// gewählter Ordner im Konto (leer: das ganze Konto).</summary>
    public string? Token { get; set; }
    public string? Host { get; set; }
    public string? Folder { get; set; }
    public string KindOrDefault => Kind ?? WebDav;
    /// <summary>Einbindung in Dateien: das externe Ziel (Id) und der Ordner darin; leer: ein externes Ziel.</summary>
    public string? Access { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Nur diese Ordner der obersten Ebene; leer: alles.</summary>
    public List<string> Include { get; set; } = new();
}

/// <summary>
/// Externe Ordner in der Dateiablage (Version 2): Ordner auf dem Server (nur vom Admin festgelegt) und WebDAV-Ordner
/// (vom Benutzer eingebunden, wenn der Admin es erlaubt). Die Dateien bleiben dort - keine Versionen, kein Papierkorb,
/// keine Freigaben. Adressen im Heimnetz (und der Server selbst) sind fuer WebDAV der Benutzer gesperrt, ausser der Admin
/// erlaubt sie - geprueft beim Verbinden, nicht nur beim Eintragen (sonst liesse sich das mit DNS umgehen).
/// </summary>
public static class ExternalFolders
{
    public const string UserKey = "files.external";

    /// <param name="Search">Server-Mount, in dem gesucht werden kann</param>
    /// <param name="Available">Gerade erreichbar (nur in Listen, die es prüfen - siehe <see cref="WithReachAsync"/>)</param>
    public sealed record Mount(string Id, string Name, string Kind, bool ReadOnly, bool Search = false, bool Available = true, string? Account = null, string? Access = null, string? Path = null);

    // ---------------- Erreichbarkeit ----------------
    // Ein Ordner auf dem Server kann fehlen (Freigabe nicht eingehängt, NAS aus), ein WebDAV-Ordner offline sein. Geprüft wird mit
    // kurzer Frist und gemerkt für ReachFresh - eine hängende Netzfreigabe blockiert so weder die Seite noch mehrere Threads
    // (läuft eine Prüfung noch, gilt das letzte Ergebnis bzw. „nicht erreichbar“).

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, bool Ok)> Reach = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>> Checking = new();
    private static readonly TimeSpan ReachFresh = TimeSpan.FromSeconds(30), ReachWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ist der externe Ordner gerade erreichbar? Antwortet spätestens nach ReachWait. Dauert die Prüfung länger (große Freigabe über
    /// einen Tunnel, gerade neu eingebunden), gilt das letzte bekannte Ergebnis, ohne eines „erreichbar“ - „nicht erreichbar“ nur, wenn
    /// eine Prüfung tatsächlich fehlschlug. Sonst standen nach jedem Ablauf von ReachFresh für einige Sekunden überall Stoppschilder.
    /// </summary>
    public static async Task<bool> ReachableAsync(UserSpace space, ServerConfig config, string id)
    {
        var key = space.User.Id + "|" + id;
        var known = Reach.TryGetValue(key, out var r);
        if (known && DateTime.UtcNow - r.At < ReachFresh) return r.Ok;
        var check = Checking.GetOrAdd(key, k => Task.Run(async () =>
        {
            bool ok;
            try { var (d, _) = Open(space, config, id); using (d) await d.ListAsync("", CancellationToken.None); ok = true; }
            catch (Exception) { ok = false; }
            Reach[key] = (DateTime.UtcNow, ok);
            Checking.TryRemove(key, out _);
            return ok;
        }));
        return await Task.WhenAny(check, Task.Delay(ReachWait)) == check ? check.Result : !known || r.Ok;
    }

    /// <summary>Die Liste mit gesetztem <see cref="Mount.Available"/> (alle gleichzeitig geprüft).</summary>
    public static async Task<List<Mount>> WithReachAsync(UserSpace space, ServerConfig config, List<Mount> mounts) =>
        (await Task.WhenAll(mounts.Select(async m => m with { Available = await ReachableAsync(space, config, m.Id) }))).ToList();

    /// <summary>Alle eigenen Einträge (externe Ziele und Einbindungen). Beim ersten Lesen nach der Umstellung auf externe Ziele
    /// (01.11.2026) wird jeder bisherige Ordner geteilt: seine Zugangsdaten werden ein externes Ziel, er selbst eine Einbindung
    /// darauf - mit derselben Id, damit Galerie, Audiothek, Importprofile, DMS und Datensicherung weiter auf ihn zeigen.</summary>
    public static List<UserMount> Own(UserSpace space)
    {
        var list = space.Db.GetJson<List<UserMount>>(UserKey);
        if (space.Db.GetJson<bool>(SplitKey)) return list;
        // Noch nichts angelegt: gleich als umgestellt merken, sonst würde das erste neue Ziel später geteilt
        if (list.Count == 0) { space.Db.SetJson(SplitKey, true); return list; }
        lock (space.Db)
        {
            list = space.Db.GetJson<List<UserMount>>(UserKey);
            if (space.Db.GetJson<bool>(SplitKey)) return list;
            foreach (var m in list.Where(x => x.Access == null).ToList())
            {
                var access = new UserMount
                {
                    Name = m.Name, Kind = m.Kind, Url = m.Url, User = m.User, Password = m.Password, RefreshToken = m.RefreshToken,
                    Drive = m.Drive, Item = m.Item, Account = m.Account, Token = m.Token, Host = m.Host, Folder = m.Folder,
                };
                list.Add(access);
                m.Access = access.Id; m.Path = "";
                m.Url = ""; m.User = m.Password = m.RefreshToken = m.Drive = m.Item = m.Token = m.Host = m.Folder = null;
            }
            space.Db.SetJson(UserKey, list);
            space.Db.SetJson(SplitKey, true);
            return list;
        }
    }
    private const string SplitKey = "files.external.split";

    private static bool SameHost(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port && x.Scheme == y.Scheme;

    /// <summary>Die externen Ziele (Zugänge), soweit die Verwaltung ihre Art erlaubt.</summary>
    public static List<Mount> TargetsOf(UserSpace space, ServerConfig config) =>
        Own(space).Where(m => m.Access == null && Allowed(config.Current, m)).Select(m => new Mount(m.Id, m.Name, m.KindOrDefault, false, Account: AccountOf(m))).ToList();

    private static string? AccountOf(UserMount m) => m.Account ?? (Uri.TryCreate(m.Url, UriKind.Absolute, out var u) ? u.Host : null);

    // ---------------- Ordnerlisten zwischenspeichern (wie Nextcloud) ----------------
    // Externe Ordner hängen oft an langsamen Wegen (Tunnel, WebDAV): eine Liste, die schon einmal geholt wurde, kommt sofort
    // aus dem Speicher; ist sie älter als ListFresh, holt der Server sie im Hintergrund neu (der nächste Aufruf sieht es).
    // Eigene Änderungen (Hochladen, Ordner, Umbenennen, Löschen) verwerfen die Listen des Ordners gleich.

    public sealed record ListEntry(string Name, bool IsDir, long Size, DateTime? Modified);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<ListEntry> Items, bool ReadOnly)> Lists = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Refreshing = new();
    private static readonly TimeSpan ListFresh = TimeSpan.FromSeconds(30), ListKeep = TimeSpan.FromHours(6);
    private static string ListKey(UserSpace space, string mount, string path) => space.User.Id + "|" + mount + "|" + path;

    private static async Task<List<ListEntry>> ReadListAsync(IStorageDriver d, string path, CancellationToken ct) =>
        (await d.ListAsync(path, ct)).Where(i => !i.Name.EndsWith(".dmoffice-part"))
            .Select(i => new ListEntry(i.Name, i.IsDir, i.Size, i.Modified == DateTime.MinValue ? null : i.Modified))
            .OrderByDescending(i => i.IsDir).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Liste eines Ordners - aus dem Zwischenspeicher, wenn vorhanden (dann im Hintergrund frisch), sonst jetzt.</summary>
    public static async Task<(List<ListEntry> Items, bool ReadOnly, DateTime At)> ListAsync(UserSpace space, ServerConfig config, string mount, string path, bool fresh, CancellationToken ct)
    {
        // Auch aus dem Zwischenspeicher nur, wenn der Benutzer den Ordner (noch) sehen darf
        // Externe Ziele selbst auch: Ordner wählen beim Einbinden und für die Datensicherung
        if (!MountsOf(space, config).Any(m => m.Id == mount) && !TargetsOf(space, config).Any(m => m.Id == mount)) throw new KeyNotFoundException("Diesen Ordner gibt es nicht.");
        var key = ListKey(space, mount, path);
        if (!fresh && Lists.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < ListKeep)
        {
            if (DateTime.UtcNow - c.At > ListFresh && Refreshing.TryAdd(key, true))
                _ = Task.Run(async () =>
                {
                    try { var (bd, bro) = Open(space, config, mount); using (bd) Lists[key] = (DateTime.UtcNow, await ReadListAsync(bd, path, CancellationToken.None), bro); }
                    catch (Exception) { Lists.TryRemove(key, out _); }   // Ordner weg oder nicht erreichbar: beim nächsten Mal frisch
                    finally { Refreshing.TryRemove(key, out _); }
                });
            return (c.Items, c.ReadOnly, c.At);
        }
        var (d, ro) = Open(space, config, mount);
        using (d)
        {
            var items = await ReadListAsync(d, path, ct);
            Lists[key] = (DateTime.UtcNow, items, ro);
            return (items, ro, DateTime.UtcNow);
        }
    }

    /// <summary>Suche in Server-Mounts: ein Ordner hat sich über dmOffice geändert (<see cref="ExternalIndexer.Changed"/>).</summary>
    public static Action<string, string>? IndexChanged { get; set; }
    /// <summary>Suche in Server-Mounts: ein Ordner wurde geöffnet (<see cref="ExternalIndexer.Opened"/>).</summary>
    public static Action<string, string>? IndexOpened { get; set; }

    /// <summary>Etwas im Ordner geändert: seine Liste (und die des übergeordneten, wegen Größe/Datum) verwerfen, die Suche nachziehen.</summary>
    public static void Changed(UserSpace space, string mount, string path)
    {
        var dir = StoragePath.Clean(path);
        Lists.TryRemove(ListKey(space, mount, dir), out _);
        var parent = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
        Lists.TryRemove(ListKey(space, mount, parent), out _);
        IndexChanged?.Invoke(mount, parent);
        if (dir != parent) IndexChanged?.Invoke(mount, dir);
    }

    /// <summary>Vorschaubilder externer Ordner (klein, verschlüsselt zwischengespeichert wie die großen Vorschauen der Galerie).</summary>
    public static DmOffice.Gallery.GalleryProxies? Thumbs { get; set; }

    public static List<Mount> MountsOf(UserSpace space, ServerConfig config)
    {
        var s = config.Current;
        var list = new List<Mount>();
        foreach (var f in s.ExternalFolders)
            if (f.Users.Count == 0 || f.Users.Contains(space.User.Id)) list.Add(new Mount(f.Id, f.Name, "folder", f.ReadOnly, f.Level != ExternalFolderDef.LevelNone));
        // Einbindungen in Dateien (die externen Ziele selbst stehen in TargetsOf)
        var own = Own(space);
        foreach (var m in own.Where(x => x.Access != null))
            if (own.FirstOrDefault(a => a.Id == m.Access) is { } acc && Allowed(s, acc))
                list.Add(new Mount(m.Id, m.Name, acc.KindOrDefault, false, Account: AccountOf(acc), Access: acc.Id, Path: m.Path));
        return list;
    }

    /// <summary>Treiber fuer einen externen Ordner des Benutzers - KeyNotFound, wenn er ihn nicht sehen darf.</summary>
    public static (IStorageDriver Driver, bool ReadOnly) Open(UserSpace space, ServerConfig config, string id)
    {
        var (d, ro, include) = OpenAll(space, config, id);
        return (include.Count > 0 ? new SubsetDriver(d, include) : d, ro);
    }

    /// <summary>Ohne die Auswahl der obersten Ebene - zum Auswählen.</summary>
    private static (IStorageDriver Driver, bool ReadOnly, List<string> Include) OpenAll(UserSpace space, ServerConfig config, string id)
    {
        var s = config.Current;
        if (s.ExternalFolders.FirstOrDefault(f => f.Id == id && (f.Users.Count == 0 || f.Users.Contains(space.User.Id))) is { } f)
            return (new FolderDriver(f.Path), f.ReadOnly, f.Include);
        var own = Own(space);
        if (own.FirstOrDefault(m => m.Id == id) is { } m)
        {
            // Einbindung: Treiber des externen Ziels, auf den Ordner darin beschränkt
            var acc = m.Access == null ? m : own.FirstOrDefault(a => a.Id == m.Access);
            if (acc != null && Allowed(s, acc))
            {
                IStorageDriver d = acc.KindOrDefault switch
                {
                    UserMount.OneDrive => OneDrive(space, acc),
                    UserMount.PCloud => PCloudFolders.Driver(acc),
                    _ => new WebDavDriver(acc.Url, acc.User, acc.Password, s.ExternalWebDavPrivate ? Open_ : Public),
                };
                if (m.Access != null && StoragePath.Clean(m.Path).Length > 0) d = new SubfolderDriver(d, StoragePath.Clean(m.Path));
                return (d, false, m.Include);
            }
        }
        throw new KeyNotFoundException("Externer Ordner nicht gefunden.");
    }

    /// <summary>Eigene Ordner nur, solange der Admin die Art erlaubt - sonst gesperrt (nicht sichtbar, nicht zu öffnen).</summary>
    private static bool Allowed(ServerSettings s, UserMount m) =>
        m.KindOrDefault switch
        {
            UserMount.OneDrive => s.ExternalOneDriveForUsers,
            UserMount.PCloud => s.ExternalPCloudForUsers,
            _ => s.ExternalWebDavForUsers,
        };

    // ---------------- OneDrive ----------------

    /// <summary>Verbindung zu Microsoft Graph - in Tests ersetzbar. Lesen folgt der Umleitung zur Download-Adresse (ohne Token).</summary>
    public static HttpClient GraphHttp { get; set; } = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(30) };
    /// <summary>Anmeldung bei Microsoft (Program setzt sie); ohne sie lässt sich kein OneDrive öffnen.</summary>
    public static DmOffice.Mail.MicrosoftOAuth? OAuth { get; set; }
    /// <summary>Access-Token zu einem Refresh-Token - in Tests ersetzbar. Vorgabe: <see cref="OAuth"/>.</summary>
    public static Func<string, string?, Action<string, string, string>?, CancellationToken, Task<string>>? FilesToken { get; set; }
    private static Task<string> TokenAsync(string key, string? refresh, Action<string, string, string>? rotated, CancellationToken ct) =>
        FilesToken?.Invoke(key, refresh, rotated, ct)
        ?? (OAuth ?? throw new InvalidOperationException("Microsoft-Anmeldung ist nicht eingerichtet (DMOFFICE_MS_CLIENT_ID).")).FilesTokenAsync(key, refresh, rotated, ct);

    /// <summary>Treiber für einen eingebundenen OneDrive-Ordner; ein neues Refresh-Token landet gleich wieder im Ordner.</summary>
    private static OneDriveDriver OneDrive(UserSpace space, UserMount m) =>
        new(ct => TokenAsync(m.Id, m.RefreshToken, (id, previous, fresh) => Rotated(space, id, previous, fresh), ct), GraphHttp, m.Drive ?? "", m.Item ?? "root");

    /// <summary>Neues Refresh-Token speichern - nur, wenn im Ordner noch das bisherige steht.</summary>
    public static void Rotated(UserSpace space, string id, string previous, string fresh)
    {
        lock (space.Db)
        {
            var list = Own(space);
            if (list.FirstOrDefault(x => x.Id == id) is not { } m || m.RefreshToken != previous) return;
            m.RefreshToken = fresh;
            space.Db.SetJson(UserKey, list);
        }
    }

    /// <summary>Treiber für die Ordnerauswahl vor dem Einbinden (Laufwerk und Ordner frei).</summary>
    private static OneDriveDriver Browse(string refresh, string? drive = null, string item = "root") => drive == null
        ? new(ct => TokenAsync("browse", refresh, null, ct), GraphHttp)
        : new(ct => TokenAsync("browse", refresh, null, ct), GraphHttp, drive, item);

    private static readonly SemaphoreSlim ThumbGate = new(2);

    /// <summary>Verbindung für WebDAV-Ziele der Benutzer (auch Datensicherung): Heimnetz nur, wenn der Admin es erlaubt.</summary>
    public static HttpClient ClientFor(ServerConfig config) => config.Current.ExternalWebDavPrivate ? Open_ : Public;

    // Gemeinsame Verbindungen: eine mit Heimnetz (Admin erlaubt es) - der Server selbst, Link-lokal (Metadaten-Dienste) und Multicast
    // bleiben auch dort gesperrt -, eine, die nur oeffentliche Adressen erreicht
    private static readonly HttpClient Open_ = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ok = addresses.Where(a => !NeverAllowed(a)).ToArray();
            if (ok.Length == 0) throw new HttpRequestException("Der Server selbst und link-lokale Adressen sind für eigene WebDAV-Ordner nicht erlaubt.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Immer gesperrt, auch mit „Heimnetz erlauben“: der Server selbst, unbestimmt, link-lokal, Multicast.</summary>
    public static bool NeverAllowed(IPAddress a)
    {
        a = Embedded(a);
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork) { var b = a.GetAddressBytes(); return b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] >= 224; }
        return a.IsIPv6LinkLocal || a.IsIPv6Multicast;
    }

    /// <summary>In IPv6 verpackte IPv4-Adressen auspacken: ::ffff:a.b.c.d, ::a.b.c.d, NAT64 (64:ff9b::/96), 6to4 (2002::/16).</summary>
    private static IPAddress Embedded(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) return a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return a;
        var b = a.GetAddressBytes();
        bool Zero(int from, int to) { for (var i = from; i < to; i++) if (b[i] != 0) return false; return true; }
        if (Zero(0, 12) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1)) return new IPAddress(b[12..16]);
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && Zero(4, 12)) return new IPAddress(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);
        return a;
    }
    private static readonly HttpClient Public = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ok = addresses.Where(a => !Private(a)).ToArray();
            if (ok.Length == 0) throw new HttpRequestException("Adressen im Heimnetz und der Server selbst sind für eigene WebDAV-Ordner nicht erlaubt.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Heimnetz, Server selbst, Link-lokal, Carrier-NAT, Multicast ...</summary>
    public static bool Private(IPAddress a)
    {
        a = Embedded(a);
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224
                   || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || (b[0] == 192 && b[1] == 0 && b[2] == 0);
        }
        return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    public record AddBody(string Name, string Url, string? User, string? Password);
    /// <summary>Externes Ziel ändern: Name, bei WebDAV auch Adresse und Benutzer; ein leeres Passwort behält das gespeicherte.</summary>
    public record EditBody(string Name, string? Url, string? User, string? Password);
    /// <summary>Replace: ein vorhandenes OneDrive-Ziel neu anmelden (Id bleibt) statt ein neues anzulegen.</summary>
    public record OneDriveBody(string Flow, string Name, string? Item, string? Path, string? Replace = null);
    public record PathBody(string Mount, string Path);
    /// <summary>Name: neuer Name der Einbindung in Dateien (leer: bleibt).</summary>
    public record IncludeBody(List<string>? Include, string? Name = null);
    public record MoveBody(string Mount, string From, string To);
    public record DeleteBody(string Mount, List<string> Paths);
    /// <summary>Kopieren in einen Ordner desselben externen Ordners (To: der Ordner, in den kopiert wird).</summary>
    public record CopyBody(string Mount, List<string> Paths, string To);

    /// <summary>Datei oder Ordner samt Inhalt kopieren - lesen und schreiben über den Treiber, das kann jeder Dienst.</summary>
    private static async Task CopyAsync(IStorageDriver d, string from, string to, CancellationToken ct)
    {
        var stat = await d.StatAsync(from, ct) ?? throw new KeyNotFoundException("„" + from + "“ gibt es nicht.");
        if (!stat.IsDir)
        {
            await using var s = await d.OpenReadAsync(from, ct);
            await d.WriteAsync(to, s, ct);
            return;
        }
        await d.MkdirAsync(to, ct);
        foreach (var e in await d.ListAsync(from, ct))
            await CopyAsync(d, StoragePath.Join(from, e.Name), StoragePath.Join(to, e.Name), ct);
    }

    /// <summary>OneDrive wird angeboten: der Admin erlaubt es und die Microsoft-Anmeldung ist eingerichtet.</summary>
    private static bool OneDriveOffered(ServerConfig config) => config.Current.ExternalOneDriveForUsers && OAuth?.Enabled == true;

    private static IResult? OneDriveRefused(UserSpace space, ServerConfig config) =>
        !config.Current.ExternalOneDriveForUsers ? Results.Json(new { error = "Eigene OneDrive-Ordner sind nicht freigeschaltet." }, statusCode: 403)
        : OAuth?.Enabled != true ? Results.Json(new { error = "Microsoft-Anmeldung ist auf diesem Server nicht eingerichtet (DMOFFICE_MS_CLIENT_ID)." }, statusCode: 404)
        : null;

    public record BindBody(string Access, string? Path, string Name);

    /// <summary>Verbindung zu einem WebDAV-Ziel prüfen (auflisten) - in Tests ersetzbar.</summary>
    public static Func<UserMount, ServerConfig, CancellationToken, Task> CheckWebDav { get; set; } = async (m, config, ct) =>
    {
        using var d = new WebDavDriver(m.Url, m.User, m.Password, ClientFor(config));
        await d.ListAsync("", ct);
    };

    /// <summary>
    /// Ein eigenes externes Ziel der Art kind ändern - die Id bleibt, Einbindungen in Dateien, DMS und die Datensicherung zeigen
    /// weiter darauf. False: kein solches Ziel (auch keins eines anderen Benutzers, keine Einbindung).
    /// </summary>
    public static bool Change(UserSpace space, string id, string kind, Action<UserMount> apply)
    {
        lock (space.Db)
        {
            var list = Own(space);
            if (list.FirstOrDefault(m => m.Id == id && m.Access == null && m.KindOrDefault == kind) is not { } target) return false;
            apply(target);
            space.Db.SetJson(UserKey, list);
        }
        Forget(space, id);
        return true;
    }

    /// <summary>Zugang geändert: gemerkte Erreichbarkeit und Ordnerlisten des Ziels und seiner Einbindungen verwerfen.</summary>
    private static void Forget(UserSpace space, string target)
    {
        var keys = Own(space).Where(m => m.Id == target || m.Access == target).Select(m => space.User.Id + "|" + m.Id).ToList();
        foreach (var k in keys) Reach.TryRemove(k, out _);
        foreach (var k in Lists.Keys)
            if (keys.Any(p => k.StartsWith(p + "|"))) Lists.TryRemove(k, out _);
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/files/external").RequireAuthorization();
        Transfer.Map(api);

        api.MapGet("/mounts", async (HttpContext http, ServerConfig config) => new
        {
            mounts = await WithReachAsync(http.Space(), config, MountsOf(http.Space(), config)),
            // Externe Ziele (Einstellungen → Externe Ziele): Zugänge, aus denen man in Dateien und der Datensicherung wählt
            targets = TargetsOf(http.Space(), config),
            canAdd = (config.Current.ExternalWebDavForUsers || OneDriveOffered(config) || PCloudFolders.Offered(config)),
            webDav = config.Current.ExternalWebDavForUsers,
            oneDrive = OneDriveOffered(config),
            pCloud = PCloudFolders.Offered(config),
        });

        // Suche in Server-Mounts - nur in denen, die man sehen darf und in denen die Suche eingeschaltet ist; im Ausschnitt stehen U+E000
        // und U+E001 um die gefundenen Wörter. stand: wie aktuell der Index ist, damit „nichts gefunden“ eine verlässliche Aussage bleibt
        api.MapGet("/search", (string q, string? mount, string? path, int? limit, HttpContext http, ServerConfig config, ExternalIndex index, ExternalIndexer indexer) =>
        {
            var mounts = MountsOf(http.Space(), config).Where(m => m.Kind == "folder" && m.Search && (mount == null || m.Id == mount)).ToList();
            var names = mounts.ToDictionary(m => m.Id, m => m.Name);
            var defs = config.Current.ExternalFolders.ToDictionary(f => f.Id);
            var max = Math.Clamp(limit ?? 100, 1, 500);
            var hits = index.Search(mounts.Select(m => m.Id), q, mount != null ? path : null, max)
                // Auswahl der obersten Ebene inzwischen geändert und noch nicht neu gelesen: nichts außerhalb zeigen
                .Where(h => defs.TryGetValue(h.Mount, out var d) && (d.Include.Count == 0 || d.Include.Contains(h.Path.Split('/')[0])));
            var stand = mounts.Select(m =>
            {
                var d = defs[m.Id];
                var st = index.StateOf(d, TimeSpan.FromSeconds(30));
                return new { mount = m.Id, name = m.Name, level = d.Level, lastScan = st.LastScan, checking = indexer.Checking?.Mount == d.Name, open = st.Wanted - st.Done, limited = d.ContentFolders.Count > 0 };
            }).ToList();
            return new { items = hits.Select(h => new { h.Mount, mountName = names[h.Mount], h.Path, h.Name, h.IsDir, h.Size, h.Modified, h.Snippet }).ToList(), stand };
        });

        // Platz je externem Ordner fuer Einstellungen → Dateien → Speicherplatz; langsame Server bremsen nicht
        api.MapGet("/space", async (HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var tasks = MountsOf(space, config).Select(async m =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    using var d = Open(space, config, m.Id).Driver;
                    var s = await d.SpaceAsync(cts.Token);
                    return new { m.Id, m.Name, m.Kind, m.ReadOnly, used = s?.Used, free = s?.Free, error = (string?)null };
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    var error = e is OperationCanceledException ? "Keine Antwort." : e.GetBaseException().Message;
                    return new { m.Id, m.Name, m.Kind, m.ReadOnly, used = (long?)null, free = (long?)null, error = (string?)error };
                }
            });
            return await Task.WhenAll(tasks);
        });

        // Eigenen WebDAV-Ordner einbinden: erst verbinden und auflisten, dann speichern
        api.MapPost("/mounts", async (AddBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (!config.Current.ExternalWebDavForUsers) return Results.Json(new { error = "Eigene WebDAV-Ordner sind nicht freigeschaltet." }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "Bitte einen Namen angeben." });
            var mount = new UserMount { Name = body.Name.Trim(), Kind = UserMount.WebDav, Url = body.Url.Trim(), User = body.User?.Trim(), Password = body.Password };
            try { await CheckWebDav(mount, config, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + e.GetBaseException().Message });
            }
            var list = Own(space);
            list.Add(mount);
            space.Db.SetJson(UserKey, list);
            return Results.Ok(new Mount(mount.Id, mount.Name, "webdav", false));
        });
        // Zum Bearbeiten: Adresse und Benutzer eines WebDAV-Ziels (das Passwort geht nicht zurück, nur ob es eins gibt)
        api.MapGet("/mounts/{id}", (string id, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            if (Own(space).FirstOrDefault(m => m.Id == id && m.Access == null) is not { } m || !Allowed(config.Current, m)) return Results.NotFound();
            var webDav = m.KindOrDefault == UserMount.WebDav;
            return Results.Ok(new { m.Id, m.Name, kind = m.KindOrDefault, account = AccountOf(m), url = webDav ? m.Url : null, user = webDav ? m.User : null, passwordSet = webDav && !string.IsNullOrEmpty(m.Password) });
        });
        // Externes Ziel ändern (Einstellungen → Externe Ziele → Bearbeiten). OneDrive und pCloud neu anmelden: über
        // POST /onedrive bzw. /pcloud mit Replace.
        api.MapPut("/mounts/{id}", async (string id, EditBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (Own(space).FirstOrDefault(m => m.Id == id && m.Access == null) is not { } old || !Allowed(config.Current, old)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "Bitte einen Namen angeben." });
            var next = new UserMount { Url = old.Url, User = old.User, Password = old.Password };
            if (old.KindOrDefault == UserMount.WebDav)
            {
                if (body.Url != null) next.Url = body.Url.Trim();
                if (body.User != null) next.User = body.User.Trim() is { Length: > 0 } u ? u : null;
                // Anderer Server oder anderer Benutzer: das gespeicherte Passwort geht nicht dorthin - neu eingeben
                if ((!SameHost(next.Url, old.Url) || next.User != old.User) && string.IsNullOrEmpty(body.Password) && !string.IsNullOrEmpty(old.Password))
                    return Results.BadRequest(new { error = "Für eine neue Adresse oder einen anderen Benutzer bitte das Passwort eingeben." });
                if (!string.IsNullOrEmpty(body.Password)) next.Password = body.Password;
                if (next.Url.Length == 0) return Results.BadRequest(new { error = "Bitte eine Adresse angeben." });
                if (next.Url != old.Url || next.User != old.User || next.Password != old.Password)
                {
                    try { await CheckWebDav(next, config, ct); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + e.GetBaseException().Message });
                    }
                }
            }
            if (!Change(space, id, old.KindOrDefault, m => { m.Name = body.Name.Trim(); m.Url = next.Url; m.User = next.User; m.Password = next.Password; }))
                return Results.NotFound();
            // Neue Adresse: eine Datensicherung auf dieses Ziel muss neu bestätigt werden (läuft bis dahin nicht)
            if (next.Url != old.Url || next.User != old.User) PlainBackupService.TargetChanged(space, id);
            return Results.Ok(TargetsOf(space, config).First(m => m.Id == id));
        });
        // Eigener OneDrive-Ordner: bei Microsoft anmelden (Gerätecode, abgefragt über /api/settings/accounts/microsoft/poll),
        // dann einen Ordner wählen und einbinden. Bis dahin bleibt das Token in der Anmeldung auf dem Server.
        api.MapPost("/onedrive/start", async (HttpContext http, ServerConfig config, DmOffice.Mail.MicrosoftOAuth oauth, CancellationToken ct) =>
        {
            if (OneDriveRefused(http.Space(), config) is { } refused) return refused;
            return Results.Ok(await oauth.StartAsync(http.Space().User.Id, ct, DmOffice.Mail.MicrosoftOAuth.FilesScopes));
        });
        api.MapGet("/onedrive/folders", async (string flow, string? item, HttpContext http, ServerConfig config, DmOffice.Mail.MicrosoftOAuth oauth, CancellationToken ct) =>
        {
            var space = http.Space();
            if (OneDriveRefused(space, config) is { } refused) return refused;
            if (oauth.FilesGrant(space.User.Id, flow) is not { } refresh) return Results.BadRequest(new { error = "Erst bei Microsoft anmelden." });
            var drive = await Browse(refresh).MyDriveAsync(ct);
            var folders = await Browse(refresh, drive.Id, string.IsNullOrEmpty(item) ? "root" : item).FoldersAsync(ct);
            return Results.Ok(new { drive = new { id = drive.Id, type = drive.Type, owner = drive.Owner }, folders = folders.Select(f => new { id = f.Id, name = f.Name }) });
        });
        api.MapPost("/onedrive", async (OneDriveBody body, HttpContext http, ServerConfig config, DmOffice.Mail.MicrosoftOAuth oauth, CancellationToken ct) =>
        {
            var space = http.Space();
            if (OneDriveRefused(space, config) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "Bitte einen Namen angeben." });
            if (oauth.FilesGrant(space.User.Id, body.Flow) is not { } refresh) return Results.BadRequest(new { error = "Erst bei Microsoft anmelden." });
            var drive = await Browse(refresh).MyDriveAsync(ct);
            var item = string.IsNullOrEmpty(body.Item) ? "root" : body.Item;
            // Den Ordner gibt es und er lässt sich lesen
            await Browse(refresh, drive.Id, item).ListAsync("", ct);
            var where = string.IsNullOrWhiteSpace(body.Path) ? "OneDrive" : "OneDrive/" + body.Path.Trim().Trim('/');
            var account = drive.Owner is { Length: > 0 } o ? o + " · " + where : where;
            if (body.Replace != null)
            {
                if (!Change(space, body.Replace, UserMount.OneDrive, m => { m.Name = body.Name.Trim(); m.RefreshToken = refresh; m.Drive = drive.Id; m.Item = item; m.Account = account; }))
                    return Results.NotFound();
                oauth.Forget(body.Flow);
                return Results.Ok(TargetsOf(space, config).First(m => m.Id == body.Replace));
            }
            var mount = new UserMount
            {
                Name = body.Name.Trim(), Kind = UserMount.OneDrive, RefreshToken = refresh, Drive = drive.Id, Item = item, Account = account,
            };
            lock (space.Db)
            {
                var list = Own(space);
                list.Add(mount);
                space.Db.SetJson(UserKey, list);
            }
            oauth.Forget(body.Flow);
            return Results.Ok(new Mount(mount.Id, mount.Name, UserMount.OneDrive, false));
        });

        // Eigener WebDAV-Ordner: welche Ordner der obersten Ebene in dmOffice erscheinen (leer: alle)
        api.MapGet("/mounts/{id}/top", async (string id, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var mount = Own(space).FirstOrDefault(m => m.Id == id);
            if (mount == null) return Results.NotFound();
            using var d = OpenAll(space, config, id).Driver;
            var dirs = (await d.ListAsync("", ct)).Where(e => e.IsDir).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(new { dirs, include = mount.Include });
        });
        api.MapPut("/mounts/{id}/include", (string id, IncludeBody body, HttpContext http) =>
        {
            var space = http.Space();
            var list = Own(space);
            var mount = list.FirstOrDefault(m => m.Id == id);
            if (mount == null) return Results.NotFound();
            mount.Include = (body.Include ?? []).Select(StoragePath.Clean).Where(n => n.Length > 0 && !n.Contains('/')).Distinct().ToList();
            // Umbenennen: die Id bleibt, Galerie, Audiothek, DMS und Datensicherung zeigen weiter darauf
            if (!string.IsNullOrWhiteSpace(body.Name)) mount.Name = body.Name.Trim();
            space.Db.SetJson(UserKey, list);
            return Results.Ok();
        });
        // Einbinden in Dateien: ein externes Ziel und ein Ordner darin (Zugangsdaten bleiben beim Ziel)
        api.MapPost("/mounts/bind", async (BindBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "Bitte einen Namen angeben." });
            if (TargetsOf(space, config).FirstOrDefault(t => t.Id == body.Access) is not { } target) return Results.BadRequest(new { error = "Dieses externe Ziel gibt es nicht." });
            var path = StoragePath.Clean(body.Path);
            // Den Ordner gibt es und er lässt sich lesen
            try { using var d = OpenAll(space, config, body.Access).Driver; await d.ListAsync(path, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Ordner nicht erreichbar: " + e.GetBaseException().Message }); }
            var mount = new UserMount { Name = body.Name.Trim(), Kind = target.Kind, Access = target.Id, Path = path };
            lock (space.Db)
            {
                var list = Own(space);
                list.Add(mount);
                space.Db.SetJson(UserKey, list);
            }
            return Results.Ok(MountsOf(space, config).First(m => m.Id == mount.Id));
        });
        api.MapDelete("/mounts/{id}", (string id, HttpContext http) =>
        {
            var space = http.Space();
            // Ein externes Ziel nimmt seine Einbindungen mit; alles, was sie verwendet, wird abgeschaltet (nicht gelöscht)
            List<string> gone;
            lock (space.Db)
            {
                var list = Own(space);
                gone = list.Where(m => m.Id == id || m.Access == id).Select(m => m.Id).ToList();
                if (gone.Count == 0) return Results.NotFound();
                list.RemoveAll(m => gone.Contains(m.Id));
                space.Db.SetJson(UserKey, list);
            }
            foreach (var g in gone) MountDetach.Run(space.Db, g);
            return Results.Ok();
        });

        // fresh: am Ordner vorbei neu holen (Knopf „Aktualisieren“); sonst aus dem Zwischenspeicher, wenn vorhanden
        api.MapGet("/list", async (string mount, string? path, bool? fresh, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var rel = StoragePath.Clean(path);
            var (items, ro, at) = await ListAsync(http.Space(), config, mount, rel, fresh == true, ct);
            IndexOpened?.Invoke(mount, rel);
            // Cryptomator-Tresor in diesem Ordner: Wurzel zum Entsperren und ob er für diesen Zugang offen ist
            var root = DmOffice.CryptomatorPaths.ExtRoot(mount, rel);
            var vault = DmOffice.CryptomatorPaths.LooksLikeVault(items.Select(i => (i.Name, i.IsDir)))
                ? new { root, unlocked = DmOffice.CryptomatorPaths.IsUnlocked(http, root) } : null;
            return Results.Ok(new { readOnly = ro, listed = at, vault, items = items.Select(i => new { name = i.Name, isDir = i.IsDir, size = i.Size, modified = i.Modified }) });
        });

        // Vorschaubild (256 px) für die Kachelansicht: einmal aus der Datei gemacht, dann verschlüsselt zwischengespeichert
        api.MapGet("/thumb", async (string mount, string path, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var rel = StoragePath.Clean(path);
            var (d, _) = Open(space, config, mount);
            using (d)
            {
                var stat = await d.StatAsync(rel, ct);
                if (stat == null || stat.IsDir || stat.Size > 150L * 1024 * 1024) return Results.NotFound();
                var kind = DmOffice.Gallery.GalleryFolders.KindOf(stat.Name);
                if (kind != "image") return Results.NotFound();
                var stamp = DmOffice.Gallery.GalleryProxies.Stamp(stat.Size, new DateTimeOffset(DateTime.SpecifyKind(stat.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
                // Server-Ordner: für alle dieselben Dateien, also ein gemeinsamer Zwischenspeicher
                var scope = DmOffice.Gallery.GalleryFolders.SharedMount(config, mount) ? DmOffice.Gallery.GalleryProxies.SharedScope : space.User.Id;
                var data = Thumbs?.Get(scope, mount, rel, stamp);
                if (data == null)
                {
                    // Höchstens zwei zugleich: viele große Dateien in einem Ordner sollen den Speicher nicht füllen
                    await ThumbGate.WaitAsync(ct);
                    try
                    {
                        await using var s = await d.OpenReadAsync(rel, ct);
                        var m = new MemoryStream((int)Math.Min(stat.Size, int.MaxValue));
                        await s.CopyToAsync(m, ct);
                        var bytes = m.ToArray();
                        data = Dav.Thumbs.IsRaw(rel) ? Dav.Thumbs.FromRaw(bytes, Dav.Thumbs.Small) : Dav.Thumbs.FromBytes(bytes, Dav.Thumbs.Small);
                    }
                    finally { ThumbGate.Release(); }
                    if (data == null) return Results.NotFound();
                    Thumbs?.Put(scope, mount, rel, stamp, data);
                }
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(data, "image/webp");
            }
        });

        // Herunterladen: als Datei (fremder Inhalt - nie als Seite in dmOffice anzeigen); Bilder, PDF, Ton, Video auf Wunsch inline
        api.MapGet("/download", async (string mount, string path, bool? show, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var (d, _) = Open(http.Space(), config, mount);
            var rel = StoragePath.Clean(path);
            var stat = await d.StatAsync(rel, ct);
            if (stat == null || stat.IsDir) { d.Dispose(); return Results.NotFound(); }
            var mime = DavServer.Mime(rel);
            var inline = show == true && FilesApi.Inline(mime);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (inline && mime != "application/pdf") http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";
            http.Response.Headers.CacheControl = "private, no-cache";
            http.Response.RegisterForDispose(d);
            var stream = await d.OpenReadAsync(rel, ct);
            return Results.Stream(stream, inline ? mime : "application/octet-stream", inline ? null : stat.Name, enableRangeProcessing: stream.CanSeek);
        });

        api.MapPost("/upload", async (string mount, string? path, HttpRequest request, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var (d, ro) = Open(http.Space(), config, mount);
            using (d)
            {
                if (ro) return Results.Json(new { error = "Dieser Ordner ist nur zum Lesen." }, statusCode: 403);
                var dir = StoragePath.Clean(path);
                var names = new List<string>();
                await foreach (var file in MultipartUpload.Files(request, ct))
                {
                    var name = System.IO.Path.GetFileName(file.FileName.Replace('\\', '/'));
                    await d.WriteAsync(StoragePath.Join(dir, StoragePath.Clean(name)), file.Body, ct);
                    names.Add(name);
                }
                Changed(http.Space(), mount, dir);
                return Results.Ok(new { files = names });
            }
        });

        api.MapPost("/mkdir", async (PathBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var (d, ro) = Open(http.Space(), config, body.Mount);
            using (d)
            {
                if (ro) return Results.Json(new { error = "Dieser Ordner ist nur zum Lesen." }, statusCode: 403);
                await d.MkdirAsync(StoragePath.Clean(body.Path), ct);
                Changed(http.Space(), body.Mount, StoragePath.Clean(body.Path));
                return Results.Ok();
            }
        });

        api.MapPost("/move", async (MoveBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var (d, ro) = Open(http.Space(), config, body.Mount);
            using (d)
            {
                if (ro) return Results.Json(new { error = "Dieser Ordner ist nur zum Lesen." }, statusCode: 403);
                if (await d.StatAsync(body.To, ct) != null) return Results.Conflict(new { error = "Unter diesem Namen gibt es schon etwas." });
                await d.MoveAsync(StoragePath.Clean(body.From), StoragePath.Clean(body.To), ct);
                Changed(http.Space(), body.Mount, StoragePath.Clean(body.From));
                Changed(http.Space(), body.Mount, StoragePath.Clean(body.To));
                return Results.Ok();
            }
        });

        api.MapPost("/copy", async (CopyBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var (d, ro) = Open(http.Space(), config, body.Mount);
            using (d)
            {
                if (ro) return Results.Json(new { error = "Dieser Ordner ist nur zum Lesen." }, statusCode: 403);
                var to = StoragePath.Clean(body.To);
                foreach (var p in body.Paths)
                {
                    var from = StoragePath.Clean(p);
                    if (from.Length == 0) continue;
                    if (to == from || to.StartsWith(from + "/")) return Results.BadRequest(new { error = "Ein Ordner lässt sich nicht in sich selbst kopieren." });
                    var name = from[(from.LastIndexOf('/') + 1)..];
                    var target = StoragePath.Join(to, name);
                    if (await d.StatAsync(target, ct) != null) return Results.Conflict(new { error = $"In diesem Ordner gibt es „{name}“ schon." });
                    await CopyAsync(d, from, target, ct);
                }
                Changed(http.Space(), body.Mount, to);
                return Results.Ok();
            }
        });

        api.MapPost("/delete", async (DeleteBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var (d, ro) = Open(http.Space(), config, body.Mount);
            using (d)
            {
                if (ro) return Results.Json(new { error = "Dieser Ordner ist nur zum Lesen." }, statusCode: 403);
                foreach (var p in body.Paths) { await d.DeleteAsync(StoragePath.Clean(p), ct); Changed(http.Space(), body.Mount, StoragePath.Clean(p)); }
                return Results.Ok();
            }
        });
    }
}
