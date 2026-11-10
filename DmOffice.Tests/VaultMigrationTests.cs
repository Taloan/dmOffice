using System.Text;
using System.Text.Json.Nodes;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>
/// Umstieg von der Schluesseldatei (master.key) auf DMOFFICE_MASTER_KEY: alles, was mit dem alten Schluessel eingepackt
/// ist, wird neu eingepackt - Schluessel in keys/, Geheimnisse in server.json, Benutzerschluessel in users.json, der
/// Schluesselring der Anmeldung - und erst dann die Datei geloescht. Frueher wurden nur die Benutzerschluessel umgepackt;
/// danach liess sich etwa keys/e2e.key nicht mehr oeffnen und der Server startete nicht mehr.
/// </summary>
public class VaultMigrationTests
{
    [Fact]
    public void Umstieg_packt_alles_um_und_ueberlebt_den_naechsten_Start()
    {
        var dir = Directory.CreateTempSubdirectory("dmvault").FullName;
        try
        {
            var paths = new DataPaths(dir);
            var before = new Vault(paths, null);
            Assert.True(File.Exists(before.KeyFile));
            Directory.CreateDirectory(paths.Keys);
            byte[] Secret(string s) => Encoding.UTF8.GetBytes(s);
            using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var pkcs8 = ecdh.ExportPkcs8PrivateKey();
            File.WriteAllText(Path.Combine(paths.Keys, "e2e.key"), before.Wrap(pkcs8));
            File.WriteAllText(Path.Combine(dir, "server.json"), new JsonObject
            {
                ["OfficeJwtSecret"] = before.Wrap(Secret("jwt")),
                ["Name"] = "kein Geheimnis, aber lang genug, um wie Base64 auszusehen ABCDEFGHIJKLMNOPQRSTUV",
                ["Limits"] = new JsonObject { ["Nested"] = before.Wrap(Secret("tief")) },
            }.ToJsonString());
            File.WriteAllText(Path.Combine(dir, "users.json"), new JsonArray(new JsonObject { ["Login"] = "anna", ["WrappedKey"] = before.Wrap(Secret("db-schluessel")) }).ToJsonString());
            File.WriteAllText(Path.Combine(paths.Keys, "key-1.xml"),
                $"<key><descriptor><encryptedSecret><vaultEncrypted><value>{before.Wrap(Secret("<masterKey/>"))}</value></vaultEncrypted></encryptedSecret></descriptor></key>");
            File.WriteAllText(Path.Combine(paths.Keys, "fcm.json"), "{\"private_key\":\"-----BEGIN PRIVATE KEY----- nicht eingepackt\"}");

            // Erster Start mit der Variable: alles umgepackt, die alte Datei bleibt fuer aeltere Sicherungen liegen
            var after = new Vault(paths, "neuer-hauptschluessel-aus-der-umgebung");
            Assert.Null(after.MigrationError);
            Assert.Equal(5, after.Migrated);
            Assert.True(after.OldKeyFileLeft);

            // Naechster Start mit der Datei: nichts mehr umzupacken
            Assert.Equal(0, new Vault(paths, "neuer-hauptschluessel-aus-der-umgebung").Migrated);

            // Datei geloescht (Knopf in der Verwaltung): kein alter Schluessel mehr - alles muss mit dem neuen aufgehen
            after.DeleteOldKeyFile();
            Assert.False(File.Exists(after.KeyFile));
            var next = new Vault(paths, "neuer-hauptschluessel-aus-der-umgebung");
            Assert.Null(next.Previous);
            string Open(string wrapped) => Encoding.UTF8.GetString(next.Unwrap(wrapped));
            Assert.Equal(pkcs8, next.Unwrap(File.ReadAllText(Path.Combine(paths.Keys, "e2e.key")).Trim()));
            var server = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "server.json")))!;
            Assert.Equal("jwt", Open((string)server["OfficeJwtSecret"]!));
            Assert.Equal("tief", Open((string)server["Limits"]!["Nested"]!));
            Assert.StartsWith("kein Geheimnis", (string)server["Name"]!);
            Assert.Equal("db-schluessel", Open((string)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "users.json")))![0]!["WrappedKey"]!));
            var xml = System.Xml.Linq.XDocument.Load(Path.Combine(paths.Keys, "key-1.xml"));
            Assert.Equal("<masterKey/>", Open(xml.Descendants("value").Single().Value));
            Assert.Contains("nicht eingepackt", File.ReadAllText(Path.Combine(paths.Keys, "fcm.json")));
            // Der Server-Schluessel fuer den App-Umschlag laesst sich wieder laden (frueher: Absturz beim Start)
            _ = new E2e.ServerKey(paths, next);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Scheitert_der_Umstieg_bleibt_die_Datei()
    {
        var dir = Directory.CreateTempSubdirectory("dmvault").FullName;
        try
        {
            var paths = new DataPaths(dir);
            var before = new Vault(paths, null);
            File.WriteAllText(Path.Combine(dir, "server.json"), "{ kaputt");
            var after = new Vault(paths, "neuer-hauptschluessel-aus-der-umgebung");
            Assert.NotNull(after.MigrationError);
            Assert.True(File.Exists(before.KeyFile));
            Assert.NotNull(after.Previous);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
