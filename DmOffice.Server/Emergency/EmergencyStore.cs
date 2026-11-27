using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Emergency;

/// <summary>
/// Ein Notfallpaket (Version 2): was jemand im Notfall bekommt - Ordner zum Lesen, das ganze Konto, die Verwaltung -,
/// wer es anfragen darf und wie lange widersprochen werden kann. Bis dahin sieht niemand etwas.
/// </summary>
public sealed class EmergencyPackage
{
    public string Id { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>fileid der Ordner in der Ablage des Besitzers - gewaehrt wird Lesen.</summary>
    public List<long> Folders { get; set; } = new();
    /// <summary>Das ganze Konto oeffnen (Mails, Kalender, Kontakte, Dateien) - Sicherheitseinstellungen bleiben gesperrt.</summary>
    public bool Account { get; set; }
    /// <summary>Verwaltung: der Anfragende wird Admin (wichtig, wenn der Besitzer der einzige ist).</summary>
    public bool Admin { get; set; }
    public List<string> Requesters { get; set; } = new();
    /// <summary>Personen ohne Konto, die anfragen dürfen: sie fragen über ihren eigenen Link an (/n/…); beim Gewähren bekommen sie ein Konto.</summary>
    public List<EmergencyContact> Contacts { get; set; } = new();
    /// <summary>Adresse des Servers beim letzten Speichern - für Links in Mails, die ohne Anfrage im Hintergrund gehen.</summary>
    public string? LinkBase { get; set; }
    /// <summary>Widersprechen duerfen der Besitzer und alle, mit denen die Ordner ohnehin geteilt sind - plus diese ...</summary>
    public List<string> ExtraVetoers { get; set; } = new();
    /// <summary>... minus diese (aus den Ordnerfreigaben herausgenommen, etwa nach einem Streit).</summary>
    public List<string> NoVeto { get; set; } = new();
    public int WaitDays { get; set; } = 7;
    public DateTime Created { get; set; }

    // Verschlüsselter Inhalt (Notfalltext, Referenzkopien) - siehe EmergencyVault. Ohne Schlüssel: nur Ordner, Konto, Verwaltung.
    /// <summary>Öffentlicher Schlüssel (SPKI, Base64): damit verschlüsselt der Server Text und Kopien.</summary>
    public string? PublicKey { get; set; }
    /// <summary>Privater Schlüssel, verpackt mit dem Paketschlüssel (Base64).</summary>
    public string? SealedPrivate { get; set; }
    /// <summary>Hälfte des Servers (Base64) - geht erst nach Ablauf der Frist in den Paketschlüssel ein.</summary>
    public string? ServerHalf { get; set; }
    public string? Salt { get; set; }
    public bool HasText { get; set; }
    public DateTime? TextChanged { get; set; }
    public List<EmergencyFile> Files { get; set; } = new();
}

/// <summary>
/// Person ohne Konto, die anfragen darf. Sie hat einen eigenen Link (nur der Hash und eine mit dem Server-Schlüssel verpackte Kopie
/// für den Besitzer liegen hier); angefordert wird über den Link mit dem Notfallcode des Pakets. <see cref="Email"/> (freiwillig)
/// bekommt nur Hinweise. Läuft die Frist ab, legt dmOffice ein Konto an (<see cref="UserId"/>); das Passwort setzt die Person
/// über ihren Link, wieder mit dem Notfallcode.
/// </summary>
public sealed class EmergencyContact
{
    /// <summary>Als Anfragender: <see cref="EmergencyStore.ContactPrefix"/> + Id.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string WrappedToken { get; set; } = "";
    public string? UserId { get; set; }
    public bool PasswordSet { get; set; }
}

/// <summary>Referenzkopie: verschlüsselte Kopie einer Datei aus der Ablage, die sich ihre Herkunft merkt.</summary>
public sealed class EmergencyFile
{
    public string Id { get; set; } = "";
    /// <summary>fileid der Herkunft in der Ablage des Besitzers</summary>
    public long Source { get; set; }
    /// <summary>Pfad der Herkunft beim letzten Kopieren - wird die Datei ersetzt (neue fileid), findet er sie wieder.</summary>
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string? Etag { get; set; }
    public DateTime Copied { get; set; }
    /// <summary>Herkunft gelöscht oder verschoben: die letzte Kopie bleibt.</summary>
    public bool Missing { get; set; }
}

public sealed class EmergencyRequest
{
    public const string Open = "open", Denied = "denied", Granted = "granted", Withdrawn = "withdrawn", Revoked = "revoked";

    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Requester { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Due { get; set; }
    public string State { get; set; } = Open;
    public string? DecidedBy { get; set; }
    public DateTime? Decided { get; set; }
    public DateTime? LastReminder { get; set; }
    /// <summary>Ordner, in denen der Anfragende durch den Notfall neu Mitglied wurde - nur die nimmt Zuruecknehmen wieder weg.</summary>
    public List<long> AddedFolders { get; set; } = new();
    public bool MadeAdmin { get; set; }
}

public sealed record EmergencyLogEntry(DateTime At, string Package, string? Request, string Who, string What);

/// <summary>
/// Notfallpakete, Anfragen und Protokoll - data/emergency.json. Ein Widerspruch genuegt; laeuft die Frist ohne Widerspruch
/// ab, gewaehrt <see cref="EmergencyService"/> genau den Inhalt des Pakets.
/// </summary>
public sealed class EmergencyStore
{
    private sealed class Data
    {
        public List<EmergencyPackage> Packages { get; set; } = new();
        public List<EmergencyRequest> Requests { get; set; } = new();
        public List<EmergencyLogEntry> Log { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    /// <summary>Nach einem Widerspruch darf dieselbe Person erst nach dieser Zeit wieder anfragen.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);
    private const int MaxLog = 2000;
    /// <summary>Anfragender ohne Konto: "c:" + Kennung der Person im Paket.</summary>
    public const string ContactPrefix = "c:";
    public const int MaxContacts = 20;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly FolderShareStore _shares;
    private readonly UserStore _users;
    private Data _data;

    public EmergencyStore(DataPaths paths, FolderShareStore shares, UserStore users)
    {
        _path = Path.Combine(paths.Root, "emergency.json");
        _shares = shares;
        _users = users;
        _data = File.Exists(_path) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    // ---------------- Lesen ----------------

    public EmergencyPackage? Package(string id) { lock (_lock) return _data.Packages.FirstOrDefault(p => p.Id == id) is { } p ? Clone(p) : null; }
    public EmergencyRequest? Request(string id) { lock (_lock) return _data.Requests.FirstOrDefault(r => r.Id == id) is { } r ? Clone(r) : null; }
    public List<EmergencyPackage> OwnedBy(string owner) { lock (_lock) return _data.Packages.Where(p => p.Owner == owner).Select(Clone).ToList(); }
    public List<EmergencyPackage> RequestableBy(string user) { lock (_lock) return _data.Packages.Where(p => p.Requesters.Contains(user)).Select(Clone).ToList(); }

    /// <summary>Darf <paramref name="requester"/> (Benutzer oder Person ohne Konto, solange sie noch keins hat) dieses Paket anfragen?</summary>
    public static bool MayAsk(EmergencyPackage p, string requester) =>
        p.Requesters.Contains(requester) || requester.StartsWith(ContactPrefix, StringComparison.Ordinal)
            && p.Contacts.Any(c => ContactPrefix + c.Id == requester && c.UserId == null);

    /// <summary>Person ohne Konto zur Kennung "c:…" - mit ihrem Paket.</summary>
    public (EmergencyPackage Package, EmergencyContact Contact)? Contact(string requester)
    {
        if (!requester.StartsWith(ContactPrefix, StringComparison.Ordinal)) return null;
        lock (_lock)
            foreach (var p in _data.Packages)
                if (p.Contacts.FirstOrDefault(c => ContactPrefix + c.Id == requester) is { } c) return (Clone(p), Clone(c));
        return null;
    }

    /// <summary>Person ohne Konto, für die beim Gewähren dieses Konto angelegt wurde.</summary>
    public (EmergencyPackage Package, EmergencyContact Contact)? ContactByUser(string userId)
    {
        lock (_lock)
            foreach (var p in _data.Packages)
                if (p.Contacts.FirstOrDefault(c => c.UserId == userId) is { } c) return (Clone(p), Clone(c));
        return null;
    }

    /// <summary>Person zum Link - null, wenn es den Link nicht (mehr) gibt.</summary>
    public (EmergencyPackage Package, EmergencyContact Contact)? ByToken(string token)
    {
        if (token.Length is < 32 or > 64) return null;
        var hash = Passwords.TokenHash(token);
        lock (_lock)
            foreach (var p in _data.Packages)
                foreach (var c in p.Contacts)
                    if (c.TokenHash.Length == hash.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.ASCII.GetBytes(c.TokenHash), System.Text.Encoding.ASCII.GetBytes(hash)))
                        return (Clone(p), Clone(c));
        return null;
    }

    /// <summary>Der Link einer Person - nur für den Besitzer (Link kopieren).</summary>
    public string TokenOf(EmergencyContact c) => System.Text.Encoding.UTF8.GetString(_users.Unwrap(c.WrappedToken));
    public List<EmergencyRequest> RequestsOf(string package) { lock (_lock) return _data.Requests.Where(r => r.Package == package).OrderByDescending(r => r.Created).Select(Clone).ToList(); }
    public List<EmergencyRequest> OpenRequests() { lock (_lock) return _data.Requests.Where(r => r.State == EmergencyRequest.Open).Select(Clone).ToList(); }
    public List<EmergencyLogEntry> LogOf(string package) { lock (_lock) return _data.Log.Where(l => l.Package == package).OrderByDescending(l => l.At).ToList(); }

    /// <summary>Wer widersprechen darf: der Besitzer, die Mitglieder der Ordnerfreigaben, die zusaetzlich Genannten - ohne die Herausgenommenen.</summary>
    public HashSet<string> Vetoers(EmergencyPackage p)
    {
        var set = new HashSet<string> { p.Owner };
        foreach (var f in p.Folders)
            if (_shares.Of(p.Owner, f) is { } s) set.UnionWith(s.Members);
        set.UnionWith(p.ExtraVetoers);
        set.ExceptWith(p.NoVeto.Where(x => x != p.Owner));
        // Wer durch einen Notfall Mitglied wurde, darf dadurch nicht widersprechen
        lock (_lock)
            foreach (var r in _data.Requests.Where(r => r.Package != p.Id && r.State == EmergencyRequest.Granted && r.AddedFolders.Count > 0))
                if (_data.Packages.FirstOrDefault(x => x.Id == r.Package)?.Owner == p.Owner && r.AddedFolders.Intersect(p.Folders).Any() && !p.ExtraVetoers.Contains(r.Requester))
                    set.Remove(r.Requester);
        return set;
    }

    /// <summary>Darf <paramref name="user"/> gegen diese Anfrage widersprechen? Nie gegen die eigene.</summary>
    public bool MayDecide(EmergencyRequest r, string user) =>
        r.Requester != user && Package(r.Package) is { } p && Vetoers(p).Contains(user);

    /// <summary>Offene Anfragen, bei denen <paramref name="user"/> widersprechen darf - fuer das rote Abzeichen.</summary>
    public List<EmergencyRequest> ToDecide(string user) => OpenRequests().Where(r => MayDecide(r, user)).ToList();

    /// <summary>Notfallsitzung im Konto des Besitzers: nur solange die Anfrage gewaehrt ist und das Paket das Konto umfasst.</summary>
    public bool MayEnter(string requestId, string ownerId) =>
        Request(requestId) is { State: EmergencyRequest.Granted } r && Package(r.Package) is { Account: true } p && p.Owner == ownerId;

    // ---------------- Pakete ----------------

    /// <summary>Anlegen (ohne Id) oder aendern - nur der Besitzer. Danach: Widersprueche Herausgenommener zaehlen nicht mehr.</summary>
    public EmergencyPackage Save(string owner, EmergencyPackage input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > 100) throw new ArgumentException("Bitte einen Namen angeben.");
        if (input.WaitDays is < 1 or > 90) throw new ArgumentException("Die Frist muss zwischen 1 und 90 Tagen liegen.");
        var requesters = input.Requesters.Where(x => x != owner && _users.ById(x) != null).Distinct().ToList();
        var contacts = new List<EmergencyContact>();
        foreach (var c in input.Contacts)
        {
            var cname = (c.Name ?? "").Trim();
            var email = (c.Email ?? "").Trim().ToLowerInvariant();
            if (cname.Length == 0 || cname.Length > 100) throw new ArgumentException("Bitte für jeden Benutzer ohne Konto einen Namen angeben.");
            // Die Mailadresse ist freiwillig: dmOffice schickt darüber nur Hinweise, angefordert wird mit dem Notfallcode
            if (email.Length > 0 && !ValidEmail(email)) throw new ArgumentException($"Die E-Mail-Adresse „{c.Email}“ stimmt nicht.");
            if (email.Length > 0 && contacts.Any(x => x.Email == email)) continue;
            contacts.Add(new EmergencyContact { Id = c.Id ?? "", Name = cname, Email = email });
        }
        if (contacts.Count > MaxContacts) throw new ArgumentException($"Ein Paket kann höchstens {MaxContacts} Benutzer ohne Konto enthalten.");
        if (requesters.Count == 0 && contacts.Count == 0) throw new ArgumentException("Bitte mindestens einen Benutzer angeben, der das Paket anfordern darf.");
        if (input.Folders.Count == 0 && !input.Account && !input.Admin && !input.HasText && input.Files.Count == 0)
            throw new ArgumentException("Bitte festlegen, was im Notfall freigegeben wird.");
        string id;
        lock (_lock)
        {
            var p = input.Id.Length > 0 ? _data.Packages.FirstOrDefault(x => x.Id == input.Id) : null;
            if (input.Id.Length > 0 && (p == null || p.Owner != owner)) throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
            if (p == null)
            {
                p = new EmergencyPackage { Id = Guid.NewGuid().ToString("N")[..16], Owner = owner, Created = DateTime.UtcNow };
                _data.Packages.Add(p);
            }
            p.Name = name;
            p.Folders = input.Folders.Distinct().ToList();
            p.Account = input.Account;
            p.Admin = input.Admin;
            p.Requesters = requesters;
            // Personen ohne Konto: Bekannte behalten Link und Konto, neue bekommen einen Link; Entfernte verlieren ihre offene Anfrage
            var kept = new List<EmergencyContact>();
            foreach (var c in contacts)
            {
                var old = c.Id.Length > 0 ? p.Contacts.FirstOrDefault(x => x.Id == c.Id) : null;
                if (old != null)
                {
                    old.Name = c.Name; old.Email = c.Email;
                    kept.Add(old);
                    continue;
                }
                var token = NewToken();
                kept.Add(new EmergencyContact
                {
                    Id = Guid.NewGuid().ToString("N")[..12], Name = c.Name, Email = c.Email,
                    TokenHash = Passwords.TokenHash(token), WrappedToken = _users.Wrap(System.Text.Encoding.UTF8.GetBytes(token)),
                });
            }
            foreach (var gone in p.Contacts.Where(x => kept.All(k => k.Id != x.Id))) EndOpen(p.Id, ContactPrefix + gone.Id, owner);
            p.Contacts = kept;
            if (input.LinkBase != null) p.LinkBase = input.LinkBase;
            p.ExtraVetoers = input.ExtraVetoers.Where(x => x != owner && _users.ById(x) != null).Distinct().ToList();
            p.NoVeto = input.NoVeto.Where(x => x != owner).Distinct().ToList();
            p.WaitDays = input.WaitDays;
            AddLog(p.Id, null, owner, "Paket gespeichert");
            Save();
            id = p.Id;
        }
        var saved = Package(id)!;
        ReopenStaleDenials(saved);
        return saved;
    }

    public static bool ValidEmail(string email) =>
        email.Length is >= 3 and <= 254 && MimeKit.MailboxAddress.TryParse(email, out var m) && m.Address == email && email.Contains('@') && !email.Contains(' ');

    private static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Offene Anfrage einer Person endet (unter der Sperre).</summary>
    private void EndOpen(string package, string requester, string owner)
    {
        foreach (var r in _data.Requests.Where(r => r.Package == package && r.Requester == requester && r.State == EmergencyRequest.Open))
        {
            r.State = EmergencyRequest.Withdrawn; r.Decided = DateTime.UtcNow;
            AddLog(package, r.Id, owner, "Anfrage beendet (Berechtigter entfernt oder Adresse geändert)");
        }
    }

    /// <summary>Paket loeschen: alles, was es gewaehrt hat, wird zurueckgenommen.</summary>
    public void Delete(string owner, string id)
    {
        var p = Package(id);
        if (p == null || p.Owner != owner) throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
        foreach (var r in RequestsOf(id).Where(r => r.State is EmergencyRequest.Granted or EmergencyRequest.Open)) Revoke(r.Id, owner);
        lock (_lock)
        {
            _data.Packages.RemoveAll(x => x.Id == id);
            AddLog(id, null, owner, "Paket gelöscht");
            Save();
        }
    }

    /// <summary>
    /// Wer nicht mehr widersprechen darf, dessen Widerspruch zaehlt nicht mehr: die Frist laeuft weiter, als haette er nie
    /// widersprochen (nur fuer juengere Widersprueche - eine Ablehnung von vor Monaten lebt nicht wieder auf).
    /// </summary>
    private void ReopenStaleDenials(EmergencyPackage p)
    {
        var vetoers = Vetoers(p);
        lock (_lock)
        {
            var changed = false;
            foreach (var r in _data.Requests.Where(r => r.Package == p.Id && r.State == EmergencyRequest.Denied && r.DecidedBy != null
                         && !vetoers.Contains(r.DecidedBy) && r.Decided > DateTime.UtcNow.AddDays(-2 * p.WaitDays)))
            {
                AddLog(p.Id, r.Id, p.Owner, "Widerspruch zählt nicht mehr (" + r.DecidedBy + " darf nicht mehr widersprechen)");
                r.State = EmergencyRequest.Open; r.DecidedBy = null; r.Decided = null; r.LastReminder = null;
                changed = true;
            }
            if (changed) Save();
        }
    }

    /// <summary>Inhalt ändern (Schlüssel, Text, Kopien) - nur der Besitzer; der Rest des Pakets bleibt.</summary>
    public EmergencyPackage Change(string owner, string id, Action<EmergencyPackage> change, string? what = null)
    {
        lock (_lock)
        {
            var p = _data.Packages.FirstOrDefault(x => x.Id == id && x.Owner == owner) ?? throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
            change(p);
            if (what != null) AddLog(p.Id, null, owner, what);
            Save();
            return Clone(p);
        }
    }

    // ---------------- Anfragen ----------------

    public EmergencyRequest Ask(string packageId, string requester)
    {
        lock (_lock)
        {
            var p = _data.Packages.FirstOrDefault(x => x.Id == packageId && MayAsk(x, requester))
                    ?? throw new KeyNotFoundException("Notfallzugang nicht gefunden.");
            var mine = _data.Requests.Where(r => r.Package == p.Id && r.Requester == requester).OrderByDescending(r => r.Created).FirstOrDefault();
            if (mine?.State is EmergencyRequest.Open) throw new InvalidOperationException("Die Anfrage läuft schon.");
            if (mine?.State is EmergencyRequest.Granted) throw new InvalidOperationException("Der Zugriff ist schon gewährt.");
            if (mine?.State is EmergencyRequest.Denied && mine.Decided > DateTime.UtcNow - Cooldown)
                throw new InvalidOperationException("Gerade wurde bestätigt, dass kein Notfall vorliegt – eine neue Anfrage geht erst morgen wieder.");
            var r = new EmergencyRequest
            {
                Id = Guid.NewGuid().ToString("N")[..16], Package = p.Id, Requester = requester,
                Created = DateTime.UtcNow, Due = DateTime.UtcNow.AddDays(p.WaitDays),
            };
            _data.Requests.Add(r);
            AddLog(p.Id, r.Id, requester, "Notfallzugang angefordert");
            Save();
            return Clone(r);
        }
    }

    /// <summary>„Kein Notfall“ - ein Widerspruch genuegt.</summary>
    public EmergencyRequest Deny(string requestId, string user)
    {
        var r = Request(requestId) ?? throw new KeyNotFoundException("Anfrage nicht gefunden.");
        if (r.State != EmergencyRequest.Open) throw new InvalidOperationException("Über diese Anfrage ist schon entschieden.");
        if (!MayDecide(r, user)) throw new UnauthorizedAccessException("Du darfst bei dieser Anfrage nicht widersprechen.");
        return Set(requestId, x => { x.State = EmergencyRequest.Denied; x.DecidedBy = user; x.Decided = DateTime.UtcNow; }, user, "Kein Notfall bestätigt");
    }

    public EmergencyRequest Withdraw(string requestId, string requester)
    {
        var r = Request(requestId) ?? throw new KeyNotFoundException("Anfrage nicht gefunden.");
        if (r.Requester != requester || r.State != EmergencyRequest.Open) throw new InvalidOperationException("Diese Anfrage läuft nicht mehr.");
        return Set(requestId, x => { x.State = EmergencyRequest.Withdrawn; x.Decided = DateTime.UtcNow; }, requester, "Anfrage zurückgezogen");
    }

    /// <summary>Frist abgelaufen: genau den Inhalt des Pakets gewaehren.</summary>
    /// <summary>Ebene einer Person: Benutzer &lt; Administrator. Kommen Rollen dazu, hier einordnen.</summary>
    public static int RankOf(UserRecord u) => u.IsAdmin ? 2 : 1;

    /// <summary>Ebene, die ein Paket weitergibt: Verwaltung = Administrator, sonst Benutzer (Ordner, Konto).</summary>
    public static int RankOf(bool admin) => admin ? 2 : 1;

    /// <summary>Im Notfallpaket nur Rechte bis zur eigenen Ebene - nie mehr, als man selbst hat.</summary>
    public static bool MayGrant(UserRecord owner, bool admin) => RankOf(owner) >= RankOf(admin);

    public EmergencyRequest Grant(string requestId)
    {
        var r = Request(requestId) ?? throw new KeyNotFoundException("Anfrage nicht gefunden.");
        var p = Package(r.Package) ?? throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
        if (r.State != EmergencyRequest.Open) return r;
        // Person ohne Konto: jetzt bekommt sie eins - ab hier gilt alles wie bei Personen mit Konto
        if (r.Requester.StartsWith(ContactPrefix, StringComparison.Ordinal))
        {
            r = AccountFor(r);
            p = Package(r.Package)!;
        }
        var added = new List<long>();
        foreach (var f in p.Folders)
        {
            var s = _shares.Of(p.Owner, f);
            var members = s?.Members ?? new List<string>();
            if (members.Contains(r.Requester)) continue;
            _shares.Set(p.Owner, f, members.Append(r.Requester), (s?.ReadOnly ?? new List<string>()).Append(r.Requester), s?.ShowOnly);
            added.Add(f);
        }
        var madeAdmin = false;
        // Nur, wenn der Besitzer beim Gewähren noch auf dieser Ebene steht
        if (p.Admin && _users.ById(p.Owner) is { } owner && MayGrant(owner, true) && _users.ById(r.Requester) is { IsAdmin: false })
        {
            _users.Update(r.Requester, u => u.IsAdmin = true);
            madeAdmin = true;
        }
        // Erst hier unter der Sperre entscheiden: Kam inzwischen ein „Kein Notfall“ oder ein Zurückziehen dazwischen, gilt das -
        // dann nimmt dieser Aufruf zurück, was er selbst hinzugefügt hat, und überschreibt nichts
        lock (_lock)
        {
            var cur = _data.Requests.FirstOrDefault(x => x.Id == requestId) ?? throw new KeyNotFoundException("Anfrage nicht gefunden.");
            if (cur.State == EmergencyRequest.Open)
            {
                cur.State = EmergencyRequest.Granted; cur.Decided = DateTime.UtcNow; cur.AddedFolders = added; cur.MadeAdmin = madeAdmin;
                AddLog(cur.Package, cur.Id, "dmoffice", "Frist abgelaufen – Zugriff gewährt");
                Save();
                return Clone(cur);
            }
            r = Clone(cur);
        }
        foreach (var f in added)
            if (_shares.Of(p.Owner, f) is { } s)
                _shares.Set(p.Owner, f, s.Members.Where(m => m != r.Requester), s.ReadOnly.Where(m => m != r.Requester), s.ShowOnly);
        if (madeAdmin) _users.Update(r.Requester, u => u.IsAdmin = false);
        return r;
    }

    /// <summary>
    /// Konto für eine Person ohne Konto: Anmeldename aus der Mailadresse, Zufallspasswort (das eigene setzt sie über ihren Link).
    /// Paket und alle ihre Anfragen zeigen danach auf das Konto.
    /// </summary>
    private EmergencyRequest AccountFor(EmergencyRequest r)
    {
        var who = r.Requester;
        var found = Contact(who) ?? throw new KeyNotFoundException("Berechtigter nicht gefunden.");
        var c = found.Contact;
        var user = c.UserId is { } existing && _users.ById(existing) != null ? _users.ById(existing)!
            : _users.Create(FreeLogin(c.Email.Length > 0 ? c.Email : c.Name), c.Name, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)), false);
        lock (_lock)
        {
            if (_data.Packages.FirstOrDefault(x => x.Id == r.Package) is { } p)
            {
                if (p.Contacts.FirstOrDefault(x => ContactPrefix + x.Id == who) is { } pc) pc.UserId = user.Id;
                if (!p.Requesters.Contains(user.Id)) p.Requesters.Add(user.Id);
            }
            foreach (var x in _data.Requests.Where(x => x.Requester == who)) x.Requester = user.Id;
            AddLog(r.Package, r.Id, "dmoffice", $"Konto „{user.Login}“ für {c.Name} angelegt");
            Save();
        }
        return Request(r.Id)!;
    }

    /// <summary>Freier Anmeldename aus dem Teil vor dem @ oder dem Namen (nur erlaubte Zeichen), bei Bedarf mit Zahl.</summary>
    private string FreeLogin(string source)
    {
        var plain = source.Split('@')[0].ToLowerInvariant().Replace(' ', '.').Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var local = new string(plain.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray());
        while (local.Contains("..")) local = local.Replace("..", ".");
        local = local.TrimStart('.', '-', '_');
        if (local.Length > 34) local = local[..34];
        if (local.Length < 2) local = "notfall";
        for (var i = 1; ; i++)
        {
            var login = i == 1 ? local : local + i;
            if (UserStore.ValidLogin(login) && _users.ByLogin(login) == null) return login;
        }
    }

    /// <summary>Eigenes Passwort über den Link - einmal; danach meldet sich die Person wie alle an.</summary>
    public UserRecord SetPassword(string package, string contactId, string password)
    {
        if (password.Length < 8) throw new ArgumentException("Passwort: mindestens 8 Zeichen.");
        if (UserStore.PasswordProblem(password) is { } weak) throw new ArgumentException(weak);
        string userId;
        lock (_lock)
        {
            var c = _data.Packages.FirstOrDefault(x => x.Id == package)?.Contacts.FirstOrDefault(x => x.Id == contactId)
                    ?? throw new KeyNotFoundException("Berechtigter nicht gefunden.");
            if (c.UserId == null || c.PasswordSet) throw new InvalidOperationException("Das Passwort lässt sich hier nicht (mehr) festlegen.");
            userId = c.UserId;
            c.PasswordSet = true;
            AddLog(package, null, ContactPrefix + c.Id, "Passwort festgelegt");
            Save();
        }
        _users.Update(userId, u => { u.PasswordHash = Passwords.Hash(password); u.NoOwnPassword = false; u.NewStamp(); });
        return _users.ById(userId)!;
    }

    /// <summary>
    /// Zuruecknehmen: der Besitzer oder wer widersprechen darf. Gewaehrtes geht wieder weg (nur was der Notfall hinzugefuegt
    /// hat); eine offene Anfrage endet wie mit einem Widerspruch.
    /// </summary>
    public EmergencyRequest Revoke(string requestId, string user)
    {
        var r = Request(requestId) ?? throw new KeyNotFoundException("Anfrage nicht gefunden.");
        var p = Package(r.Package) ?? throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
        if (user != p.Owner && !MayDecide(r, user)) throw new UnauthorizedAccessException("Du darfst diesen Zugriff nicht zurücknehmen.");
        if (r.State == EmergencyRequest.Open) return Deny(requestId, user);
        if (r.State != EmergencyRequest.Granted) throw new InvalidOperationException("Hier ist nichts mehr zurückzunehmen.");
        foreach (var f in r.AddedFolders)
            if (_shares.Of(p.Owner, f) is { } s && s.Members.Contains(r.Requester))
                _shares.Set(p.Owner, f, s.Members.Where(m => m != r.Requester), s.ReadOnly.Where(m => m != r.Requester), s.ShowOnly.Where(m => m != r.Requester));
        if (r.MadeAdmin) _users.Update(r.Requester, u => u.IsAdmin = false);
        return Set(requestId, x => { x.State = EmergencyRequest.Revoked; x.DecidedBy = user; x.Decided = DateTime.UtcNow; }, user, "Zugriff zurückgenommen");
    }

    public void Reminded(string requestId) => Set(requestId, x => x.LastReminder = DateTime.UtcNow, null, null);

    public void Logged(string package, string? request, string who, string what) { lock (_lock) { AddLog(package, request, who, what); Save(); } }

    /// <summary>Benutzer geloescht: seine Pakete und Anfragen weg, aus fremden Paketen heraus.</summary>
    public void Forget(string user)
    {
        lock (_lock)
        {
            var gone = _data.Packages.Where(p => p.Owner == user).Select(p => p.Id).ToHashSet();
            _data.Packages.RemoveAll(p => gone.Contains(p.Id));
            _data.Requests.RemoveAll(r => gone.Contains(r.Package) || r.Requester == user);
            foreach (var p in _data.Packages)
            {
                p.Requesters.Remove(user); p.ExtraVetoers.Remove(user); p.NoVeto.Remove(user);
                // Konto einer Person ohne Konto gelöscht: sie fragt wieder über ihren Link an
                foreach (var c in p.Contacts.Where(c => c.UserId == user)) { c.UserId = null; c.PasswordSet = false; }
            }
            Save();
        }
    }

    // ---------------- intern ----------------

    private EmergencyRequest Set(string requestId, Action<EmergencyRequest> change, string? who, string? what)
    {
        lock (_lock)
        {
            var r = _data.Requests.FirstOrDefault(x => x.Id == requestId) ?? throw new KeyNotFoundException("Anfrage nicht gefunden.");
            change(r);
            if (who != null && what != null) AddLog(r.Package, r.Id, who, what);
            Save();
            return Clone(r);
        }
    }

    private void AddLog(string package, string? request, string who, string what)
    {
        _data.Log.Add(new EmergencyLogEntry(DateTime.UtcNow, package, request, who, what));
        if (_data.Log.Count > MaxLog) _data.Log.RemoveRange(0, _data.Log.Count - MaxLog);
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static EmergencyPackage Clone(EmergencyPackage p) => JsonSerializer.Deserialize<EmergencyPackage>(JsonSerializer.Serialize(p))!;
    private static EmergencyContact Clone(EmergencyContact c) => JsonSerializer.Deserialize<EmergencyContact>(JsonSerializer.Serialize(c))!;
    private static EmergencyRequest Clone(EmergencyRequest r) => JsonSerializer.Deserialize<EmergencyRequest>(JsonSerializer.Serialize(r))!;
}
