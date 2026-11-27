using System.Text.Json;
using System.Text.RegularExpressions;

namespace DmOffice.Core;

/// <summary>
/// Release-Liste: welche Bereiche und Funktionen in dieser Version freigeschaltet sind. features.json (neben der Assembly)
/// nennt je Kennung die Version, ab der sie gilt, oder „aus“ (in keiner Version); release.txt die Version dieses Stands.
/// Kennungen, die nicht in der Liste stehen, gelten immer. Testserver schalten mit DMOFFICE_FEATURES mehr oder weniger
/// frei: „all“ (alles), eine Versionsnummer wie „2.1“ (alles, was laut features.json bis dahin gilt) oder eine Liste wie
/// „+journal,-pgp“ - auch gemischt: „2.1,+journal“.
/// Nicht Freigeschaltetes ist nur inaktiv: Die Pfade der API antworten mit 404, Web und App blenden es aus
/// (/api/auth/state: featuresOff), Hintergrundarbeit und Querverbindungen fragen <see cref="On"/>.
/// Dazu die Hauptschalter der Module (<see cref="Modules"/>): Was der Admin ausschaltet (ServerSettings.ModulesOff), gilt
/// genauso als aus.
/// </summary>
public static class Features
{
    public const string Emergency = "emergency", Catchall = "catchall",
        MailExpiry = "mailexpiry", MailMove = "mailmove", Oidc = "oidc", Sftp = "sftp", Pgp = "pgp", FileCategories = "filecats", VirusScan = "clamav", Vault = "vault",
        ImportProfiles = "importprofiles", Audio = "audio", Gallery = "gallery", Notes = "notes", Tasks = "tasks", Office = "office",
        Dms = "dms", DmsFolders = "dmsfolders", DmsHide = "dmshide", DmsYear = "dmsyear", DmsSave = "dmssave", DmsAmount = "dmsamount", DmsRelearn = "dmsrelearn", AutoUnsubscribe = "autounsub",                 DefaultAccount = "defaultaccount", GalleryDupes = "galdupes", DmsMark = "dmsmark", DmsRework = "dmsrework", SaveToAblage = "ablagesave", UnifiedPick = "unifiedpick", DmsPlace = "dmsplace", DmsAiCheck = "dmsaicheck",
        CalendarSearch = "calsearch", ExtenderUsage = "extenderusage", RecentClear = "recentclear", FolderViews = "folderviews", NavTop = "navtop", TreeAuto = "treeauto", BackupCron = "backupcron", AddressBar = "addressbar", FolderProps = "folderprops", FKeys = "fkeys", ExportCron = "exportcron", SelectPattern = "selectpattern", CopyPath = "copypath", MailPlain = "mailplain", MailPreview = "mailpreview", DarkSwap = "darkswap", LimeAccent = "limeaccent", EnhanceButton = "enhancebtn", AiPhotos = "aiphotos", MailSearchHistory = "mailsearchhist", JunkRead = "junkread", AttachSave = "attsave", MosaicWeb = "mosaicweb", BulkRename = "bulkrename", F1Help = "f1help", DualPane = "dualpane", TypeAhead = "typeahead", FolderTabs = "foldertabs", DmsRoleName = "dmsrolename", MailTrackers = "mailtrackers", CalClip = "calclip", DmsHistory = "dmshistory", FilterPick = "filterpick", DialogTabs = "dialogtabs", UserLock = "userlock", MailPaneSize = "mailpanesize", ContextAll = "ctxall", DmsMerge = "dmsmerge", DmsKeys = "dmskeys", DmsFields = "dmsfields", GalleryUnread = "galunread", JobControl = "jobcontrol", NavGroups = "navgroups", DmsCopy = "dmscopy", DmsTreePick = "dmstreepick", NotesVersions = "notesversions", ExtTable = "exttable", TaskClip = "taskclip", PeopleSelect = "peoplesel", UndoAll = "undoall", ProxyTrust = "proxytrust", ToolIsolation = "tooliso", LinkHandler = "linkhandler", PickXmp = "pickxmp", MountDrivers = "mountdrivers", TileModes = "tilemodes", TileBar = "tilebar", QuickCollection = "quickcoll", Relink = "relink", PhishTrust = "phishtrust", TiffPreview = "tiffpreview", Plugins = "plugins", PluginRequires = "pluginreq", SyslogFile = "syslogfile", CalRemind = "calremind", CalRemDefault = "calremdefault", CalBulkRemind = "calbulkremind", CalRemTask = "calremtask", CalRemShared = "calremshared", UnifiedFav = "unifiedfav", SimilarMail = "similarmail", ContextGroups = "ctxgroups", AdminRibbon = "adminribbon", SaveMode = "savemode", RailSeparators = "railsep", MailDms = "maildms", RuleTabs = "ruletabs", RuleForward = "ruleforward", MailOwnAccounts = "mailownaccounts", MailServer = "mailserver", SpamStalwart = "spamstalwart", JunkRecheck = "junkrecheck",
        // Nur die App fragt sie ab (Galerie: P/X/U filtern, Markierung und Farbe für mehrere Fotos, RAW aus Galerie-Ordnern; externe Ordner)
        AppPick = "apppick", AppMarkMany = "appmarkmany", AppRaw = "appraw", AppExternal = "appext", AppUploadNet = "appuploadnet", AppTileRating = "tilerating", AppGalleryMosaic = "galmosaic";

    // Kennungen späterer Versionen je auf eigener Zeile: tools/release-strip.mjs entfernt sie beim Release-Schnitt samt Zeile
    public const string MailSnippets = "mailsnippets"; // [feature:mailsnippets]
    public const string LensCorr = "lenscorr"; // [feature:lenscorr]
    public const string LensLib = "lenslib"; // [feature:lenslib]
    public const string AdminClean = "adminclean"; // [feature:adminclean]
    public const string EditFull = "editfull"; // [feature:editfull]
    public const string ImportSubfolders = "importsub"; // [feature:importsub]
    public const string PhotoExport = "photoexport"; // [feature:photoexport]
    public const string GalleryMedium = "galmedium"; // [feature:galmedium]
    public const string MailAccountOff = "mailaccountoff"; // [feature:mailaccountoff]
    public const string CatchallDomains = "catchalldomains"; // [feature:catchalldomains]
    public const string MailSignatures = "mailsignatures"; // [feature:mailsignatures]
    public const string MailToColumn = "mailtocolumn"; // [feature:mailtocolumn]
    public const string NoteImages = "noteimg"; // [feature:noteimg]
    public const string MailAvatarFilter = "mailavatarfilter"; // [feature:mailavatarfilter]
    public const string MailThreads = "mailthreads"; // [feature:mailthreads]
    public const string MailPrefetch = "mailprefetch"; // [feature:mailprefetch]
    public const string MailPush = "mailpush"; // [feature:mailpush]
    public const string MasterKeyRequired = "masterkeyreq"; // [feature:masterkeyreq]
    public const string GallerySharedSkip = "galsharedskip"; // [feature:galsharedskip]
    public const string ConfigSeal = "cfgseal"; // [feature:cfgseal]
    public const string MailAiReply = "mailaireply"; // [feature:mailaireply]
    public const string DmsTresorPlace = "dmstresorplace"; // [feature:dmstresorplace]
    public const string Journal = "journal"; // [feature:journal]
    public const string JunkLock = "junklock"; // [feature:junklock]
    public const string GateKeeper = "gatekeeper"; // [feature:gatekeeper]
    public const string DiaryRecallThumbs = "diaryrecallthumbs"; // [feature:diaryrecallthumbs]
    public const string GkStats = "gkstats"; // [feature:gkstats]
    public const string ProxyConfirm = "proxyconfirm"; // [feature:proxyconfirm]
    public const string SftpTrust = "sftptrust"; // [feature:sftptrust]
    public const string PublicUrl = "publicurl"; // [feature:publicurl]
    public const string UploadLimit = "uploadlimit"; // [feature:uploadlimit]
    public const string MigrationProgress = "migrationprogress"; // [feature:migrationprogress]

    /// <summary>
    /// Zusatzmodule, die der Admin serverweit aus- und einschaltet (Reihenfolge wie in der Leiste). Die Grundausstattung
    /// (Mail, Kalender, Kontakte, Aufgaben, Notizen, Dateien, Galerie, Chat) ist nicht abschaltbar - andere Bereiche bauen
    /// darauf auf. Früher gespeicherte Schalter dafür zählen nicht mehr (SetModulesOff).
    /// </summary>
    public static readonly string[] Modules = [
        Journal, // [feature:journal]
        Audio, Dms,
    ];

    /// <summary>Angezeigte Version: die höchste, deren Funktionen alle freigeschaltet sind (Testserver mit DMOFFICE_FEATURES
    /// zeigen so 2.1 statt 2.0). Steht vor _off, weil Load beides setzt.</summary>
    public static string Shown { get; private set; } = "";
    /// <summary>Darüber hinaus freigeschaltete Funktionen späterer Versionen oder solche, die in keiner Version gelten („aus“)</summary>
    public static IReadOnlyList<string> ShownExtra { get; private set; } = [];

    // Alle Kennungen aus features.json (für „requires“ der Plugins); vor _off, weil Load es setzt
    private static HashSet<string> _known = [];
    private static HashSet<string> _off = Load();
    // Vom Admin ausgeschaltete Module (ServerSettings.ModulesOff), gesetzt in Use und bei jeder Änderung der Einstellungen
    private static HashSet<string> _modulesOff = new();

    /// <summary>Ist die Funktion in dieser Version freigeschaltet und (bei Modulen) vom Admin eingeschaltet?</summary>
    public static bool On(string id) => !_off.Contains(id) && !_modulesOff.Contains(id);

    /// <summary>Ist die Funktion in dieser Version freigeschaltet (Hauptschalter des Admins außer Acht)?</summary>
    public static bool InRelease(string id) => !_off.Contains(id);

    /// <summary>Steht die Kennung in features.json?</summary>
    public static bool Known(string id) => _known.Contains(id);

    /// <summary>
    /// Kennungen von Funktionen, die dieser Stand gar nicht enthält. Clients, die sie kennen (die App), blenden sie nur aus, wenn
    /// sie in <see cref="Off"/> stehen - darum meldet der Server sie immer als aus. Auf v2 leer; tools/release-strip.mjs trägt
    /// beim Release-Schnitt die entfernten Kennungen ein.
    /// </summary>
    internal static readonly string[] NotIncluded = ["faces", "helpdesk", "pluginreq", "vereinpdf", "finbook", "vereinkasse", "coach", "finance", "finfiling", "zugferd", "finaccounts", "finassets", "einvoice", "finbankimport", "finbooking", "fints", "mailnudge", "navgroups", "fintsdebit", "webrich", "webimg", "webfetch", "webwp", "webpreview", "webmenu", "webblog", "webredirect", "webnav", "websearch", "webseo", "weblang", "webfonts", "webform", "webhero", "webanchor", "websearchicon", "webdblclick", "webcode", "webpreviewkeep", "webpreviewtoggle", "webaddsort", "webtagsplit", "webimguse", "webmenulang", "webleaveask", "webconsent", "webstage", "webhosts", "hdnoticket", "hdlook", "hdembed", "hdsuggest", "webhelpdesk", "mailownaccounts", "mailserver", "projects", "household", "hhdouble", "mailpw", "webspace", "webstyle", "realtabs", "sharechat", "helpdeskoauth", "hdinline", "hdlist", "hdformlang", "eurexport", "fincustomers", "finetag", "finiban", "finrecfrom"];

    /// <summary>Die nicht freigeschalteten, ausgeschalteten oder nicht enthaltenen Kennungen (für Web und App).</summary>
    public static IReadOnlyCollection<string> Off => _off.Union(_modulesOff).Union(NotIncluded).ToList();

    /// <summary>Nur für Tests: die nicht freigeschalteten Kennungen ohne die vom Admin ausgeschalteten Module.</summary>
    internal static IReadOnlyCollection<string> InReleaseOff => _off;

    /// <summary>Nur für Tests: Liste neu festlegen.</summary>
    internal static void Set(IEnumerable<string> off) => _off = off.ToHashSet();

    /// <summary>Vom Admin ausgeschaltete Module übernehmen (unbekannte Kennungen zählen nicht).</summary>
    internal static void SetModulesOff(IEnumerable<string>? ids) => _modulesOff = (ids ?? []).Where(Modules.Contains).ToHashSet();

    private static HashSet<string> Load()
    {
        var dir = AppContext.BaseDirectory;
        string release;
        Dictionary<string, string> list;
        try { release = File.ReadAllText(Path.Combine(dir, "release.txt")).Trim(); }
        catch (IOException) { release = ""; }
        try { list = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "features.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { list = new(); }
        _known = list.Keys.ToHashSet();
        var off = Compute(release, list, Environment.GetEnvironmentVariable("DMOFFICE_FEATURES"));
        (Shown, var extra) = Display(release, list, off);
        ShownExtra = extra;
        // Demo-Server: einiges ist dort immer aus (Tresor, Importprofile)
        if (Demo.Enabled) off.UnionWith(Demo.FeaturesOff);
        return off;
    }

    /// <summary>Nicht freigeschaltet: Version der Kennung über der dieses Stands oder „aus“, dann DMOFFICE_FEATURES.</summary>
    internal static HashSet<string> Compute(string release, Dictionary<string, string> list, string? env)
    {
        var off = list.Where(kv => !Released(kv.Value, release)).Select(kv => kv.Key).ToHashSet();
        foreach (var part in (env ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("all", StringComparison.OrdinalIgnoreCase)) off.Clear();
            // Versionsnummer: verhalten wie diese Version (Testserver der nächsten Version)
            else if (char.IsDigit(part[0])) off.RemoveWhere(id => list.TryGetValue(id, out var since) && Released(since, part));
            else if (part.StartsWith('+')) off.Remove(part[1..]);
            else if (part.StartsWith('-')) off.Add(part[1..]);
        }
        return off;
    }

    /// <summary>Angezeigte Version und die Funktionen, die darüber hinaus freigeschaltet sind.</summary>
    internal static (string Shown, List<string> Extra) Display(string release, Dictionary<string, string> list, HashSet<string> off)
    {
        var shown = release;
        var later = list.Values.Where(v => Version.TryParse(v, out _) && !Released(v, release)).Distinct().OrderBy(Version.Parse);
        foreach (var v in later)
        {
            if (list.Where(kv => kv.Value == v).Any(kv => off.Contains(kv.Key))) break;
            shown = v;
        }
        var extra = list.Where(kv => !Released(kv.Value, shown) && !off.Contains(kv.Key)).Select(kv => kv.Key).Order().ToList();
        return (shown, extra);
    }

    private static bool Released(string since, string release) =>
        Version.TryParse(since, out var s) && Version.TryParse(release, out var r) && s <= r;

    // Pfade der API je Kennung - wer nicht freigeschaltet ist, bekommt 404
    private static readonly (Regex Path, string Id)[] Paths =
    [
        (new(@"^/api/journal(/|$)", RegexOptions.IgnoreCase), Journal), // [feature:journal]
        (new(@"^/api/(admin/)?gatekeeper(/|$)", RegexOptions.IgnoreCase), GateKeeper), // [feature:gatekeeper]
        (new(@"^/api/admin/security/proxies(/|$)", RegexOptions.IgnoreCase), ProxyConfirm), // [feature:proxyconfirm]
        (new(@"^/api/admin/public-url(/|$)", RegexOptions.IgnoreCase), PublicUrl), // [feature:publicurl]
        (new(@"^/api/(sftp|auth/restore)/(host-key|trust-host)$", RegexOptions.IgnoreCase), SftpTrust), // [feature:sftptrust]
        // Plugin-Rahmen: Liste und Verwaltung (Routen der Plugins selbst sperrt PluginHost)
        (new(@"^/api/(admin/)?plugins(/|$)", RegexOptions.IgnoreCase), Plugins),
        (new(@"^/api/(auth|admin)/oidc(/|$)", RegexOptions.IgnoreCase), Oidc),
        (new(@"^/api/gallery/save(/|$)", RegexOptions.IgnoreCase), SaveToAblage),
        (new(@"^/api/settings/forward-targets(/|$)", RegexOptions.IgnoreCase), RuleForward),
        (new(@"^/forward-confirm(/|$)", RegexOptions.IgnoreCase), RuleForward),
        (new(@"^/api/(auth/)?emergency(/|$)", RegexOptions.IgnoreCase), Emergency),
        (new(@"^/n(/|$)", RegexOptions.IgnoreCase), Emergency),
        (new(@"^/api/mail/aliases(/|$)", RegexOptions.IgnoreCase), Catchall),
        (new(@"^/api/mail/aliases/(sites|reply-from|senders)$", RegexOptions.IgnoreCase), CatchallDomains), // [feature:catchalldomains]
        (new(@"^/api/mail/(expiries$|[^/]+/expire(/|$))", RegexOptions.IgnoreCase), MailExpiry),
        (new(@"^/api/mail/(pgp(/|$)|[^/]+/signature$)", RegexOptions.IgnoreCase), Pgp),
        (new(@"^/api/mail/snippets(/|$)", RegexOptions.IgnoreCase), MailSnippets), // [feature:mailsnippets]
        (new(@"^/api/mail/signatures(/|$)", RegexOptions.IgnoreCase), MailSignatures), // [feature:mailsignatures]
        (new(@"^/api/files/(categories|tags/items)(/|$)", RegexOptions.IgnoreCase), FileCategories),
        (new(@"^/api/admin/virus(/|$)", RegexOptions.IgnoreCase), VirusScan),
        // Systemprotokoll auf Dauer: frühere Tage aus <Daten>/logs
        (new(@"^/api/admin/log/days?(/|$)", RegexOptions.IgnoreCase), SyslogFile),
        (new(@"^/api/admin/users/[^/]+/(lock|unlock)$", RegexOptions.IgnoreCase), UserLock),
        (new(@"^/api/tresor(/|$)", RegexOptions.IgnoreCase), Vault),
        (new(@"^/api/gallery/imports(/|$)", RegexOptions.IgnoreCase), ImportProfiles),
        (new(@"^/api/gallery/lens(/|$)", RegexOptions.IgnoreCase), LensCorr), // [feature:lenscorr]
        (new(@"^/api/gallery/lens/lib(/|$)", RegexOptions.IgnoreCase), LensLib), // [feature:lenslib]
        (new(@"^/api/dms(/|$)", RegexOptions.IgnoreCase), Dms),
        (new(@"^/api/mail/[^/]+/to-dms$", RegexOptions.IgnoreCase), Dms),
        // Kontoumsätze aus Kontoauszügen, ohne Verbindung zur Bank
        // E-Rechnungen empfangen: Auswertung am DMS-Dokument und an Mail-Anhängen
        // Einträge zusammenführen
        (new(@"^/api/dms/lists/merge$", RegexOptions.IgnoreCase), DmsMerge),
        // Bankzugang per FinTS: Person und Verwaltung, wie FinTsApi.On()
        (new(@"^/api/notes/(vault/|databases/[^/]+/(tresor|out)$)", RegexOptions.IgnoreCase), Vault),
        (new(@"^/api/notes/(vault/[^/]+/)?-?\d+/versions(/|$)", RegexOptions.IgnoreCase), NotesVersions),
        // Einheitliches Rückgängig: Mails zurückverschieben, gelöschten Tagebuch-Eintrag wiederherstellen
        (new(@"^/api/mail/[^/]+/move-back$", RegexOptions.IgnoreCase), UndoAll),
        (new(@"^/api/journal/entries/\d+/[^/]+/restore$", RegexOptions.IgnoreCase), UndoAll),
        // Module (Hauptschalter des Admins). Ein Pfad kann zu mehreren Kennungen gehören - aus ist er, wenn eine davon aus ist.
        (new(@"^/api/audio(/|$)", RegexOptions.IgnoreCase), Audio),
        (new(@"^/api/gallery/export(/|$)", RegexOptions.IgnoreCase), PhotoExport), // [feature:photoexport]
        // Galerie: /api/gallery/info schreibt Bildangaben auch für die Dateien-Ansicht und bleibt erreichbar
        (new(@"^/api/(gallery(?!/info$)|public-album)(/|$)", RegexOptions.IgnoreCase), Gallery),
        (new(@"^/api/notes(/|$)", RegexOptions.IgnoreCase), Notes),
        (new(@"^/api/pim/(tasks|lists|list-people)(/|$)", RegexOptions.IgnoreCase), Tasks),
        // Aufgaben: gelöschte direkt danach wiederherstellen („Rückgängig“)
        (new(@"^/api/pim/tasks/\d+/[^/]+/restore$", RegexOptions.IgnoreCase), TaskClip),
        // Office: nur die Editor-Anbindung - Öffnen und Herunterladen in den Dateien bleibt
        (new(@"^/(api/office|wopi|onlyoffice)(/|$)", RegexOptions.IgnoreCase), Office),
    ];

    /// <summary>Kennung, zu der ein Pfad der API gehört (oder null) - die erste passende.</summary>
    internal static string? OfPath(string path) => Paths.FirstOrDefault(p => p.Path.IsMatch(path)).Id;

    /// <summary>Gehört der Pfad zu einer Kennung, die aus ist?</summary>
    internal static bool Blocked(string path) => Paths.Any(p => !On(p.Id) && p.Path.IsMatch(path));

    public static void Use(WebApplication app)
    {
        // Hauptschalter der Module aus den Einstellungen, auch nach jeder Änderung
        var config = app.Services.GetRequiredService<ServerConfig>();
        SetModulesOff(config.Current.ModulesOff);
        config.Changed += () => SetModulesOff(config.Current.ModulesOff);
        app.Use(async (ctx, next) =>
        {
            if ((_off.Count > 0 || _modulesOff.Count > 0) && Blocked(ctx.Request.Path.Value ?? ""))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }
}
