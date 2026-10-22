import {
  Button, Checkbox, Dropdown, Field, MessageBar, MessageBarBody, Option, Radio, RadioGroup, Switch, Text, Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { ArrowDownload20Regular } from '@fluentui/react-icons'
import { api } from '../api'
import { useApp } from '../App'
import { UiPrefs, usePrefs } from '../prefs'
import { lang, locale, t } from '../i18n'
import { accents, SIGNATURE, storedTheme, storeTheme, ThemeChoice } from '../theme'
import { fmtDay, fmtTime } from '../shell/hooks'
import { desktopPossible } from '../shell/Notifier'
import { install, isIos, pwaPossible, usePwa } from '../pwa'
import { Hint, PageTitle, Section } from './parts'
import { TextIndexSection } from './TextIndexSection'

const useStyles = makeStyles({
  field: { maxWidth: '400px' },
  modes: { display: 'flex', gap: '16px', flexWrap: 'wrap' },
  mode: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px', cursor: 'pointer', backgroundColor: 'transparent', border: 'none', paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0, color: 'inherit', fontFamily: 'inherit', fontSize: '14px' },
  preview: { width: '120px', height: '80px', borderRadius: '8px', overflow: 'hidden', border: `2px solid ${tokens.colorNeutralStroke1}`, display: 'flex' },
  picked: { border: `2px solid ${tokens.colorBrandStroke1}`, boxShadow: `0 0 0 2px ${tokens.colorBrandStroke1}` },
  colors: { display: 'flex', gap: '10px', flexWrap: 'wrap' },
  color: { width: '48px', height: '48px', borderRadius: '6px', border: '2px solid transparent', cursor: 'pointer', padding: 0 },
  colorOn: { outline: `2px solid ${tokens.colorNeutralForeground1}`, outlineOffset: '2px' },
})

const zones = ['Europe/Berlin', 'Europe/Vienna', 'Europe/Zurich', 'Europe/London', 'Europe/Paris', 'Europe/Madrid', 'Europe/Rome', 'Europe/Amsterdam',
  'Europe/Warsaw', 'Europe/Istanbul', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Asia/Tokyo', 'UTC']

const zoneLabel = (z: string) => {
  try {
    const off = new Intl.DateTimeFormat(locale, { timeZone: z, timeZoneName: 'shortOffset' }).formatToParts(new Date()).find(p => p.type === 'timeZoneName')?.value
    return `(${off?.replace('GMT', 'UTC') || 'UTC'}) ${z.replace('_', ' ')}`
  } catch { return z }
}

/** Server-Einstellungen (Zeitzone, Hell/Dunkel) lesen und speichern. */
function useServerSettings() {
  const { fail } = useApp()
  const [settings, setSettings] = useState<any>(null)
  useEffect(() => { api.get('/api/settings/').then(setSettings).catch(fail) }, [fail])
  const save = async (p: any) => {
    setSettings((x: any) => ({ ...x, ...p }))
    try { await api.put('/api/settings/general', p) } catch (e) { fail(e) }
  }
  return { settings, save }
}

export function LanguageSettings() {
  const s = useStyles()
  const { prefs, update } = usePrefs()
  const { settings, save } = useServerSettings()
  const sample = new Date(2027, 8, 1, 13, 5)
  const dateOptions: { v: UiPrefs['dateFormat']; l: string }[] = [
    { v: 'dd.MM.yyyy', l: '01.09.2027' }, { v: 'd.M.yyyy', l: '1.9.2027' }, { v: 'dd.MM.yy', l: '01.09.27' }, { v: 'yyyy-MM-dd', l: '2027-09-01' },
  ]
  return (
    <>
      <PageTitle>{t('Sprache und Zeit')}</PageTitle>
      <Section title={t('Sprache und Formate')}>
        <Field label={t('Sprache der Oberfläche')} hint={t('Gilt für dein Konto auf allen Geräten. Die Seite lädt danach neu.')} className={s.field}>
          <Dropdown value={(prefs.language ?? lang) === 'en' ? 'English' : 'Deutsch'} selectedOptions={[prefs.language ?? lang]}
            onOptionSelect={(_, d) => {
              const v = d.optionValue as 'de' | 'en'
              if (v === lang) return
              // Direkt speichern (nicht gesammelt), dann neu laden
              api.put('/api/settings/ui', { ...prefs, language: v }).finally(() => { update({ language: v }); location.reload() })
            }}>
            <Option value="de">Deutsch</Option>
            <Option value="en">English</Option>
          </Dropdown>
        </Field>
        <Field label={t('Datumsformat (Beispiel: 1. September 2027 wird so angezeigt: {sample})', { sample: fmtDay(sample) })} className={s.field}>
          <Dropdown value={dateOptions.find(o => o.v === (prefs.dateFormat ?? 'dd.MM.yyyy'))!.l} selectedOptions={[prefs.dateFormat ?? 'dd.MM.yyyy']}
            onOptionSelect={(_, d) => update({ dateFormat: d.optionValue as any })}>
            {dateOptions.map(o => <Option key={o.v} value={o.v!}>{o.l}</Option>)}
          </Dropdown>
        </Field>
        <Field label={t('Zeitformat (Beispiel: {sample})', { sample: fmtTime(sample) })} className={s.field}>
          <Dropdown value={prefs.timeFormat === '12' ? '1:01 AM – 11:59 PM' : '01:01 – 23:59'} selectedOptions={[prefs.timeFormat ?? '24']}
            onOptionSelect={(_, d) => update({ timeFormat: d.optionValue as any })}>
            <Option value="24">01:01 – 23:59</Option>
            <Option value="12">1:01 AM – 11:59 PM</Option>
          </Dropdown>
        </Field>
      </Section>
      <Section title={t('Zeitzone')} hint={t('Gilt für neue und geänderte Termine. Programme am Handy rechnen in ihre eigene Zeitzone um.')} last>
        {settings && (
          <Field className={s.field}>
            <Dropdown value={zoneLabel(settings.timeZone)} selectedOptions={[settings.timeZone]} onOptionSelect={(_, d) => save({ timeZone: d.optionValue })}>
              {(zones.includes(settings.timeZone) ? zones : [settings.timeZone, ...zones]).map(z => <Option key={z} value={z} text={zoneLabel(z)}>{zoneLabel(z)}</Option>)}
            </Dropdown>
          </Field>
        )}
      </Section>
    </>
  )
}

/** Kleine Vorschau eines Fensters in hell oder dunkel, wie in Outlook. */
function ModePreview({ mode }: { mode: 'light' | 'dark' | 'system' }) {
  const half = (dark: boolean) => (
    <div style={{ flex: 1, background: dark ? '#1f1f1f' : '#f5f5f5', padding: 6, display: 'flex', flexDirection: 'column', gap: 4 }}>
      <div style={{ height: 6, borderRadius: 2, background: dark ? '#3a3a3a' : '#dcdcdc' }} />
      <div style={{ flex: 1, display: 'flex', gap: 4 }}>
        <div style={{ width: '30%', borderRadius: 2, background: dark ? '#2c2c2c' : '#ffffff' }} />
        <div style={{ flex: 1, borderRadius: 2, background: dark ? '#333' : '#ffffff' }} />
      </div>
    </div>
  )
  if (mode === 'system') return <>{half(false)}{half(true)}</>
  return half(mode === 'dark')
}

export function AppearanceSettings() {
  const s = useStyles()
  const { prefs, update } = usePrefs()
  const { save } = useServerSettings()
  const [mode, setMode] = useState<ThemeChoice>(storedTheme())
  const pick = (m: ThemeChoice) => { setMode(m); storeTheme(m); save({ theme: m }) }
  const accent = (prefs.accent ?? SIGNATURE).toUpperCase()
  return (
    <>
      <PageTitle>{t('Darstellung')}</PageTitle>
      <Section title={t('Modus auswählen')}>
        <div className={s.modes}>
          {([['light', t('Hell')], ['dark', t('Dunkel')], ['system', t('Systemeinstellungen verwenden')]] as const).map(([m, l]) => (
            <button key={m} className={s.mode} onClick={() => pick(m)}>
              <div className={mergeClasses(s.preview, mode === m && s.picked)}><ModePreview mode={m} /></div>
              <span style={{ maxWidth: 130, textAlign: 'center' }}>{l}</span>
            </button>
          ))}
        </div>
      </Section>
      <Section title={t('Designs')} hint={t('Die Farbe für Kopfleiste, Schaltflächen und Markierungen.')} last>
        <Text weight="semibold">{t('Farbe')}</Text>
        <div className={s.colors}>
          {accents.map(a => (
            <Tooltip key={a.color} content={t(a.name)} relationship="label">
              <button className={mergeClasses(s.color, accent === a.color.toUpperCase() && s.colorOn)} style={{ background: a.color }}
                onClick={() => update({ accent: a.color })} aria-label={t(a.name)} />
            </Tooltip>
          ))}
        </div>
      </Section>
    </>
  )
}

export function NotificationSettings() {
  const { prefs, update } = usePrefs()
  const { notify } = useApp()
  const [permission, setPermission] = useState(typeof Notification !== 'undefined' ? Notification.permission : 'denied')
  const ask = async () => {
    const r = await Notification.requestPermission()
    setPermission(r)
    if (r === 'granted') new Notification('dmOffice', { body: t('Desktop-Benachrichtigungen sind eingeschaltet.'), icon: '/icon-128.png' })
  }
  return (
    <>
      <PageTitle>{t('Benachrichtigungen')}</PageTitle>
      <Section title={t('Mich benachrichtigen über')}>
        <Switch label={t('E-Mail – neue Nachrichten im Posteingang')} checked={prefs.notifyMail ?? true} onChange={(_, d) => update({ notifyMail: d.checked })} />
        <Switch label={t('Kalender – Erinnerungen an Termine')} checked={prefs.notifyCalendar ?? true} onChange={(_, d) => update({ notifyCalendar: d.checked })} />
        <Switch label={t('Ton abspielen')} checked={prefs.notifySound ?? true} onChange={(_, d) => update({ notifySound: d.checked })} />
        <div><Button size="small" onClick={() => notify(t('So sieht ein Hinweis aus'), t('Max Müller · Angebot 2026-117'), 'info')}>{t('Probe')}</Button></div>
      </Section>
      <Section title={t('Desktop-Benachrichtigungen')} hint={t('Erscheinen auch, wenn dmOffice im Hintergrund liegt – solange ein Tab offen ist.')} last>
        {!desktopPossible() ? (
          <MessageBar intent="warning">
            <MessageBarBody>
              {t('Der Browser erlaubt Desktop-Benachrichtigungen nur über HTTPS. dmOffice läuft hier über http://{host} – dafür einen Eintrag im Reverse-Proxy (Nginx Proxy Manager) mit Zertifikat anlegen. Bis dahin erscheinen Hinweise nur innerhalb von dmOffice.', { host: location.host })}
            </MessageBarBody>
          </MessageBar>
        ) : (
          <>
            <Switch label={t('Desktop-Benachrichtigungen')} checked={prefs.notifyDesktop ?? true} onChange={(_, d) => update({ notifyDesktop: d.checked })} />
            {permission !== 'granted' && (
              <div>
                <Button onClick={ask} disabled={permission === 'denied'}>{t('Im Browser erlauben')}</Button>
                {permission === 'denied' && <Hint> {t('Im Browser gesperrt – über das Schloss neben der Adresse wieder erlauben.')}</Hint>}
              </div>
            )}
          </>
        )}
      </Section>
    </>
  )
}

export function SearchSettings() {
  const { prefs, update } = usePrefs()
  return (
    <>
      <PageTitle>{t('Suchen')}</PageTitle>
      <Section title={t('Suchbereich')}>
        <RadioGroup value={prefs.searchScope ?? 'inboxAll'} onChange={(_, d) => update({ searchScope: d.value as any })}>
          <Radio value="inboxAll" label={t('Alle Ordner beim Durchsuchen aus dem Posteingang')} />
          <Radio value="all" label={t('Alle Ordner')} />
          <Radio value="current" label={t('Aktueller Ordner')} />
        </RadioGroup>
        <Hint>{t('Über „Alle Konten“ in der Suchleiste lässt sich jederzeit über alle Konten suchen.')}</Hint>
      </Section>
      <Section title={t('Suchergebnisse')}>
        <Checkbox label={t('Gelöschte Elemente und Junk-E-Mail einschließen')} checked={prefs.searchDeleted ?? true} onChange={(_, d) => update({ searchDeleted: !!d.checked })} />
        <Checkbox label={t('Relevanteste Suchergebnisse oben anzeigen')} checked={(prefs.searchSort ?? (prefs.searchRelevance ? 'relevance' : 'newest')) === 'relevance'} onChange={(_, d) => update({ searchRelevance: !!d.checked, searchSort: d.checked ? 'relevance' : 'newest' })} />
        <Hint>{t('Gesucht wird in Betreff, Absender, Empfängern und Text – mit „von:“, „an:“ und „betreff:“ gezielt in einem Feld.')}</Hint>
      </Section>
      <TextIndexSection />
    </>
  )
}

// Signierte APK aus dem neuesten GitHub-Release (android.yml haengt sie zusaetzlich unter festem Namen an)
const APK_URL = 'https://github.com/Taloan/dmOffice/releases/latest/download/dmOffice.apk'

/** dmOffice als eigene App mit Fenster und Symbol (PWA) - Windows, macOS, Linux, Android, iOS - und als Android-App (APK). */
export function InstallSettings() {
  const { canInstall, installed } = usePwa()
  const { notify } = useApp()
  const [apkQr, setApkQr] = useState('')
  useEffect(() => { QRCode.toDataURL(APK_URL, { width: 200, margin: 1 }).then(setApkQr, () => {}) }, [])
  const go = async () => { if (await install()) notify(t('dmOffice wird installiert'), t('Die App findest du gleich im Startmenü bzw. auf dem Home-Bildschirm.')) }
  return (
    <>
      <PageTitle>{t('Apps installieren')}</PageTitle>
      <Section title={t('Als PWA installieren')} hint={t('dmOffice läuft dann in einem eigenen Fenster mit eigenem Symbol – ohne Adressleiste, wie ein installiertes Programm. Daten bleiben auf dem Server, Updates kommen von selbst.')}>
        {installed ? (
          <>
            <MessageBar intent="success"><MessageBarBody>{t('dmOffice läuft bereits als installierte App.')}</MessageBarBody></MessageBar>
            <Hint>{t('Chrome und Edge am Computer: Die Fensterknöpfe sitzen in der blauen Kopfleiste. Über den kleinen Pfeil daneben lässt sich die gewohnte Titelleiste wieder einblenden.')}</Hint>
          </>
        ) : !pwaPossible() ? (
          <MessageBar intent="warning">
            <MessageBarBody>{t('Installieren erlaubt der Browser nur über HTTPS. dmOffice läuft hier über http://{host} – dafür einen Eintrag im Reverse-Proxy mit Zertifikat anlegen.', { host: location.host })}</MessageBarBody>
          </MessageBar>
        ) : canInstall ? (
          <div><Button appearance="primary" onClick={go}>{t('Installieren')}</Button></div>
        ) : isIos() ? (
          <Text>{t('In Safari unten auf „Teilen“ tippen und dann „Zum Home-Bildschirm“ wählen.')}</Text>
        ) : (
          <>
            <Text>{t('Der Browser bietet die Installation gerade nicht direkt an. So geht es von Hand:')}</Text>
            <ul style={{ margin: 0, paddingLeft: 20, lineHeight: 1.6 }}>
              <li>{t('Chrome und Edge: Symbol „App installieren“ rechts in der Adressleiste oder Menü ⋮ → „Streamen, speichern und teilen“ → „Seite als App installieren“.')}</li>
              <li>{t('Safari auf dem Mac: Ablage → „Zum Dock hinzufügen“.')}</li>
              <li>{t('iPhone und iPad: in Safari „Teilen“ → „Zum Home-Bildschirm“.')}</li>
              <li>{t('Android: Menü ⋮ → „App installieren“ oder „Zum Startbildschirm hinzufügen“.')}</li>
              <li>{t('Firefox kann Web-Apps auf dem Desktop nicht installieren – dafür Chrome, Edge oder Safari nehmen.')}</li>
            </ul>
          </>
        )}
      </Section>
      <Section title={t('Android-App (APK)')} hint={t('Die eigene dmOffice-App für Android 8 und neuer: Kalender und Aufgaben mit Widgets auf dem Startbildschirm. Sie liegt nicht im Play Store, sondern als APK-Datei auf GitHub.')} last>
        <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap', alignItems: 'flex-start' }}>
          {apkQr && <img src={apkQr} width={200} height={200} alt={t('QR-Code zum APK-Download')} style={{ background: '#fff', borderRadius: 8 }} />}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, flex: '1 1 280px' }}>
            <div><Button appearance="primary" icon={<ArrowDownload20Regular />} as="a" href={APK_URL} target="_blank" rel="noopener">{t('APK herunterladen')}</Button></div>
            <ol style={{ margin: 0, paddingLeft: 20, lineHeight: 1.6 }}>
              <li>{t('Auf dem Handy „APK herunterladen“ antippen – oder den QR-Code mit der Handy-Kamera scannen.')}</li>
              <li>{t('Die heruntergeladene Datei dmOffice.apk öffnen. Beim ersten Mal fragt Android, ob der Browser Apps installieren darf: „Einstellungen“ → „Aus dieser Quelle zulassen“ einschalten, zurück und „Installieren“ tippen.')}</li>
              <li>{t('Warnt Google Play Protect vor einer unbekannten App, „Weitere Details“ → „Trotzdem installieren“ wählen.')}</li>
              <li>{t('dmOffice öffnen und „QR-Code scannen“ tippen. Den Code dazu hier unter Konto → Geräte & Sync → „Handy-App koppeln“ erzeugen.')}</li>
            </ol>
            <Hint>{t('Updates: die neue APK genauso herunterladen und über die alte installieren – Kopplung und Einstellungen bleiben erhalten.')}</Hint>
          </div>
        </div>
      </Section>
    </>
  )
}
