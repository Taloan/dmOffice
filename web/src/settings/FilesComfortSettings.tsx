import { Checkbox, Field, Input, Radio, RadioGroup, Switch, Text, tokens } from '@fluentui/react-components'
import { featureOn } from '../api'
import { t } from '../i18n'
import { usePrefs } from '../prefs'
import { Hint, Section, SectionTabs } from './parts'

/** Komfort im Dateibereich, je Person einstellbar - jeder arbeitet anders (Vorbild: Einstellungen guter Dateimanager). */
export type FilesComfort = { perFolder?: boolean; collapse?: boolean; highlight?: boolean; dragOpen?: boolean; dragDelay?: number; addressBar?: boolean; commander?: boolean; dualTree?: 'shared' | 'own'; typeAhead?: boolean; tabs?: boolean }

/** Wirksame Werte: ohne Einstellung ist alles an, Verzögerung beim Ziehen eine Sekunde */
export const comfortOf = (c: FilesComfort | undefined) => ({
  perFolder: c?.perFolder !== false, collapse: c?.collapse !== false, highlight: c?.highlight !== false,
  dragOpen: c?.dragOpen !== false, dragDelay: Math.max(200, Math.min(5000, c?.dragDelay ?? 1000)), addressBar: c?.addressBar !== false,
  commander: c?.commander === true,
  // Zwei Fenster: ein Baum steuert das aktive Fenster (Vorgabe) oder jedes Fenster hat seinen eigenen
  dualTree: c?.dualTree === 'own' ? 'own' as const : 'shared' as const,
  typeAhead: c?.typeAhead !== false,
  tabs: c?.tabs !== false,
})

/** Gibt es auf dieser Seite etwas einzustellen? (Nur mit den Schaltern der Version 2.2) */
export const comfortAvailable = () => featureOn('folderviews') || featureOn('navtop') || featureOn('treeauto') || featureOn('addressbar') || featureOn('fkeys') || featureOn('typeahead') || featureOn('foldertabs') || featureOn('dualpane')

const NAV: [string, () => string][] = [['favorites', () => t('Favoriten')], ['recent', () => t('Zuletzt verwendet')], ['shared', () => t('Von mir geteilt')], ['trash', () => t('Papierkorb')]]

export function FilesComfortSettings() {
  const { prefs, update } = usePrefs()
  const c = comfortOf(prefs.filesComfort)
  const set = (patch: FilesComfort) => update({ filesComfort: { ...prefs.filesComfort, ...patch } })
  const top = prefs.filesNavTop ?? ['favorites']
  const muted = { color: tokens.colorNeutralForeground3 }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
      <SectionTabs page="files-comfort">
      <Text>{t('So verhält sich der Dateibereich. Die Einstellungen gelten für dein Konto auf allen Geräten.')}</Text>
      {featureOn('folderviews') && <Section title={t('Ansicht')}>
        <Switch checked={c.perFolder} onChange={(_, d) => set({ perFolder: d.checked })} label={t('Ansicht je Ordner merken')} />
        <Hint>{t('Darstellung, Sortierung, Gruppierung und Kachelgröße gelten für den Ordner, in dem sie eingestellt wurden. Ist die Einstellung deaktiviert, gilt überall dieselbe Ansicht.')}</Hint>
      </Section>}
      {featureOn('foldertabs') && <Section title={t('Ordner-Tabs')}>
        <Switch checked={c.tabs} onChange={(_, d) => set({ tabs: d.checked })} label={t('Mehrere Ordner als Tabs je Fenster')} />
        <Hint>{t('Neuer Tab mit „+“, per Rechtsklick „In neuem Tab öffnen“ oder mit der mittleren Maustaste auf einen Ordner. Die mittlere Maustaste auf einem Tab schließt ihn.')}</Hint>
      </Section>}
      {featureOn('addressbar') && <Section title={t('Adresszeile')}>
        <Switch checked={c.addressBar} onChange={(_, d) => set({ addressBar: d.checked })} label={t('Zurück, Vor und Eine Ebene höher neben dem Pfad; Pfad eintippen')} />
        <Hint>{t('Ein Klick in die freie Fläche neben dem Pfad öffnet die Eingabe. Tasten: Alt+← zurück, Alt+→ vor, Alt+↑ eine Ebene höher.')}</Hint>
      </Section>}
      {(featureOn('fkeys') || featureOn('typeahead')) && <Section title={t('Tastatur')}>
        {featureOn('typeahead') && <>
          <Switch checked={c.typeAhead} onChange={(_, d) => set({ typeAhead: d.checked })} label={t('Tippen springt zur Datei')} />
          <Hint>{t('Die ersten Buchstaben eines Namens markieren den passenden Eintrag. Derselbe Buchstabe noch einmal springt zum nächsten.')}</Hint>
        </>}
        {featureOn('fkeys') && <>
        <Switch checked={c.commander} onChange={(_, d) => set({ commander: d.checked })} label={t('Tasten wie im Norton Commander')} />
        <Hint>{t('F3 Ansehen, F4 Bearbeiten, F5 Kopieren nach, F6 Verschieben nach, Umschalt+F6 Umbenennen, F7 Neuer Ordner, F8 Löschen. Einfg markiert und geht weiter, + und \\ wählen nach Muster aus und ab, * kehrt die Auswahl um (auch auf dem Ziffernblock). Strg+R liest neu ein, Alt+. zeigt versteckte Dateien, Alt+C öffnet die Pfadeingabe, Strg+Leertaste zeigt die Eigenschaften. F5 lädt dann nicht mehr neu.')}</Hint>
        </>}
      </Section>}
      {featureOn('dualpane') && <Section title={t('Zwei Fenster')}>
        <Text>{t('Ordnerbaum, wenn zwei Fenster offen sind:')}</Text>
        <RadioGroup value={c.dualTree} onChange={(_, d) => set({ dualTree: d.value as 'shared' | 'own' })}>
          <Radio value="shared" label={t('Ein Baum steuert das aktive Fenster')} />
          <Radio value="own" label={t('Jedes Fenster mit eigenem Baum')} />
        </RadioGroup>
        <Hint>{t('Zwei Fenster schaltet der Knopf „Zwei Fenster“ im Menüband unter Ansicht ein.')}</Hint>
        <Hint>{t('Mit den Tasten wie im Norton Commander: F10 schaltet zwei Fenster ein und aus, Tab wechselt das aktive Fenster, F5 kopiert und F6 verschiebt die Auswahl in den Ordner des anderen Fensters, Alt+I öffnet den Ordner des aktiven Fensters auch im anderen, Alt+O öffnet den markierten Ordner im anderen Fenster, Strg+U tauscht die Ordner der beiden Fenster, Strg+Q zeigt im anderen Fenster eine Schnellansicht der aktuellen Datei.')}</Hint>
      </Section>}
      {featureOn('navtop') && <Section title={t('Oberer Bereich')}>
        <Text>{t('Diese Einträge stehen in der Seitenleiste über „Meine Dateien“:')}</Text>
        {NAV.map(([k, label]) => <Checkbox key={k} checked={top.includes(k)} label={label()}
          onChange={(_, d) => update({ filesNavTop: d.checked ? [...top, k] : top.filter(x => x !== k) })} />)}
        <Hint>{t('Auch per Rechtsklick auf den Eintrag in der Seitenleiste.')}</Hint>
      </Section>}
      {featureOn('treeauto') && <Section title={t('Ordnerbaum')}>
        <Switch checked={c.collapse} onChange={(_, d) => set({ collapse: d.checked })} label={t('Andere Zweige zuklappen, wenn ein Ordner gewählt wird')} />
        <Switch checked={c.highlight} onChange={(_, d) => set({ highlight: d.checked })} label={t('Weg zum gewählten Ordner hervorheben')} />
        <Switch checked={c.dragOpen} onChange={(_, d) => set({ dragOpen: d.checked })} label={t('Beim Ziehen Ordner aufklappen und danach wieder zuklappen')} />
        {c.dragOpen && <Field label={t('Verzögerung beim Ziehen')} hint={t('In Millisekunden, 200 bis 5000.')}>
          <Input type="number" min={200} max={5000} step={100} value={String(c.dragDelay)} style={{ width: 120 }}
            onChange={(_, d) => { const n = Number(d.value); if (Number.isFinite(n)) set({ dragDelay: n }) }} />
        </Field>}
        <Text size={200} style={muted}>{t('Bis zum gewählten Ordner klappt der Baum immer auf.')}</Text>
      </Section>}
      </SectionTabs>
    </div>
  )
}
