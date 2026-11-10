using DmOffice.Core;
using DmOffice.Dav;
using MimeKit;

namespace DmOffice.Pim;

public sealed record TypedValue(string Type, string Value);

public sealed class AddressDto
{
    public string Type { get; set; } = "home";
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Zip { get; set; } = "";
    public string Country { get; set; } = "";
}

public sealed class ContactDto
{
    public string Id { get; set; } = "";
    public long Book { get; set; }
    public string Uri { get; set; } = "";
    public string Etag { get; set; } = "";
    public string? Uid { get; set; }
    public string DisplayName { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Given { get; set; } = "";
    public string Additional { get; set; } = "";
    public string Family { get; set; } = "";
    public string Suffix { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Org { get; set; } = "";
    public string Department { get; set; } = "";
    public string Title { get; set; } = "";
    public List<TypedValue> Emails { get; set; } = new();
    public List<TypedValue> Phones { get; set; } = new();
    public List<AddressDto> Addresses { get; set; } = new();
    public List<string> Urls { get; set; } = new();
    public string? Birthday { get; set; }
    public string Note { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    /// <summary>VIP: Benachrichtigungen "nur VIPs" und Prio-Posteingang. Im vCard als X-DM-VIP:1, andere Programme lassen es stehen.</summary>
    public bool Vip { get; set; }
    /// <summary>data:-URL, oder null. Beim Speichern: null = unveraendert, "" = entfernen.</summary>
    public string? Photo { get; set; }
    public long? MoveTo { get; set; }
}

/// <summary>vCard (3.0 und 4.0) lesen und schreiben. Geschrieben wird 3.0 -
/// das lesen alle Programme, auch die aelteren Handys.</summary>
public static class ContactModel
{
    /// <summary>E-Mail-Adressen aller VIP-Kontakte (klein geschrieben) - fuer Benachrichtigungen und den Prio-Posteingang.</summary>
    public static HashSet<string> VipAddresses(UserSpace space)
    {
        var books = PimStore.Collections(space.Db, PimStore.AddressBook).Select(c => c.Id).ToList();
        return All(space, books).Where(c => c.Vip).SelectMany(c => c.Emails).Select(e => e.Value.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0).ToHashSet();
    }

    public static List<ContactDto> All(UserSpace space, IEnumerable<long> books, bool withPhoto = false)
    {
        var result = new List<ContactDto>();
        foreach (var book in books)
            foreach (var obj in PimStore.Objects(space.Db, book))
                if (Read(obj, withPhoto) is { } c) result.Add(c);
        return result.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Ein Kontakt als .vcf-Datei (Rechtsklick "Exportieren"): die gespeicherte vCard unveraendert, Dateiname aus dem Namen.</summary>
    public static (byte[] Data, string Name) Export(UserDb db, long book, string uri)
    {
        var obj = PimStore.Object(db, book, uri) ?? throw new KeyNotFoundException();
        var name = Read(obj, withPhoto: false)?.DisplayName ?? "";
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        return (System.Text.Encoding.UTF8.GetBytes(obj.Data), (name.Length > 0 ? name : "Kontakt") + ".vcf");
    }

    public static ContactDto? Read(DavObject obj, bool withPhoto)
    {
        Component card;
        try { card = Ics.Parse(obj.Data).First(r => r.Name == "VCARD"); }
        catch { return null; }
        var c = new ContactDto { Id = obj.Collection + "/" + obj.Uri, Book = obj.Collection, Uri = obj.Uri, Etag = obj.Etag, Uid = card.Text("UID") };
        c.DisplayName = card.Text("FN") ?? "";
        if (card.Get("N") is { } n)
        {
            var parts = Ics.Structured(n.Value);
            string At(int i) => i < parts.Count ? parts[i] : "";
            c.Family = At(0); c.Given = At(1); c.Additional = At(2); c.Prefix = At(3); c.Suffix = At(4);
        }
        c.Nickname = card.Text("NICKNAME") ?? "";
        if (card.Get("ORG") is { } org)
        {
            var parts = Ics.Structured(org.Value);
            c.Org = parts.ElementAtOrDefault(0) ?? "";
            c.Department = string.Join(", ", parts.Skip(1).Where(p => p.Length > 0));
        }
        c.Title = card.Text("TITLE") ?? "";
        c.Emails = card.All("EMAIL").Select(p => new TypedValue(MainType(p, "home"), p.Text)).ToList();
        c.Phones = card.All("TEL").Select(p => new TypedValue(PhoneType(p), p.Text.Replace("tel:", ""))).ToList();
        c.Addresses = card.All("ADR").Select(p =>
        {
            var a = Ics.Structured(p.Value);
            string At(int i) => i < a.Count ? a[i] : "";
            return new AddressDto { Type = MainType(p, "home"), Street = At(2), City = At(3), Region = At(4), Zip = At(5), Country = At(6) };
        }).ToList();
        c.Urls = card.All("URL").Select(p => p.Text).ToList();
        c.Birthday = Birthday(card.Text("BDAY"));
        c.Note = card.Text("NOTE") ?? "";
        c.Categories = card.All("CATEGORIES").SelectMany(p => p.Value.Split(',')).Select(Ics.Unescape).Where(x => x.Length > 0).ToList();
        c.Vip = card.Text("X-DM-VIP") == "1";
        if (string.IsNullOrWhiteSpace(c.DisplayName))
            c.DisplayName = string.Join(" ", new[] { c.Given, c.Family }.Where(x => x.Length > 0)) is { Length: > 0 } nm ? nm
                : c.Org.Length > 0 ? c.Org : c.Emails.FirstOrDefault()?.Value ?? "(ohne Namen)";
        if (withPhoto && card.Get("PHOTO") is { } photo) c.Photo = PhotoUrl(photo);
        return c;
    }

    private static string MainType(Prop p, string fallback)
    {
        var types = p.Types();
        foreach (var t in new[] { "work", "home", "other" }) if (types.Contains(t)) return t;
        return types.FirstOrDefault(t => t is not ("internet" or "pref" or "voice" or "x400")) ?? fallback;
    }

    private static string PhoneType(Prop p)
    {
        var types = p.Types();
        if (types.Contains("cell")) return "cell";
        if (types.Contains("fax")) return "fax";
        if (types.Contains("work")) return "work";
        if (types.Contains("home")) return "home";
        return "other";
    }

    private static string? Birthday(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var digits = raw.Replace("-", "");
        if (digits.StartsWith("--") || raw.StartsWith("--")) return null; // ohne Jahr: selten, hier nicht gezeigt
        if (digits.Length >= 8 && digits[..8].All(char.IsDigit)) return $"{digits[..4]}-{digits[4..6]}-{digits[6..8]}";
        return null;
    }

    /// <summary>Text nur setzen, wenn er sich geaendert hat - sonst bleiben die Parameter.</summary>
    private static void KeepText(Component card, string name, string? text)
    {
        if ((card.Text(name) ?? "") == (text ?? "")) return;
        card.SetText(name, text);
    }

    /// <summary>
    /// Fuehrt die Eintraege der Oberflaeche mit den vorhandenen Zeilen zusammen:
    /// Gleiches (zuerst an derselben Stelle, dann irgendwo) bleibt die Original-Zeile,
    /// Geaendertes wird neu erzeugt - mit der unbenutzten Zeile derselben Stelle als Vorlage.
    /// </summary>
    internal static List<Prop> Merge<T>(List<Prop> old, List<T> wanted, Func<Prop, T> represent, Func<T, Prop?, Prop> create)
    {
        var eq = EqualityComparer<T>.Default;
        var shown = old.Select(represent).ToList();
        var used = new bool[old.Count];
        var result = new Prop?[wanted.Count];
        for (int i = 0; i < wanted.Count && i < old.Count; i++)
            if (eq.Equals(shown[i], wanted[i])) { result[i] = old[i]; used[i] = true; }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (result[i] != null) continue;
            var j = Enumerable.Range(0, old.Count).FirstOrDefault(k => !used[k] && eq.Equals(shown[k], wanted[i]), -1);
            if (j >= 0) { result[i] = old[j]; used[j] = true; }
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (result[i] != null) continue;
            Prop? same = i < old.Count && !used[i] ? old[i] : null;
            if (same != null) used[i] = true;
            result[i] = create(wanted[i], same);
        }
        return result.Select(p => p!).ToList();
    }

    /// <summary>Eine neue Zeile. Gleicher Typ wie die alte: deren Parameter (PREF, mehrere Typen) mitnehmen.</summary>
    private static Prop Fresh(string name, string value, Prop? old, bool sameType, string? type)
    {
        var p = new Prop(name, value);
        if (old != null && sameType)
        {
            // Was zum alten Wert gehoerte, nicht: Kodierung, fertige Anschrift, Wertart
            foreach (var kv in old.Params)
                if (kv.Key is not ("ENCODING" or "CHARSET" or "LABEL" or "VALUE")) p.Params.Add(kv);
        }
        else if (type != null) p.With("TYPE", type);
        return p;
    }

    /// <summary>Ersetzt alle Zeilen eines Namens, an der Stelle der ersten.</summary>
    private static void Replace(Component card, string name, List<Prop> props)
    {
        var at = card.Props.FindIndex(p => p.Name == name);
        card.Remove(name);
        if (at < 0 || at > card.Props.Count) at = card.Props.Count;
        card.Props.InsertRange(at, props);
    }

    private static string? PhotoUrl(Prop p)
    {
        var v = p.Value.Trim();
        if (v.StartsWith("data:")) return v;                        // vCard 4.0
        if (v.StartsWith("http")) return null;                       // entfernte Bilder nicht laden
        var type = (p.Param("TYPE") ?? "jpeg").ToLowerInvariant();
        return $"data:image/{(type == "jpg" ? "jpeg" : type)};base64,{v}";
    }

    public static string Save(UserSpace space, ContactDto input)
    {
        var db = space.Db;
        var coll = PimStore.Find(db, input.Book) ?? throw new KeyNotFoundException("Adressbuch nicht gefunden.");
        Component card;
        string uri;
        string? etag = null;
        if (string.IsNullOrEmpty(input.Uri))
        {
            uri = PimStore.NewUri(".vcf");
            card = new Component("VCARD");
            card.Set("VERSION", "3.0");
            card.Set("UID", Guid.NewGuid().ToString("D"));
        }
        else
        {
            uri = input.Uri;
            var obj = PimStore.Object(db, coll.Id, uri) ?? throw new KeyNotFoundException("Kontakt nicht gefunden.");
            // Stand aus der Oberflaeche pruefen - sonst ueberschriebe sie Aenderungen vom Handy
            if (!string.IsNullOrEmpty(input.Etag) && input.Etag != obj.Etag) throw new PreconditionFailedException();
            etag = obj.Etag;
            card = Ics.Parse(obj.Data).First(r => r.Name == "VCARD");
        }
        bool v4 = card.Text("VERSION") == "4.0";

        var fn = input.DisplayName.Trim();
        if (fn.Length == 0) fn = string.Join(" ", new[] { input.Prefix, input.Given, input.Additional, input.Family, input.Suffix }.Where(x => x.Length > 0));
        if (fn.Length == 0) fn = input.Org.Length > 0 ? input.Org : input.Emails.FirstOrDefault()?.Value ?? "Kontakt";
        // Unveraendertes bleibt, wie es war - mit allen Parametern (PREF, LANGUAGE ...)
        KeepText(card, "FN", fn);
        var name = new[] { input.Family, input.Given, input.Additional, input.Prefix, input.Suffix };
        if (card.Get("N") is not { } oldN || !Ics.Structured(oldN.Value).Concat(Enumerable.Repeat("", 5)).Take(5).SequenceEqual(name.Select(x => x ?? "")))
            card.Set("N", Ics.JoinStructured(name));
        KeepText(card, "NICKNAME", input.Nickname);
        var oldOrg = card.Get("ORG") is { } o ? Ics.Structured(o.Value) : null;
        bool orgSame = oldOrg != null && (oldOrg.ElementAtOrDefault(0) ?? "") == input.Org
                       && string.Join(", ", oldOrg.Skip(1).Where(x => x.Length > 0)) == input.Department;
        if (!orgSame)
        {
            if (input.Org.Length + input.Department.Length > 0) card.Set("ORG", Ics.JoinStructured(new[] { input.Org, input.Department }));
            else card.Remove("ORG");
        }
        KeepText(card, "TITLE", input.Title);

        // Mehrfaches: gleiche Eintraege behalten ihre Zeile, nur Geaendertes wird neu geschrieben
        var emails = input.Emails.Where(e => e.Value.Trim().Length > 0).Select(e => new TypedValue(e.Type, e.Value.Trim())).ToList();
        Replace(card, "EMAIL", Merge(card.All("EMAIL").ToList(), emails,
            p => new TypedValue(MainType(p, "home"), p.Text.Trim()),
            (e, old) => Fresh("EMAIL", Ics.Escape(e.Value), old, old != null && MainType(old, "home") == e.Type,
                v4 ? e.Type : "INTERNET," + e.Type.ToUpperInvariant())));

        var phones = input.Phones.Where(t => t.Value.Trim().Length > 0).Select(t => new TypedValue(t.Type, t.Value.Trim())).ToList();
        Replace(card, "TEL", Merge(card.All("TEL").ToList(), phones,
            p => new TypedValue(PhoneType(p), p.Text.Replace("tel:", "").Trim()),
            (t, old) =>
            {
                var type = t.Type switch { "cell" => "CELL", "fax" => "FAX", "work" => "WORK,VOICE", "home" => "HOME,VOICE", _ => "VOICE" };
                bool same = old != null && PhoneType(old) == t.Type;
                // vCard 4.0 mit tel:-Adresse bleibt eine
                bool asUri = same && old!.Param("VALUE")?.Equals("uri", StringComparison.OrdinalIgnoreCase) == true;
                var tel = Fresh("TEL", asUri ? "tel:" + t.Value : Ics.Escape(t.Value), old, same, v4 ? type.ToLowerInvariant() : type);
                if (asUri) tel.With("VALUE", "uri");
                return tel;
            }));

        var addresses = input.Addresses.Where(a => (a.Street + a.City + a.Zip + a.Country + a.Region).Trim().Length > 0)
            .Select(a => (Type: a.Type, Street: a.Street, City: a.City, Region: a.Region, Zip: a.Zip, Country: a.Country)).ToList();
        Replace(card, "ADR", Merge(card.All("ADR").ToList(), addresses,
            p =>
            {
                var a = Ics.Structured(p.Value);
                string At(int i) => i < a.Count ? a[i] : "";
                return (Type: MainType(p, "home"), Street: At(2), City: At(3), Region: At(4), Zip: At(5), Country: At(6));
            },
            (a, old) =>
            {
                // Postfach und Adresszusatz (Teil 0 und 1) zeigt die Oberflaeche nicht - von der Adresse an dieser Stelle behalten
                var parts = old != null ? Ics.Structured(old.Value) : new List<string>();
                string Keep(int i) => i < parts.Count ? parts[i] : "";
                return Fresh("ADR", Ics.JoinStructured(new[] { Keep(0), Keep(1), a.Street, a.City, a.Region, a.Zip, a.Country }),
                    old, old != null && MainType(old, "home") == a.Type, v4 ? a.Type : a.Type.ToUpperInvariant());
            }));

        var urls = input.Urls.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        Replace(card, "URL", Merge(card.All("URL").ToList(), urls, p => p.Text.Trim(),
            (u, old) => Fresh("URL", Ics.Escape(u), old, old != null, null)));

        // Geburtstag ohne Jahr (--0501) und anderes, das die Oberflaeche nicht zeigen kann, bleibt stehen
        var shownBirthday = Birthday(card.Text("BDAY"));
        if (string.IsNullOrEmpty(input.Birthday)) { if (shownBirthday != null) card.Remove("BDAY"); }
        else if (input.Birthday != shownBirthday) card.Set("BDAY", v4 ? input.Birthday.Replace("-", "") : input.Birthday);
        KeepText(card, "NOTE", input.Note);
        card.Remove("CATEGORIES");
        if (input.Categories.Count > 0) card.Set("CATEGORIES", string.Join(",", input.Categories.Select(Ics.Escape)));
        if (input.Vip) card.Set("X-DM-VIP", "1"); else card.Remove("X-DM-VIP");

        if (input.Photo == "") card.Remove("PHOTO");
        else if (input.Photo is { } photo && photo.StartsWith("data:image/"))
        {
            var comma = photo.IndexOf(',');
            var type = photo[11..photo.IndexOf(';')].ToUpperInvariant();
            if (v4) card.Set("PHOTO", photo);
            else card.Set("PHOTO", photo[(comma + 1)..]).With("ENCODING", "b").With("TYPE", type == "JPG" ? "JPEG" : type);
        }

        card.Set("REV", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
        if (card.Get("VERSION") == null) card.Props.Insert(0, new Prop("VERSION", "3.0"));
        else
        {
            // VERSION muss direkt nach BEGIN stehen
            var ver = card.Get("VERSION")!;
            card.Props.Remove(ver);
            card.Props.Insert(0, ver);
        }

        var data = card.ToString();
        if (input.MoveTo is long move && move != coll.Id)
        {
            var dest = PimStore.Find(db, move) ?? throw new KeyNotFoundException();
            PimStore.Put(db, dest, uri, data);
            PimStore.Delete(db, coll, uri);
            return dest.Id + "/" + uri;
        }
        PimStore.Put(db, coll, uri, data, ifMatch: etag);
        return coll.Id + "/" + uri;
    }
}

/// <summary>Adressvorschlaege beim Schreiben: Kontakte und zuletzt benutzte Empfaenger.</summary>
public static class ContactStore
{
    public sealed record Recent(string? Name, string Address, int Count, DateTime Last);
    public sealed record Suggestion(string? Name, string Address, string Source);

    public static void RememberRecipients(UserSpace space, MimeMessage m)
    {
        var list = space.Db.GetJson<List<Recent>>("mail.recent");
        foreach (var a in m.To.Mailboxes.Concat(m.Cc.Mailboxes).Concat(m.Bcc.Mailboxes))
        {
            var i = list.FindIndex(r => r.Address.Equals(a.Address, StringComparison.OrdinalIgnoreCase));
            var name = string.IsNullOrEmpty(a.Name) ? (i >= 0 ? list[i].Name : null) : a.Name;
            if (i >= 0) list[i] = list[i] with { Name = name, Count = list[i].Count + 1, Last = DateTime.UtcNow };
            else list.Add(new Recent(name, a.Address, 1, DateTime.UtcNow));
        }
        space.Db.SetJson("mail.recent", list.OrderByDescending(r => r.Last).Take(500).ToList());
    }

    public static List<Recent> Recents(UserSpace space) => space.Db.GetJson<List<Recent>>("mail.recent");

    /// <summary>Adressen aus den Vorschlaegen nehmen.</summary>
    public static int Forget(UserSpace space, IEnumerable<string> addresses)
    {
        var drop = addresses.Select(a => a.ToLowerInvariant()).ToHashSet();
        var list = Recents(space);
        var n = list.RemoveAll(r => drop.Contains(r.Address.ToLowerInvariant()));
        space.Db.SetJson("mail.recent", list);
        return n;
    }

    /// <summary>Alle Adressen, die schon in einem Adressbuch stehen (klein geschrieben).</summary>
    public static HashSet<string> KnownAddresses(UserSpace space) =>
        space.Db.Query("SELECT emails FROM contacts_fts", r => r.GetString(0))
            .SelectMany(e => e.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(e => e.ToLowerInvariant()).ToHashSet();

    /// <summary>Vorschlaege als Kontakte anlegen; der Name wird in Vor- und Nachname geteilt.</summary>
    public static int AddToBook(UserSpace space, IEnumerable<string> addresses, long book)
    {
        var known = KnownAddresses(space);
        var recents = Recents(space).ToDictionary(r => r.Address.ToLowerInvariant(), r => r);
        int n = 0;
        foreach (var address in addresses.Select(a => a.Trim()).Where(a => a.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (known.Contains(address.ToLowerInvariant())) continue;
            var name = recents.TryGetValue(address.ToLowerInvariant(), out var r) ? r.Name?.Trim() ?? "" : "";
            // "Mueller, Max" oder "Max Mueller"
            string given = "", family = "";
            if (name.Contains(','))
            {
                var p = name.Split(',', 2);
                family = p[0].Trim(); given = p[1].Trim();
            }
            else if (name.Contains(' '))
            {
                var i = name.LastIndexOf(' ');
                given = name[..i].Trim(); family = name[(i + 1)..].Trim();
            }
            else given = name;
            ContactModel.Save(space, new ContactDto
            {
                Book = book, DisplayName = name, Given = given, Family = family,
                Emails = new() { new TypedValue("home", address) }, Phones = new(), Addresses = new(), Urls = new(), Categories = new(),
            });
            n++;
        }
        return n;
    }

    public static List<Suggestion> Suggest(UserSpace space, string q, int limit = 12)
    {
        q = q.Trim();
        if (q.Length == 0) return new();
        var result = new List<Suggestion>();
        var match = string.Join(" ", q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => "\"" + new string(w.Where(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '-').ToArray()) + "\"*")
            .Where(w => w.Length > 3));
        if (match.Length > 0)
        {
            var objs = space.Db.Query("SELECT o.collection, o.uri, o.uid, o.component, o.etag, o.data, o.modified, o.id FROM objects o WHERE o.id IN (SELECT rowid FROM contacts_fts WHERE contacts_fts MATCH $q) LIMIT 50",
                r => new DavObject(r.GetInt64(7), r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)),
                ("$q", match));
            foreach (var c in objs.Select(o => ContactModel.Read(o, false)).Where(c => c != null))
                foreach (var e in c!.Emails)
                    result.Add(new Suggestion(c.DisplayName, e.Value, "contact"));
        }
        foreach (var r in space.Db.GetJson<List<Recent>>("mail.recent").OrderByDescending(r => r.Count))
            if ((r.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || r.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                result.Add(new Suggestion(r.Name, r.Address, "recent"));
        return result.DistinctBy(s => s.Address.ToLowerInvariant()).Take(limit).ToList();
    }
}
