import { Button, Checkbox, Dropdown, Field, Option, Radio, RadioGroup, Switch, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { t } from '../i18n'
import { Hint, Section, SectionTabs } from './parts'
import { FolderPicker } from '../files/FilesView'
import { useAttachSettings } from '../mail/AttachSave'

const useStyles = makeStyles({
  field: { maxWidth: '400px' },
  layouts: { display: 'flex', gap: '16px', flexWrap: 'wrap' },
  layout: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px', cursor: 'pointer', backgroundColor: 'transparent', border: 'none', paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0, color: 'inherit', fontFamily: 'inherit', fontSize: '14px' },
  frame: {
    width: '112px', height: '76px', borderRadius: '8px', border: `2px solid ${tokens.colorNeutralStroke1}`, padding: '8px', display: 'flex', gap: '4px',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  picked: { border: `2px solid ${tokens.colorBrandStroke1}`, boxShadow: `0 0 0 2px ${tokens.colorBrandStroke1}` },
  box: { borderRadius: '3px', backgroundColor: tokens.colorNeutralBackground6 },
  pane: { borderRadius: '3px', backgroundColor: tokens.colorBrandBackground },
})

export function MessageListSettings() {
  const { prefs, update } = usePrefs()
  return (
    <>
      <Section title={t('Abstände')} hint={t('Zeilenhöhe getrennt für Ordnerliste und Nachrichtenliste. Die Schrift des Mailtexts lässt sich im Lesebereich unten rechts zoomen.')}>
        <Field label={t('Ordnerliste')}>
          <RadioGroup value={prefs.folderDensity ?? prefs.density ?? 'normal'} onChange={(_, d) => update({ folderDensity: d.value as any })} layout="horizontal">
            <Radio value="compact" label={t('Kompakt')} /><Radio value="normal" label={t('Normal')} /><Radio value="roomy" label={t('Geräumig')} />
          </RadioGroup>
        </Field>
        <Field label={t('Nachrichtenliste')}>
          <RadioGroup value={prefs.listDensity ?? prefs.density ?? 'normal'} onChange={(_, d) => update({ listDensity: d.value as any })} layout="horizontal">
            <Radio value="compact" label={t('Kompakt')} /><Radio value="normal" label={t('Normal')} /><Radio value="roomy" label={t('Geräumig')} />
          </RadioGroup>
        </Field>
      </Section>
      <Section title={t('Anzeige')} last>
        {featureOn('mailpreview')
          ? <Field label={t('Vorschautext in der Nachrichtenliste')}>
            <RadioGroup layout="horizontal" value={String(prefs.previewLines ?? ((prefs.showPreview ?? true) ? 1 : 0))}
              onChange={(_, d) => update({ previewLines: Number(d.value) as 0 | 1 | 2, showPreview: d.value !== '0' })}>
              <Radio value="0" label={t('Aus')} /><Radio value="1" label={t('1 Zeile')} /><Radio value="2" label={t('2 Zeilen')} />
            </RadioGroup>
          </Field>
          : <Checkbox label={t('Vorschautext anzeigen')} checked={prefs.showPreview ?? true} onChange={(_, d) => update({ showPreview: !!d.checked })} />}
        <Checkbox label={t('Absenderbilder anzeigen')} checked={prefs.showAvatars ?? true} onChange={(_, d) => update({ showAvatars: !!d.checked })} />
        {featureOn('mailsearchhist') && <Checkbox label={t('Suchbegriffe merken')} checked={!prefs.mailSearchHistoryOff}
          onChange={(_, d) => update(d.checked ? { mailSearchHistoryOff: false } : { mailSearchHistoryOff: true, mailSearchHistory: [] })} />}
        <Hint>{t('Gruppieren nach Unterhaltung folgt.')}</Hint>
      </Section>
    </>
  )
}

function LayoutPreview({ kind }: { kind: 'right' | 'bottom' | 'off' }) {
  const s = useStyles()
  if (kind === 'right') return <><div className={s.box} style={{ width: '28%' }} /><div className={s.box} style={{ width: '28%' }} /><div className={s.pane} style={{ flex: 1 }} /></>
  if (kind === 'bottom') return <><div className={s.box} style={{ width: '28%' }} /><div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 4 }}><div className={s.box} style={{ height: '40%' }} /><div className={s.pane} style={{ flex: 1 }} /></div></>
  return <><div className={s.box} style={{ width: '28%' }} /><div className={s.pane} style={{ flex: 1 }} /></>
}

export function PreviewPaneSettings() {
  const s = useStyles()
  const { prefs, update } = usePrefs()
  const { fail } = useApp()
  const [server, setServer] = useState<any>(null)
  useEffect(() => { api.get('/api/settings/').then(setServer).catch(fail) }, [fail])
  const saveServer = async (p: any) => {
    setServer((x: any) => ({ ...x, ...p }))
    try { await api.put('/api/settings/general', p); window.dispatchEvent(new Event('dmoffice:mailsettings')) } catch (e) { fail(e) }
  }
  const pane = prefs.readingPane ?? 'right'
  const delay = server?.markReadDelay ?? 2
  return (
    <>
      <SectionTabs page="pane">
      <Section title={t('Layout')} hint={t('Wie möchten Sie Ihr Vorschaufenster gestalten?')}>
        <div className={s.layouts}>
          {([['right', t('Rechts')], ['bottom', t('Unten')], ['off', t('Vollbildansicht')]] as const).map(([k, l]) => (
            <button key={k} className={s.layout} onClick={() => update({ readingPane: k })}>
              <div className={mergeClasses(s.frame, pane === k && s.picked)}><LayoutPreview kind={k} /></div>
              {l}
            </button>
          ))}
        </div>
        <Hint>{t('Vollbildansicht: Die Liste nimmt den ganzen Platz ein, eine Mail öffnet sich darüber.')}</Hint>
      </Section>
      <Section title={t('Als gelesen markieren')}>
        {server && (
          <RadioGroup value={delay < 0 ? 'never' : delay === 0 ? 'now' : 'delay'}
            onChange={(_, d) => saveServer({ markReadDelay: d.value === 'never' ? -1 : d.value === 'now' ? 0 : 2 })}>
            <Radio value="delay" label={t('Nach kurzer Anzeige im Lesebereich')} />
            <Radio value="now" label={t('Sofort beim Auswählen')} />
            <Radio value="never" label={t('Nie automatisch')} />
          </RadioGroup>
        )}
        {server && delay > 0 && (
          <Field label={t('Wartezeit')} style={{ maxWidth: 200 }}>
            <Dropdown value={delay === 1 ? t('1 Sekunde') : t('{n} Sekunden', { n: delay })} selectedOptions={[String(delay)]} onOptionSelect={(_, d) => saveServer({ markReadDelay: Number(d.optionValue) })}>
              {[1, 2, 3, 5, 10, 30].map(n => <Option key={n} value={String(n)}>{n === 1 ? t('1 Sekunde') : t('{n} Sekunden', { n })}</Option>)}
            </Dropdown>
          </Field>
        )}
      </Section>
      {featureOn('mailplain') && <Section title={t('Mails ohne Formatierung')}>
        <Field label={t('Zeilenlänge')} hint={t('Mails mit eigener Gestaltung behalten ihr Layout.')}>
        <RadioGroup layout="horizontal" value={String(prefs.plainWidth ?? 110)} onChange={(_, d) => update({ plainWidth: Number(d.value) })}>
          <Radio value="80" label={t('Schmal')} /><Radio value="110" label={t('Mittel')} /><Radio value="0" label={t('Volle Breite')} />
        </RadioGroup>
      </Field>
      </Section>}
      <Section title={t('Beim Öffnen eines Ordners')} last>
        {server && (
          <RadioGroup value={server.openOnFolder ?? 'last'} onChange={(_, d) => saveServer({ openOnFolder: d.value })}>
            <Radio value="last" label={t('Die zuletzt gewählte Mail anzeigen')} />
            <Radio value="unread" label={t('Die neueste ungelesene Mail anzeigen')} />
            <Radio value="first" label={t('Die neueste Mail anzeigen')} />
            <Radio value="none" label={t('Nichts auswählen')} />
          </RadioGroup>
        )}
      </Section>
      </SectionTabs>
    </>
  )
}

export function FolderPaneSettings() {
  const { prefs, update } = usePrefs()
  return (
    <>
      <Section title={t('Layout')}>
        <Checkbox label={t('Favoriten anzeigen')} checked={prefs.showFavorites ?? true} onChange={(_, d) => update({ showFavorites: !!d.checked })} />
        <Checkbox label={t('Favoriten oben anheften')} checked={prefs.pinFavorites ?? true} onChange={(_, d) => update({ pinFavorites: !!d.checked })} />
        <Hint>{t('Favoriten hinzufügen: Rechtsklick auf einen Ordner → „Zu Favoriten hinzufügen“.')}</Hint>
      </Section>
      <Section title={t('Ordner sortieren')} last>
        <RadioGroup value={prefs.sortFolders ?? 'custom'} onChange={(_, d) => update({ sortFolders: d.value as any })}>
          <Radio value="alpha" label={t('Ordner von A bis Z sortieren')} />
          <Radio value="custom" label={t('Benutzerdefinierte Reihenfolge verwenden')} />
        </RadioGroup>
        <Hint>{t('Reihenfolge ändern: Rechtsklick auf einen Ordner → „In Liste nach oben/unten verschieben“. Posteingang, Entwürfe, Gesendet usw. bleiben vorne.')}</Hint>
      </Section>
    </>
  )
}

const fonts = ['Aptos', 'Calibri', 'Segoe UI', 'Arial', 'Verdana', 'Tahoma', 'Georgia', 'Times New Roman', 'Consolas']
const sizes = ['9pt', '10pt', '11pt', '12pt', '14pt', '16pt']

export function ComposeSettings() {
  const { prefs, update } = usePrefs()
  const font = (prefs.composeFont ?? 'Aptos').split(',')[0].replace(/"/g, '')
  return (
    <>
      <Section title={t('Senden')} hint={t('Gesendete Mails warten so lange im Postausgang. Bis dahin lassen sie sich zurückrufen.')}>
        <Field label={t('Zurückrufen möglich für')} style={{ maxWidth: 240 }}>
          <Dropdown value={(prefs.sendDelay ?? 10) === 0 ? t('Aus – sofort senden') : t('{n} Sekunden', { n: prefs.sendDelay ?? 10 })}
            selectedOptions={[String(prefs.sendDelay ?? 10)]} onOptionSelect={(_, d) => update({ sendDelay: Number(d.optionValue) })}>
            {[0, 5, 10, 20, 30].map(n => <Option key={n} value={String(n)} text={n === 0 ? t('Aus – sofort senden') : t('{n} Sekunden', { n })}>
              {n === 0 ? t('Aus – sofort senden') : t('{n} Sekunden', { n })}</Option>)}
          </Dropdown>
        </Field>
      </Section>
      <Section title={t('Nachrichtenformat')} hint={t('Standardschrift für neue Mails, Antworten und Weiterleitungen.')}>
        <div style={{ display: 'flex', gap: 12 }}>
          <Field label={t('Schriftart')} style={{ minWidth: 220 }}>
            <Dropdown value={font} selectedOptions={[font]} onOptionSelect={(_, d) => update({ composeFont: `${d.optionValue}, Calibri, Arial, sans-serif` })}>
              {fonts.map(f => <Option key={f} value={f} style={{ fontFamily: f }}>{f}</Option>)}
            </Dropdown>
          </Field>
          <Field label={t('Größe')} style={{ minWidth: 100 }}>
            <Dropdown value={prefs.composeSize ?? '11pt'} selectedOptions={[prefs.composeSize ?? '11pt']} onOptionSelect={(_, d) => update({ composeSize: d.optionValue })}>
              {sizes.map(z => <Option key={z} value={z}>{z}</Option>)}
            </Dropdown>
          </Field>
        </div>
        <div style={{ fontFamily: prefs.composeFont ?? 'Aptos, Calibri, sans-serif', fontSize: prefs.composeSize ?? '11pt', padding: 12, borderRadius: 6, background: tokens.colorNeutralBackground3 }}>
          {t('So sieht Ihr Text aus.')}
        </div>
      </Section>
      <Section title={t('Antworten oder Allen antworten')} hint={t('Welche Schaltfläche im Lesebereich vorne steht.')} last>
        <RadioGroup value={prefs.replyAll ? 'all' : 'one'} onChange={(_, d) => update({ replyAll: d.value === 'all' })}>
          <Radio value="one" label={t('Antworten')} /><Radio value="all" label={t('Allen antworten')} />
        </RadioGroup>
        <Switch label={t('Signaturen pflegen: E-Mail → E-Mail-Konten → Konto bearbeiten')} disabled checked={false} style={{ display: 'none' }} />
        <Hint>{t('Signaturen stehen je Konto unter E-Mail → E-Mail-Konten.')}</Hint>
      </Section>
    </>
  )
}

/** E-Mail → Anhänge (Kennung attsave): wohin der Hauptknopf am Anhang speichert. */
export function AttachmentSettings() {
  const { settings, save } = useAttachSettings()
  const { fail } = useApp()
  const [picking, setPicking] = useState(false)
  const set = (p: Parameters<typeof save>[0]) => save(p).catch(fail)
  const intern = settings.attachSaveTarget === 'ablage'
  return (
    <>
      <Section title={t('Beim Speichern von Anhängen')} hint={t('Gilt für den Hauptknopf an jedem Anhang, in Web und App.')}>
        <RadioGroup value={settings.attachSaveTarget} onChange={(_, d) => set({ attachSaveTarget: d.value as 'ablage' | 'browser' })}>
          <Radio value="ablage" label={t('Intern: in meine Ablage speichern')} />
          <Radio value="browser" label={t('Extern: Download des Browsers')} />
        </RadioGroup>
        {intern ? <>
          <Field label={t('Standardordner')} hint={t('Leer: Beim ersten Speichern wird nach dem Ordner gefragt.')}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
              <Text>{settings.attachSaveFolder || t('Noch keiner gewählt')}</Text>
              <Button size="small" onClick={() => setPicking(true)}>{t('Ändern …')}</Button>
              {settings.attachSaveFolder && <Button size="small" appearance="subtle" onClick={() => set({ attachSaveFolder: null })}>{t('Zurücksetzen')}</Button>}
            </div>
          </Field>
          <Switch label={t('Vor dem Speichern nach dem Ort fragen')} checked={settings.attachSaveAsk} onChange={(_, d) => set({ attachSaveAsk: d.checked })} />
        </> : <Hint>{t('Ort und Rückfrage bestimmt der Browser (seine Einstellung „Vor dem Download nach dem Speicherort fragen“).')}</Hint>}
      </Section>
      {picking && <FolderPicker title={t('Standardordner für Anhänge')} exclude={[]} start={settings.attachSaveFolder ?? ''}
        onPick={dir => { setPicking(false); set({ attachSaveFolder: dir || null }) }} onClose={() => setPicking(false)} />}
    </>
  )
}
