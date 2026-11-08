using System.Text;
using DmOffice.Core;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace DmOffice.Mail;

/// <summary>
/// OpenPGP-Schlüssel je Benutzer (docs/openpgp-konzept.md): öffentliche Schlüssel anderer (aus angehängten Schlüsseln,
/// Autocrypt-Kopfzeilen, WKD oder von Hand importiert) und je Mailkonto ein eigener Schlüssel zum Signieren. Alles in
/// eigenen Tabellen der Benutzerdatenbank (per CREATE IF NOT EXISTS, keine Migration) - der private Schlüssel liegt wie
/// die Zugangsdaten der Mailkonten in der verschlüsselten Benutzerdatenbank.
/// </summary>
public static class OpenPgpKeys
{
    /// <summary>Woher ein Schlüssel kommt. Bestätigt sind: von Hand importiert, per WKD vom Mailanbieter des Absenders,
    /// eigene, und von Hand bestätigte - aus einer Mail (Anhang, Autocrypt) sind sie es erst nach dem Bestätigen.</summary>
    public const string Own = "own", Import = "import", Wkd = "wkd", Attached = "attached", Autocrypt = "autocrypt";

    public sealed record KeyInfo(string Fingerprint, List<string> Emails, List<string> UserIds, string Source, bool Confirmed,
        DateTime Created, DateTime? Expires, bool Revoked, string Algorithm, DateTime Added);

    public sealed record OwnKey(string Account, string Fingerprint, PgpSecretKeyRing Secret, DateTime Created)
    {
        public PgpPublicKeyRing Public => PublicOf(Secret);
    }

    public sealed class Settings
    {
        /// <summary>Unbekannte Schlüssel beim Mailanbieter des Absenders nachschlagen (Web Key Directory).</summary>
        public bool Wkd { get; set; } = true;
        public Dictionary<string, AccountSettings> Accounts { get; set; } = new();
    }

    public sealed class AccountSettings
    {
        /// <summary>Neue Mails aus diesem Konto standardmäßig signieren (im Editor umschaltbar).</summary>
        public bool Sign { get; set; }
        /// <summary>Den eigenen öffentlichen Schlüssel als Datei mitschicken (nur bei signierten Mails).</summary>
        public bool AttachKey { get; set; }
        /// <summary>Kopfzeile „Autocrypt“ mit dem öffentlichen Schlüssel setzen (bei jeder Mail aus dem Konto).</summary>
        public bool Autocrypt { get; set; }
    }

    private static readonly SecureRandom Random = new();

    internal static void Ensure(UserDb db) => db.Exec(@"
        CREATE TABLE IF NOT EXISTS pgp_keys (fingerprint TEXT PRIMARY KEY, emails TEXT NOT NULL, ring BLOB NOT NULL, source TEXT NOT NULL,
            confirmed INTEGER NOT NULL DEFAULT 0, added INTEGER NOT NULL, updated INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS pgp_key_ids (key_id INTEGER NOT NULL, fingerprint TEXT NOT NULL, PRIMARY KEY (key_id, fingerprint));
        CREATE TABLE IF NOT EXISTS pgp_own (account TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, secret BLOB NOT NULL, created INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS pgp_wkd (email TEXT PRIMARY KEY, checked INTEGER NOT NULL, found INTEGER NOT NULL);");

    public static Settings GetSettings(UserDb db) => db.GetJson<Settings>("pgp.settings");
    public static void SaveSettings(UserDb db, Settings s) => db.SetJson("pgp.settings", s);
    public static AccountSettings Of(UserDb db, string account) =>
        GetSettings(db).Accounts.TryGetValue(account, out var a) ? a : new AccountSettings();

    /// <summary>Ohne Angabe im Editor: signieren, wenn eingestellt und ein eigener Schlüssel da ist.</summary>
    public static bool SignsByDefault(UserDb db, string account) => Of(db, account).Sign && GetOwn(db, account) != null;

    // ---------------- öffentliche Schlüssel ----------------

    public static string Fingerprint(PgpPublicKey key) => Convert.ToHexString(key.GetFingerprint());

    /// <summary>Lange Schlüsselkennung als Hex (16 Zeichen), wie sie in Signaturen steht.</summary>
    public static string KeyId(long id) => id.ToString("X16");

    /// <summary>Alle Schlüsselringe aus Text (ASCII-Hülle) oder Binärdaten; Unlesbares: ArgumentException.</summary>
    public static List<PgpPublicKeyRing> ReadPublic(byte[] data)
    {
        try
        {
            using var stream = PgpUtilities.GetDecoderStream(new MemoryStream(data));
            var rings = new PgpPublicKeyRingBundle(stream).GetKeyRings().ToList();
            if (rings.Count == 0) throw new ArgumentException("Darin steht kein öffentlicher OpenPGP-Schlüssel.");
            return rings;
        }
        catch (Exception e) when (e is PgpException or IOException or InvalidOperationException)
        {
            throw new ArgumentException("Darin steht kein lesbarer öffentlicher OpenPGP-Schlüssel.");
        }
    }

    /// <summary>Mailadressen der Benutzerkennungen mit gültiger Eigenbeglaubigung - eine fremde Kennung ohne Unterschrift
    /// des Schlüssels zählt nicht.</summary>
    public static List<string> Emails(PgpPublicKeyRing ring) => UserIds(ring).Select(AddressOf).Where(a => a != null).Select(a => a!).Distinct().ToList();

    public static List<string> UserIds(PgpPublicKeyRing ring)
    {
        var master = ring.GetPublicKey();
        var result = new List<string>();
        foreach (var id in master.GetUserIds().OfType<string>())
        {
            bool ok = false;
            foreach (var sig in master.GetSignaturesForId(id))
            {
                if (sig.KeyId != master.KeyId) continue;
                try { sig.InitVerify(master); ok = sig.VerifyCertification(id, master); }
                catch (PgpException) { }
                if (ok) break;
            }
            if (ok) result.Add(id);
        }
        return result;
    }

    /// <summary>„Name &lt;a@b.de&gt;“ oder „a@b.de“ → a@b.de in Kleinbuchstaben.</summary>
    public static string? AddressOf(string userId)
    {
        var s = userId.Trim();
        int lt = s.LastIndexOf('<'), gt = s.LastIndexOf('>');
        if (lt >= 0 && gt > lt) s = s[(lt + 1)..gt];
        s = s.Trim().ToLowerInvariant();
        return s.Length > 2 && s.IndexOf('@') > 0 && !s.Contains(' ') ? s : null;
    }

    public static string NameOf(string userId)
    {
        int lt = userId.LastIndexOf('<');
        return (lt > 0 ? userId[..lt] : "").Trim().Trim('"');
    }

    /// <summary>Speichert einen Schlüsselring. Ein schon bekannter Schlüssel (gleicher Fingerabdruck) wird nur durch eine
    /// mindestens so verlässliche Quelle ersetzt; bestätigt bleibt bestätigt. Gibt zurück, ob er neu ist.</summary>
    public static bool Store(UserDb db, PgpPublicKeyRing ring, string source)
    {
        Ensure(db);
        var fp = Fingerprint(ring.GetPublicKey());
        var emails = Emails(ring);
        var confirmed = source is Own or Import or Wkd;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var old = db.Query("SELECT source, confirmed FROM pgp_keys WHERE fingerprint=$f", r => (Source: r.GetString(0), Confirmed: r.GetInt64(1) == 1), ("$f", fp)).FirstOrDefault();
        if (old.Source != null)
        {
            if (Rank(source) < Rank(old.Source) && old.Confirmed) return false;
            db.Exec("UPDATE pgp_keys SET emails=$e, ring=$r, source=$s, confirmed=max(confirmed, $c), updated=$n WHERE fingerprint=$f",
                ("$e", System.Text.Json.JsonSerializer.Serialize(emails)), ("$r", ring.GetEncoded()),
                ("$s", Rank(source) >= Rank(old.Source) ? source : old.Source), ("$c", confirmed ? 1 : 0), ("$n", now), ("$f", fp));
        }
        else
            db.Exec("INSERT INTO pgp_keys(fingerprint, emails, ring, source, confirmed, added, updated) VALUES($f, $e, $r, $s, $c, $n, $n)",
                ("$f", fp), ("$e", System.Text.Json.JsonSerializer.Serialize(emails)), ("$r", ring.GetEncoded()), ("$s", source), ("$c", confirmed ? 1 : 0), ("$n", now));
        db.Exec("DELETE FROM pgp_key_ids WHERE fingerprint=$f", ("$f", fp));
        foreach (var k in ring.GetPublicKeys())
            db.Exec("INSERT OR IGNORE INTO pgp_key_ids(key_id, fingerprint) VALUES($k, $f)", ("$k", k.KeyId), ("$f", fp));
        return old.Source == null;
    }

    private static int Rank(string source) => source switch { Own => 4, Import => 3, Wkd => 2, _ => 1 };

    /// <summary>Schlüsselring, der den (Unter-)Schlüssel mit dieser Kennung enthält.</summary>
    public static (PgpPublicKeyRing Ring, string Source, bool Confirmed)? ByKeyId(UserDb db, long keyId)
    {
        Ensure(db);
        return db.Query(@"SELECT k.ring, k.source, k.confirmed FROM pgp_key_ids i JOIN pgp_keys k ON k.fingerprint = i.fingerprint
                WHERE i.key_id = $k ORDER BY k.confirmed DESC, k.updated DESC LIMIT 1",
            r => ((PgpPublicKeyRing Ring, string Source, bool Confirmed)?)(new PgpPublicKeyRing((byte[])r[0]), r.GetString(1), r.GetInt64(2) == 1),
            ("$k", keyId)).FirstOrDefault();
    }

    public static (PgpPublicKeyRing Ring, string Source, bool Confirmed)? ByFingerprint(UserDb db, string fingerprint)
    {
        Ensure(db);
        return db.Query("SELECT ring, source, confirmed FROM pgp_keys WHERE fingerprint=$f",
            r => ((PgpPublicKeyRing Ring, string Source, bool Confirmed)?)(new PgpPublicKeyRing((byte[])r[0]), r.GetString(1), r.GetInt64(2) == 1),
            ("$f", fingerprint.ToUpperInvariant())).FirstOrDefault();
    }

    /// <summary>Bekannte Schlüssel einer Adresse (für Stufe 3: Verschlüsseln).</summary>
    public static List<KeyInfo> ForEmail(UserDb db, string email) =>
        List(db).Where(k => k.Emails.Contains(email.Trim().ToLowerInvariant())).ToList();

    public static List<KeyInfo> List(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT ring, source, confirmed, added FROM pgp_keys ORDER BY added DESC",
            r => Info(new PgpPublicKeyRing((byte[])r[0]), r.GetString(1), r.GetInt64(2) == 1, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).UtcDateTime));
    }

    public static KeyInfo Info(PgpPublicKeyRing ring, string source, bool confirmed, DateTime added)
    {
        var k = ring.GetPublicKey();
        var valid = k.GetValidSeconds();
        return new KeyInfo(Fingerprint(k), Emails(ring), UserIds(ring), source, confirmed, k.CreationTime,
            valid > 0 ? k.CreationTime.AddSeconds(valid) : null, k.HasRevocation(), AlgorithmName(k), added);
    }

    public static string AlgorithmName(PgpPublicKey k) => k.Algorithm switch
    {
        PublicKeyAlgorithmTag.EdDsa_Legacy or PublicKeyAlgorithmTag.Ed25519 => "Ed25519",
        PublicKeyAlgorithmTag.Ed448 => "Ed448",
        PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign => $"RSA {k.BitStrength}",
        PublicKeyAlgorithmTag.ECDsa => $"ECDSA {k.BitStrength}",
        PublicKeyAlgorithmTag.Dsa => $"DSA {k.BitStrength}",
        _ => k.Algorithm.ToString(),
    };

    public static void Confirm(UserDb db, string fingerprint)
    {
        Ensure(db);
        if (db.Exec("UPDATE pgp_keys SET confirmed=1, updated=$n WHERE fingerprint=$f",
                ("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$f", fingerprint.ToUpperInvariant())) == 0)
            throw new KeyNotFoundException("Diesen Schlüssel gibt es nicht.");
    }

    public static void Delete(UserDb db, string fingerprint)
    {
        Ensure(db);
        var fp = fingerprint.ToUpperInvariant();
        if (db.Scalar("SELECT 1 FROM pgp_own WHERE fingerprint=$f", ("$f", fp)) != null)
            throw new ArgumentException("Das ist der Schlüssel eines eigenen Kontos - bitte dort entfernen.");
        db.Exec("DELETE FROM pgp_keys WHERE fingerprint=$f", ("$f", fp));
        db.Exec("DELETE FROM pgp_key_ids WHERE fingerprint=$f", ("$f", fp));
    }

    /// <summary>Öffentliche Schlüssel von Hand importieren (Text oder Datei). Gibt die Fingerabdrücke zurück.</summary>
    public static List<string> ImportPublic(UserDb db, byte[] data)
    {
        var result = new List<string>();
        foreach (var ring in ReadPublic(data))
        {
            Store(db, ring, Import);
            result.Add(Fingerprint(ring.GetPublicKey()));
        }
        return result;
    }

    public static string Armor(PgpPublicKeyRing ring)
    {
        var ms = new MemoryStream();
        using (var armored = new ArmoredOutputStream(ms, new Dictionary<string, string>()))
            ring.Encode(armored);
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    // ---------------- eigene Schlüssel ----------------

    public static OwnKey? GetOwn(UserDb db, string account)
    {
        Ensure(db);
        return db.Query("SELECT fingerprint, secret, created FROM pgp_own WHERE account=$a",
            r => new OwnKey(account, r.GetString(0), new PgpSecretKeyRing((byte[])r[1]), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).UtcDateTime),
            ("$a", account)).FirstOrDefault();
    }

    /// <summary>Öffentlicher Teil wie bei „gpg --export“: Paket für Paket, geheime Schlüsselpakete werden zu öffentlichen
    /// (Unterschlüssel bleiben Unterschlüssel), Kennungen und Signaturen bleiben.</summary>
    public static PgpPublicKeyRing PublicOf(PgpSecretKeyRing secret)
    {
        var ms = new MemoryStream();
        var output = new BcpgOutputStream(ms);
        var input = new BcpgInputStream(new MemoryStream(secret.GetEncoded()));
        while (input.NextPacketTag() >= 0)
        {
            switch (input.ReadPacket())
            {
                case SecretSubkeyPacket sub:
                    var pk = sub.PublicKeyPacket;
                    new PublicSubkeyPacket(pk.Algorithm, pk.GetTime(), pk.Key).Encode(output);
                    break;
                case SecretKeyPacket main: main.PublicKeyPacket.Encode(output); break;
                case TrustPacket: break;
                case ContainedPacket other: other.Encode(output); break;
            }
        }
        output.Flush();
        return new PgpPublicKeyRing(ms.ToArray());
    }

    /// <summary>Neuer Schlüssel: Ed25519 zum Signieren (Hauptschlüssel) und Curve25519 zum Verschlüsseln (Unterschlüssel,
    /// für Stufe 3) - das Format, das GnuPG, Thunderbird, Proton und K-9/FairEmail lesen.</summary>
    public static OwnKey Generate(UserDb db, string account, string name, string email)
    {
        var now = DateTime.UtcNow;
        var ed = new Ed25519KeyPairGenerator();
        ed.Init(new Ed25519KeyGenerationParameters(Random));
        var signing = new PgpKeyPair(PublicKeyAlgorithmTag.EdDsa_Legacy, ed.GenerateKeyPair(), now);
        var x = new X25519KeyPairGenerator();
        x.Init(new X25519KeyGenerationParameters(Random));
        var encryption = new PgpKeyPair(PublicKeyAlgorithmTag.ECDH, x.GenerateKeyPair(), now);

        var main = new PgpSignatureSubpacketGenerator();
        main.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        main.SetPreferredHashAlgorithms(false, [(int)HashAlgorithmTag.Sha512, (int)HashAlgorithmTag.Sha384, (int)HashAlgorithmTag.Sha256]);
        main.SetPreferredSymmetricAlgorithms(false, [(int)SymmetricKeyAlgorithmTag.Aes256, (int)SymmetricKeyAlgorithmTag.Aes192, (int)SymmetricKeyAlgorithmTag.Aes128]);
        main.SetPreferredCompressionAlgorithms(false, [(int)CompressionAlgorithmTag.ZLib, (int)CompressionAlgorithmTag.Zip, (int)CompressionAlgorithmTag.Uncompressed]);
        main.SetFeature(false, Org.BouncyCastle.Bcpg.Sig.Features.FEATURE_MODIFICATION_DETECTION);
        var sub = new PgpSignatureSubpacketGenerator();
        sub.SetKeyFlags(false, PgpKeyFlags.CanEncryptCommunications | PgpKeyFlags.CanEncryptStorage);

        var userId = string.IsNullOrWhiteSpace(name) ? $"<{email.Trim()}>" : $"{name.Trim()} <{email.Trim()}>";
        var gen = new PgpKeyRingGenerator(PgpSignature.PositiveCertification, signing, userId, SymmetricKeyAlgorithmTag.Null,
            Array.Empty<char>(), true, main.Generate(), null, Random);
        gen.AddSubKey(encryption, sub.Generate(), null);
        return SaveOwn(db, account, gen.GenerateSecretKeyRing());
    }

    /// <summary>Eigenen Schlüssel importieren (privater Schlüssel, meist mit Passwort geschützt). Gespeichert wird er ohne
    /// das Passwort - geschützt durch die Verschlüsselung der Benutzerdatenbank. Gibt eine Warnung zurück, wenn die
    /// Adresse des Kontos nicht im Schlüssel steht.</summary>
    public static (OwnKey Key, string? Warning) ImportOwn(UserDb db, string account, string email, byte[] data, string? passphrase)
    {
        PgpSecretKeyRing ring;
        try
        {
            using var stream = PgpUtilities.GetDecoderStream(new MemoryStream(data));
            ring = new PgpSecretKeyRingBundle(stream).GetKeyRings().FirstOrDefault()
                   ?? throw new ArgumentException("Darin steht kein privater OpenPGP-Schlüssel.");
        }
        catch (Exception e) when (e is PgpException or IOException or InvalidOperationException)
        {
            throw new ArgumentException("Darin steht kein lesbarer privater OpenPGP-Schlüssel. Bitte den privaten Schlüssel exportieren, nicht den öffentlichen.");
        }
        if (SigningKey(ring) == null) throw new ArgumentException("Dieser Schlüssel kann nicht signieren.");
        var pass = (passphrase ?? "").ToCharArray();
        try
        {
            foreach (var k in ring.GetSecretKeys()) if (!k.IsPrivateKeyEmpty) k.ExtractPrivateKey(pass);
        }
        catch (PgpException)
        {
            throw new ArgumentException(pass.Length == 0 ? "Der Schlüssel ist mit einem Passwort geschützt - bitte angeben." : "Das Passwort des Schlüssels stimmt nicht.");
        }
        if (pass.Length > 0 || ring.GetSecretKeys().Any(k => k.KeyEncryptionAlgorithm != SymmetricKeyAlgorithmTag.Null))
            ring = PgpSecretKeyRing.CopyWithNewPassword(ring, pass, Array.Empty<char>(), SymmetricKeyAlgorithmTag.Null, Random);
        var own = SaveOwn(db, account, ring);
        var warning = Emails(own.Public).Contains(email.Trim().ToLowerInvariant()) ? null
            : $"Die Adresse {email} steht nicht in diesem Schlüssel - Empfänger sehen die Signatur dann nicht als passend an.";
        return (own, warning);
    }

    private static OwnKey SaveOwn(UserDb db, string account, PgpSecretKeyRing ring)
    {
        Ensure(db);
        var pub = PublicOf(ring);
        var fp = Fingerprint(pub.GetPublicKey());
        var created = DateTime.UtcNow;
        RemoveOwn(db, account);
        db.Exec("INSERT INTO pgp_own(account, fingerprint, secret, created) VALUES($a, $f, $s, $c)",
            ("$a", account), ("$f", fp), ("$s", ring.GetEncoded()), ("$c", new DateTimeOffset(created).ToUnixTimeSeconds()));
        Store(db, pub, Own);
        return new OwnKey(account, fp, ring, created);
    }

    /// <summary>Eigenen Schlüssel des Kontos entfernen; der öffentliche bleibt als bekannter Schlüssel (alte Mails lassen
    /// sich weiter prüfen), wenn ihn kein anderes Konto mehr nutzt.</summary>
    public static void RemoveOwn(UserDb db, string account)
    {
        Ensure(db);
        var fp = db.Scalar("SELECT fingerprint FROM pgp_own WHERE account=$a", ("$a", account)) as string;
        db.Exec("DELETE FROM pgp_own WHERE account=$a", ("$a", account));
        if (fp != null && db.Scalar("SELECT 1 FROM pgp_own WHERE fingerprint=$f", ("$f", fp)) == null)
            db.Exec("UPDATE pgp_keys SET source=$s WHERE fingerprint=$f AND source=$o", ("$s", Import), ("$f", fp), ("$o", Own));
    }

    /// <summary>Schlüssel zum Signieren: ein Unterschlüssel mit Signier-Kennzeichen, sonst der Hauptschlüssel.</summary>
    public static PgpSecretKey? SigningKey(PgpSecretKeyRing ring)
    {
        var keys = ring.GetSecretKeys().Where(k => !k.IsPrivateKeyEmpty && k.IsSigningKey && !k.PublicKey.HasRevocation() && !Expired(k.PublicKey)).ToList();
        return keys.FirstOrDefault(k => !k.IsMasterKey && CanSign(k.PublicKey)) ?? keys.FirstOrDefault(k => k.IsMasterKey);
    }

    private static bool CanSign(PgpPublicKey k) =>
        k.GetSignatures().OfType<PgpSignature>().Any(s => s.HasSubpackets && (s.GetHashedSubPackets().GetKeyFlags() & PgpKeyFlags.CanSign) != 0);

    public static bool Expired(PgpPublicKey k, DateTime? at = null)
    {
        var valid = k.GetValidSeconds();
        return valid > 0 && k.CreationTime.AddSeconds(valid) < (at ?? DateTime.UtcNow);
    }

    // ---------------- WKD-Zwischenspeicher ----------------

    /// <summary>Nicht öfter als einmal am Tag je Adresse beim Anbieter nachfragen.</summary>
    internal static bool WkdDue(UserDb db, string email)
    {
        Ensure(db);
        var at = db.Scalar("SELECT checked FROM pgp_wkd WHERE email=$e", ("$e", email)) as long?;
        return at == null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at.Value > 24 * 3600;
    }

    internal static void WkdChecked(UserDb db, string email, bool found) =>
        db.Exec("INSERT INTO pgp_wkd(email, checked, found) VALUES($e, $c, $f) ON CONFLICT(email) DO UPDATE SET checked=$c, found=$f",
            ("$e", email), ("$c", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$f", found ? 1 : 0));
}
