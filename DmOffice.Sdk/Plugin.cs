using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Sdk;

/// <summary>Stufe der Kern-Schnittstelle. Ein Plugin nennt in plugin.json unter „core“ den Bereich, mit dem es läuft (">=1 &lt;2").</summary>
public static class SdkInfo
{
    public const int Level = 1;
}

/// <summary>
/// Einstieg eines Plugins (genau eine öffentliche Klasse mit parameterlosem Konstruktor je Assembly). Alles ist optional;
/// was ein Plugin nicht braucht, lässt es weg. Der Kern ruft nie einen Typ des Plugins direkt auf, nur diese Beiträge.
/// </summary>
public interface IDmOfficePlugin
{
    /// <summary>S1: Dienste und Hintergrundjobs. Jobs prüfen selbst <see cref="PluginContext.On"/> und ruhen, solange das Plugin aus ist.</summary>
    void ConfigureServices(IServiceCollection services, PluginContext context) { }

    /// <summary>
    /// S2: API-Routen. Alles, was hier angelegt wird, antwortet mit 404, solange das Plugin ausgeschaltet ist. Pfade nur unter
    /// /api/&lt;key&gt; (etwa /api/coach/...); was woanders liegt, legt der Kern gesperrt an (404). Nur mit Anmeldung (Cookie oder
    /// App-Passwort) - der Kern setzt das für die ganze Gruppe; Wege ohne Anmeldung gehören nach <see cref="MapPublic"/>, Wege nur für
    /// Admins nach <see cref="MapAdmin"/>.
    /// </summary>
    void Map(IEndpointRouteBuilder routes, PluginContext context) { }

    /// <summary>
    /// S2: Wege nur für Admins. Die Gruppe liegt unter /api/&lt;key&gt;/admin, verlangt die Admin-Rolle und antwortet mit 404, solange das
    /// Plugin aus ist. Pfade relativ zur Gruppe angeben („/config“ wird /api/&lt;key&gt;/admin/config).
    /// </summary>
    void MapAdmin(IEndpointRouteBuilder routes, PluginContext context) { }

    /// <summary>S4: Schemaschritte in der Benutzerdatenbank (Tabellen mit Präfix „&lt;key&gt;_“). Nur anhängen, nie ändern.</summary>
    IReadOnlyList<SchemaStep> UserSchema => [];

    /// <summary>S4: Schemaschritte in der Tresor-Datenbank (nur bei geöffnetem Tresor). Nur anhängen, nie ändern.</summary>
    IReadOnlyList<SchemaStep> TresorSchema => [];

    /// <summary>S8: Bereiche im Datenexport.</summary>
    IReadOnlyList<IExportArea> ExportAreas => [];

    /// <summary>
    /// S9: Schemaschritte der serverweiten Datenbank des Plugins (gehört dem Server, nicht einer Person - etwa Kontakte eines
    /// Teams). Zugriff über <see cref="IServerDataAccessor"/>. Nur anhängen, nie ändern.
    /// </summary>
    IReadOnlyList<SchemaStep> ServerSchema => [];

    /// <summary>
    /// S10: Wege ohne Anmeldung (Abmeldelink, Formular, öffentliche Seite). Die Gruppe liegt unter /p/&lt;key&gt;, ist je Adresse
    /// gebremst und antwortet mit 404, solange das Plugin aus ist. Alles hier ist für jeden im Netz erreichbar: nur zeigen und
    /// annehmen, was ausdrücklich öffentlich sein soll; Kennungen nicht erratbar.
    /// </summary>
    void MapPublic(IEndpointRouteBuilder routes, PluginContext context) { }

    /// <summary>
    /// S11: Eigener Rechnername für eine öffentliche Seite (etwa hilfe.beispiel.de für die Website in einer Sprache). Liefert für den Rechnernamen
    /// der Anfrage (http.Request.Host) einen Weg unter /p/&lt;key&gt; (etwa „/en“); der Kern leitet dann jede Anfrage dieses Rechnernamens
    /// dorthin (außer /api, /p und Dateien der Oberfläche). Sonst null. Wird bei jeder Anfrage gefragt: zwischengespeichert beantworten.
    /// </summary>
    string? PublicHostPath(HttpContext http, PluginContext context) => null;
}

/// <summary>S9: serverweite Datenbank eines Plugins (verschlüsselt, in der Sicherung des Servers).</summary>
public interface IServerData
{
    int Execute(string sql, params (string Name, object? Value)[] args);
    List<T> Query<T>(string sql, Func<IDataRecord, T> read, params (string Name, object? Value)[] args);
    object? Scalar(string sql, params (string Name, object? Value)[] args);
    /// <summary>Mehrere Schritte als Ganzes: alles oder nichts. Innerhalb nur das übergebene Objekt benutzen.</summary>
    T Transaction<T>(Func<IServerData, T> work);
}

/// <summary>Vom Kern bereitgestellt (über DI): die serverweite Datenbank eines Plugins.</summary>
public interface IServerDataAccessor
{
    /// <summary>Datenbank des Plugins mit diesem Schlüssel (aus plugin.json); Schema ist angewandt.</summary>
    IServerData For(string pluginKey);
}

/// <summary>Eine Person auf dem Server (ohne Geheimnisse).</summary>
public sealed record Person(string Id, string Login, string Name, bool Admin);

/// <summary>Kennung der angemeldeten Person einer Anfrage (für die Dienst-Varianten mit HttpContext).</summary>
public static class PluginUser
{
    /// <summary>Kennung der angemeldeten Person; wirft UnauthorizedAccessException ohne Anmeldung.</summary>
    public static string Id(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true && http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id
            ? id : throw new UnauthorizedAccessException("Nicht angemeldet.");
}

/// <summary>S11: Wer ist angemeldet, wer gibt es auf dem Server (für eigene Rechte eines Plugins).</summary>
public interface IPeople
{
    /// <summary>Die angemeldete Person; null ohne Anmeldung.</summary>
    Person? Me(HttpContext http);
    IReadOnlyList<Person> All();
}

/// <summary>Ein Mailkonto einer Person (zum Auswählen des Absenders).</summary>
public sealed record MailAccountInfo(string Id, string Name, string Address);

/// <summary>Eine ausgehende Mail an genau einen Empfänger. Kopfzeilen etwa List-Unsubscribe.</summary>
public sealed record OutgoingMail(string ToAddress, string? ToName, string Subject, string? Html, string? Text, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// S12: Mail aus dem Konto einer Person senden (ohne Kopie in „Gesendet“ - für Serienmails). Bei einer Anfrage die Varianten mit
/// HttpContext nehmen (Konto der angemeldeten Person); die mit userId nur für Aufträge im Hintergrund, deren Person das Plugin selbst
/// bei ihrer Anfrage festgehalten hat - nie eine Kennung aus der Anfrage durchreichen.
/// </summary>
public interface IMailOut
{
    IReadOnlyList<MailAccountInfo> Accounts(string userId);
    /// <summary>Sendet per SMTP des Kontos. Wirft bei Fehlern (Konto weg, Server lehnt ab).</summary>
    Task SendAsync(string userId, string accountId, OutgoingMail mail, CancellationToken ct);
    /// <summary>Konten der angemeldeten Person.</summary>
    IReadOnlyList<MailAccountInfo> Accounts(HttpContext http) => Accounts(PluginUser.Id(http));
    /// <summary>Sendet aus einem Konto der angemeldeten Person.</summary>
    Task SendAsync(HttpContext http, string accountId, OutgoingMail mail, CancellationToken ct) => SendAsync(PluginUser.Id(http), accountId, mail, ct);
}


/// <summary>
/// S14: Adresse des Servers für Links, die Empfänger außerhalb der Browsersitzung öffnen (Abmeldelinks, Links in Mails). Ist
/// eine öffentliche Adresse eingestellt, gilt diese, sonst die Adresse der Anfrage. Ältere Kerne
/// bieten den Dienst nicht an: über GetService holen und sonst selbst aus der Anfrage ableiten.
/// </summary>
public interface IPublicAddress
{
    /// <summary>Basis samt PathBase, ohne Schrägstrich am Ende, etwa https://office.example.de.</summary>
    string Base(HttpContext http);
}

/// <summary>S13: Ist eine Kennung des Kerns (features.json) in dieser Version freigeschaltet und an?</summary>
public interface ICoreFeatures
{
    bool On(string id);
}


/// <summary>Konto bzw. Kategorie im serverweiten Buch. Kind: „income“ oder „expense“; Number: Kontonummer (leer: ohne).</summary>
public sealed record BookCategory(string Id, string Kind, string Name, string Number);

/// <summary>
/// Buchung im serverweiten Buch. AmountCents ist positiv; eine Korrektur (Corrects = Kennung der korrigierten Buchung) trägt den
/// Gegenbetrag. Voucher: Belegnummer, falls Finanzen Belegnummern vergibt.
/// </summary>
public sealed record BookEntry(long Id, string Kind, string Date, long AmountCents, string Category, string Text, string Party, long? Corrects, string Voucher);

/// <summary>Neue Buchung: Art („income“, „expense“), Tag (yyyy-MM-dd), Betrag in Cent (&gt; 0), Kennung der Kategorie.</summary>
public sealed record BookEntryInput(string Kind, string Date, long AmountCents, string Category, string Text, string Party);

/// <summary>
/// S13: das serverweite Buch in Finanzen (eines je Server; Kennung finbook). Liegt verschlüsselt im Datenordner und geht in die
/// Sicherung des Servers. Ohne die Kennung ist <see cref="Available"/> false und jeder andere Aufruf wirft InvalidOperationException.
/// Rechte prüft das Plugin selbst (siehe <see cref="IServerBookAccess"/>); der Kern protokolliert jede Buchung im Protokoll von Finanzen.
/// </summary>
public interface IServerBook
{
    bool Available { get; }
    /// <summary>Name des Buchs, wie Finanzen es zeigt (etwa der Name des Vereins).</summary>
    string Name { get; }
    IReadOnlyList<BookCategory> Categories();
    /// <summary>Kategorie anlegen; mit Nummer als Konto. Gibt es den Namen in dieser Art schon, kommt die vorhandene zurück.</summary>
    BookCategory EnsureCategory(string kind, string name, string number = "");
    /// <summary>Buchungen eines Jahres, neueste zuerst (fällige wiederkehrende Einträge sind dann gebucht).</summary>
    IReadOnlyList<BookEntry> Entries(int year);
    BookEntry Entry(long id);
    BookEntry Add(BookEntryInput entry);
    /// <summary>Gegenbuchung, die den (Rest-)Betrag aufhebt; date leer: heute. Abgeschlossene Jahre bleiben unverändert.</summary>
    BookEntry Correct(long id, string? date = null);
}

/// <summary>
/// S13: Zugriff auf das serverweite Buch, den ein Plugin beisteuert (über DI anmelden, mehrere erlaubt). Admins dürfen immer alles;
/// sonst gilt das höchste Recht aus der Zugriffsliste in Finanzen und allen Beiträgen. Antwort: „edit“, „read“ oder null.
/// </summary>
public interface IServerBookAccess
{
    /// <summary>Wer gibt das Recht (für die Anzeige in der Zugriffsliste), etwa „Verein“.</summary>
    string Source { get; }
    string? LevelOf(string userId);
}

/// <summary>Ein Schemaschritt: Version (ab 1, aufsteigend) und SQL. Der Kern führt jeden Schritt samt Stand in einer Transaktion aus.</summary>
public sealed record SchemaStep(int Version, string Sql);

/// <summary>Text in beiden Sprachen der Oberfläche.</summary>
public sealed record LocalText(string De, string En)
{
    public string In(string lang) => lang == "en" ? En : De;
}

/// <summary>Was ein Plugin über sich und seinen Stand erfährt.</summary>
public sealed class PluginContext(string key, string version, string directory, Func<bool> on, Func<JsonElement?> settings)
{
    /// <summary>Schlüssel aus plugin.json („coach“).</summary>
    public string Key { get; } = key;
    public string Version { get; } = version;
    /// <summary>Ordner des Plugins (dort liegen plugin.json, lang/, Daten zum Mitliefern).</summary>
    public string Directory { get; } = directory;
    /// <summary>Ist das Plugin gerade eingeschaltet (Verwaltung → Plugins)?</summary>
    public bool On => on();
    /// <summary>S5 (vorbereitet): eigene Einstellungen des Plugins aus den Servereinstellungen, null wenn keine.</summary>
    public JsonElement? Settings => settings();
}

/// <summary>Zugriff auf die Benutzerdatenbank der angemeldeten Person (verschlüsselt; Schlüssel nur im Server).</summary>
public interface IUserData
{
    /// <summary>Kennung der Person.</summary>
    string UserId { get; }
    /// <summary>Zeitzone der Person.</summary>
    TimeZoneInfo Zone { get; }
    int Execute(string sql, params (string Name, object? Value)[] args);
    List<T> Query<T>(string sql, Func<IDataRecord, T> read, params (string Name, object? Value)[] args);
    object? Scalar(string sql, params (string Name, object? Value)[] args);
}

/// <summary>Vom Kern bereitgestellt (über DI): die Daten der Person einer Anfrage.</summary>
public interface IUserDataAccessor
{
    /// <summary>Daten der angemeldeten Person; wirft, wenn niemand angemeldet ist.</summary>
    IUserData For(HttpContext http);
}

/// <summary>S8: Bereich im Datenexport (Einstellungen → Datenexport). Erscheint nur, solange das Plugin an ist.</summary>
public interface IExportArea
{
    /// <summary>Kennung, eindeutig über alle Bereiche; üblich: der Schlüssel des Plugins.</summary>
    string Key { get; }
    /// <summary>Name in der Auswahl und als Ordner im Export.</summary>
    LocalText Label { get; }
    /// <summary>Dateien des Bereichs, Pfade relativ zum Ordner des Bereichs, mit „/“ getrennt.</summary>
    IEnumerable<ExportItem> Items(IUserData user);
}

/// <summary>Eine Datei im Export. Write schreibt den Inhalt; Collection: ändert sich als Ganzes (kein Vergleich nach Größe).</summary>
public sealed record ExportItem(string Path, Func<Stream, Task> Write, bool Collection = true);

