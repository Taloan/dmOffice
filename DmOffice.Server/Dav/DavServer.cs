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
/// </summary>
public static class DavServer
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CR = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";
    private static readonly XNamespace ICAL = "http://apple.com/ns/ical/";

    public static void Map(WebApplication app)
    {
        app.MapMethods("/.well-known/caldav", new[] { "GET", "PROPFIND", "OPTIONS", "HEAD" }, (HttpContext h) => Results.Redirect("/dav/", permanent: true));
        app.MapMethods("/.well-known/carddav", new[] { "GET", "PROPFIND", "OPTIONS", "HEAD" }, (HttpContext h) => Results.Redirect("/dav/", permanent: true));
        app.Map("/dav", Handle).RequireAuthorization("dav");
        app.Map("/dav/{**rest}", Handle).RequireAuthorization("dav");
    }

    // ==================================================================
    //  Adressen
    // ==================================================================

    private enum Kind { Root, PrincipalsRoot, Principal, HomeRoot, CalendarHome, Calendar, CalendarObject, BookHome, Book, BookObject, FilesRoot, Files }

    private sealed record Target(Kind Kind, string? Login = null, Collection? Coll = null, string? Name = null, string FilePath = "");

    private static List<string> Segments(HttpContext http)
    {
        // Request.Path ist schon dekodiert - nicht noch einmal (sonst wird aus %2520 ein Leerzeichen)
        var path = http.Request.Path.Value ?? "/";
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
    }

    private static Target? Resolve(UserSpace space, List<string> s)
    {
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
                var coll = PimStore.Find(space.Db, cal ? PimStore.Calendar : PimStore.AddressBook, s[2]);
                if (s.Count == 3) return new Target(cal ? Kind.Calendar : Kind.Book, s[1], coll, s[2]);
                if (s.Count == 4 && coll != null) return new Target(cal ? Kind.CalendarObject : Kind.BookObject, s[1], coll, s[3]);
                return null;
            }
            case "files":
                if (s.Count == 1) return new Target(Kind.FilesRoot);
                return new Target(Kind.Files, s[1], FilePath: FileStore.Normalize(string.Join("/", s.Skip(2))));
        }
        return null;
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);
    private static string PrincipalHref(string login) => $"/dav/principals/{Esc(login)}/";
    private static string CalHome(string login) => $"/dav/calendars/{Esc(login)}/";
    private static string BookHome(string login) => $"/dav/addressbooks/{Esc(login)}/";
    private static string CollHref(Collection c, string login) => (c.Kind == PimStore.Calendar ? CalHome(login) : BookHome(login)) + Esc(c.Uri) + "/";
    private static string FileHref(string login, FileEntry f) =>
        $"/dav/files/{Esc(login)}/" + string.Join("/", f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Esc)) + (f.IsDir && f.Path.Length > 0 ? "/" : "");

    /// <summary>Ziel aus dem Destination-Kopf (absolute oder relative Adresse).</summary>
    private static List<string>? DestinationSegments(HttpContext http)
    {
        var dest = http.Request.Headers["Destination"].ToString();
        if (dest.Length == 0) return null;
        var path = Uri.TryCreate(dest, UriKind.Absolute, out var abs) ? abs.AbsolutePath : dest;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segs.Count == 0 || segs[0] != "dav") return null;
        return segs.Skip(1).ToList();
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
        if (!LogDav || path.StartsWith("/dav/files/")) { await HandleCore(http); return; }

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
            var target = Resolve(space, segments);
            if (target == null)
            {
                http.Response.StatusCode = segments is { Count: >= 2 } s && s[1] != space.User.Login ? 403 : 404;
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
                // Abonnierte Kalender sind nur lesbar - auch fuer Handy und Thunderbird
                case "PROPPATCH" or "PUT" or "DELETE" or "MOVE" when target.Coll != null && Subscriptions.IsSubscribed(space.Db, target.Coll.Id):
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
                case "COPY": MoveCopy(http, space, target, method == "COPY"); return;
                case "LOCK": await Lock(http, target); return;
                case "UNLOCK": http.Response.StatusCode = 204; return;
                default: http.Response.StatusCode = 405; return;
            }
        }
        catch (PreconditionFailedException) { http.Response.StatusCode = 412; }
        catch (DavConflictException) { http.Response.StatusCode = 409; }
        catch (DavForbiddenException) { http.Response.StatusCode = 403; }
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
        var self = Describe(space, t);
        if (self == null) { http.Response.StatusCode = 404; return; }
        responses.Add(Response(self, req));
        if (depth >= 1)
            foreach (var child in Members(space, t)) responses.Add(Response(child, req));
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
            new XAttribute(XNamespace.Xmlns + "x1", ICAL), new XAttribute(XNamespace.Xmlns + "x2", "http://nextcloud.com/ns"), responses);
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

    private static Resource? Describe(UserSpace space, Target t)
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
                return t.Coll == null ? null : DescribeCollection(space, t.Coll);
            case Kind.CalendarObject:
            case Kind.BookObject:
            {
                var obj = PimStore.Object(space.Db, t.Coll!.Id, t.Name!);
                return obj == null ? null : DescribeObject(space, t.Coll, obj.Uri, obj.Etag, obj.Data.Length, obj.Modified, obj.Data);
            }
            case Kind.FilesRoot:
                return new Resource("/dav/files/", Base(space, "Dateien"));
            case Kind.Files:
            {
                var f = FileStore.Get(space.Db, t.FilePath);
                return f == null ? null : DescribeFile(space, f);
            }
        }
        return null;
    }

    private static Dictionary<XName, object?> WithoutName(Dictionary<XName, object?> p) { p.Remove(D + "displayname"); return p; }

    private static Resource DescribeCollection(UserSpace space, Collection c)
    {
        bool cal = c.Kind == PimStore.Calendar;
        var p = Base(space, c.Name, cal ? new XElement(C + "calendar") : new XElement(CR + "addressbook"));
        if (cal && Subscriptions.IsSubscribed(space.Db, c.Id)) p[D + "current-user-privilege-set"] = Privileges(false);
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

    private static Resource DescribeObject(UserSpace space, Collection c, string uri, string etag, long size, string modified, string? data)
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
            [D + "current-user-privilege-set"] = Privileges(true),
            [D + "owner"] = Href(PrincipalHref(space.User.Login)),
        };
        if (data != null)
        {
            if (cal) p[C + "calendar-data"] = new XElement(C + "calendar-data", data);
            else p[CR + "address-data"] = new XElement(CR + "address-data", data);
        }
        return new Resource(CollHref(c, space.User.Login) + Esc(uri), p);
    }

    private static Resource DescribeFile(UserSpace space, FileEntry f)
    {
        var p = new Dictionary<XName, object?>
        {
            [D + "displayname"] = f.Path.Length == 0 ? space.User.Login : f.Name,
            [D + "resourcetype"] = f.IsDir ? new XElement(D + "resourcetype", new XElement(D + "collection")) : new XElement(D + "resourcetype"),
            [D + "getetag"] = f.Etag,
            [D + "getlastmodified"] = f.Modified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture),
            [D + "creationdate"] = f.Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            [D + "current-user-principal"] = Href(PrincipalHref(space.User.Login)),
            [D + "current-user-privilege-set"] = Privileges(true),
            [D + "supportedlock"] = new XElement(D + "supportedlock", new XElement(D + "lockentry",
                new XElement(D + "lockscope", new XElement(D + "exclusive")), new XElement(D + "locktype", new XElement(D + "write")))),
            [D + "lockdiscovery"] = new XElement(D + "lockdiscovery"),
        };
        if (!f.IsDir)
        {
            p[D + "getcontentlength"] = f.Size.ToString();
            p[D + "getcontenttype"] = f.Mime ?? "application/octet-stream";
        }
        else if (f.Path.Length == 0)
        {
            p[D + "quota-used-bytes"] = FileStore.Usage(space.Db).ToString();
            p[D + "quota-available-bytes"] = (1L << 40).ToString();
        }
        return new Resource(FileHref(space.User.Login, f), p);
    }

    private static IEnumerable<Resource> Members(UserSpace space, Target t)
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
                yield return Describe(space, new Target(Kind.Principal, login))!;
                break;
            case Kind.HomeRoot:
                yield return Describe(space, new Target(t.Name == "calendars" ? Kind.CalendarHome : Kind.BookHome, login))!;
                break;
            case Kind.CalendarHome:
                foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar)) yield return DescribeCollection(space, c);
                break;
            case Kind.BookHome:
                foreach (var c in PimStore.Collections(space.Db, PimStore.AddressBook)) yield return DescribeCollection(space, c);
                break;
            case Kind.Calendar:
            case Kind.Book:
                if (t.Coll != null)
                    foreach (var (uri, etag, size, modified) in PimStore.Index(space.Db, t.Coll.Id))
                        yield return DescribeObject(space, t.Coll, uri, etag, size, modified, null);
                break;
            case Kind.FilesRoot:
                yield return DescribeFile(space, FileStore.Get(space.Db, "")!);
                break;
            case Kind.Files:
                if (FileStore.Get(space.Db, t.FilePath) is { IsDir: true })
                {
                    bool off = FileStore.FilesOff(space.Db);
                    foreach (var f in FileStore.Children(space.Db, t.FilePath))
                        if (!off || FileStore.InRoot(f.Path, space.Settings.Notizi.Root)) yield return DescribeFile(space, f);
                }
                break;
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
        var href = Describe(space, t)?.Href ?? http.Request.Path.Value!;
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
                var ot = Resolve(space, segs);
                var obj = ot?.Coll != null && ot.Name != null && ot.Kind is Kind.CalendarObject or Kind.BookObject
                    ? PimStore.Object(space.Db, ot.Coll.Id, ot.Name) : null;
                if (obj == null)
                    responses.Add(new XElement(D + "response", new XElement(D + "href", href), new XElement(D + "status", "HTTP/1.1 404 Not Found")));
                else
                    responses.Add(Response(DescribeObject(space, ot!.Coll!, obj.Uri, obj.Etag, obj.Data.Length, obj.Modified, obj.Data), req));
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
                objects = PimStore.InRange(space.Db, new[] { t.Coll.Id }, comp, start, end);
            }
            else objects = PimStore.Objects(space.Db, t.Coll.Id);
            await Multistatus(http, objects.Select(o => Response(DescribeObject(space, t.Coll, o.Uri, o.Etag, o.Data.Length, o.Modified, o.Data), req)));
            return;
        }

        if (root.Name == D + "sync-collection")
        {
            if (t.Coll == null) { http.Response.StatusCode = 403; return; }
            var tokenText = root.Element(D + "sync-token")?.Value ?? "";
            var current = PimStore.Find(space.Db, t.Coll.Id)!;
            var responses = new List<XElement>();
            if (tokenText.Length == 0)
            {
                foreach (var o in PimStore.Objects(space.Db, current.Id))
                    responses.Add(Response(DescribeObject(space, current, o.Uri, o.Etag, o.Data.Length, o.Modified, o.Data), req));
            }
            else
            {
                var token = ParseSyncToken(tokenText, current.Id);
                var changes = token is long tk && tk <= current.SyncToken ? PimStore.ChangesSince(space.Db, current.Id, tk) : null;
                if (changes == null)
                {
                    http.Response.StatusCode = 403;
                    http.Response.ContentType = "application/xml; charset=utf-8";
                    await http.Response.WriteAsync(new XElement(D + "error", new XAttribute(XNamespace.Xmlns + "d", D), new XElement(D + "valid-sync-token")).ToString());
                    return;
                }
                foreach (var (uri, deleted) in changes)
                {
                    var o = deleted ? null : PimStore.Object(space.Db, current.Id, uri);
                    if (o == null)
                        responses.Add(new XElement(D + "response", new XElement(D + "href", CollHref(current, space.User.Login) + Esc(uri)),
                            new XElement(D + "status", "HTTP/1.1 404 Not Found")));
                    else
                        responses.Add(Response(DescribeObject(space, current, o.Uri, o.Etag, o.Data.Length, o.Modified, o.Data), req));
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
                var obj = PimStore.Object(space.Db, t.Coll!.Id, t.Name!) ?? throw new KeyNotFoundException();
                data = Encoding.UTF8.GetBytes(obj.Data);
                type = t.Kind == Kind.CalendarObject ? "text/calendar; charset=utf-8" : "text/vcard; charset=utf-8";
                etag = obj.Etag;
                break;
            }
            case Kind.Files:
            {
                var f = FileStore.Get(space.Db, t.FilePath) ?? throw new KeyNotFoundException();
                if (f.IsDir) { await DirListing(http, space, f); return; }
                if (http.Request.Headers.IfNoneMatch.ToString() == f.Etag) { http.Response.StatusCode = 304; return; }
                data = FileStore.Data(space.Db, t.FilePath) ?? Array.Empty<byte>();
                type = f.Mime ?? "application/octet-stream";
                etag = f.Etag;
                http.Response.Headers.LastModified = f.Modified.ToUniversalTime().ToString("R");
                break;
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

    private static async Task DirListing(HttpContext http, UserSpace space, FileEntry dir)
    {
        var sb = new StringBuilder("<!doctype html><meta charset=utf-8><title>dmOffice</title><ul>");
        foreach (var f in FileStore.Children(space.Db, dir.Path))
            sb.Append($"<li><a href=\"{FileHref(space.User.Login, f)}\">{System.Net.WebUtility.HtmlEncode(f.Name)}{(f.IsDir ? "/" : "")}</a></li>");
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        await http.Response.WriteAsync(sb.Append("</ul>").ToString());
    }

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers[name].ToString() is { Length: > 0 } v ? v.Trim() : null;

    private static async Task Put(HttpContext http, UserSpace space, Target t)
    {
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms);
        var ifMatch = Header(http, "If-Match");
        var ifNoneMatch = Header(http, "If-None-Match");
        switch (t.Kind)
        {
            case Kind.CalendarObject:
            case Kind.BookObject:
            {
                bool existed = PimStore.Object(space.Db, t.Coll!.Id, t.Name!) != null;
                var etag = PimStore.Put(space.Db, t.Coll, t.Name!, Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿'), ifMatch, ifNoneMatch, space.Zone);
                http.Response.Headers.ETag = etag;
                http.Response.StatusCode = existed ? 204 : 201;
                return;
            }
            case Kind.Files:
            {
                var (etag, created) = FileStore.Put(space.Db, t.FilePath, ms.ToArray(),
                    http.Request.ContentType is { Length: > 0 } ct && !ct.StartsWith("application/x-www-form") ? ct : Mime(t.FilePath), ifMatch, ifNoneMatch);
                http.Response.Headers.ETag = etag;
                http.Response.Headers["OC-ETag"] = etag;
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
                http.Response.StatusCode = PimStore.Delete(space.Db, t.Coll!, t.Name!, ifMatch) ? 204 : 404;
                return;
            case Kind.Calendar:
            case Kind.Book:
                if (t.Coll == null) { http.Response.StatusCode = 404; return; }
                if (PimStore.Collections(space.Db, t.Coll.Kind).Count <= 1) { http.Response.StatusCode = 403; return; }
                PimStore.DeleteCollection(space.Db, t.Coll.Id);
                http.Response.StatusCode = 204;
                return;
            case Kind.Files:
                if (t.FilePath.Length == 0) { http.Response.StatusCode = 403; return; }
                if (ifMatch != null && FileStore.Get(space.Db, t.FilePath) is { } f && ifMatch != "*" && f.Etag != ifMatch) throw new PreconditionFailedException();
                http.Response.StatusCode = FileStore.Delete(space.Db, t.FilePath) ? 204 : 404;
                return;
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
            if (FileStore.Get(space.Db, t.FilePath) != null) { http.Response.StatusCode = 405; return; }
            FileStore.Mkcol(space.Db, t.FilePath);
            http.Response.StatusCode = 201;
            return;
        }
        http.Response.StatusCode = 403;
    }

    // ==================================================================
    //  MOVE, COPY, LOCK
    // ==================================================================

    private static void MoveCopy(HttpContext http, UserSpace space, Target t, bool copy)
    {
        var destSegs = DestinationSegments(http);
        var dest = destSegs == null ? null : Resolve(space, destSegs);
        if (dest == null) { http.Response.StatusCode = 502; return; }
        bool overwrite = !string.Equals(Header(http, "Overwrite"), "F", StringComparison.OrdinalIgnoreCase);

        if (t.Kind == Kind.Files && dest.Kind == Kind.Files)
        {
            if (FileStore.FilesOff(space.Db) && !FileStore.InRoot(dest.FilePath!, space.Settings.Notizi.Root)) { http.Response.StatusCode = 403; return; }
            bool created = FileStore.MoveOrCopy(space.Db, t.FilePath, dest.FilePath, overwrite, copy);
            http.Response.StatusCode = created ? 201 : 204;
            return;
        }
        if (t.Kind is Kind.CalendarObject or Kind.BookObject && dest.Kind == t.Kind && dest.Coll != null)
        {
            var obj = PimStore.Object(space.Db, t.Coll!.Id, t.Name!) ?? throw new KeyNotFoundException();
            // Auf sich selbst: nichts zu tun, und Verschieben wuerde es sonst loeschen
            if (dest.Coll.Id == t.Coll.Id && dest.Name == t.Name) { http.Response.StatusCode = 403; return; }
            bool existed = PimStore.Object(space.Db, dest.Coll.Id, dest.Name!) != null;
            if (existed && !overwrite) throw new PreconditionFailedException();
            // Verschieben in einem Zug: scheitert das Schreiben ins Ziel, bleibt die Quelle
            var etag = copy
                ? PimStore.Put(space.Db, dest.Coll, dest.Name!, obj.Data, ifNoneMatch: overwrite ? null : "*", zone: space.Zone)
                : PimStore.Move(space.Db, t.Coll, t.Name!, dest.Coll, dest.Name!, overwrite, space.Zone);
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
        _ => "application/octet-stream"
    };
}
