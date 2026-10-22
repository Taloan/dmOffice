using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace DmOffice.Office;

/// <summary>Was ein access_token erlaubt: eine Datei eines Benutzers, lesen oder schreiben, bis zu einer Zeit.</summary>
/// <param name="U">Benutzer (Besitzer der Datei)</param>
/// <param name="F">fileid in seiner Datenbank</param>
/// <param name="W">schreiben erlaubt</param>
/// <param name="B">WOPISrc-Basis, wie sie dem Office-Server genannt wurde (fuer die Proof-Pruefung)</param>
/// <param name="O">Herkunft der Seite mit dem Editor (PostMessageOrigin)</param>
/// <param name="S">Sicherheitsstempel des Benutzers - neues Passwort oder Abmelden ueberall macht Tokens ungueltig</param>
/// <param name="L">Oeffentlicher Link, ueber den angesehen wird (sonst null): wird er geloescht oder laeuft ab, endet der Zugang</param>
/// <param name="N">Anzeigename fuer den Office-Server (Mitbearbeiter sehen ihn)</param>
/// <param name="E">ETag beim Oeffnen (ONLYOFFICE: Konflikt erkennen, wenn die Datei inzwischen anders geaendert wurde)</param>
/// <param name="V">Mitglied, das eine mit ihm geteilte Datei des Besitzers (U) oeffnet; sonst null. S ist dann sein Stempel.
/// Die Freigabe wird bei jeder Anfrage neu geprueft - Widerruf beendet auch laufende Sitzungen.</param>
public sealed record WopiGrant(string U, long F, bool W, string B, string O, string S, string? L, string N, string? E = null, string? V = null);

/// <summary>
/// Kurzlebige, signierte und verschluesselte access_tokens fuer WOPI (Data Protection, wie das Anmelde-Cookie,
/// aber mit eigenem Zweck - das Cookie selbst geht nie an den Office-Server).
/// </summary>
public static class WopiTokens
{
    /// <summary>So lange gilt ein Token. Collabora erneuert es waehrend einer Sitzung nicht - zehn Stunden empfiehlt auch Microsoft.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(10);

    /// <summary>Zweck der Tokens fuer /wopi (Collabora und ONLYOFFICE im WOPI-Modus).</summary>
    public const string WopiPurpose = "dmoffice.wopi.token";
    /// <summary>Zweck der Tokens fuer /onlyoffice (nativer ONLYOFFICE-Modus). Getrennt von WOPI: ein Token der
    /// einen Art gilt bei der anderen nicht - sonst liesse sich etwa ein WOPI-Token beim ONLYOFFICE-Callback
    /// einsetzen oder ein mitgeloggtes WOPI-Token ueber /onlyoffice/file ohne Proof-Pruefung verwenden.</summary>
    public const string OnlyOfficePurpose = "dmoffice.onlyoffice.token";

    private static ITimeLimitedDataProtector Protector(IServiceProvider services, string purpose) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).ToTimeLimitedDataProtector();

    public static (string Token, DateTimeOffset Expires) Issue(IServiceProvider services, WopiGrant grant, TimeSpan? lifetime = null, string purpose = WopiPurpose)
    {
        var expires = DateTimeOffset.UtcNow + (lifetime ?? Lifetime);
        return (Protector(services, purpose).Protect(JsonSerializer.Serialize(grant), expires), expires);
    }

    public static WopiGrant? Read(IServiceProvider services, string? token, string purpose = WopiPurpose)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        try { return JsonSerializer.Deserialize<WopiGrant>(Protector(services, purpose).Unprotect(token, out _)); }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Datei-Id fuer die WOPI-Adresse: Benutzer und fileid - der Office-Server fasst Sitzungen je WOPISrc zusammen,
    /// darum darf die Adresse nicht bei zwei Benutzern gleich sein.</summary>
    public static string FileId(string user, long fileId) => $"{user}_{fileId}";
}

/// <summary>WOPI-Sperren je Datei, im Speicher (ein Server-Prozess). Laufen nach 30 Minuten ohne Erneuern ab.</summary>
public static class WopiLocks
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, (string Id, DateTime Until)> Locks = new();

    private static string Key(string user, long file) => user + "/" + file;

    public static string? Current(string user, long file)
    {
        if (!Locks.TryGetValue(Key(user, file), out var l)) return null;
        if (l.Until > DateTime.UtcNow) return l.Id;
        Locks.TryRemove(new KeyValuePair<string, (string, DateTime)>(Key(user, file), l));
        return null;
    }

    public static void Set(string user, long file, string id) => Locks[Key(user, file)] = (id, DateTime.UtcNow + Duration);

    public static void Remove(string user, long file) => Locks.TryRemove(Key(user, file), out _);

    /// <summary>Fuer die Tests: alles vergessen.</summary>
    public static void Clear() => Locks.Clear();
}

/// <summary>Proof-Keys (X-WOPI-Proof): der Office-Server signiert jede Anfrage mit seinem privaten Schluessel aus der Discovery.</summary>
public static class WopiProof
{
    /// <summary>Was signiert wird: Laenge+Token, Laenge+Adresse (gross geschrieben), Laenge+Zeitstempel - alles big-endian.</summary>
    public static byte[] Data(string token, string url, long timestamp)
    {
        var t = Encoding.UTF8.GetBytes(token);
        var u = Encoding.UTF8.GetBytes(url.ToUpperInvariant());
        using var ms = new MemoryStream();
        void Int(int v) { var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); ms.Write(b); }
        Int(t.Length); ms.Write(t);
        Int(u.Length); ms.Write(u);
        Int(8);
        var ts = BitConverter.GetBytes(timestamp);
        if (BitConverter.IsLittleEndian) Array.Reverse(ts);
        ms.Write(ts);
        return ms.ToArray();
    }

    private static bool Check(RSAParameters? key, byte[] data, string? signature)
    {
        if (key == null || string.IsNullOrEmpty(signature)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(key.Value);
            return rsa.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    /// <summary>Gueltig, wenn der aktuelle Schluessel X-WOPI-Proof oder X-WOPI-ProofOld bestaetigt, oder der alte X-WOPI-Proof
    /// (Schluesselwechsel). Der Zeitstempel (.NET-Ticks, UTC) darf hoechstens 20 Minuten alt sein.</summary>
    public static bool Valid(Discovery d, string token, string url, string? timestamp, string? proof, string? proofOld, DateTime now)
    {
        if (!long.TryParse(timestamp, out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return false;
        if ((now - new DateTime(ticks, DateTimeKind.Utc)).Duration() > TimeSpan.FromMinutes(20)) return false;
        var data = Data(token, url, ticks);
        return Check(d.ProofKey, data, proof) || Check(d.ProofKey, data, proofOld) || Check(d.OldProofKey, data, proof);
    }
}
