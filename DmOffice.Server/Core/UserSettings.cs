namespace DmOffice.Core;

public enum Security { Ssl, StartTls, None }

public sealed class MailAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public Security ImapSecurity { get; set; } = Security.Ssl;
    public string ImapUser { get; set; } = "";
    public string? ImapPassword { get; set; }

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public Security SmtpSecurity { get; set; } = Security.Ssl;
    /// <summary>Leer: dieselbe Anmeldung wie IMAP.</summary>
    public string SmtpUser { get; set; } = "";
    public string? SmtpPassword { get; set; }

    /// <summary>Sprache der Mails aus diesem Konto: "de" oder "en" - fuer AW/Re, WG/Fwd,
    /// Zitatkopf und Rechtschreibpruefung.</summary>
    public string Language { get; set; } = "de";

    /// <summary>HTML, wird beim Verfassen unter den Text gesetzt.</summary>
    public string Signature { get; set; } = "";
    /// <summary>Selbstsignierte Zertifikate annehmen (Server im eigenen Netz).</summary>
    public bool AcceptAnyCertificate { get; set; }
    /// <summary>Gesendetes in "Gesendet" ablegen. Aus bei Anbietern, die das
    /// selbst tun (Gmail) - sonst liegt jede Mail doppelt da.</summary>
    public bool SaveSentCopy { get; set; } = true;

    /// <summary>0: alles synchronisieren; sonst so viele Monate zurueck.</summary>
    public int SyncMonths { get; set; } = 12;
    /// <summary>Mails bis zu dieser Groesse kommen vollstaendig in die
    /// Datenbank (samt Anhaengen); groessere werden beim Oeffnen geholt.</summary>
    public int MaxCachedMb { get; set; } = 50;

    /// <summary>Papierkorb/Junk automatisch leeren: Mails, die laut Datum aelter als so
    /// viele Tage sind, endgueltig loeschen. 0: nie.</summary>
    public int TrashDays { get; set; }
    public int JunkDays { get; set; }

    /// <summary>Prio-Posteingang (Relevant/Sonstige) fuer dieses Konto.</summary>
    public bool FocusedInbox { get; set; } = true;
}

public sealed class RuleCondition
{
    /// <summary>from | to | subject | body | any | header:&lt;Name&gt;</summary>
    public string Field { get; set; } = "from";
    /// <summary>contains | equals | startsWith | endsWith | regex | notContains</summary>
    public string Op { get; set; } = "contains";
    public string Value { get; set; } = "";
}

public sealed class RuleAction
{
    /// <summary>move | copy | markRead | flag | delete | forward | stop</summary>
    public string Type { get; set; } = "move";
    /// <summary>Ordner bei move/copy, Adressen bei forward.</summary>
    public string Value { get; set; } = "";
}

public sealed class MailRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Null: alle Konten.</summary>
    public string? AccountId { get; set; }
    /// <summary>all | any</summary>
    public string Match { get; set; } = "all";
    public List<RuleCondition> Conditions { get; set; } = new();
    public List<RuleAction> Actions { get; set; } = new();
}

/// <summary>Kategorie einer Mail - auf dem Server ein IMAP-Schlagwort (Key), hier Name und Farbe.</summary>
public sealed class MailCategory
{
    /// <summary>IMAP-Schlagwort, nur a-z, 0-9 und _ (Atom nach RFC 3501).</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#0F6CBD";
}

/// <summary>Wonach ein Suchordner sucht - alles optional, mehrere Angaben gelten zusammen (UND).</summary>
public sealed class MailCriteria
{
    /// <summary>Suchbegriffe wie in der Suche, auch von:/an:/betreff:.</summary>
    public string? Text { get; set; }
    /// <summary>Nur diese Konten (Id). Leer: alle.</summary>
    public List<string>? Accounts { get; set; }
    /// <summary>Nur diese Ordner als "konto|pfad". Leer: alle.</summary>
    public List<string>? Folders { get; set; }
    /// <summary>Mindestens eine dieser Kategorien (IMAP-Schlagwort).</summary>
    public List<string>? Categories { get; set; }
    /// <summary>Nur die letzten N Tage.</summary>
    public int? Days { get; set; }
    /// <summary>Fester Zeitraum, ISO-Datum (yyyy-MM-dd), jeweils einschliesslich.</summary>
    public string? From { get; set; }
    public string? To { get; set; }
    public bool Unread { get; set; }
    public bool Flagged { get; set; }
    public bool Attachments { get; set; }
    /// <summary>Auch Papierkorb und Junk durchsuchen.</summary>
    public bool IncludeDeleted { get; set; }
}

/// <summary>Gespeicherte Suche, die wie ein Ordner erscheint (virtuell - die Mails bleiben, wo sie sind).</summary>
public sealed class SearchFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#0F6CBD";
    public MailCriteria Criteria { get; set; } = new();
}

public sealed class SpamSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Ab diesem Wert (0..1) Junk.</summary>
    public double SpamCutoff { get; set; } = 0.90;
    /// <summary>Ab diesem Wert "unsicher" - markiert, bleibt im Posteingang.</summary>
    public double HamCutoff { get; set; } = 0.20;
    /// <summary>Erst ab so vielen gelernten Mails je Seite wird verschoben.</summary>
    public int MinTrained { get; set; } = 10;
    /// <summary>Kontakte und Empfaenger gesendeter Mails sind sichere Absender: nie Junk (SafeSenders).</summary>
    public bool TrustKnown { get; set; } = true;
    /// <summary>Ihre neuen Mails als gut lernen.</summary>
    public bool LearnKnown { get; set; } = true;
}

public sealed class NotiziSettings
{
    public bool Enabled { get; set; }
    /// <summary>Das E2EE-Passwort, wie in notizi unter Einstellungen → Sync.</summary>
    public string? E2eePassword { get; set; }
    /// <summary>Wurzel des Abgleichs im eigenen WebDAV, z. B. "notizisync".</summary>
    public string Root { get; set; } = "notizisync";
    /// <summary>Ordner der Datenbank unter der Wurzel.</summary>
    public string DbFolder { get; set; } = "";
    public string? DbId { get; set; }
    public string? BlobKeyMark { get; set; }
}

public sealed class UserSettings
{
    public List<MailAccount> MailAccounts { get; set; } = new();
    public List<MailRule> Rules { get; set; } = new();
    /// <summary>Null: noch nie eingerichtet - dann die Vorgaben. Leere Liste: bewusst keine.</summary>
    public List<MailCategory>? Categories { get; set; }
    public SpamSettings Spam { get; set; } = new();
    /// <summary>Prio-Posteingang: neue Mails im Posteingang nach Relevant/Sonstige einordnen (Mail/Focus.cs).</summary>
    public bool FocusedInbox { get; set; } = true;
    public NotiziSettings Notizi { get; set; } = new();
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";
    /// <summary>IANA-Zone fuer Termine.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";
    /// <summary>Gelesen markieren nach so vielen Sekunden im Lesebereich; -1: nie automatisch.</summary>
    public int MarkReadDelay { get; set; } = 2;
    /// <summary>Beim Oeffnen eines Mailordners: none | first (neueste) | last (zuletzt gewaehlte, sonst neueste).</summary>
    public string OpenOnFolder { get; set; } = "last";
    /// <summary>Absender (adresse@x.de) oder Domains (@x.de), deren Bilder aus dem Netz immer geladen werden.</summary>
    public List<string> TrustedImageSenders { get; set; } = new();
    public List<SearchFolder> SearchFolders { get; set; } = new();
    /// <summary>Pakete, Fluege, Hotels, Reservierungen aus Mails in den Kalender "E-Mail-Ankündigungen".</summary>
    public bool SmartCalendar { get; set; } = true;
    /// <summary>Kalender fuer angenommene Einladungen. Null: der erste Kalender fuer Termine.</summary>
    public long? InviteCalendar { get; set; }
}
