using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Siegel für die Benutzerliste (users.json) und die Server-Einstellungen (server.json): Jedes Schreiben durch dmOffice legt
/// in <see cref="FileName"/> einen HMAC-SHA256 des Inhalts ab, mit einem aus dem Hauptschlüssel abgeleiteten Schlüssel. Beim
/// Start prüft der Server (Schalter cfgseal), ob die Dateien noch so sind, wie dmOffice sie geschrieben hat - eine von außen
/// geänderte, gelöschte oder ausgetauschte Datei hält den Start an, statt still zu gelten.
/// <para>
/// Absturzsicher: Das Siegel nennt den neuen und den vorigen Wert und wird vor dem Umbenennen der Datei geschrieben; der
/// nächste geprüfte Start räumt den vorigen weg. Fehlt das Siegel ganz (erster Start mit dem Schalter, Sicherung von vorher),
/// versiegelt der Start die vorhandenen Dateien und schreibt eine Warnung ins Protokoll. Ausweg bei einer gewollten Änderung
/// (Datei von Hand bearbeitet, ältere Sicherung zurückgelegt, Rückkehr von einer älteren Version): <see cref="Command"/>.
/// </para>
/// Lesen und Schreiben dieser Dateien laufen über <see cref="Save"/>; hier setzt auch die spätere Verschlüsselung an
/// (<see cref="Seal.Version"/>).
/// </summary>
public static class ConfigSeal
{
    public const string FileName = "config.seal";
    /// <summary>Befehl zum Neuversiegeln: dotnet DmOffice.Server.dll reseal-config (im Container: docker run … reseal-config)</summary>
    public const string Command = "reseal-config";
    public static readonly string[] Files = ["users.json", "server.json"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    public sealed class Entry
    {
        public string Mac { get; set; } = "";
        /// <summary>Stand vor dem letzten Schreiben - nur bis zum nächsten Start gültig (Absturz zwischen Siegel und Datei).</summary>
        public string? Previous { get; set; }
    }

    public sealed class Seal
    {
        /// <summary>1 = HMAC-SHA256 über den Klartext. Eine verschlüsselte Ablage bekommt eine neue Nummer.</summary>
        public int Version { get; set; } = 1;
        public string Algorithm { get; set; } = "HMAC-SHA256";
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public enum State { Ok, SealedNow, Refused }

    public sealed record Result(State State, IReadOnlyList<string> Files, string? Message);

    /// <summary>Datei schreiben (atomar über .tmp) und das Siegel nachführen. Ohne Tresor (Werkzeuge, Tests) nur schreiben.</summary>
    public static void Save(string path, string text, Vault? vault)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (vault != null) Record(Path.GetDirectoryName(path)!, Path.GetFileName(path), bytes, vault.Key);
        File.Move(tmp, path, overwrite: true);
    }

    private static string Mac(byte[] key, string name, byte[] content)
    {
        var sealKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: Encoding.UTF8.GetBytes("dmOffice.cfgseal.v1"));
        using var h = new HMACSHA256(sealKey);
        var head = Encoding.UTF8.GetBytes(name.ToLowerInvariant() + "\n");
        h.TransformBlock(head, 0, head.Length, null, 0);
        h.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(h.Hash!);
    }

    private static string SealPath(string dir) => Path.Combine(dir, FileName);

    /// <summary>Null: kein Siegel da. Wirft bei einem unlesbaren Siegel.</summary>
    private static Seal? Load(string dir)
    {
        var p = SealPath(dir);
        if (!File.Exists(p)) return null;
        var s = JsonSerializer.Deserialize<Seal>(File.ReadAllText(p), Json) ?? throw new JsonException("leer");
        if (s.Version != 1) throw new JsonException($"Version {s.Version} unbekannt");
        s.Entries = new Dictionary<string, Entry>(s.Entries, StringComparer.OrdinalIgnoreCase);
        return s;
    }

    private static void Store(string dir, Seal s)
    {
        var p = SealPath(dir);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(s, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(p + ".tmp", p, overwrite: true);
    }

    internal static void Record(string dir, string name, byte[] content, byte[] key)
    {
        lock (Gate)
        {
            Seal? s;
            try { s = Load(dir); }
            // Unlesbares Siegel: nicht stillschweigend ersetzen - der nächste Start meldet es
            catch (JsonException) { return; }
            if (s == null)
            {
                // Noch kein Siegel (Schalter aus): die übrigen Dateien gleich mit, damit das Siegel vollständig ist
                s = new Seal();
                foreach (var other in Files.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    if (File.Exists(Path.Combine(dir, other))) s.Entries[other] = new Entry { Mac = Mac(key, other, File.ReadAllBytes(Path.Combine(dir, other))) };
            }
            var mac = Mac(key, name, content);
            var old = s.Entries.GetValueOrDefault(name);
            s.Entries[name] = new Entry { Mac = mac, Previous = old?.Mac is { } m && m != mac ? m : old?.Previous };
            Store(dir, s);
        }
    }

    /// <summary>Alle vorhandenen Dateien mit diesem Schlüssel versiegeln (ohne vorige Werte). Rückgabe: versiegelte Dateien.</summary>
    public static List<string> Reseal(string dir, byte[] key, IEnumerable<string>? only = null)
    {
        lock (Gate)
        {
            Seal s;
            try { s = Load(dir) ?? new Seal(); }
            catch (JsonException) { s = new Seal(); }
            var done = new List<string>();
            foreach (var name in only ?? Files)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) { s.Entries[name] = new Entry { Mac = Mac(key, name, File.ReadAllBytes(p)) }; done.Add(name); }
                else s.Entries.Remove(name);
            }
            Store(dir, s);
            return done;
        }
    }

    /// <summary>Dateien, deren Inhalt zum Siegel unter diesem Schlüssel passt (für den Umstieg auf einen neuen Hauptschlüssel).</summary>
    public static List<string> ValidUnder(string dir, byte[] key)
    {
        try
        {
            if (Load(dir) is not { } s) return [];
            return Files.Where(n => File.Exists(Path.Combine(dir, n)) && s.Entries.TryGetValue(n, out var e)
                                    && Matches(e, Mac(key, n, File.ReadAllBytes(Path.Combine(dir, n))))).ToList();
        }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }

    private static bool Matches(Entry e, string mac) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(e.Mac), Encoding.ASCII.GetBytes(mac))
        || (e.Previous != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(e.Previous), Encoding.ASCII.GetBytes(mac)));

    /// <summary>
    /// Prüfung beim Start, bevor Benutzerliste und Einstellungen geladen werden. Passt alles, werden vorige Werte und ein noch
    /// mit dem früheren Hauptschlüssel gebildetes Siegel nachgezogen.
    /// </summary>
    public static Result Check(DataPaths paths, Vault vault)
    {
        var dir = paths.Root;
        lock (Gate)
        {
            Seal? s;
            try { s = Load(dir); }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                return new Result(State.Refused, [FileName], Refusal([FileName], $"{FileName} ist nicht lesbar ({e.Message})"));
            }
            var present = Files.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
            if (s == null)
            {
                if (present.Count == 0) return new Result(State.Ok, [], null);
                Reseal(dir, vault.Key);
                return new Result(State.SealedNow, present, null);
            }
            var bad = new List<string>();
            var keys = vault.Previous is { } prev ? new[] { vault.Key, prev } : new[] { vault.Key };
            foreach (var n in Files)
            {
                var p = Path.Combine(dir, n);
                var has = s.Entries.TryGetValue(n, out var e);
                if (!File.Exists(p)) { if (has) bad.Add(n); continue; }
                var content = File.ReadAllBytes(p);
                if (!has || !keys.Any(k => Matches(e!, Mac(k, n, content)))) bad.Add(n);
            }
            if (bad.Count > 0) return new Result(State.Refused, bad, Refusal(bad, null));
            Reseal(dir, vault.Key);
            return new Result(State.Ok, present, null);
        }
    }

    private static string Refusal(List<string> files, string? detail) =>
        $"dmOffice startet nicht: {string.Join(" und ", files)} wurde außerhalb von dmOffice geändert, gelöscht oder ersetzt " +
        $"(das Siegel in {FileName} passt nicht{(detail == null ? "" : ": " + detail)}). Ist die Änderung gewollt (Datei von Hand bearbeitet, " +
        "ältere Sicherung zurückgelegt, Rückkehr von einer älteren Version), den Inhalt prüfen und neu versiegeln - bei gestopptem Container: " +
        $"„docker run --rm -e DMOFFICE_MASTER_KEY -v <Datenordner>:/data <Image> {Command}“ (ohne Docker: „dotnet DmOffice.Server.dll {Command}“). " +
        "Sonst die Datei aus einer Sicherung zurückholen.";

    /// <summary>Befehl reseal-config: Dateien so, wie sie jetzt sind, neu versiegeln und Benutzer mit Verwaltungsrecht nennen.</summary>
    public static int Run(Vault vault, DataPaths paths, TextWriter o)
    {
        var done = Reseal(paths.Root, vault.Key);
        o.WriteLine(done.Count == 0 ? "Keine Einstellungsdateien gefunden - nichts zu versiegeln." : "Neu versiegelt: " + string.Join(", ", done));
        var users = Path.Combine(paths.Root, "users.json");
        if (File.Exists(users))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(users)) ?? [];
                o.WriteLine($"Benutzer: {list.Count}, davon mit Verwaltungsrecht: " +
                            (list.Where(u => u.IsAdmin).Select(u => u.Login).DefaultIfEmpty("keiner").Aggregate((a, b) => a + ", " + b)));
                o.WriteLine("Bitte prüfen, ob das stimmt. Sonst users.json aus einer Sicherung zurückholen und erneut versiegeln.");
            }
            catch (JsonException e) { o.WriteLine("users.json ist nicht lesbar: " + e.Message); return 1; }
        }
        return 0;
    }
}
