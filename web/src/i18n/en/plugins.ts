// Plugins (Verwaltung → System → Plugins, Kennung plugins). Feste Meldungen des Servers (Plugins/PluginHost.cs, PluginSeal.cs) mit.
export const plugins: Record<string, string> = {
  "Plugins": "Plugins",
  "Server-Erweiterungen": "Server extensions", // [feature:modplugins]
  "Gilt für alle Benutzer. Version, Siegel und Voraussetzungen stehen unter Plugins.": "Applies to all users. Version, seal and requirements are shown under Plugins.", // [feature:modplugins]
  "{name} eingeschaltet": "{name} switched on",
  "{name} ausgeschaltet": "{name} switched off",
  "Die Leiste ändert sich nach dem Neuladen der Seite.": "The navigation bar changes after the page is reloaded.",
  "Plugins liegen als Ordner unter {dir}. Geladen wird nur, was gesiegelt ist und zur Kern-Schnittstelle (Stufe {level}) passt. Die Daten ausgeschalteter Plugins bleiben erhalten.":
    "Plugins are folders in {dir}. Only sealed plugins that match the core interface (level {level}) are loaded. The data of switched-off plugins is kept.",
  "Keine Plugins vorhanden.": "No plugins available.",
  "Siegel: dmOffice": "Seal: dmOffice",
  "Siegel: Betreiber": "Seal: operator",
  "nicht geladen": "not loaded",
  "Server-Teil": "Server part",
  "Web-Teil": "Web part",
  "braucht {list}": "requires {list}",
  "API-Stufe {n}": "API level {n}",
  "Ohne Siegel.": "Not sealed.",
  "Siegel ist unlesbar.": "The seal cannot be read.",
  "Signatur ist unlesbar.": "The signature cannot be read.",
  "Siegel gehört zu einem anderen Plugin oder einer anderen Version.": "The seal belongs to a different plugin or version.",
  "Kein vertrauter Schlüssel hinterlegt.": "No trusted key is configured.",
  "Signatur passt zu keinem vertrauten Schlüssel.": "The signature does not match any trusted key.",
  "Schlüssel doppelt vergeben.": "The key is used twice.",
  "Dieses Plugin ist nicht geladen.": "This plugin is not loaded.",
}
