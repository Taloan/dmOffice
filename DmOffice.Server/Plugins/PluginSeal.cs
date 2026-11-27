using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DmOffice.Plugins;

/// <summary>
/// Siegel eines Plugins (certificate.json, docs/plugins-konzept.md 2.2 und 6.2): SHA-256 jeder Datei im Plugin-Ordner und eine
/// ECDSA-P-256-Signatur über die sortierte Liste. Gültig nur, wenn jede Datei gelistet ist, keine fehlt und keine dazugekommen
/// ist, und die Signatur zu einem vertrauten Schlüssel passt: eingebaut (dmOffice) oder vom Betreiber über DMOFFICE_PLUGIN_KEY.
/// Erzeugt mit tools/plugins/seal.mjs. Der private Schlüssel liegt nie im Repo.
/// </summary>
public static class PluginSeal
{
    public const string FileName = "certificate.json";
    private const string Head = "dmoffice-plugin-seal-1";

    /// <summary>
    /// Öffentliche Schlüssel von dmOffice (SubjectPublicKeyInfo, Base64). Platzhalter: noch keiner hinterlegt (offene Entscheidung,
    /// siehe Bericht) - bis dahin laden nur Plugins, die mit dem Schlüssel aus DMOFFICE_PLUGIN_KEY gesiegelt sind.
    /// </summary>
    /// <summary>
    /// Öffentlicher Schlüssel von dmOffice (ECDSA P-256, SubjectPublicKeyInfo, Base64; seit 17.11.2026). Der private liegt nur beim
    /// Bau (Geheimnis plugin_key) - nie im Repository.
    /// </summary>
    internal static readonly string[] BuiltIn = ["MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAETYUzKrUUBM55+4Yzdo7BUixdySrexGgdZLCxv9RuDt8U3uigPO3rEEKZ7tpCCMnTOWW7sOZAr1KaDFHJ0HlTqA=="];

    public sealed record Certificate(string Key, string Version, Dictionary<string, string> Files, string Signature);

    public sealed record Result(bool Valid, string? Reason, string? Signer)
    {
        public static Result Fail(string reason) => new(false, reason, null);
    }

    /// <summary>Vertraute Schlüssel: eingebaute und der des Betreibers (DMOFFICE_PLUGIN_KEY: Base64 oder PEM, mehrere mit Komma).</summary>
    public static List<(string Name, byte[] Spki)> TrustedKeys(string? env = null)
    {
        var list = BuiltIn.Select(k => ("dmOffice", Convert.FromBase64String(k))).ToList();
        foreach (var raw in (env ?? Environment.GetEnvironmentVariable("DMOFFICE_PLUGIN_KEY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var b64 = raw.Replace("-----BEGIN PUBLIC KEY-----", "").Replace("-----END PUBLIC KEY-----", "").Replace("\n", "").Replace("\r", "").Trim();
            try { list.Add(("Betreiber", Convert.FromBase64String(b64))); } catch (FormatException) { /* ungültig: zählt nicht */ }
        }
        return list;
    }

    /// <summary>Text, über den signiert wird: Kopfzeile, Schlüssel, Version, dann „Pfad SHA-256“ je Datei, nach Pfad sortiert (ordinal).</summary>
    public static string Canonical(string key, string version, IDictionary<string, string> files)
    {
        var sb = new StringBuilder().Append(Head).Append('\n').Append(key).Append('\n').Append(version).Append('\n');
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal)) sb.Append(path).Append(' ').Append(hash.ToLowerInvariant()).Append('\n');
        return sb.ToString();
    }

    /// <summary>Alle Dateien des Ordners außer dem Siegel, relativ mit „/“, mit SHA-256 (hex, klein).</summary>
    public static Dictionary<string, string> Hashes(string dir)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (rel == FileName) continue;
            using var s = File.OpenRead(f);
            result[rel] = Convert.ToHexStringLower(SHA256.HashData(s));
        }
        return result;
    }

    public static Result Verify(string dir, PluginManifest manifest, IReadOnlyList<(string Name, byte[] Spki)> keys)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return Result.Fail("Ohne Siegel.");
        Certificate cert;
        try { cert = JsonSerializer.Deserialize<Certificate>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException(); }
        catch (JsonException) { return Result.Fail("Siegel ist unlesbar."); }
        if (cert.Key != manifest.Key || cert.Version != manifest.Version) return Result.Fail("Siegel gehört zu einem anderen Plugin oder einer anderen Version.");
        var actual = Hashes(dir);
        var listed = new Dictionary<string, string>(cert.Files ?? [], StringComparer.Ordinal);
        foreach (var (rel, hash) in actual)
            if (!listed.TryGetValue(rel, out var want)) return Result.Fail($"Datei nicht im Siegel: {rel}");
            else if (!string.Equals(want, hash, StringComparison.OrdinalIgnoreCase)) return Result.Fail($"Datei verändert: {rel}");
        foreach (var rel in listed.Keys)
            if (!actual.ContainsKey(rel)) return Result.Fail($"Datei fehlt: {rel}");
        byte[] signature;
        try { signature = Convert.FromBase64String(cert.Signature ?? ""); } catch (FormatException) { return Result.Fail("Signatur ist unlesbar."); }
        var data = Encoding.UTF8.GetBytes(Canonical(cert.Key, cert.Version, listed));
        if (keys.Count == 0) return Result.Fail("Kein vertrauter Schlüssel hinterlegt.");
        foreach (var (name, spki) in keys)
        {
            using var ecdsa = ECDsa.Create();
            try { ecdsa.ImportSubjectPublicKeyInfo(spki, out _); } catch (CryptographicException) { continue; }
            // Signatur im Format von Node (DER) oder als r||s (IEEE P1363)
            if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                || ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new Result(true, null, name);
        }
        return Result.Fail("Signatur passt zu keinem vertrauten Schlüssel.");
    }

    /// <summary>Für Tests und Werkzeuge: Ordner mit einem privaten Schlüssel siegeln.</summary>
    public static void Seal(string dir, PluginManifest manifest, ECDsa key)
    {
        File.Delete(Path.Combine(dir, FileName));
        var files = Hashes(dir);
        var sig = key.SignData(Encoding.UTF8.GetBytes(Canonical(manifest.Key, manifest.Version, files)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(new Certificate(manifest.Key, manifest.Version, files, Convert.ToBase64String(sig)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
