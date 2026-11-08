using System.Text;
using DmOffice.Core;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace DmOffice.Mail;

/// <summary>
/// OpenPGP in Mails, Stufen 1 und 2 (docs/openpgp-konzept.md): Signaturen eingehender Mails prüfen (PGP/MIME
/// „multipart/signed“ und Inline-PGP), Schlüssel aus Mails übernehmen (Anhang „application/pgp-keys“, Kopfzeile
/// „Autocrypt“) und eigene Mails signieren.
/// </summary>
public static class OpenPgpMail
{
    /// <summary>
    /// Ergebnis der Prüfung. State: valid (Signatur stimmt, Schlüssel bestätigt und passt zum Absender), unconfirmed
    /// (stimmt, Schlüssel aber nur aus einer Mail), mismatch (stimmt, aber der Schlüssel gehört nicht zur Absenderadresse),
    /// bad (Inhalt passt nicht zur Signatur), unknown (Schlüssel unbekannt), expired/revoked (Schlüssel abgelaufen oder
    /// zurückgezogen), error (Signatur unlesbar).
    /// </summary>
    public sealed record Status(string State, string Kind, string? Email = null, string? Name = null, string? Fingerprint = null,
        string? KeyId = null, DateTime? Signed = null, string? Source = null, bool Partial = false, bool KeyAdded = false, bool WkdTried = false);

    /// <summary>Kinds: „mime“ (multipart/signed), „inline“ (BEGIN PGP SIGNED MESSAGE), „key“ (nur ein Schlüssel dabei).</summary>
    public static string? Hint(MimeMessage m)
    {
        if (FindSigned(m.Body) != null) return "mime";
        if (InlineBlock(m) != null) return "inline";
        if (m.Headers.Contains("Autocrypt") || m.BodyParts.OfType<MimePart>().Any(IsKeyPart)) return "key";
        return null;
    }

    private static bool IsKeyPart(MimePart p) => p.ContentType.IsMimeType("application", "pgp-keys");

    /// <summary>Der erste PGP/MIME-signierte Teil (auch innerhalb von multipart/mixed, z. B. mit Fußzeile einer Liste).</summary>
    internal static MultipartSigned? FindSigned(MimeEntity? e)
    {
        if (e is MultipartSigned s && (s.ContentType.Parameters["protocol"] ?? "").Trim().Equals("application/pgp-signature", StringComparison.OrdinalIgnoreCase))
            return s;
        if (e is Multipart mp && e is not MultipartSigned)
            foreach (var child in mp)
                if (FindSigned(child) is { } found) return found;
        return null;
    }

    // ---------------- Schlüssel aus der Mail ----------------

    /// <summary>Schlüssel des Absenders aus der Mail übernehmen: angehängte Schlüssel und Autocrypt, jeweils nur, wenn die
    /// Absenderadresse im Schlüssel steht. Unbestätigt, bis der Benutzer sie bestätigt. Gibt zurück, ob einer neu ist.</summary>
    public static bool Harvest(UserDb db, MimeMessage m)
    {
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(from)) return false;
        bool added = false;
        foreach (var part in m.BodyParts.OfType<MimePart>().Where(IsKeyPart))
        {
            List<PgpPublicKeyRing> rings;
            try { rings = OpenPgpKeys.ReadPublic(MailCompose.Decode(part)); }
            catch (ArgumentException) { continue; }
            foreach (var r in rings.Where(r => OpenPgpKeys.Emails(r).Contains(from)))
                added |= OpenPgpKeys.Store(db, r, OpenPgpKeys.Attached);
        }
        if (AutocryptKey(m, from) is { } ring) added |= OpenPgpKeys.Store(db, ring, OpenPgpKeys.Autocrypt);
        return added;
    }

    /// <summary>Kopfzeile Autocrypt (Level 1): addr muss der Absender sein, unbekannte Pflichtangaben machen sie ungültig.</summary>
    internal static PgpPublicKeyRing? AutocryptKey(MimeMessage m, string from)
    {
        var headers = m.Headers.Where(h => h.Field.Equals("Autocrypt", StringComparison.OrdinalIgnoreCase)).ToList();
        if (headers.Count != 1) return null;
        string? addr = null, keydata = null;
        foreach (var attr in headers[0].Value.Split(';'))
        {
            var eq = attr.IndexOf('=');
            if (eq <= 0) continue;
            var name = attr[..eq].Trim().ToLowerInvariant();
            var value = attr[(eq + 1)..];
            if (name == "addr") addr = value.Trim().ToLowerInvariant();
            else if (name == "keydata") keydata = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
            else if (name != "prefer-encrypt" && !name.StartsWith('_')) return null;
        }
        if (addr != from || string.IsNullOrEmpty(keydata)) return null;
        try
        {
            var ring = OpenPgpKeys.ReadPublic(Convert.FromBase64String(keydata)).First();
            return OpenPgpKeys.Emails(ring).Contains(from) ? ring : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; }
    }

    // ---------------- prüfen ----------------

    /// <summary>Signatur prüfen; null, wenn die Mail nicht signiert ist.</summary>
    public static Status? Verify(UserDb db, MimeMessage m)
    {
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant() ?? "";
        if (FindSigned(m.Body) is { } signed)
        {
            DigitalSignatureCollection sigs;
            try { sigs = signed.Verify(new Context(db)); }
            catch (Exception e) when (e is FormatException or PgpException or IOException or NotSupportedException or ArgumentException)
            {
                return new Status("error", "mime");
            }
            var sig = sigs.OfType<OpenPgpDigitalSignature>().FirstOrDefault();
            if (sig == null) return new Status("error", "mime");
            bool? ok;
            try { ok = sig.Verify(); }
            catch (DigitalSignatureVerifyException) { ok = null; }
            catch (Exception e) when (e is PgpException or IOException) { ok = false; }
            var partial = !ReferenceEquals(signed, m.Body);
            var keyId = SignatureKeyId(signed);
            if (ok == null || sig.SignerCertificate is not OpenPgpDigitalCertificate cert)
                return new Status("unknown", "mime", KeyId: keyId, Signed: sig.CreationDate, Partial: partial);
            return Judge(db, from, cert.PublicKey, ok.Value, sig.CreationDate, "mime", partial);
        }
        if (InlineBlock(m) is { } block) return VerifyInline(db, from, block);
        return null;
    }

    /// <summary>Kennung des signierenden Schlüssels aus dem Signaturteil - MimeKit nennt sie bei unbekanntem Schlüssel nicht.</summary>
    private static string? SignatureKeyId(MultipartSigned signed)
    {
        if (signed.Count < 2 || signed[1] is not MimePart part) return null;
        try
        {
            using var stream = PgpUtilities.GetDecoderStream(new MemoryStream(MailCompose.Decode(part)));
            return new PgpObjectFactory(stream).NextPgpObject() is PgpSignatureList { Count: > 0 } list ? OpenPgpKeys.KeyId(list[0].KeyId) : null;
        }
        catch (Exception e) when (e is IOException or PgpException) { return null; }
    }

    private static Status Judge(UserDb db, string from, PgpPublicKey signer, bool ok, DateTime signedAt, string kind, bool partial)
    {
        var known = OpenPgpKeys.ByKeyId(db, signer.KeyId);
        var ring = known?.Ring;
        var master = ring?.GetPublicKey() ?? signer;
        var fp = OpenPgpKeys.Fingerprint(master);
        var ids = ring != null ? OpenPgpKeys.UserIds(ring) : [];
        var match = ids.FirstOrDefault(i => OpenPgpKeys.AddressOf(i) == from);
        var shown = match ?? ids.FirstOrDefault();
        string state;
        if (!ok) state = "bad";
        else if (signer.HasRevocation() || master.HasRevocation()) state = "revoked";
        else if (OpenPgpKeys.Expired(master, signedAt) || OpenPgpKeys.Expired(signer, signedAt)) state = "expired";
        else if (match == null) state = "mismatch";
        else state = known?.Confirmed == true ? "valid" : "unconfirmed";
        return new Status(state, kind, shown != null ? OpenPgpKeys.AddressOf(shown) : null, shown != null ? OpenPgpKeys.NameOf(shown) : null,
            fp, OpenPgpKeys.KeyId(signer.KeyId), signedAt, known?.Source, partial);
    }

    /// <summary>Inline-PGP: „-----BEGIN PGP SIGNED MESSAGE-----“ im Text der Mail.</summary>
    internal static string? InlineBlock(MimeMessage m)
    {
        var text = m.TextBody;
        if (text == null) return null;
        var start = text.IndexOf("-----BEGIN PGP SIGNED MESSAGE-----", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf("-----END PGP SIGNATURE-----", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..(end + "-----END PGP SIGNATURE-----".Length)];
    }

    /// <summary>Klartext-Signatur nach RFC 4880 Abschnitt 7: Text ohne Leerzeichen am Zeilenende, Zeilen mit CRLF
    /// verbunden, „- “ am Zeilenanfang entfernt.</summary>
    internal static Status VerifyInline(UserDb db, string from, string block)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        int i = 1;
        while (i < lines.Length && lines[i].Trim().Length > 0) i++;   // Hash: …
        i++;
        var text = new List<string>();
        for (; i < lines.Length && !lines[i].StartsWith("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal); i++)
            text.Add(lines[i].StartsWith("- ", StringComparison.Ordinal) ? lines[i][2..] : lines[i]);
        if (i >= lines.Length) return new Status("error", "inline");
        var armored = string.Join("\n", lines[i..]);
        PgpSignature sig;
        try
        {
            using var stream = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(armored)));
            var list = new PgpObjectFactory(stream).NextPgpObject() as PgpSignatureList;
            if (list == null || list.Count == 0) return new Status("error", "inline");
            sig = list[0];
        }
        catch (Exception e) when (e is IOException or PgpException) { return new Status("error", "inline"); }

        var known = OpenPgpKeys.ByKeyId(db, sig.KeyId);
        var key = known?.Ring.GetPublicKey(sig.KeyId);
        if (key == null) return new Status("unknown", "inline", KeyId: OpenPgpKeys.KeyId(sig.KeyId), Signed: sig.CreationTime);
        bool ok;
        try
        {
            sig.InitVerify(key);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", text.Select(l => l.TrimEnd(' ', '\t', '\r'))));
            sig.Update(bytes);
            ok = sig.Verify();
        }
        catch (PgpException) { ok = false; }
        return Judge(db, from, key, ok, sig.CreationTime, "inline", false);
    }

    // ---------------- signieren ----------------

    /// <summary>Mail aus diesem Konto vorbereiten: signieren (wenn sign), dabei auf Wunsch den öffentlichen Schlüssel
    /// anhängen, und - wenn eingeschaltet - die Kopfzeile Autocrypt setzen. Ohne eigenen Schlüssel und mit sign:
    /// ArgumentException.</summary>
    public static void Outgoing(UserDb db, MailAccount account, MimeMessage m, bool sign)
    {
        var own = OpenPgpKeys.GetOwn(db, account.Id);
        if (own == null)
        {
            if (sign) throw new ArgumentException("Für dieses Konto gibt es keinen OpenPGP-Schlüssel (Einstellungen → E-Mail → OpenPGP).");
            return;
        }
        var opts = OpenPgpKeys.Of(db, account.Id);
        if (opts.Autocrypt) m.Headers.Add("Autocrypt", AutocryptValue(account.Email, own.Public));
        if (!sign) return;
        var key = OpenPgpKeys.SigningKey(own.Secret) ?? throw new ArgumentException("Der OpenPGP-Schlüssel dieses Kontos kann nicht signieren.");
        var body = m.Body ?? throw new ArgumentException("Die Mail ist leer.");
        if (opts.AttachKey)
            body = new Multipart("mixed") { body, KeyAttachment(own.Public, account.Email) };
        m.Body = MultipartSigned.Create(new Context(db, own.Secret), key, DigestAlgorithm.Sha256, body);
    }

    public static MimePart KeyAttachment(PgpPublicKeyRing ring, string email) =>
        new("application", "pgp-keys")
        {
            Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(ring)))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.SevenBit,
            FileName = $"OpenPGP_0x{OpenPgpKeys.Fingerprint(ring.GetPublicKey())[^16..]}.asc",
        };

    /// <summary>„addr=…; keydata=…“, der Schlüssel in Stücken, damit die Kopfzeile umbrochen werden kann.</summary>
    internal static string AutocryptValue(string email, PgpPublicKeyRing ring)
    {
        var b64 = Convert.ToBase64String(ring.GetEncoded());
        var chunks = Enumerable.Range(0, (b64.Length + 75) / 76).Select(i => b64.Substring(i * 76, Math.Min(76, b64.Length - i * 76)));
        return $"addr={email.Trim()}; keydata= {string.Join(" ", chunks)}";
    }

    /// <summary>MimeKit-Kontext auf den Schlüsseln des Benutzers: öffentliche aus pgp_keys, zum Signieren der eigene.</summary>
    internal sealed class Context(UserDb db, PgpSecretKeyRing? secret = null) : OpenPgpContext
    {
        protected override string GetPasswordForKey(PgpSecretKey key) => "";

        protected override PgpPublicKeyRing GetPublicKeyRing(long keyId, CancellationToken cancellationToken) =>
            OpenPgpKeys.ByKeyId(db, keyId)?.Ring!;

        protected override Task<PgpPublicKeyRing> GetPublicKeyRingAsync(long keyId, CancellationToken cancellationToken) =>
            Task.FromResult<PgpPublicKeyRing>(GetPublicKeyRing(keyId, cancellationToken));

        protected override PgpSecretKey GetSecretKey(long keyId, CancellationToken cancellationToken) => secret?.GetSecretKey(keyId)!;

        public override PgpSecretKey GetSigningKey(MailboxAddress mailbox, CancellationToken cancellationToken = default) =>
            (secret != null ? OpenPgpKeys.SigningKey(secret) : null) ?? throw new PrivateKeyNotFoundException(mailbox, "Kein eigener Schlüssel.");

        public override IList<PgpPublicKey> GetPublicKeys(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Verschlüsseln kommt mit Stufe 3.");

        public override bool CanSign(MailboxAddress signer, CancellationToken cancellationToken = default) => secret != null;
        public override Task<bool> CanSignAsync(MailboxAddress signer, CancellationToken cancellationToken = default) => Task.FromResult(CanSign(signer, cancellationToken));
        public override bool CanEncrypt(MailboxAddress mailbox, CancellationToken cancellationToken = default) => false;
        public override Task<bool> CanEncryptAsync(MailboxAddress mailbox, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public override void Import(PgpPublicKeyRing keyring, CancellationToken cancellationToken = default) => OpenPgpKeys.Store(db, keyring, OpenPgpKeys.Import);
        public override void Import(PgpPublicKeyRingBundle bundle, CancellationToken cancellationToken = default)
        {
            foreach (var r in bundle.GetKeyRings()) Import(r, cancellationToken);
        }
        public override void Import(PgpSecretKeyRing keyring, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override void Import(PgpSecretKeyRingBundle bundle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override MimePart Export(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<MimePart> ExportAsync(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override void Export(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task ExportAsync(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
