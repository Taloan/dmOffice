using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notizi.Sync
{
    /// <summary>
    /// Verschluesselung des Abgleichordners (docs/konzept-sync.md,
    /// Abschnitt 5).
    ///
    /// Schluessel aus dem Abgleich-Passwort ueber PBKDF2-SHA256 statt Argon2:
    /// PBKDF2 bringt .NET selbst mit, auf Windows wie auf Android, Argon2
    /// braeuchte eine Bibliothek auf beiden Seiten. Verfahren und Rundenzahl
    /// stehen in info.json - ein spaeterer Wechsel ist ein neuer Eintrag dort,
    /// kein neues Format.
    ///
    /// Jede Datei: AES-256-GCM mit eigenem Zufallswert. Art und ID gehen als
    /// zusaetzliche Daten in die Pruefsumme ein - eine Datei unter einem
    /// anderen Namen abzulegen faellt beim Oeffnen auf.
    ///
    /// Der Schluessel, mit dem die Dateien verschluesselt sind, ist NICHT
    /// der aus dem Passwort. Er ist zufaellig und liegt - mit dem aus dem
    /// Passwort abgeleiteten eingepackt - in info.json ("wrapped").
    ///
    /// Das kostet eine Ebene und spart das Teuerste: Ein neues Passwort
    /// packt nur diese eine Angabe neu ein, rund hundert Byte. Haenge der
    /// Dateischluessel direkt am Passwort, muesste jede Datei im Ordner neu
    /// verschluesselt werden - und jedes andere Geraet muesste neu
    /// verbinden. Dasselbe Vorgehen wie bei Proton: Das Kontopasswort
    /// oeffnet den Schluessel, nicht die Daten.
    ///
    /// Ordner im Format 1 haengen noch unmittelbar am Passwort. Sie bleiben
    /// lesbar; nur das Wechseln des Passworts geht dort nicht ohne
    /// Neuanlegen.
    /// </summary>
    public sealed class SyncCrypto
    {
        /// <summary>Kennung am Dateianfang: "NZ" und die Formatversion.</summary>
        private static readonly byte[] Magic = { (byte)'N', (byte)'Z', 1 };

        private const int NonceSize = 12;
        private const int TagSize = 16;

        /// <summary>Empfehlung von OWASP fuer PBKDF2-SHA256 (Stand 2023).</summary>
        public const int DefaultIterations = 600_000;

        private readonly byte[] _key;
        private readonly byte[] _nameKey;

        private SyncCrypto(byte[] master)
        {
            // Zwei Schluessel aus einem: einer verschluesselt, einer benennt
            // die Blobs. So verraet ein Blobname nichts ueber den Inhalt -
            // ein blosser SHA-256 liesse jeden pruefen, ob eine bekannte
            // Datei im Ordner liegt.
            _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("notizi-sync-data"));
            _nameKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("notizi-sync-names"));
        }

        // ==================================================================
        //  Einrichten und Oeffnen
        // ==================================================================

        /// <summary>Das aktuelle Format: Schluessel eingepackt statt
        /// unmittelbar aus dem Passwort.</summary>
        public const int CurrentFormat = 2;

        /// <summary>Was in info.json steht.</summary>
        public sealed class Info
        {
            /// <summary>1: Schluessel unmittelbar aus dem Passwort (nur noch
            /// zum Lesen). 2: eingepackter Zufallsschluessel.</summary>
            [JsonPropertyName("format")] public int Format { get; set; } = 1;
            [JsonPropertyName("kdf")] public string Kdf { get; set; } = "pbkdf2-sha256";
            [JsonPropertyName("iterations")] public int Iterations { get; set; } = DefaultIterations;
            [JsonPropertyName("salt")] public string Salt { get; set; } = "";

            /// <summary>Format 1: Ein verschluesselter fester Wert, an dem ein
            /// falsches Passwort auffaellt.</summary>
            [JsonPropertyName("check")] public string Check { get; set; } = "";

            /// <summary>Format 2: der Dateischluessel, eingepackt mit dem aus
            /// dem Passwort abgeleiteten. Ein falsches Passwort faellt beim
            /// Auspacken auf - ein eigener Pruefwert eruebrigt sich.</summary>
            [JsonPropertyName("wrapped")] public string Wrapped { get; set; } = "";

            public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            public static Info? FromJson(string json) => JsonSerializer.Deserialize<Info>(json);
        }

        private const string CheckText = "notizi";

        /// <summary>Gehoert zum Einpacken des Schluessels - eine Datei von
        /// dort laesst sich damit nicht als Schluessel ausgeben.</summary>
        private static readonly byte[] WrapAssociated = Encoding.UTF8.GetBytes("notizi-sync-key");

        /// <summary>Neuer Abgleichordner: frisches Salz, zufaelliger
        /// Dateischluessel, mit dem Passwort eingepackt.</summary>
        public static (SyncCrypto Crypto, Info Info) Create(string password, int iterations = DefaultIterations)
        {
            var info = new Info
            {
                Format = CurrentFormat,
                Iterations = iterations,
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            };

            var master = RandomNumberGenerator.GetBytes(32);
            info.Wrapped = Convert.ToBase64String(Wrap(Derive(password, info), master));
            return (new SyncCrypto(master), info);
        }

        /// <summary>Null bei falschem Passwort.</summary>
        public static SyncCrypto? Open(string password, Info info)
        {
            if (info.Kdf != "pbkdf2-sha256" || info.Format is not (1 or CurrentFormat))
                throw new NotSupportedException($"Abgleichordner in unbekanntem Format ({info.Format}, {info.Kdf}).");

            var derived = Derive(password, info);

            if (info.Format == CurrentFormat)
                return Unwrap(derived, info.Wrapped) is { } master ? new SyncCrypto(master) : null;

            // Format 1: Der Dateischluessel ist der aus dem Passwort.
            var crypto = new SyncCrypto(derived);
            try
            {
                var check = crypto.Unseal("check", "info", Convert.FromBase64String(info.Check));
                return Encoding.UTF8.GetString(check) == CheckText ? crypto : null;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>
        /// Dasselbe Verzeichnis, neues Passwort. Gibt die neue info.json
        /// zurueck - mehr aendert sich nicht: Die Dateien bleiben, wie sie
        /// sind, denn ihr Schluessel bleibt derselbe.
        ///
        /// Null, wenn das bisherige Passwort nicht passt oder der Ordner noch
        /// im Format 1 liegt. Dort haengt der Dateischluessel am Passwort;
        /// ihn zu wechseln hiesse, jede Datei neu zu verschluesseln.
        /// </summary>
        public static Info? ChangePassword(Info info, string oldPassword, string newPassword,
                                           int iterations = DefaultIterations)
        {
            if (info.Format != CurrentFormat) return null;
            if (Unwrap(Derive(oldPassword, info), info.Wrapped) is not { } master) return null;

            var next = new Info
            {
                Format = CurrentFormat,
                Kdf = info.Kdf,
                Iterations = iterations,
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            };
            next.Wrapped = Convert.ToBase64String(Wrap(Derive(newPassword, next), master));
            return next;
        }

        // ------------------------------------------------ Schluessel einpacken

        private static byte[] Wrap(byte[] kek, byte[] master)
        {
            var packed = new byte[NonceSize + TagSize + master.Length];
            var nonce = packed.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(nonce, master, packed.AsSpan(NonceSize + TagSize),
                        packed.AsSpan(NonceSize, TagSize), WrapAssociated);
            return packed;
        }

        private static byte[]? Unwrap(byte[] kek, string wrapped)
        {
            byte[] packed;
            try { packed = Convert.FromBase64String(wrapped); }
            catch (FormatException) { return null; }

            if (packed.Length <= NonceSize + TagSize) return null;

            var master = new byte[packed.Length - NonceSize - TagSize];
            using var aes = new AesGcm(kek, TagSize);
            try
            {
                aes.Decrypt(packed.AsSpan(0, NonceSize), packed.AsSpan(NonceSize + TagSize),
                            packed.AsSpan(NonceSize, TagSize), master, WrapAssociated);
                return master;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        private static byte[] Derive(string password, Info info) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(info.Salt),
                                      info.Iterations, HashAlgorithmName.SHA256, 32);

        // ==================================================================
        //  Ver- und Entschluesseln
        // ==================================================================

        public byte[] Seal(string kind, string id, byte[] plain)
        {
            var result = new byte[Magic.Length + NonceSize + TagSize + plain.Length];
            Magic.CopyTo(result, 0);
            var nonce = result.AsSpan(Magic.Length, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            var tag = result.AsSpan(Magic.Length + NonceSize, TagSize);
            var cipher = result.AsSpan(Magic.Length + NonceSize + TagSize);

            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, Associated(kind, id));
            return result;
        }

        /// <summary>Wirft CryptographicException, wenn die Datei veraendert,
        /// unter falschem Namen abgelegt oder mit anderem Schluessel
        /// geschrieben wurde.</summary>
        public byte[] Unseal(string kind, string id, byte[] sealedData)
        {
            if (sealedData.Length < Magic.Length + NonceSize + TagSize
                || !sealedData.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new CryptographicException("Keine Abgleichdatei von notizi oder unbekannte Version.");

            var nonce = sealedData.AsSpan(Magic.Length, NonceSize);
            var tag = sealedData.AsSpan(Magic.Length + NonceSize, TagSize);
            var cipher = sealedData.AsSpan(Magic.Length + NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Associated(kind, id));
            return plain;
        }

        /// <summary>Name eines Blobs: HMAC ueber den Inhalt. Gleicher Inhalt,
        /// gleicher Name - ohne dass der Name den Inhalt verraet.</summary>
        public string BlobName(byte[] content) =>
            Convert.ToHexString(HMACSHA256.HashData(_nameKey, content)).ToLowerInvariant();

        private static byte[] Associated(string kind, string id) => Encoding.UTF8.GetBytes(kind + "/" + id);
    }
}
