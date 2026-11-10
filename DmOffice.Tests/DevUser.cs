using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>
/// Lokaler Testbenutzer zum Ausprobieren im Browser - nur zum Entwickeln.
/// Laeuft mit DMOFFICE_SEED=&lt;Datenverzeichnis&gt; und DMOFFICE_DEVUSER=&lt;login&gt;;
/// das Passwort steht danach in &lt;Datenverzeichnis&gt;/dev-login.txt (data/ ist nicht im Repo).
/// </summary>
public class DevUser
{
    [Fact]
    public void Testbenutzer()
    {
        var dir = Environment.GetEnvironmentVariable("DMOFFICE_SEED");
        var login = Environment.GetEnvironmentVariable("DMOFFICE_DEVUSER");
        if (dir == null || login == null) return;
        var paths = new DataPaths(dir);
        var users = new UserStore(paths, new Vault(paths, null));
        if (users.ByLogin(login) != null) return;
        var password = Passwords.NewToken();
        users.Create(login, "Test", password, isAdmin: false);
        File.WriteAllText(Path.Combine(dir, "dev-login.txt"), $"{login}\n{password}\n");
    }
}
