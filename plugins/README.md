# Plugins

Ein Ordner je Plugin (`docs/plugins-konzept.md`, Abschnitt 4). Der Kern kennt keine Plugins; ein fehlender Ordner ist ein fehlendes Plugin.

```
plugins/<key>/
  plugin.json        Schlüssel, Version, api-Stufe, Namen, core, requires, server/web
  server/            Klassenbibliothek gegen DmOffice.Sdk (ProjectReference mit Private="false"), gebaut nach server/bin
  web/src/index.tsx  Web-Teil; importiert nur react, @fluentui/react-components und @dmoffice/sdk aus dem Kern
  lang/en.json       Übersetzungen: deutscher Text → englisch
  LICENSE            nur wenn abweichend vom Kern
```

Bauen und Siegeln:
- Web: `node web/build-plugins.mjs` → `plugins/<key>/web/entry.js`
- Server: `dotnet publish plugins/<key>/server -o <bins>/<key>`
- Zusammenstellen und siegeln: `node tools/plugins/assemble.mjs plugins <bins> <ziel> <private.pem>`; einzeln: `node tools/plugins/seal.mjs`
- Im Image macht das der Dockerfile (Stufe „plugins“); der Schlüssel kommt als Build-Geheimnis `plugin_key`.

Geladen wird nur mit der Kennung `plugins` (features.json) und nur mit gültigem Siegel (eingebauter Schlüssel oder `DMOFFICE_PLUGIN_KEY`).
Ordner der Plugins zur Laufzeit: `DMOFFICE_PLUGINS`, sonst `plugins/` neben dem Programm.

Releases (Abschnitt 7 und 6.1):
- `releases.json` (Wurzel des Repos): je Version die Plugins der Community und der Enterprise Edition mit ihrer Version, z. B. `"2.4": { "community": { "journal": "1.0.0" }, "enterprise": { "finance": "1.0.0" } }`.
- Jede freigegebene Plugin-Version bekommt das Tag `plugin/<key>/<version>`.
- `node tools/release-cut.mjs X.Y.Z <arbeitsbaum>` legt `release-X.Y.Z` von `v2` in einem eigenen Arbeitsbaum an: nicht gelistete Plugin-Ordner (auch Enterprise) raus, gelistete auf den Stand ihres Tags, `requires` geprüft, `release.txt` gesetzt; danach entfernt `release-strip` die Funktionen späterer Versionen. Zwei Commits, nicht gepusht. `--dry-run` zeigt nur den Plan.
