using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Dav;

/// <summary>
/// CalDAV (RFC 4791), CardDAV (RFC 6352), WebDAV (RFC 4918) und
/// Sync-Collection (RFC 6578) unter /dav - fuer DAVx⁵, iOS/macOS,
/// Thunderbird und notizi. Anmeldung mit App-Passwort.
///
///   /dav/principals/&lt;login&gt;/
///   /dav/calendars/&lt;login&gt;/&lt;kalender&gt;/&lt;objekt&gt;.ics
///   /dav/addressbooks/&lt;login&gt;/&lt;buch&gt;/&lt;objekt&gt;.vcf
///   /dav/files/&lt;login&gt;/&lt;pfad&gt;
///
/// Die Dateiablage auch unter den Adressen von Nextcloud, fuer deren Sync-Clients
/// (siehe NextcloudApi): /remote.php/dav/files/&lt;login&gt;/&lt;pfad&gt; und /remote.php/webdav/&lt;pfad&gt;.
/// </summary>
public static class DavServer
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CR = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";
    private static readonly XNamespace ICAL = "http://apple.com/ns/ical/";
    private static readonly XNamespace OC = "http://owncloud.org/ns";
    private static readonly XNamespace NC = "http://nextcloud.org/ns";

    public static void Map(WebApplication app)
    {
        app.MapMethods("/.well-known/caldav", new[] { "GET", "PROPFIND", "OPTIONS", "HEAD" }, (HttpContext h) => Results.Redirect("/dav/", permanent: true));
        app.MapMethods("/.well-known/carddav", new[] { "GET", "PROPFIND", "OPTIONS", "HEAD" }, (HttpContext h) => Results.Redirect("/dav/", permanent: true));
        app.Map("/dav", Handle).RequireAuthorization("dav");
        app.Map("/dav/{**rest}", Handle).RequireAuthorization("dav");
        app.Map("/remote.php/dav", Handle).RequireAuthorization("dav");
        app.Map("/remote.php/dav/{**rest}", Handle).RequireAuthorization("dav");
        app.Map("/remote.php/webdav", Handle).RequireAuthorization("dav");
        app.Map("/remote.php/webdav/{**rest}", Handle).RequireAuthorization("dav");
    }

    // ==================================================================
    //  Adressen
    // ==================================================================

    private enum Kind { Root, PrincipalsRoot, Principal, HomeRoot, CalendarHome, Calendar, CalendarObject, BookHome, Book, BookObject, FilesRoot, Files }

    /// <param name="FilesBase">Adresse der Dateiwurzel des Benutzers, wie angefragt (/dav/files/&lt;login&gt;/ oder unter /remote.php).</param>
    private sealed record Target(Kind Kind, string? Login = null, Collection? Coll = null, string? Name = null, string FilePath = "", string FilesBase = "",
        Shared? Sh = null);

    /// <summary>
    /// Ein mit dem Benutzer geteilter Kalender: die Daten liegen in der Datenbank des Besitzers (Owner), die Sammlung
    /// heisst bei dem Benutzer "shared-&lt;Freigabe&gt;" und "Name (von Besitzer)". Coll in Target traegt diese Adresse
    /// und diesen Namen, aber die Id der Sammlung beim Besitzer.
    /// </summary>
    private sealed record Shared(Share Share, UserSpace Owner, bool ReadOnly);

    /// <summary>Datenbank, in der die Sammlung des Ziels liegt: die eigene oder die des Besitzers.</summary>
    private static UserDb DbOf(UserSpace space, Target t) => t.Sh?.Owner.Db ?? space.Db;

    /// <summary>Schreiben verboten: abonniert oder nur lesend geteilt.</summary>
    private static bool ReadOnlyFor(UserSpace space, Target t) =>
        t.Coll != null && (t.Sh?.ReadOnly == true || Subscriptions.IsSubscribed(DbOf(space, t), t.Coll.Id));

    public const string SharedPrefix = "shared-";

    /// <summary>Die geteilten Kalender dieses Benutzers in der Form, wie sein Programm sie sieht.</summary>
    private static IEnumerable<(Collection Coll, Shared Sh)> SharedCalendars(HttpContext http, UserSpace space) =>
        SharedLists.For(http.RequestServices.GetRequiredService<ShareStore>(), http.RequestServices.GetRequiredService<Spaces>(), space.User.Id)
            .Select(x => (Present(x.Coll, x.Share, x.Owner), new Shared(x.Share, x.Owner, x.Share.ReadOnly.Contains(space.User.Id))));

    private static Collection Present(Collection c, Share share, UserSpace owner) =>
        c with { Uri = SharedPrefix + share.Id, Name = SharedLists.DisplayName(c, owner), SortOrder = 1000 + c.SortOrder };

    private static (Collection? Coll, Shared? Sh) FindShared(HttpContext http, UserSpace space, string uri)
    {
        if (!uri.StartsWith(SharedPrefix, StringComparison.Ordinal) || !int.TryParse(uri[SharedPrefix.Length..], out var id)) return (null, null);
        var share = http.RequestServices.GetRequiredService<ShareStore>().ById(id);
        if (share == null || !share.Members.Contains(space.User.Id)) return (null, null);
        UserSpace owner;
        try { owner = http.RequestServices.GetRequiredService<Spaces>().Get(share.Owner); } catch { return (null, null); }
        var c = PimStore.Find(owner.Db, share.Collection);
        return c == null ? (null, null) : (Present(c, share, owner), new Shared(share, owner, share.ReadOnly.Contains(space.User.Id)));
    }

    /// <summary>Unter welcher Adresse die Anfrage kam: /dav, /remote.php/dav oder /remote.php/webdav.</summary>
    private enum Flavor { Dav, RemoteDav, RemoteWebdav }

    private static bool HiddenForNextcloud(UserSpace space, string path) => FileStore.InRoot(path, space.Settings.Notizi.Root);

    /// <summary>Abgleichordner von notizi fuer diesen Zugang ausblenden? Fuer Nextcloud-Clients immer; ueber /dav,
    /// sobald es einen eigenen notizi-Zugang gibt, fuer alle anderen App-Passwoerter.</summary>
    private static bool HiddenFor(HttpContext http, UserSpace space, string path, bool nextcloud) =>
        FileStore.InRoot(path, space.Settings.Notizi.Root)
        // jeder Zugang ausser dem notizi-Zugang selbst (auch App-Passwoerter mit Bereich "dav" aus dem Login-Flow)
        && (nextcloud || (Scope(http) != AppPasswords.ScopeNotizi && AppPasswords.HasNotiziScope(space.Db)));

    private static string? Scope(HttpContext http) => http.User.FindFirst(Auth.ScopeClaim)?.Value;

    private static (Flavor, List<string>) Split(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segs.Count >= 2 && segs[0] == "remote.php")
            return segs[1] == "webdav" ? (Flavor.RemoteWebdav, segs.Skip(2).ToList()) : (Flavor.RemoteDav, segs.Skip(2).ToList());
        return (Flavor.Dav, segs.Skip(1).ToList());
    }

    private static (Flavor, List<string>) Segments(HttpContext http) =>
        // Request.Path ist schon dekodiert - nicht noch einmal (sonst wird aus %2520 ein Leerzeichen)
        Split(http.Request.Path.Value ?? "/");

    private static Target? Resolve(HttpContext http, UserSpace space, (Flavor Flavor, List<string> Segs) a)
    {
        var s = a.Segs;
        var login = space.User.Login;
        switch (a.Flavor)
        {
            // Wie Nextcloud: nur die Dateiablage, die Wurzel /remote.php/webdav/ ist die des Benutzers.
            // Den Abgleichordner von notizi sehen Nextcloud-Clients nicht: er gehoert notizi (verschluesselt,
            // eigener Abgleich je Notiz) - ein Datei-Sync daneben wuerde nur Konfliktkopien hineinlegen.
            case Flavor.RemoteWebdav:
            {
                var path = FileStore.Normalize(string.Join("/", s));
                return HiddenForNextcloud(space, path) ? null : new Target(Kind.Files, login, FilePath: path, FilesBase: "/remote.php/webdav/");
            }
            case Flavor.RemoteDav:
            {
                if (s.Count == 0 || s[0] != "files") return null;
                if (s.Count >= 2 && s[1] != login) return null;
                if (s.Count == 1) return new Target(Kind.FilesRoot, FilesBase: $"/remote.php/dav/files/{Esc(login)}/");
                var path = FileStore.Normalize(string.Join("/", s.Skip(2)));
                return HiddenForNextcloud(space, path) ? null : new Target(Kind.Files, s[1], FilePath: path, FilesBase: $"/remote.php/dav/files/{Esc(login)}/");
            }
        }
        if (s.Count == 0) return new Target(Kind.Root);
        var top = s[0];
        if (s.Count >= 2 && s[1] != space.User.Login) return null;
        switch (top)
        {
            case "principals":
                return s.Count == 1 ? new Target(Kind.PrincipalsRoot) : s.Count == 2 ? new Target(Kind.Principal, s[1]) : null;
            case "calendars":
            case "addressbooks":
            {
                bool cal = top == "calendars";
                if (s.Count == 1) return new Target(Kind.HomeRoot, Name: top);
                if (s.Count == 2) return new Target(cal ? Kind.CalendarHome : Kind.BookHome, s[1]);
                var (shColl, sh) = cal ? FindShared(http, space, s[2]) : (null, null);
                var coll = shColl ?? PimStore.Find(space.Db, cal ? PimStore.Calendar : PimStore.AddressBook, s[2]);
                if (s.Count == 3) return new Target(cal ? Kind.Calendar : Kind.Book, s[1], coll, s[2], Sh: sh);
                if (s.Count == 4 && coll != null) return new Target(cal ? Kind.CalendarObject : Kind.BookObject, s[1], coll, s[3], Sh: sh);
                return null;
            }
            case "files":
                if (s.Count == 1) return new Target(Kind.FilesRoot, FilesBase: $"/dav/files/{Esc(login)}/");
                return new Target(Kind.Files, s[1], FilePath: FileStore.Normalize(string.Join("/", s.Skip(2))), FilesBase: $"/dav/files/{Esc(login)}/");
        }
        return null;
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);
    private static string PrincipalHref(string login) => $"/dav/principals/{Esc(login)}/";
    private static string CalHome(string login) => $"/dav/calendars/{Esc(login)}/";
    private static string BookHome(string login) => $"/dav/addressbooks/{Esc(login)}/";
    private static string CollHref(Collection c, string login) => (c.Kind == PimStore.Calendar ? CalHome(login) : BookHome(login)) + Esc(c.Uri) + "/";
    private static string FileHref(string filesBase, FileEntry f) =>
        filesBase + string.Join("/", f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Esc)) + (f.IsDir && f.Path.Length > 0 ? "/" : "");

    /// <summary>Ziel aus dem Destination-Kopf (absolute oder relative Adresse).</summary>
    private static (Flavor, List<string>)? DestinationSegments(HttpContext http)
    {
        var dest = http.Request.Headers["Destination"].ToString();
        if (dest.Length == 0) return null;
        var path = Uri.TryCreate(dest, UriKind.Absolute, out var abs) ? abs.AbsolutePath : dest;
        // Einzeln dekodieren: ein %2F im Namen bleibt so Teil des Namens
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segs.Count >= 2 && segs[0] == "remote.php" && segs[1] is "dav" or "webdav")
            return (segs[1] == "webdav" ? Flavor.RemoteWebdav : Flavor.RemoteDav, segs.Skip(2).ToList());
        if (segs.Count == 0 || segs[0] != "dav") return null;
        return (Flavor.Dav, segs.Skip(1).ToList());
    }

    // ==================================================================
    //  Verteiler
    // ==================================================================

    /// <summary>Mitschnitt fuer die Fehlersuche mit fremden Programmen: DMOFFICE_DAV_LOG=1.
    /// Kalender, Adressbuecher und Principal - nicht die Dateiablage, kein Passwort.</summary>
    private static readonly bool LogDav = Environment.GetEnvironmentVariable("DMOFFICE_DAV_LOG") is "1" or "true";

    private static async Task Handle(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        if (!LogDav || path.StartsWith("/dav/files/") || path.StartsWith("/remote.php/")) { await HandleCore(http); return; }

        var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Dav");
        http.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true)) body = await reader.ReadToEndAsync();
        http.Request.Body.Position = 0;
        log.LogInformation("DAV >> {Method} {Path} Depth={Depth} UA={Agent}\n{Body}", http.Request.Method, path,
            http.Request.Headers["Depth"].ToString(), http.Request.Headers.UserAgent.ToString(), Cut(body, 4000));

        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try { await HandleCore(http); }
        finally
        {
            buffer.Position = 0;
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            log.LogInformation("DAV << {Status} {Method} {Path}\n{Body}", http.Response.StatusCode, http.Request.Method, path, Cut(text, 12000));
            buffer.Position = 0;
            http.Response.Body = original;
            await buffer.CopyToAsync(original);
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + $"… (+{s.Length - max} Zeichen)";

    private static async Task HandleCore(HttpContext http)
    {
        var space = http.Space();
        PimStore.EnsureDefaults(space.Db);
        EnsureNotiziRoot(space);
        var method = http.Request.Method.ToUpperInvariant();

        http.Response.Headers["DAV"] = "1, 2, 3, calendar-access, addressbook, extended-mkcol";
        try
        {
            // Im try: ein ungueltiger Pfad (FileStore.Normalize) gibt 400 statt 500
            var segments = Segments(http);
            var target = Resolve(http, space, segments);
            // notizi-Zugang: ausschliesslich der Abgleichordner von notizi
            if (Scope(http) == AppPasswords.ScopeNotizi && http.Request.Method != "OPTIONS"
                && !(target is { Kind: Kind.Files } && FileStore.InRoot(target.FilePath, space.Settings.Notizi.Root)))
            {
                http.Response.StatusCode = 403;
                return;
            }
            // Alle anderen Zugaenge: den Ordner gibt es fuer sie nicht, sobald notizi einen eigenen Zugang hat
            if (target is { Kind: Kind.Files } && segments.Item1 == Flavor.Dav && HiddenFor(http, space, target.FilePath, false))
                target = null;
            if (target == null)
            {
                http.Response.StatusCode = segments.Item1 != Flavor.RemoteWebdav && segments.Item2 is { Count: >= 2 } s && s[1] != space.User.Login ? 403 : 404;
                return;
            }

            // Dateiablage aus: nur der notizi-Ordner, dazu die Wurzel zum Durchsuchen (zeigt nur ihn)
            if (target.Kind == Kind.Files && FileStore.FilesOff(space.Db) && !FileStore.InRoot(target.FilePath!, space.Settings.Notizi.Root)
                && !(target.FilePath == "" && method is "PROPFIND" or "OPTIONS"))
            {
                http.Response.StatusCode = 403;
                return;
            }

            switch (method)
            {
                case "OPTIONS":
                    http.Response.Headers.Allow = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, REPORT, MKCOL, MKCALENDAR, MOVE, COPY, LOCK, UNLOCK";
                    http.Response.StatusCode = 200;
                    return;
                case "PROPFIND": await Propfind(http, space, target); return;
                // Einen geteilten Kalender loeschen heisst: nicht mehr teilnehmen (der Besitzer behaelt ihn)
                case "DELETE" when target is { Kind: Kind.Calendar, Sh: not null }:
                    http.RequestServices.GetRequiredService<ShareStore>().Leave(target.Sh.Share.Id, space.User.Id);
                    http.Response.StatusCode = 204; return;
                // Name und Farbe eines geteilten Kalenders bestimmt der Besitzer
                case "PROPPATCH" when target.Sh != null:
                    http.Response.StatusCode = 403; return;
                // Abonnierte und nur lesend geteilte Kalender sind nur lesbar - auch fuer Handy und Thunderbird
                case "PROPPATCH" or "PUT" or "DELETE" or "MOVE" when ReadOnlyFor(space, target):
                    http.Response.StatusCode = 403; return;
                case "PROPPATCH": await Proppatch(http, space, target); return;
                case "REPORT": await Report(http, space, target); return;
                case "GET":
                case "HEAD": await Get(http, space, target, method == "HEAD"); return;
                case "PUT": await Put(http, space, target); return;
                case "DELETE": Delete(http, space, target); return;
                case "MKCOL":
                case "MKCALENDAR": await Mkcol(http, space, target, method == "MKCALENDAR"); return;
                case "MOVE":
                case "COPY": await MoveCopy(http, space, target, method == "COPY"); return;
                case "LOCK": await Lock(http, target); return;
                case "UNLOCK": http.Response.StatusCode = 204; return;
                default: http.Response.StatusCode = 405; return;
            }
        }
        catch (PreconditionFailedException) { http.Response.StatusCode = 412; }
        catch (DavConflictException) { http.Response.StatusCode = 409; }
        catch (DavForbiddenException) { http.Response.StatusCode = 403; }
        catch (UnauthorizedAccessException) { http.Response.StatusCode = 403; }
        catch (QuotaExceededException) { http.Response.StatusCode = 507; }
        catch (KeyNotFoundException) { http.Response.StatusCode = 404; }
        catch (ArgumentException ex)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsync(ex.Message);
        }
        catch (InvalidOperationException) { http.Response.StatusCode = 405; }
        catch (System.Xml.XmlException) { http.Response.StatusCode = 400; }
    }

    /// <summary>
    /// Die Wurzel fuer notizi (Einstellungen -> Notizen, Vorgabe "notizisync") gibt es immer - notizi legt sie
    /// bewusst nicht selbst an, und ohne sie meldet "Pruefen" dort nur 404.
    /// </summary>
    private static void EnsureNotiziRoot(UserSpace space)
    {
        var root = FileStore.Normalize(space.Settings.Notizi.Root);
        if (root.Length > 0 && FileStore.Get(space.Db, root) == null)
            try { FileStore.EnsureDir(space.Db, root); } catch (DavConflictException) { /* dort liegt eine Datei */ }
    }

    // ==================================================================
    //  PROPFIND
    // ==================================================================

    /// <summary>Was ein Programm erfragt: alle Eigenschaften, nur ihre Namen, oder eine Liste.</summary>
    private sealed record PropRequest(bool All, bool NamesOnly, List<XName> Names);

    private static async Task<XDocument?> Body(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return XDocument.Parse(text);
    }

    private static PropRequest ParseProps(XElement? root)
    {
        if (root == null || root.Element(D + "allprop") != null) return new PropRequest(true, false, new());
        if (root.Element(D + "propname") != null) return new PropRequest(false, true, new());
        var prop = root.Element(D + "prop");
        return new PropRequest(false, false, prop?.Elements().Select(e => e.Name).ToList() ?? new());
    }

    private static async Task Propfind(HttpContext http, UserSpace space, Target t)
    {
        var doc = await Body(http);
        var req = ParseProps(doc?.Root);
        var depthHeader = http.Request.Headers["Depth"].ToString();
        int depth = depthHeader == "0" ? 0 : depthHeader == "1" ? 1 : (t.Kind == Kind.Files ? 1 : 1);   // infinity wie 1: genuegt jedem Programm

        var responses = new List<XElement>();
        var self = Describe(http, space, t);
        if (self == null) { http.Response.StatusCode = 404; return; }
        responses.Add(Response(self, req));
        if (depth >= 1)
            foreach (var child in Members(http, space, t)) responses.Add(Response(child, req));
        await Multistatus(http, responses);
    }

    /// <summary>Eine Ressource mit allen Eigenschaften, die sie hat.</summary>
    private sealed record Resource(string Href, Dictionary<XName, object?> Props);

    private static XElement Response(Resource r, PropRequest req)
    {
        var found = new List<XElement>();
        var missing = new List<XElement>();
        IEnumerable<XName> names = req.All || req.NamesOnly ? r.Props.Keys : req.Names;
        foreach (var name in names)
        {
            if (r.Props.TryGetValue(name, out var value))
            {
                if (req.NamesOnly) found.Add(new XElement(name));
                else if (!req.All || name != C + "calendar-data" && name != CR + "address-data")
                    found.Add(value is XElement el && el.Name == name ? el : new XElement(name, value));
            }
            else missing.Add(new XElement(name));
        }
        var response = new XElement(D + "response", new XElement(D + "href", r.Href));
        if (found.Count > 0) response.Add(PropStat(found, 200));
        if (missing.Count > 0) response.Add(PropStat(missing, 404));
        if (found.Count == 0 && missing.Count == 0) response.Add(PropStat(new List<XElement>(), 200));
        return response;
    }

    private static XElement PropStat(List<XElement> props, int status) =>
        new(D + "propstat", new XElement(D + "prop", props), new XElement(D + "status", $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}"));

    private static async Task Multistatus(HttpContext http, IEnumerable<XElement> responses, string? syncToken = null)
    {
        // Praefixe wie Nextcloud (sabre/dav): manche Apps lesen nach Praefix statt nach Namespace (Business Calendar: x1:calendar-color)
        var ms = new XElement(D + "multistatus",
            new XAttribute(XNamespace.Xmlns + "d", D), new XAttribute(XNamespace.Xmlns + "cal", C),
            new XAttribute(XNamespace.Xmlns + "card", CR), new XAttribute(XNamespace.Xmlns + "cs", CS),
            new XAttribute(XNamespace.Xmlns + "x1", ICAL), new XAttribute(XNamespace.Xmlns + "x2", "http://nextcloud.com/ns"),
            new XAttribute(XNamespace.Xmlns + "oc", OC), new XAttribute(XNamespace.Xmlns + "nc", NC), responses);
        if (syncToken != null) ms.Add(new XElement(D + "sync-token", syncToken));
        http.Response.StatusCode = 207;
        http.Response.ContentType = "application/xml; charset=utf-8";
        // Auch die Schreibweise wie sabre/dav: leere Elemente ohne Leerzeichen ("<d:displayname/>").
        // " />" kommt nur in Tags vor - im Text maskiert XmlWriter das ">".
        await http.Response.WriteAsync("<?xml version=\"1.0\"?>\n" + ms.ToString(SaveOptions.DisableFormatting).Replace(" />", "/>"));
    }

    private static object Href(string h) => new XElement(D + "href", h);

    private static XElement Privileges(bool write) => new(D + "current-user-privilege-set",
        new XElement(D + "privilege", new XElement(D + "read")),
        write ? new XElement(D + "privilege", new XElement(D + "all")) : null,
        write ? new XElement(D + "privilege", new XElement(D + "write")) : null,
        write ? new XElement(D + "privilege", new XElement(D + "write-content")) : null,
        write ? new XElement(D + "privilege", new XElement(D + "write-properties")) : null,
        write ? new XElement(D + "privilege", new XElement(D + "bind")) : null,
        write ? new XElement(D + "privilege", new XElement(D + "unbind")) : null,
        new XElement(D + "privilege", new XElement(D + "read-current-user-privilege-set")));

    private static Dictionary<XName, object?> Base(UserSpace space, string displayName, params XElement[] types)
    {
        var login = space.User.Login;
        return new Dictionary<XName, object?>
        {
            [D + "displayname"] = displayName,
            [D + "resourcetype"] = new XElement(D + "resourcetype", new XElement(D + "collection"), types),
            [D + "current-user-principal"] = Href(PrincipalHref(login)),
            [D + "principal-URL"] = Href(PrincipalHref(login)),
            [D + "owner"] = Href(PrincipalHref(login)),
            // Manche Programme fragen danach schon an der Wurzel statt am Principal
            [C + "calendar-home-set"] = Href(CalHome(login)),
            [CR + "addressbook-home-set"] = Href(BookHome(login)),
            [D + "current-user-privilege-set"] = Privileges(true),
            [D + "supported-report-set"] = new XElement(D + "supported-report-set",
                new[] { D + "sync-collection", C + "calendar-multiget", C + "calendar-query", CR + "addressbook-multiget", CR + "addressbook-query", D + "expand-property" }
                    .Select(n => new XElement(D + "supported-report", new XElement(D + "report", new XElement(n)))))
        };
    }

    private static Resource? Describe(HttpContext http, UserSpace space, Target t)
    {
        var login = space.User.Login;
        switch (t.Kind)
        {
            case Kind.Root:
                return new Resource("/dav/", Base(space, "dmOffice"));
            case Kind.PrincipalsRoot:
                return new Resource("/dav/principals/", Base(space, "Benutzer"));
            case Kind.Principal:
            {
                var p = Base(space, space.User.DisplayName, new XElement(D + "principal"));
                p[C + "calendar-home-set"] = Href(CalHome(login));
                p[CR + "addressbook-home-set"] = Href(BookHome(login));
                p[C + "calendar-user-address-set"] = new XElement(C + "calendar-user-address-set",
                    space.Settings.MailAccounts.Select(a => new XElement(D + "href", "mailto:" + a.Email)),
                    new XElement(D + "href", PrincipalHref(login)));
                p[CR + "principal-address"] = null;
                p[C + "schedule-inbox-URL"] = null;
                return new Resource(PrincipalHref(login), p.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));
            }
            case Kind.HomeRoot:
                return new Resource($"/dav/{t.Name}/", Base(space, t.Name!));
            // Ohne Anzeigenamen wie bei Nextcloud: Manche Apps (Business Calendar) halten den
            // Oberordner sonst fuer einen eigenen Kalender namens "Kalender"
            case Kind.CalendarHome:
                return new Resource(CalHome(login), WithoutName(Base(space, "")));
            case Kind.BookHome:
                return new Resource(BookHome(login), WithoutName(Base(space, "")));
            case Kind.Calendar:
            case Kind.Book:
                return t.Coll == null ? null : DescribeCollection(space, t.Coll, t.Sh);
            case Kind.CalendarObject:
            case Kind.BookObject:
            {
                var obj = PimStore.Object(DbOf(space, t), t.Coll!.Id, t.Name!);
                return obj == null ? null : DescribeObject(space, t.Coll, obj.Uri, obj.Etag, obj.Data.Length, obj.Modified, obj.Data, ReadOnlyFor(space, t));
            }
            case Kind.FilesRoot:
                return new Resource(t.FilesBase[..(t.FilesBase.TrimEnd('/').LastIndexOf('/') + 1)], Base(space, "Dateien"));
            case Kind.Files:
            {
                var loc = SharedFiles.Resolve(http, t.FilePath);
                if (loc.Virtual) return DescribeShared(http, space, t.FilesBase);
                var f = FileStore.Get(loc.Db, loc.Path);
                return f == null ? null : DescribeFile(http, space, loc, f, t.FilesBase);
            }
        }
        return null;
    }

    private static Dictionary<XName, object?> WithoutName(Dictionary<XName, object?> p) { p.Remove(D + "displayname"); return p; }

    private static Resource DescribeCollection(UserSpace space, Collection c, Shared? sh = null)
    {
        bool cal = c.Kind == PimStore.Calendar;
        var p = Base(space, c.Name, cal ? new XElement(C + "calendar") : new XElement(CR + "addressbook"));
        if (cal && (sh?.ReadOnly == true || Subscriptions.IsSubscribed(sh?.Owner.Db ?? space.Db, c.Id))) p[D + "current-user-privilege-set"] = Privileges(false);
        // Geteilt: der Besitzer ist der Besitzer
        if (sh != null) p[D + "owner"] = Href(PrincipalHref(sh.Owner.User.Login));
        // ctag gleich dem sync-token, wie Nextcloud - manche Apps vergleichen beides
        p[CS + "getctag"] = SyncToken(c);
        p[D + "sync-token"] = SyncToken(c);
        p[D + "getetag"] = "\"" + c.SyncToken + "\"";
        if (cal)
        {
            p[C + "supported-calendar-component-set"] = new XElement(C + "supported-calendar-component-set",
                c.Components.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => new XElement(C + "comp", new XAttribute("name", x))));
            p[C + "supported-calendar-data"] = new XElement(C + "supported-calendar-data",
                new XElement(C + "calendar-data", new XAttribute("content-type", "text/calendar"), new XAttribute("version", "2.0")));
            p[C + "calendar-description"] = c.Description ?? "";
            p[ICAL + "calendar-color"] = c.Color ?? "#0F6CBD";
            p[ICAL + "calendar-order"] = c.SortOrder.ToString();
            p[C + "max-resource-size"] = (20 * 1024 * 1024).ToString();
            p[C + "calendar-timezone"] = null;
        }
        else
        {
            p[CR + "addressbook-description"] = c.Description ?? "";
            p[CR + "supported-address-data"] = new XElement(CR + "supported-address-data",
                new XElement(CR + "address-data-type", new XAttribute("content-type", "text/vcard"), new XAttribute("version", "3.0")),
                new XElement(CR + "address-data-type", new XAttribute("content-type", "text/vcard"), new XAttribute("version", "4.0")));
            p[CR + "max-resource-size"] = (20 * 1024 * 1024).ToString();
        }
        return new Resource(CollHref(c, space.User.Login), p.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>Sync-Token im Format von sabre/dav (Nextcloud): eine Zahl je Sammlung. Business Calendar
    /// liest die Zahl am Ende und verwirft Sammlungen mit anderem Aufbau. Das fruehere Format
    /// "http://dmoffice/sync/&lt;id&gt;/&lt;n&gt;" gespeicherter Clients wird weiter angenommen.</summary>
    private static string SyncToken(Collection c) => $"http://sabre.io/ns/sync/{c.SyncToken}";

    private static long? ParseSyncToken(string text, long collectionId)
    {
        var parts = text.TrimEnd('/').Split('/');
        if (!long.TryParse(parts[^1], out var n)) return null;
        if (text.StartsWith("http://sabre.io/ns/sync/")) return n;
        if (text.StartsWith("http://dmoffice/sync/") && parts.Length >= 2 && parts[^2] == collectionId.ToString()) return n;
        return null;
    }

    private static Resource DescribeObject(UserSpace space, Collection c, string uri, string etag, long size, string modified, string? data, bool readOnly = false)
    {
        bool cal = c.Kind == PimStore.Calendar;
        var p = new Dictionary<XName, object?>
        {
            [D + "getetag"] = etag,
            [D + "getcontenttype"] = cal ? "text/calendar; charset=utf-8; component=vevent" : "text/vcard; charset=utf-8",
            [D + "getcontentlength"] = size.ToString(),
            [D + "getlastmodified"] = modified,
            [D + "resourcetype"] = new XElement(D + "resourcetype"),
            [D + "displayname"] = uri,
            [D + "current-user-privilege-set"] = Privileges(!readOnly),
            [D + "owner"] = Href(PrincipalHref(space.User.Login)),
        };
        if (data != null)
        {
            if (cal) p[C + "calendar-data"] = new XElement(C + "calendar-data", data);
            else p[CR + "address-data"] = new XElement(CR + "address-data", data);
        }
        return new Resource(CollHref(c, space.User.Login) + Esc(uri), p);
    }

    /// <summary>Nummer der Datei wie bei Nextcloud: oc:id ist sie mit Instanzkennung, OC-FileId im Kopf ebenso.</summary>
    public static string OcId(FileEntry f) => f.FileId.ToString("D8", CultureInfo.InvariantCulture) + NextcloudApi.InstanceId;

    /// <summary>Rechte wie bei Nextcloud: G lesen, D loeschen, N umbenennen, V verschieben, W schreiben, C/K Datei/Ordner anlegen.</summary>
    /// In einem mit mir geteilten Ordner (S): ohne Bearbeitungsrecht nur lesen, den geteilten Ordner selbst nicht loeschen, umbenennen, verschieben.
    private static string OcPermissions(FileEntry f, FileLoc loc) =>
        !loc.Shared ? (f.IsDir ? "RGDNVCK" : "RGDNVW")
        : !loc.CanWrite ? "SG"
        : loc.IsMountRoot ? "SGCK"
        : f.IsDir ? "SGDNVCK" : "SGDNVW";

    /// <summary>Eine Datei oder ein Ordner; entry ist der Eintrag in der Ablage, in der er liegt (bei geteilten Ordnern die des Besitzers).</summary>
    private static Resource DescribeFile(HttpContext http, UserSpace space, FileLoc loc, FileEntry entry, string filesBase)
    {
        var f = loc.Show(entry);
        var db = loc.Db;
        var p = new Dictionary<XName, object?>
        {
            [D + "displayname"] = f.Path.Length == 0 ? space.User.Login : f.Name,
            [D + "resourcetype"] = f.IsDir ? new XElement(D + "resourcetype", new XElement(D + "collection")) : new XElement(D + "resourcetype"),
            [D + "getetag"] = f.Etag,
            [D + "getlastmodified"] = f.Modified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture),
            [D + "creationdate"] = f.Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            [D + "current-user-principal"] = Href(PrincipalHref(space.User.Login)),
            [D + "current-user-privilege-set"] = Privileges(loc.CanWrite),
            [D + "supportedlock"] = new XElement(D + "supportedlock", new XElement(D + "lockentry",
                new XElement(D + "lockscope", new XElement(D + "exclusive")), new XElement(D + "locktype", new XElement(D + "write")))),
            [D + "lockdiscovery"] = new XElement(D + "lockdiscovery"),
            // Fuer die Sync-Clients von Nextcloud
            [OC + "id"] = OcId(f),
            [OC + "fileid"] = f.FileId.ToString(CultureInfo.InvariantCulture),
            [OC + "permissions"] = OcPermissions(entry, loc),
            [OC + "size"] = FileStore.TreeSize(db, entry).ToString(CultureInfo.InvariantCulture),
            [OC + "owner-id"] = loc.Space.User.Login,
            [OC + "owner-display-name"] = loc.Space.User.DisplayName,
            [OC + "share-types"] = new XElement(OC + "share-types"),
            [NC + "has-preview"] = Thumbs.Available(db, entry) ? "true" : "false",
            [NC + "is-encrypted"] = "0",
        };
        if (!f.IsDir)
        {
            p[D + "getcontentlength"] = f.Size.ToString();
            p[D + "getcontenttype"] = f.Mime ?? "application/octet-stream";
        }
        else if (f.Path.Length == 0)
        {
            // Aendert sich etwas in einem mit mir geteilten Ordner, muss sich auch die Wurzel aendern - sonst steigen
            // Nextcloud-Clients nicht hinab und finden es nie
            if (SharedFiles.Mounts(http) is { Count: > 0 } mounts)
                p[D + "getetag"] = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(f.Etag + SharedFiles.RootEntry(mounts).Etag)))[..16].ToLowerInvariant() + "\"";
            var used = UserStorage.Used(space.Db);
            var quota = FileStore.Quota(space.Db);
            p[D + "quota-used-bytes"] = used.ToString();
            // Ohne Grenze wie Nextcloud: -3 heisst unbegrenzt
            p[D + "quota-available-bytes"] = (quota > 0 ? Math.Max(0, quota - used) : -3).ToString();
        }
        return new Resource(FileHref(filesBase, f), p);
    }

    /// <summary>„Mit mir geteilt“ selbst: nur lesen, darin liegen die geteilten Ordner.</summary>
    private static Resource DescribeShared(HttpContext http, UserSpace space, string filesBase)
    {
        var mounts = SharedFiles.Mounts(http);
        var f = SharedFiles.RootEntry(mounts);
        var p = new Dictionary<XName, object?>
        {
            [D + "displayname"] = f.Name,
            [D + "resourcetype"] = new XElement(D + "resourcetype", new XElement(D + "collection")),
            [D + "getetag"] = f.Etag,
            [D + "getlastmodified"] = f.Modified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture),
            [D + "creationdate"] = f.Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            [D + "current-user-principal"] = Href(PrincipalHref(space.User.Login)),
            [D + "current-user-privilege-set"] = Privileges(false),
            [D + "supportedlock"] = new XElement(D + "supportedlock"),
            [D + "lockdiscovery"] = new XElement(D + "lockdiscovery"),
            [OC + "id"] = OcId(f),
            [OC + "fileid"] = f.FileId.ToString(CultureInfo.InvariantCulture),
            [OC + "permissions"] = "G",
            [OC + "size"] = mounts.Sum(m => FileStore.TreeSize(m.Owner.Db, m.Dir)).ToString(CultureInfo.InvariantCulture),
            [OC + "owner-id"] = space.User.Login,
            [OC + "owner-display-name"] = space.User.DisplayName,
            [OC + "share-types"] = new XElement(OC + "share-types"),
            [NC + "has-preview"] = "false",
            [NC + "is-encrypted"] = "0",
        };
        return new Resource(FileHref(filesBase, f), p);
    }

    private static IEnumerable<Resource> Members(HttpContext http, UserSpace space, Target t)
    {
        var login = space.User.Login;
        switch (t.Kind)
        {
            case Kind.Root:
                yield return new Resource("/dav/principals/", Base(space, "Benutzer"));
                yield return new Resource("/dav/calendars/", Base(space, "calendars"));
                yield return new Resource("/dav/addressbooks/", Base(space, "addressbooks"));
                yield return new Resource("/dav/files/", Base(space, "Dateien"));
                break;
            case Kind.PrincipalsRoot:
                yield return Describe(http, space, new Target(Kind.Principal, login))!;
                break;
            case Kind.HomeRoot:
                yield return Describe(http, space, new Target(t.Name == "calendars" ? Kind.CalendarHome : Kind.BookHome, login))!;
                break;
            case Kind.CalendarHome:
                foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar)) yield return DescribeCollection(space, c);
                foreach (var (c, sh) in SharedCalendars(http, space)) yield return DescribeCollection(space, c, sh);
                break;
            case Kind.BookHome:
                foreach (var c in PimStore.Collections(space.Db, PimStore.AddressBook)) yield return DescribeCollection(space, c);
                break;
            case Kind.Calendar:
            case Kind.Book:
                if (t.Coll != null)
                    foreach (var (uri, etag, size, modified) in PimStore.Index(DbOf(space, t), t.Coll.Id))
                        yield return DescribeObject(space, t.Coll, uri, etag, size, modified, null, ReadOnlyFor(space, t));
                break;
            case Kind.FilesRoot:
                yield return DescribeFile(http, space, new FileLoc(space, "", null), FileStore.Get(space.Db, "")!, t.FilesBase);
                break;
            case Kind.Files:
            {
                var loc = SharedFiles.Resolve(http, t.FilePath);
                if (loc.Virtual)
                {
                    foreach (var m in SharedFiles.Mounts(http)) yield return DescribeFile(http, space, new FileLoc(m.Owner, m.Dir.Path, m), m.Dir, t.FilesBase);
                }
                else if (loc.Shared && FileStore.Get(loc.Db, loc.Path) is { IsDir: true })
                {
                    foreach (var f in FileStore.Children(loc.Db, loc.Path))
                        if (!loc.Protected(f.Path)) yield return DescribeFile(http, space, loc, f, t.FilesBase);
                }
                else if (FileStore.Get(space.Db, t.FilePath) is { IsDir: true })
                {
                    bool off = FileStore.FilesOff(space.Db);
                    bool nextcloud = t.FilesBase.StartsWith("/remote.php/", StringComparison.Ordinal);
                    foreach (var f in FileStore.Children(space.Db, t.FilePath))
                    {
                        if (HiddenFor(http, space, f.Path, nextcloud) || SharedFiles.Hidden(http, f.Path)) continue;
                        if (!off || FileStore.InRoot(f.Path, space.Settings.Notizi.Root)) yield return DescribeFile(http, space, loc, f, t.FilesBase);
                    }
                    // „Mit mir geteilt“ in der Wurzel, solange jemand etwas mit mir teilt
                    if (t.FilePath.Length == 0 && !off && SharedFiles.Mounts(http).Count > 0) yield return DescribeShared(http, space, t.FilesBase);
                }
                break;
            }
        }
    }

    // ==================================================================
    //  PROPPATCH
    // ==================================================================

    private static async Task Proppatch(HttpContext http, UserSpace space, Target t)
    {
        var doc = await Body(http) ?? throw new ArgumentException("Leer.");
        var names = new List<XElement>();
        string? name = null, color = null, description = null;
        foreach (var set in doc.Root!.Elements(D + "set"))
            foreach (var prop in set.Elements(D + "prop").Elements())
            {
                names.Add(new XElement(prop.Name));
                if (prop.Name == D + "displayname") name = prop.Value;
                else if (prop.Name == ICAL + "calendar-color") color = prop.Value;
                else if (prop.Name == C + "calendar-description" || prop.Name == CR + "addressbook-description") description = prop.Value;
            }
        foreach (var rem in doc.Root!.Elements(D + "remove").Elements(D + "prop").Elements()) names.Add(new XElement(rem.Name));

        if (t.Coll != null && t.Kind is Kind.Calendar or Kind.Book)
            PimStore.UpdateCollection(space.Db, t.Coll.Id, name, color, description);
        // Unbekanntes (Zeitzone des Kalenders, Reihenfolge ...) wird angenommen und verworfen - Programme brechen sonst ab
        var href = Describe(http, space, t)?.Href ?? http.Request.Path.Value!;
        await Multistatus(http, new[] { new XElement(D + "response", new XElement(D + "href", href), PropStat(names, 200)) });
    }

    // ==================================================================
    //  REPORT
    // ==================================================================

    private static async Task Report(HttpContext http, UserSpace space, Target t)
    {
        var doc = await Body(http) ?? throw new ArgumentException("Leer.");
        var root = doc.Root!;
        var req = ParseProps(root);
        if (req.All) req = new PropRequest(false, false, new List<XName> { D + "getetag" });

        if (root.Name == C + "calendar-multiget" || root.Name == CR + "addressbook-multiget")
        {
            var responses = new List<XElement>();
            foreach (var href in root.Elements(D + "href").Select(h => h.Value))
            {
                var segs = (Uri.TryCreate(href, UriKind.Absolute, out var a) ? a.AbsolutePath : href)
                    .Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(Uri.UnescapeDataString).ToList();
                var ot = Resolve(http, space, (Flavor.Dav, segs));
                var obj = ot?.Coll != null && ot.Name != null && ot.Kind is Kind.CalendarObject or Kind.BookObject
                    ? PimStore.Object(DbOf(space, ot), ot.Coll.Id, ot.Name) : null;
                if (obj == null)
                    responses.Add(new XElement(D + "response", new XElement(D + "href", href), new XElement(D + "status", "HTTP/1.1 404 Not Found")));
                else
                    responses.Add(Response(DescribeObject(space, ot!.Coll!, obj.Uri, obj.Etag, obj.Data.Length, obj.Modified, obj.Data, ReadOnlyFor(space, ot)), req));
            }
            await Multistatus(http, responses);
            return;
        }

        if (root.Name == C + "calendar-query" || root.Name == CR + "addressbook-query")
        {
            if (t.Coll == null) { http.Response.StatusCode = 404; return; }
            List<DavObject> objects;
            if (root.Name == C + "calendar-query")
            {
                var compFilter = root.Element(C + "filter")?.Element(C + "comp-filter")?.Element(C + "comp-filter");
                var comp = compFilter?.Attribute("name")?.Value ?? "VEVENT";
                var range = compFilter?.Element(C + "time-range");
                DateTime? start = Ics.Time(range?.Attribute("start") is { } s ? new Prop("X", s.Value) : null, TimeZoneInfo.Utc, out _);
                DateTime? end = Ics.Time(range?.Attribute("end") is { } e ? new Prop("X", e.Value) : null, TimeZoneInfo.Utc, out _);
                objects = PimStore.InRange(DbOf(space, t), new[] { t.Coll.Id }, comp, start, end);
            }
            else objects = PimStore.Objects(DbOf(space, t), t.Coll.Id);
            await Multistatus(http, objects.Select(o => Response(DescribeObject(space, t.Coll, o.Uri, o.Etag, o.Data.Length, o.Modified, o.Data, ReadOnlyFor(space, t)), req)));
            return;
        }

        if (root.Name == D + "sync-collection")
        {
            if (t.Coll == null) { http.Response.StatusCode = 403; return; }
            var tokenText = root.Element(D + "sync-token")?.Value ?? "";
            var db = DbOf(space, t);
            // Adresse und Name so, wie der Benutzer sie kennt; Stand des Sync-Tokens frisch aus der Datenbank
            var current = t.Coll with { SyncToken = PimStore.Find(db, t.Coll.Id)!.SyncToken };
            bool ro = ReadOnlyFor(space, t);
            var responses = new List<XElement>();
            if (tokenText.Length == 0)
            {
                foreach (var o in PimStore.Objects(db, current.Id))
                    responses.Add(Response(DescribeObject(space, current, o.Uri, o.Etag, o.Data.Length, o.Modified, o.Data, ro), req));
            }
            else
            {
                var token = ParseSyncToken(tokenText, current.Id);
                var changes = token is long tk && tk <= current.SyncToken ? PimStore.ChangesSince(db, current.Id, tk) : null;
                if (changes == null)
                {
                    http.Response.StatusCode = 403;
                    http.Response.ContentType = "application/xml; charset=utf-8";
                    await http.Response.WriteAsync(new XElement(D + "error", new XAttribute(XNamespace.Xmlns + "d", D), new XElement(D + "valid-sync-token")).ToString());
                    return;
                }
                foreach (var (uri, deleted) in changes)
                {
                    var o = deleted ? null : PimStore.Object(db, current.Id, uri);
                    if (o == null)
                        responses.Add(new XElement(D + "response", new XElement(D + "href", CollHref(current, space.User.Login) + Esc(uri)),
                            new XElement(D + "status", "HTTP/1.1 404 Not Found")));
                    else
                        responses.Add(Response(DescribeObject(space, current, o.Uri, o.Etag, o.Data.Length, o.Modified, o.Data, ro), req));
                }
            }
            await Multistatus(http, responses, SyncToken(current));
            return;
        }

        if (root.Name == D + "expand-property" || root.Name == D + "principal-property-search" || root.Name == D + "principal-search-property-set")
        {
            // iOS fragt so nach Stellvertretern - es gibt keine
            await Multistatus(http, Array.Empty<XElement>());
            return;
        }

        http.Response.StatusCode = 403;
    }

    // ==================================================================
    //  GET, PUT, DELETE
    // ==================================================================

    private static async Task Get(HttpContext http, UserSpace space, Target t, bool head)
    {
        byte[] data;
        string type, etag;
        switch (t.Kind)
        {
            case Kind.CalendarObject:
            case Kind.BookObject:
            {
                var obj = PimStore.Object(DbOf(space, t), t.Coll!.Id, t.Name!) ?? throw new KeyNotFoundException();
                data = Encoding.UTF8.GetBytes(obj.Data);
                type = t.Kind == Kind.CalendarObject ? "text/calendar; charset=utf-8" : "text/vcard; charset=utf-8";
                etag = obj.Etag;
                break;
            }
            case Kind.Files:
            {
                var loc = SharedFiles.Resolve(http, t.FilePath);
                if (loc.Virtual) { await DirListing(http, SharedFiles.Mounts(http).Select(SharedFiles.MountEntry), t.FilesBase); return; }
                var f = FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException();
                if (f.IsDir) { await DirListing(http, FileStore.Children(loc.Db, loc.Path).Where(x => !loc.Protected(x.Path)).Select(loc.Show), t.FilesBase); return; }
                if (http.Request.Headers.IfNoneMatch.ToString() == f.Etag) { http.Response.StatusCode = 304; return; }
                http.Response.Headers["OC-FileId"] = OcId(loc.Show(f));
                http.Response.Headers["OC-ETag"] = f.Etag;
                // Hochgeladenes nie als Seite dieser Anwendung ausfuehren lassen
                http.Response.Headers["Content-Security-Policy"] = "sandbox";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (head)
                {
                    http.Response.ContentType = f.Mime ?? "application/octet-stream";
                    http.Response.Headers.ETag = f.Etag;
                    http.Response.Headers.LastModified = f.Modified.ToUniversalTime().ToString("R");
                    http.Response.Headers.AcceptRanges = "bytes";
                    http.Response.ContentLength = f.Size;
                    return;
                }
                // Gestreamt aus dem BlobStore, mit Bereichen (Range) - Clients setzen abgebrochene Downloads fort, Player spulen
                await Results.Stream(FileStore.OpenRead(loc.Db, loc.Path), f.Mime ?? "application/octet-stream", null,
                    new DateTimeOffset(DateTime.SpecifyKind(f.Modified, DateTimeKind.Utc)),
                    new Microsoft.Net.Http.Headers.EntityTagHeaderValue(f.Etag.StartsWith('"') ? f.Etag : $"\"{f.Etag}\""),
                    enableRangeProcessing: true).ExecuteAsync(http);
                return;
            }
            default:
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync("dmOffice DAV");
                return;
        }
        http.Response.ContentType = type;
        http.Response.Headers.ETag = etag;
        http.Response.ContentLength = data.Length;
        // Hochgeladenes nie als Seite dieser Anwendung ausfuehren lassen
        http.Response.Headers["Content-Security-Policy"] = "sandbox";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!head) await http.Response.Body.WriteAsync(data);
    }

    private static async Task DirListing(HttpContext http, IEnumerable<FileEntry> children, string filesBase)
    {
        var sb = new StringBuilder("<!doctype html><meta charset=utf-8><title>dmOffice</title><ul>");
        foreach (var f in children)
            sb.Append($"<li><a href=\"{FileHref(filesBase, f)}\">{System.Net.WebUtility.HtmlEncode(f.Name)}{(f.IsDir ? "/" : "")}</a></li>");
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        await http.Response.WriteAsync(sb.Append("</ul>").ToString());
    }

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers[name].ToString() is { Length: > 0 } v ? v.Trim() : null;

    private static async Task Put(HttpContext http, UserSpace space, Target t)
    {
        var ifMatch = Header(http, "If-Match");
        var ifNoneMatch = Header(http, "If-None-Match");
        switch (t.Kind)
        {
            case Kind.CalendarObject:
            case Kind.BookObject:
            {
                using var ms = new MemoryStream();
                await http.Request.Body.CopyToAsync(ms);
                var db = DbOf(space, t);
                var old = PimStore.Object(db, t.Coll!.Id, t.Name!);
                bool existed = old != null;
                var data = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
                var etag = PimStore.Put(db, t.Coll, t.Name!, data, ifMatch, ifNoneMatch, space.Zone);
                // Einkaufsliste aus der App (oder einem anderen CalDAV-Programm): neue Artikel in die Vorschlaege der Liste,
                // bei geteilten Listen in die des Besitzers - so wie beim Eintragen im Web
                if (t.Kind == Kind.CalendarObject) Lists.LearnFromIcs(db, t.Coll.Id, old?.Data, data);
                http.Response.Headers.ETag = etag;
                http.Response.StatusCode = existed ? 204 : 201;
                return;
            }
            case Kind.Files:
            {
                // Altes Hochladen in Stuecken (ownCloud-Chunking v1): jedes Stueck waere sonst eine eigene Datei
                if (Header(http, "OC-Chunked") != null)
                {
                    http.Response.StatusCode = 501;
                    await http.Response.WriteAsync("Hochladen in Stücken wird noch nicht unterstützt.");
                    return;
                }
                // Nextcloud-Clients schicken die Aenderungszeit der Datei (Sekunden seit 1970) mit
                DateTime? mtime = double.TryParse(Header(http, "X-OC-Mtime"), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0
                    ? DateTime.UnixEpoch.AddSeconds(Math.Floor(secs)) : null;
                // Als Stream verschluesselt auf die Platte - auch grosse Videos nicht im Speicher; in einem geteilten Ordner beim Besitzer
                var loc = SharedFiles.Resolve(http, t.FilePath).Creatable();
                var (etag, created) = await FileStore.PutAsync(loc.Db, loc.Path, http.Request.Body,
                    http.Request.ContentType is { Length: > 0 } ct && !ct.StartsWith("application/x-www-form") ? ct : Mime(t.FilePath), ifMatch, ifNoneMatch, mtime,
                    http.RequestAborted);
                http.Response.Headers.ETag = etag;
                http.Response.Headers["OC-ETag"] = etag;
                if (FileStore.Get(loc.Db, loc.Path) is { } f) http.Response.Headers["OC-FileId"] = OcId(loc.Show(f));
                if (mtime != null) http.Response.Headers["X-OC-MTime"] = "accepted";
                http.Response.StatusCode = created ? 201 : 204;
                return;
            }
            default:
                http.Response.StatusCode = 405;
                return;
        }
    }

    private static void Delete(HttpContext http, UserSpace space, Target t)
    {
        var ifMatch = Header(http, "If-Match");
        switch (t.Kind)
        {
            case Kind.CalendarObject:
            case Kind.BookObject:
                http.Response.StatusCode = PimStore.Delete(DbOf(space, t), t.Coll!, t.Name!, ifMatch) ? 204 : 404;
                return;
            case Kind.Calendar:
            case Kind.Book:
                if (t.Coll == null) { http.Response.StatusCode = 404; return; }
                if (PimStore.Collections(space.Db, t.Coll.Kind).Count <= 1) { http.Response.StatusCode = 403; return; }
                PimStore.DeleteCollection(space.Db, t.Coll.Id);
                http.Response.StatusCode = 204;
                return;
            case Kind.Files:
            {
                if (t.FilePath.Length == 0) { http.Response.StatusCode = 403; return; }
                // In einem geteilten Ordner: in den Papierkorb des Besitzers
                var loc = SharedFiles.Resolve(http, t.FilePath).Changeable();
                // Ein Ordner, in dem der notizi-Abgleichordner liegt, wuerde ihn mitnehmen (wie FilesApi.LocTree)
                if (FileStore.Contains(loc.Path, FileStore.ProtectedRoot(loc.Db))) { http.Response.StatusCode = 403; return; }
                if (ifMatch != null && FileStore.Get(loc.Db, loc.Path) is { } f && ifMatch != "*" && f.Etag != ifMatch) throw new PreconditionFailedException();
                http.Response.StatusCode = FileStore.Delete(loc.Db, loc.Path) ? 204 : 404;
                return;
            }
            default:
                http.Response.StatusCode = 403;
                return;
        }
    }

    // ==================================================================
    //  MKCOL, MKCALENDAR
    // ==================================================================

    private static async Task Mkcol(HttpContext http, UserSpace space, Target t, bool calendar)
    {
        var doc = await Body(http);
        var props = doc?.Root?.Descendants(D + "prop").FirstOrDefault();
        if (t.Kind is Kind.Calendar or Kind.Book)
        {
            if (t.Coll != null) { http.Response.StatusCode = 405; return; }
            if (t.Name!.StartsWith(SharedPrefix, StringComparison.Ordinal)) { http.Response.StatusCode = 403; return; }
            // Unter calendars/ nur Kalender (MKCALENDAR oder erweitertes MKCOL, RFC 5689), unter addressbooks/ nur Adressbuecher
            bool wantsCalendar = calendar || props?.Element(D + "resourcetype")?.Element(C + "calendar") != null;
            if (t.Kind == Kind.Calendar && !wantsCalendar || t.Kind == Kind.Book && calendar)
            {
                http.Response.StatusCode = 403;
                return;
            }
            var comps = props?.Element(C + "supported-calendar-component-set")?.Elements(C + "comp").Select(c => c.Attribute("name")?.Value).Where(x => x != null).ToList();
            PimStore.CreateCollection(space.Db, t.Kind == Kind.Calendar ? PimStore.Calendar : PimStore.AddressBook, t.Name!,
                props?.Element(D + "displayname")?.Value is { Length: > 0 } dn ? dn : t.Name!,
                props?.Element(ICAL + "calendar-color")?.Value,
                t.Kind == Kind.Calendar ? (comps is { Count: > 0 } ? string.Join(",", comps) : "VEVENT,VTODO") : "");
            http.Response.StatusCode = 201;
            return;
        }
        if (t.Kind == Kind.Files && !calendar)
        {
            var loc = SharedFiles.Resolve(http, t.FilePath);
            if (loc.Virtual || FileStore.Get(loc.Db, loc.Path) != null) { http.Response.StatusCode = 405; return; }
            loc.Creatable();
            FileStore.Mkcol(loc.Db, loc.Path);
            if (FileStore.Get(loc.Db, loc.Path) is { } f) http.Response.Headers["OC-FileId"] = OcId(loc.Show(f));
            http.Response.StatusCode = 201;
            return;
        }
        http.Response.StatusCode = 403;
    }

    // ==================================================================
    //  MOVE, COPY, LOCK
    // ==================================================================

    private static async Task MoveCopy(HttpContext http, UserSpace space, Target t, bool copy)
    {
        var destSegs = DestinationSegments(http);
        var dest = destSegs == null ? null : Resolve(http, space, destSegs.Value);
        if (dest == null) { http.Response.StatusCode = 502; return; }
        bool overwrite = !string.Equals(Header(http, "Overwrite"), "F", StringComparison.OrdinalIgnoreCase);

        if (t.Kind == Kind.Files && dest.Kind == Kind.Files)
        {
            if (FileStore.FilesOff(space.Db) && !FileStore.InRoot(dest.FilePath!, space.Settings.Notizi.Root)) { http.Response.StatusCode = 403; return; }
            // notizi-Zugang: auch das Ziel muss im Abgleichordner liegen. Sonst liesse sich per MOVE/COPY mit
            // Destination-Kopf aus dem Bereich ausbrechen (die Quellpruefung oben deckt nur die Anfrage-URL ab).
            if (Scope(http) == AppPasswords.ScopeNotizi && !FileStore.InRoot(dest.FilePath!, space.Settings.Notizi.Root)) { http.Response.StatusCode = 403; return; }
            var from = SharedFiles.Resolve(http, t.FilePath);
            var to = SharedFiles.Resolve(http, dest.FilePath).Creatable();
            if (from.Virtual) { http.Response.StatusCode = 403; return; }
            // Ziel wie die Anfrage-URL behandeln: den ausgeblendeten notizi-Ordner nicht beschreiben, und keinen Ordner
            // ueberschreiben, in dem er liegt (Ziele im notizi-Ordner landen nicht im Papierkorb)
            if (to.Db == space.Db && HiddenFor(http, space, to.Path, destSegs!.Value.Item1 != Flavor.Dav)
                || FileStore.Contains(to.Path, FileStore.ProtectedRoot(to.Db)))
            {
                http.Response.StatusCode = 403;
                return;
            }
            // Aus einem geteilten Ordner heraus wird kopiert - das Original bleibt beim Besitzer
            bool sameArea = from.Db == to.Db && from.Mount?.Share.Id == to.Mount?.Share.Id;
            bool asCopy = copy || (from.Shared && !sameArea);
            if (!asCopy) from.Changeable();
            // Verschieben eines Ordners, in dem der notizi-Abgleichordner liegt, nimmt ihn mit (wie FilesApi.LocTree)
            if (!asCopy && FileStore.Contains(from.Path, FileStore.ProtectedRoot(from.Db))) { http.Response.StatusCode = 403; return; }
            bool created;
            if (from.Db == to.Db) created = FileStore.MoveOrCopy(to.Db, from.Path, to.Path, overwrite, asCopy);
            else
            {
                created = await SharedFiles.CopyAcross(from, to, overwrite, http.RequestAborted);
                if (!asCopy) FileStore.Delete(from.Db, from.Path);
            }
            if (FileStore.Get(to.Db, to.Path) is { } f)
            {
                http.Response.Headers["OC-FileId"] = OcId(to.Show(f));
                http.Response.Headers["OC-ETag"] = f.Etag;
                http.Response.Headers.ETag = f.Etag;
            }
            http.Response.StatusCode = created ? 201 : 204;
            return;
        }
        if (t.Kind is Kind.CalendarObject or Kind.BookObject && dest.Kind == t.Kind && dest.Coll != null)
        {
            if (ReadOnlyFor(space, dest)) { http.Response.StatusCode = 403; return; }
            var srcDb = DbOf(space, t);
            var destDb = DbOf(space, dest);
            var obj = PimStore.Object(srcDb, t.Coll!.Id, t.Name!) ?? throw new KeyNotFoundException();
            // In eine andere Datenbank (eigener <-> geteilter Kalender): schreiben, dann erst loeschen
            if (srcDb != destDb)
            {
                bool there = PimStore.Object(destDb, dest.Coll.Id, dest.Name!) != null;
                if (there && !overwrite) throw new PreconditionFailedException();
                var e2 = PimStore.Put(destDb, dest.Coll, dest.Name!, obj.Data, ifNoneMatch: overwrite ? null : "*", zone: space.Zone);
                if (!copy) PimStore.Delete(srcDb, t.Coll, t.Name!);
                http.Response.Headers.ETag = e2;
                http.Response.StatusCode = there ? 204 : 201;
                return;
            }
            // Auf sich selbst: nichts zu tun, und Verschieben wuerde es sonst loeschen
            if (dest.Coll.Id == t.Coll.Id && dest.Name == t.Name) { http.Response.StatusCode = 403; return; }
            bool existed = PimStore.Object(srcDb, dest.Coll.Id, dest.Name!) != null;
            if (existed && !overwrite) throw new PreconditionFailedException();
            // Verschieben in einem Zug: scheitert das Schreiben ins Ziel, bleibt die Quelle
            var etag = copy
                ? PimStore.Put(srcDb, dest.Coll, dest.Name!, obj.Data, ifNoneMatch: overwrite ? null : "*", zone: space.Zone)
                : PimStore.Move(srcDb, t.Coll, t.Name!, dest.Coll, dest.Name!, overwrite, space.Zone);
            http.Response.Headers.ETag = etag;
            http.Response.StatusCode = existed ? 204 : 201;
            return;
        }
        http.Response.StatusCode = 403;
    }

    /// <summary>
    /// Sperren gibt es nicht wirklich - jede Aenderung ist ohnehin atomar.
    /// Windows und macOS schreiben aber nur mit einer Sperre; sie bekommen eine.
    /// </summary>
    private static async Task Lock(HttpContext http, Target t)
    {
        var token = "opaquelocktoken:" + Guid.NewGuid();
        http.Response.Headers["Lock-Token"] = "<" + token + ">";
        http.Response.ContentType = "application/xml; charset=utf-8";
        http.Response.StatusCode = 200;
        var x = new XElement(D + "prop", new XAttribute(XNamespace.Xmlns + "d", D),
            new XElement(D + "lockdiscovery", new XElement(D + "activelock",
                new XElement(D + "locktype", new XElement(D + "write")),
                new XElement(D + "lockscope", new XElement(D + "exclusive")),
                new XElement(D + "depth", "infinity"),
                new XElement(D + "timeout", "Second-3600"),
                new XElement(D + "locktoken", new XElement(D + "href", token)),
                new XElement(D + "lockroot", new XElement(D + "href", http.Request.Path.Value)))));
        await http.Response.WriteAsync(x.ToString(SaveOptions.DisableFormatting));
    }

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" => "text/plain", ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", ".json" => "application/json",
        ".zip" => "application/zip", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".mp4" => "video/mp4", ".mp3" => "audio/mpeg",
        ".ics" => "text/calendar", ".vcf" => "text/vcard", ".md" => "text/markdown", ".html" or ".htm" => "text/html",
        ".bmp" => "image/bmp", ".ico" => "image/x-icon", ".heic" => "image/heic", ".avif" => "image/avif", ".tif" or ".tiff" => "image/tiff",
        ".webm" => "video/webm", ".mov" => "video/quicktime", ".m4v" => "video/x-m4v", ".mkv" => "video/x-matroska", ".ogv" => "video/ogg",
        ".m4a" => "audio/mp4", ".wav" => "audio/wav", ".ogg" or ".oga" => "audio/ogg", ".opus" => "audio/opus", ".flac" => "audio/flac", ".aac" => "audio/aac",
        ".csv" => "text/csv", ".xml" => "application/xml", ".log" => "text/plain", ".doc" => "application/msword", ".xls" => "application/vnd.ms-excel",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet", ".7z" => "application/x-7z-compressed", ".gz" => "application/gzip",
        _ => "application/octet-stream"
    };
}
