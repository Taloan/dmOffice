using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace DmOffice.Tests;

/// <summary>OpenPGP Stufen 1 und 2: Signaturen prüfen (gültig, manipuliert, unbekannter Schlüssel), signieren und
/// gegenprüfen, Schlüssel aus Mails übernehmen, importieren und speichern.</summary>
public class OpenPgpTests
{
    private static UserDb Db() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("dmpgp").FullName, "u.db"), new byte[32], "t");

    private static readonly MailAccount Account = new() { Id = "acc", Email = "ich@beispiel.de", DisplayName = "Ich Selbst" };

    private static MimeMessage Mail(string from = "ich@beispiel.de", string text = "Hallo Anna,\nbis morgen.\n")
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Ich Selbst", from));
        m.To.Add(new MailboxAddress("Anna", "anna@example.org"));
        m.Subject = "Test";
        m.Body = new BodyBuilder { TextBody = text, HtmlBody = "<p>Hallo Anna,<br>bis morgen.</p>" }.ToMessageBody();
        return m;
    }

    /// <summary>Wie über die Leitung: schreiben und neu lesen.</summary>
    private static MimeMessage Roundtrip(MimeMessage m, Func<string, string>? change = null)
    {
        var ms = new MemoryStream();
        m.WriteTo(FormatOptions.Default, ms);
        var raw = Encoding.UTF8.GetString(ms.ToArray());
        if (change != null) raw = change(raw);
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
    }

    private static MimeMessage Signed(UserDb sender, Action<OpenPgpKeys.AccountSettings>? opts = null, string from = "ich@beispiel.de")
    {
        if (opts != null)
        {
            var s = OpenPgpKeys.GetSettings(sender);
            var a = new OpenPgpKeys.AccountSettings();
            opts(a);
            s.Accounts[Account.Id] = a;
            OpenPgpKeys.SaveSettings(sender, s);
        }
        var m = Mail(from);
        OpenPgpMail.Outgoing(sender, Account, m, sign: true);
        return Roundtrip(m);
    }

    [Fact]
    public void Erzeugter_Schluessel_ist_Ed25519_mit_Verschluesselungs_Unterschluessel()
    {
        var db = Db();
        var own = OpenPgpKeys.Generate(db, Account.Id, Account.DisplayName, Account.Email);
        var keys = own.Public.GetPublicKeys().ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal(PublicKeyAlgorithmTag.EdDsa_Legacy, keys[0].Algorithm);
        Assert.Equal(PublicKeyAlgorithmTag.ECDH, keys[1].Algorithm);
        Assert.True(keys[1].IsEncryptionKey);
        Assert.Equal(["ich selbst <ich@beispiel.de>"], OpenPgpKeys.UserIds(own.Public).Select(u => u.ToLowerInvariant()));
        Assert.Equal(40, own.Fingerprint.Length);
        // Gespeichert und wieder gelesen
        var again = OpenPgpKeys.GetOwn(db, Account.Id)!;
        Assert.Equal(own.Fingerprint, again.Fingerprint);
        Assert.NotNull(OpenPgpKeys.SigningKey(again.Secret));
        Assert.Contains(OpenPgpKeys.List(db), k => k.Fingerprint == own.Fingerprint && k.Source == OpenPgpKeys.Own && k.Confirmed);
    }

    [Fact]
    public void Signieren_und_gegenpruefen()
    {
        var db = Db();
        OpenPgpKeys.Generate(db, Account.Id, Account.DisplayName, Account.Email);
        var m = Signed(db);
        Assert.IsType<MultipartSigned>(m.Body);
        Assert.Equal("mime", OpenPgpMail.Hint(m));
        // Der Inhalt bleibt lesbar
        Assert.Contains("bis morgen", m.TextBody);
        var st = OpenPgpMail.Verify(db, m)!;
        Assert.Equal("valid", st.State);
        Assert.Equal("ich@beispiel.de", st.Email);
        Assert.Equal("Ich Selbst", st.Name);
        Assert.Equal(OpenPgpKeys.GetOwn(db, Account.Id)!.Fingerprint, st.Fingerprint);
    }

    [Fact]
    public void Manipulierter_Inhalt_faellt_auf()
    {
        var db = Db();
        OpenPgpKeys.Generate(db, Account.Id, Account.DisplayName, Account.Email);
        var ms = new MemoryStream();
        var m = Mail();
        OpenPgpMail.Outgoing(db, Account, m, sign: true);
        var bad = Roundtrip(m, raw => raw.Replace("bis morgen", "bis gleich"));
        Assert.Equal("bad", OpenPgpMail.Verify(db, bad)!.State);
    }

    [Fact]
    public void Unbekannter_Schluessel_nennt_die_Kennung()
    {
        var sender = Db();
        var own = OpenPgpKeys.Generate(sender, Account.Id, Account.DisplayName, Account.Email);
        var st = OpenPgpMail.Verify(Db(), Signed(sender))!;
        Assert.Equal("unknown", st.State);
        Assert.Equal(own.Fingerprint[^16..], st.KeyId);
    }

    [Fact]
    public void Nicht_signierte_Mail_hat_keinen_Status()
    {
        Assert.Null(OpenPgpMail.Verify(Db(), Roundtrip(Mail())));
        Assert.Null(OpenPgpMail.Hint(Mail()));
    }

    [Fact]
    public void Schluessel_per_Autocrypt_ist_erst_nach_Bestaetigen_geprueft()
    {
        var sender = Db();
        var own = OpenPgpKeys.Generate(sender, Account.Id, Account.DisplayName, Account.Email);
        var m = Signed(sender, a => a.Autocrypt = true);
        Assert.Contains("addr=ich@beispiel.de", m.Headers["Autocrypt"]);

        var me = Db();
        Assert.True(OpenPgpMail.Harvest(me, m));
        Assert.False(OpenPgpMail.Harvest(me, m));   // schon bekannt
        var st = OpenPgpMail.Verify(me, m)!;
        Assert.Equal("unconfirmed", st.State);
        Assert.Equal(OpenPgpKeys.Autocrypt, st.Source);

        OpenPgpKeys.Confirm(me, own.Fingerprint);
        Assert.Equal("valid", OpenPgpMail.Verify(me, m)!.State);
    }

    [Fact]
    public void Autocrypt_einer_fremden_Adresse_zaehlt_nicht()
    {
        var sender = Db();
        OpenPgpKeys.Generate(sender, Account.Id, Account.DisplayName, Account.Email);
        var m = Signed(sender, a => a.Autocrypt = true);
        // Absender umgeschrieben: addr passt nicht mehr
        m.From.Clear();
        m.From.Add(new MailboxAddress("", "boese@example.org"));
        Assert.False(OpenPgpMail.Harvest(Db(), m));
    }

    [Fact]
    public void Angehaengter_Schluessel_wird_uebernommen_und_nicht_als_Anhang_gezeigt()
    {
        var sender = Db();
        var own = OpenPgpKeys.Generate(sender, Account.Id, Account.DisplayName, Account.Email);
        var m = Signed(sender, a => a.AttachKey = true);
        var key = Assert.Single(m.BodyParts.OfType<MimePart>(), p => p.ContentType.IsMimeType("application", "pgp-keys"));
        Assert.EndsWith(".asc", key.FileName);

        var me = Db();
        Assert.True(OpenPgpMail.Harvest(me, m));
        var info = Assert.Single(OpenPgpKeys.List(me));
        Assert.Equal(own.Fingerprint, info.Fingerprint);
        Assert.Equal(OpenPgpKeys.Attached, info.Source);
        Assert.False(info.Confirmed);
        Assert.Equal("unconfirmed", OpenPgpMail.Verify(me, m)!.State);
    }

    [Fact]
    public void Schluessel_einer_anderen_Adresse_passt_nicht_zum_Absender()
    {
        var sender = Db();
        var own = OpenPgpKeys.Generate(sender, Account.Id, "Jemand", "jemand@example.org");
        var m = Signed(sender);
        var me = Db();
        OpenPgpKeys.ImportPublic(me, Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(own.Public)));
        var st = OpenPgpMail.Verify(me, m)!;
        Assert.Equal("mismatch", st.State);
        Assert.Equal("jemand@example.org", st.Email);
    }

    [Fact]
    public void Von_Hand_importiert_gilt_als_bestaetigt_und_laesst_sich_loeschen()
    {
        var sender = Db();
        var own = OpenPgpKeys.Generate(sender, Account.Id, Account.DisplayName, Account.Email);
        var me = Db();
        var fps = OpenPgpKeys.ImportPublic(me, Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(own.Public)));
        Assert.Equal([own.Fingerprint], fps);
        Assert.Equal("valid", OpenPgpMail.Verify(me, Signed(sender))!.State);
        Assert.Single(OpenPgpKeys.ForEmail(me, "ICH@beispiel.de"));

        OpenPgpKeys.Delete(me, own.Fingerprint);
        Assert.Empty(OpenPgpKeys.List(me));
        Assert.Equal("unknown", OpenPgpMail.Verify(me, Signed(sender))!.State);
        Assert.Throws<ArgumentException>(() => OpenPgpKeys.ImportPublic(me, Encoding.ASCII.GetBytes("kein Schlüssel")));
    }

    [Fact]
    public void Eigenen_Schluessel_mit_Passwort_importieren()
    {
        var armored = SecretWithPassword("Importiert <ich@beispiel.de>", "geheim", out var fp);
        var db = Db();
        Assert.Throws<ArgumentException>(() => OpenPgpKeys.ImportOwn(db, Account.Id, Account.Email, armored, null));
        Assert.Throws<ArgumentException>(() => OpenPgpKeys.ImportOwn(db, Account.Id, Account.Email, armored, "falsch"));
        var (own, warning) = OpenPgpKeys.ImportOwn(db, Account.Id, Account.Email, armored, "geheim");
        Assert.Null(warning);
        Assert.Equal(fp, own.Fingerprint);
        // Gespeichert ohne das Passwort - signieren geht ohne Nachfrage
        Assert.Equal("valid", OpenPgpMail.Verify(db, Signed(db))!.State);
        // Andere Adresse: Warnung, aber übernommen
        var (_, other) = OpenPgpKeys.ImportOwn(Db(), Account.Id, "anders@beispiel.de", armored, "geheim");
        Assert.NotNull(other);
        // Öffentlicher statt privater Schlüssel
        Assert.Throws<ArgumentException>(() => OpenPgpKeys.ImportOwn(Db(), Account.Id, Account.Email,
            Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(own.Public)), null));
    }

    [Fact]
    public void Ohne_eigenen_Schluessel_kein_Signieren()
    {
        var db = Db();
        Assert.Throws<ArgumentException>(() => OpenPgpMail.Outgoing(db, Account, Mail(), sign: true));
        var m = Mail();
        OpenPgpMail.Outgoing(db, Account, m, sign: false);
        Assert.IsNotType<MultipartSigned>(m.Body);
        // Schlüssel entfernt: kein Signieren mehr, der öffentliche bleibt bekannt
        var own = OpenPgpKeys.Generate(db, Account.Id, Account.DisplayName, Account.Email);
        OpenPgpKeys.RemoveOwn(db, Account.Id);
        Assert.Null(OpenPgpKeys.GetOwn(db, Account.Id));
        Assert.Throws<ArgumentException>(() => OpenPgpMail.Outgoing(db, Account, Mail(), sign: true));
        Assert.Contains(OpenPgpKeys.List(db), k => k.Fingerprint == own.Fingerprint && k.Source == OpenPgpKeys.Import);
    }

    [Fact]
    public void Inline_PGP_pruefen()
    {
        var sender = Db();
        var own = OpenPgpKeys.Generate(sender, Account.Id, Account.DisplayName, Account.Email);
        var text = "Hallo Anna,\n- Strich am Anfang\nbis morgen.  \n";
        var m = Mail(text: "Vorweg\n" + ClearSign(OpenPgpKeys.SigningKey(own.Secret)!, text) + "\nFußzeile");
        Assert.Equal("inline", OpenPgpMail.Hint(m));
        var me = Db();
        OpenPgpKeys.ImportPublic(me, Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(own.Public)));
        var st = OpenPgpMail.Verify(me, Roundtrip(m))!;
        Assert.Equal("inline", st.Kind);
        Assert.Equal("valid", st.State);

        var bad = Roundtrip(m, raw => raw.Replace("bis morgen", "bis heute"));
        Assert.Equal("bad", OpenPgpMail.Verify(me, bad)!.State);
        Assert.Equal("unknown", OpenPgpMail.Verify(Db(), m)!.State);
    }

    [Fact]
    public void Wkd_Adresse_wie_im_Entwurf()
    {
        // Beispiel aus draft-koch-openpgp-webkey-service
        var (advanced, direct) = OpenPgpWkd.Urls("Joe.Doe@Example.ORG")!.Value;
        Assert.Equal("https://openpgpkey.example.org/.well-known/openpgpkey/example.org/hu/iy9q119eutrkn8s1mk4r39qejnbu3n5q?l=Joe.Doe", advanced.ToString());
        Assert.Equal("https://example.org/.well-known/openpgpkey/hu/iy9q119eutrkn8s1mk4r39qejnbu3n5q?l=Joe.Doe", direct.ToString());
        Assert.Null(OpenPgpWkd.Urls("kein-at"));
        Assert.Null(OpenPgpWkd.Urls("a@lokal"));
    }

    [Fact]
    public void Einstellungen_je_Konto()
    {
        var db = Db();
        Assert.True(OpenPgpKeys.GetSettings(db).Wkd);
        Assert.False(OpenPgpKeys.Of(db, Account.Id).Sign);
        var s = OpenPgpKeys.GetSettings(db);
        s.Accounts[Account.Id] = new OpenPgpKeys.AccountSettings { Sign = true };
        s.Wkd = false;
        OpenPgpKeys.SaveSettings(db, s);
        Assert.True(OpenPgpKeys.Of(db, Account.Id).Sign);
        Assert.False(OpenPgpKeys.GetSettings(db).Wkd);
    }

    // ---------------- Hilfen ----------------

    /// <summary>Klartext-Signatur, wie GnuPG sie mit --clearsign schreibt.</summary>
    private static string ClearSign(PgpSecretKey key, string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var gen = new PgpSignatureGenerator(key.PublicKey.Algorithm, HashAlgorithmTag.Sha256);
        gen.InitSign(PgpSignature.CanonicalTextDocument, key.ExtractPrivateKey(Array.Empty<char>()));
        gen.Update(Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Select(l => l.TrimEnd(' ', '\t')))));
        var ms = new MemoryStream();
        using (var armored = new ArmoredOutputStream(ms))
        {
            armored.BeginClearText(HashAlgorithmTag.Sha256);
            armored.Write(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"));
            armored.EndClearText();
            using var bcpg = new BcpgOutputStream(armored);
            gen.Generate().Encode(bcpg);
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n");
    }

    private static byte[] SecretWithPassword(string userId, string password, out string fingerprint)
    {
        var rnd = new SecureRandom();
        var ed = new Ed25519KeyPairGenerator();
        ed.Init(new Ed25519KeyGenerationParameters(rnd));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.EdDsa_Legacy, ed.GenerateKeyPair(), DateTime.UtcNow);
        var flags = new PgpSignatureSubpacketGenerator();
        flags.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        var gen = new PgpKeyRingGenerator(PgpSignature.PositiveCertification, pair, userId, SymmetricKeyAlgorithmTag.Aes256,
            password.ToCharArray(), true, flags.Generate(), null, rnd);
        var ring = gen.GenerateSecretKeyRing();
        fingerprint = Convert.ToHexString(ring.GetPublicKey().GetFingerprint());
        var ms = new MemoryStream();
        using (var armored = new ArmoredOutputStream(ms)) ring.Encode(armored);
        return ms.ToArray();
    }
}
