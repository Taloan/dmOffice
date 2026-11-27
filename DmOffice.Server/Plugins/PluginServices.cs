using MailKit;
using System.Collections.Concurrent;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using DmOffice.Core;
using DmOffice.Sdk;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace DmOffice.Plugins;

/// <summary>
/// S9: serverweite Datenbank je Plugin (docs/plugins-konzept.md 10.8, Voraussetzung für CRM, Website, Verein). Datei
/// &lt;Daten&gt;/plugins/&lt;key&gt;.db, verschlüsselt mit einem eigenen Schlüssel (keys/plugin-&lt;key&gt;.key, mit dem Server-Schlüssel
/// eingepackt - wie die Helpdesk-Datenbank). Schema aus <see cref="IDmOfficePlugin.ServerSchema"/>, Stand in plugin_schema.
/// Geht in das Core-Backup (S3Backup) und wird bei der Wiederherstellung zurückgelegt.
/// </summary>
public sealed class ServerDataStore(DataPaths paths, Vault vault) : IServerDataAccessor
{
    private readonly ConcurrentDictionary<string, Lazy<ServerData>> _open = new(StringComparer.Ordinal);

    public string Folder => Path.Combine(paths.Root, "plugins");

    public IServerData For(string pluginKey)
    {
        if (pluginKey == GateKeeper.GateKeeperStore.Key) throw new ArgumentException("Ungültiger Plugin-Schlüssel."); // [feature:gatekeeper]
        return Open(pluginKey);
    }


    internal ServerData Open(string key)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9]{1,30}$")) throw new ArgumentException("Ungültiger Plugin-Schlüssel.");
        return _open.GetOrAdd(key, k => new Lazy<ServerData>(() =>
        {
            Directory.CreateDirectory(Folder);
            var db = new ServerData(UserDb.ConnectionStringOf(Path.Combine(Folder, k + ".db"), KeyOf(k)));
            var steps = PluginHost.Loaded.FirstOrDefault(e => e.Key == k)?.Instance?.ServerSchema ?? [];
            db.Apply(k, steps);
            return db;
        })).Value;
    }

    /// <summary>Datenbanken aller Plugins, die eine haben (für die Sicherung): Schlüssel und Verbindung.</summary>
    public IEnumerable<(string Key, string ConnectionString)> All() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, "*.db").Select(f => Path.GetFileNameWithoutExtension(f)).Where(k => File.Exists(KeyFile(k)))
                .Select(k => (k, UserDb.ConnectionStringOf(Path.Combine(Folder, k + ".db"), KeyOf(k))))
            : [];

    private string KeyFile(string key) => Path.Combine(paths.Keys, "plugin-" + key + ".key");

    private byte[] KeyOf(string key)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = KeyFile(key);
        if (!File.Exists(file))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, vault.Wrap(fresh));
            return fresh;
        }
        return vault.Unwrap(File.ReadAllText(file).Trim());
    }
}

/// <summary>Eine serverweite Plugin-Datenbank: kurze Verbindungen je Aufruf, WAL, gemeinsame Sperre für Schreibvorgänge.</summary>
public sealed class ServerData(string connectionString) : IServerData
{
    private readonly object _write = new();

    internal string ConnectionString => connectionString;

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        UserDb.Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;");
        return conn;
    }

    internal void Apply(string key, IReadOnlyList<SchemaStep> steps)
    {
        lock (_write)
        {
            using var conn = Connect();
            UserDb.Exec(conn, "CREATE TABLE IF NOT EXISTS plugin_schema (key TEXT PRIMARY KEY, version INTEGER NOT NULL)");
            var have = UserDb.Query(conn, "SELECT version FROM plugin_schema WHERE key=$k", r => r.GetInt32(0), ("$k", key)).FirstOrDefault();
            foreach (var step in steps.OrderBy(s => s.Version).Where(s => s.Version > have))
            {
                using var tx = conn.BeginTransaction();
                UserDb.Exec(conn, step.Sql);
                UserDb.Exec(conn, "INSERT INTO plugin_schema (key, version) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET version=excluded.version",
                    ("$k", key), ("$v", step.Version));
                tx.Commit();
            }
        }
    }

    public int Execute(string sql, params (string Name, object? Value)[] args)
    {
        lock (_write) { using var conn = Connect(); return UserDb.Exec(conn, sql, args); }
    }

    public List<T> Query<T>(string sql, Func<IDataRecord, T> read, params (string Name, object? Value)[] args)
    {
        using var conn = Connect();
        return UserDb.Query(conn, sql, r => read(r), args);
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Connect();
        return ScalarOn(conn, sql, args);
    }

    private static object? ScalarOn(SqliteConnection conn, string sql, (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public T Transaction<T>(Func<IServerData, T> work)
    {
        lock (_write)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            var result = work(new InTransaction(conn));
            tx.Commit();
            return result;
        }
    }

    public void SnapshotTo(string file) => S3Backup.Vacuum(connectionString, file);

    /// <summary>Dieselbe Verbindung für alle Schritte einer Transaktion.</summary>
    private sealed class InTransaction(SqliteConnection conn) : IServerData
    {
        public int Execute(string sql, params (string Name, object? Value)[] args) => UserDb.Exec(conn, sql, args);
        public List<T> Query<T>(string sql, Func<IDataRecord, T> read, params (string Name, object? Value)[] args) => UserDb.Query(conn, sql, r => read(r), args);
        public object? Scalar(string sql, params (string Name, object? Value)[] args) => ScalarOn(conn, sql, args);
        public T Transaction<T>(Func<IServerData, T> work) => work(this);
    }
}

/// <summary>S13: Kennungen des Kerns für Plugins (etwa ein Teil eines Plugins hinter eigener Kennung).</summary>
public sealed class CoreFeatures : ICoreFeatures
{
    public bool On(string id) => Features.Known(id) && Features.On(id);
}

/// <summary>S14: Adresse des Servers für Links an Empfänger außerhalb der Browsersitzung.</summary>
public sealed class PluginPublicAddress : IPublicAddress
{
    public string Base(HttpContext http) => PublicAddress.Base(http);
}

/// <summary>S11: Personen auf dem Server für Plugins - nur Kennung, Anmeldename, Anzeigename, Admin.</summary>
public sealed class People(UserStore users) : IPeople
{
    public Person? Me(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true && http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id && users.ById(id) is { } u ? Of(u) : null;

    public IReadOnlyList<Person> All() => users.All().Select(Of).ToList();

    private static Person Of(UserRecord u) => new(u.Id, u.Login, string.IsNullOrWhiteSpace(u.DisplayName) ? u.Login : u.DisplayName, u.IsAdmin);
}

/// <summary>S12: Mail aus dem Konto einer Person senden - per SMTP des Kontos, ohne Kopie in „Gesendet“.</summary>
public sealed class MailOut(Spaces spaces) : IMailOut
{
    public IReadOnlyList<MailAccountInfo> Accounts(string userId) =>
        spaces.Get(userId).Settings.ActiveMailAccounts().Select(a => new MailAccountInfo(a.Id, string.IsNullOrWhiteSpace(a.Name) ? a.Email : a.Name, a.Email)).ToList();

    public async Task SendAsync(string userId, string accountId, OutgoingMail mail, CancellationToken ct)
    {
        var space = spaces.Get(userId);
        var account = space.Settings.ActiveMailAccounts().FirstOrDefault(a => a.Id == accountId) ?? throw new KeyNotFoundException("Mailkonto nicht gefunden oder deaktiviert.");
        if (!MailboxAddress.TryParse(mail.ToAddress, out var to) || string.IsNullOrWhiteSpace(to.Address)) throw new ArgumentException("Ungültige Empfängeradresse.");
        if (!string.IsNullOrWhiteSpace(mail.ToName)) to = new MailboxAddress(mail.ToName, to.Address);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName, account.Email));
        message.To.Add(to);
        message.Subject = mail.Subject;
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(account.Email.Split('@').LastOrDefault() ?? "dmoffice");
        foreach (var (name, value) in mail.Headers ?? new Dictionary<string, string>())
        {
            // Nur zusätzliche Kopfzeilen, keine, die Absender oder Empfänger verändern
            if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Ungültige Kopfzeile.");
            if (name.Equals("From", StringComparison.OrdinalIgnoreCase) || name.Equals("To", StringComparison.OrdinalIgnoreCase) || name.Equals("Cc", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Bcc", StringComparison.OrdinalIgnoreCase) || name.Equals("Sender", StringComparison.OrdinalIgnoreCase) || name.Equals("Reply-To", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.Add(name, value);
        }
        var body = new BodyBuilder { TextBody = mail.Text, HtmlBody = mail.Html };
        message.Body = body.ToMessageBody();
        using var smtp = await Mail.ImapPool.SmtpAsync(account, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
