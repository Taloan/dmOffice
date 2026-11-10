import { ChatSettingsPage } from '../chat/ChatSettings'
import { SearchBox, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import {
  Person20Regular, Settings20Regular, Mail20Regular, CalendarLtr20Regular, People20Regular, Note20Regular, Folder20Regular, Chat20Regular,
  TaskListSquareLtr20Regular, Image20Regular, MusicNote220Regular, DocumentText20Regular,
} from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { PageTitle } from './parts'
import { BackBar, useBackClose, useIsMobile } from '../shell/mobile'
import { pressable } from '../shell/nav'
import { t } from '../i18n'
import { demoMode, featureOn } from '../api'
import { AccountsSettings } from './AccountsSettings'
import { RulesSettings, SpamSettings } from './RulesSettings'
import { DevicesSettings, NotesSettings } from './DevicesSettings'
import { RecipeScanSettings } from '../notes/RecipeScanDialog'
import { StatsSettings } from './StatsSettings'
import { ActivitySettings } from './ActivitySettings'
import { FilesLinkSettings, FilesStorageSettings, FilesTrashSettings, FilesVersionSettings } from './FilesSettings'
import { SecuritySettings } from './SecuritySettings'
import { TresorSettings } from './TresorSettings'
import { ExternalTargetsSettings } from './ExternalTargetsSettings'
import { GallerySettings } from './GallerySettings'
import { PlainBackupSettings } from './PlainBackupSettings'
import { AppearanceSettings, InstallSettings, LanguageSettings, NotificationSettings, SearchSettings } from './GeneralPages'
import { ComposeSettings, FolderPaneSettings, MessageListSettings, PreviewPaneSettings } from './MailPages'
import { CategorySettings } from '../mail/Categories'
import { SmartInboxSettings } from './SmartInboxSettings'
import { CalendarViewSettings, EventSettings, PeopleSettings } from './CalendarPages'
import { DmsAssignSettings, DmsProfilesSettings, DmsSourcesSettings } from '../dms/DmsSettings'
import { DmsPaperlessSettings } from '../dms/DmsPaperless'

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', minWidth: 0 },
  // Handy: immer nur eine Ebene in voller Breite
  rootMobile: { flexDirection: 'column', minHeight: 0 },
  navMobile: { width: 'auto', flex: 1, borderRight: 'none', overflowY: 'auto', padding: '12px 8px' },
  cats: { width: '210px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '16px 8px', display: 'flex', flexDirection: 'column', gap: '2px' },
  title: { fontSize: '20px', fontWeight: 600, padding: '0 8px 12px' },
  pages: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '16px 8px', display: 'flex', flexDirection: 'column', gap: '2px', overflowY: 'auto' },
  entry: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    position: 'relative', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '-2px' },
  },
  on: {
    fontWeight: 600, backgroundColor: tokens.colorNeutralBackground1Selected,
    '::before': { content: '""', position: 'absolute', left: 0, top: '8px', bottom: '8px', width: '3px', borderRadius: '2px', backgroundColor: tokens.colorBrandBackground },
  },
  sub: { fontSize: '11px', color: tokens.colorNeutralForeground3 },
  body: { flex: 1, overflowY: 'auto', padding: '24px 32px 48px', display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0, '@media (max-width: 767px)': { padding: '12px 14px 32px' } },
  inner: { maxWidth: '980px', display: 'flex', flexDirection: 'column', gap: '16px' },
})

interface Page { key: string; label: string; words: string; render: () => JSX.Element }

// Suchwoerter: deutsche immer, bei Englisch die englischen dazu - die Suche klappt so in beiden Sprachen
const kw = (de: string) => { const en = t(de); return en === de ? de : de + ' ' + en }
interface Category { key: string; label: string; icon: JSX.Element; pages: Page[] }

// Aufbau wie in Outlook: Bereich links, Seiten in der Mitte, Inhalt rechts
const categories: Category[] = [
  {
    key: 'account', label: t('Konto'), icon: <Person20Regular />, pages: [
      // Demo-Server: Sicherheit (Passwort, Zwei-Faktor) und Datensicherung gibt es dort nicht
      ...(demoMode.on ? [] : [{ key: 'security', label: t('Sicherheit'), words: kw('passwort ändern zwei-faktor 2fa authenticator wiederherstellungscodes'), render: () => <SecuritySettings /> }]),
      ...(demoMode.on ? [] : [{ key: 'plainbackup', label: t('Datenexport'), words: kw('datenexport export sicherung backup klartext lesbar webdav pcloud kopie notfall trash zip'), render: () => <PlainBackupSettings /> }]),
      { key: 'devices', label: t('Geräte & Sync'), words: kw('app-passwort caldav carddav webdav handy davx iphone thunderbird notizi'), render: () => <DevicesSettings /> },
    ],
  },
  {
    key: 'general', label: t('Allgemein'), icon: <Settings20Regular />, pages: [
      { key: 'language', label: t('Sprache und Zeit'), words: kw('sprache datumsformat zeitformat zeitzone uhrzeit'), render: () => <LanguageSettings /> },
      { key: 'appearance', label: t('Darstellung'), words: kw('hell dunkel modus design farbe akzent theme'), render: () => <AppearanceSettings /> },
      { key: 'notifications', label: t('Benachrichtigungen'), words: kw('hinweis neue mail erinnerung ton desktop'), render: () => <NotificationSettings /> },
      { key: 'install', label: t('Apps installieren'), words: kw('app installieren pwa apk android download fenster symbol startmenü dock home-bildschirm desktop handy'), render: () => <InstallSettings /> },
      { key: 'search', label: t('Suchen'), words: kw('suchbereich ordner gelöschte relevanz anhang inhalt volltext texterkennung ocr scan pdf'), render: () => <SearchSettings /> },
      { key: 'stats', label: t('Speicher & Statistik'), words: kw('größe ordnergröße datenbank anzahl statistik'), render: () => <StatsSettings /> },
      { key: 'activity', label: t('Aktivität'), words: kw('hintergrund aufgaben läuft fortschritt sync auslagern index ocr vorschaubilder aufräumen verdichten'), render: () => <ActivitySettings /> },
    ],
  },
  {
    key: 'mail', label: t('E-Mail'), icon: <Mail20Regular />, pages: [
      ...(demoMode.on ? [] : [{ key: 'accounts', label: t('E-Mail-Konten'), words: kw('konto imap smtp server passwort signatur sync zeitraum'), render: () => <AccountsSettings /> }]),
      { key: 'list', label: t('Nachrichtenliste'), words: kw('textgröße abstand vorschau bild avatar liste'), render: () => <MessageListSettings /> },
      { key: 'pane', label: t('Vorschaufenster'), words: kw('lesebereich rechts unten layout gelesen markieren ordner öffnen'), render: () => <PreviewPaneSettings /> },
      { key: 'folders', label: t('Ordnerbereich'), words: kw('favoriten ordner sortieren reihenfolge'), render: () => <FolderPaneSettings /> },
      { key: 'compose', label: t('Verfassen und Antworten'), words: kw('schrift schriftart antworten allen signatur'), render: () => <ComposeSettings /> },
      { key: 'smart', label: t('Smart Inbox'), words: kw('smart inbox intelligent relevant sonstige prio kategorien lernen pakete reisen newsletter ankündigungen termine'), render: () => <SmartInboxSettings /> },
      { key: 'categories', label: t('Kategorien'), words: kw('kategorie farbe schlagwort keyword label einordnen'), render: () => <><PageTitle>{t('Kategorien')}</PageTitle><CategorySettings /></> },
      { key: 'rules', label: t('Regeln'), words: kw('regel weiterleiten verschieben automatisch filter'), render: () => <RulesSettings /> },
      { key: 'junk', label: t('Junk-E-Mail'), words: kw('spam junk filter spambayes'), render: () => <SpamSettings /> },
    ],
  },
  {
    key: 'calendar', label: t('Kalender'), icon: <CalendarLtr20Regular />, pages: [
      { key: 'calview', label: t('Ansicht'), words: kw('wochentag arbeitszeit zeitraster wochennummern'), render: () => <CalendarViewSettings /> },
      { key: 'events', label: t('Ereignisse'), words: kw('erinnerung termin import export ics'), render: () => <EventSettings /> },
    ],
  },
  {
    key: 'chat', label: t('Chat'), icon: <Chat20Regular />, pages: [
      { key: 'chat', label: t('Profil'), words: kw('chat profilbild foto bild avatar'), render: () => <ChatSettingsPage part="profile" /> },
      { key: 'chat-share', label: t('Teilen'), words: kw('chat zeigen senden anhang weiterleiten weiterzeigen ablauf sichtbar speichern ordner'), render: () => <ChatSettingsPage part="share" /> },
      { key: 'chat-privacy', label: t('Privatsphäre'), words: kw('chat lesebestätigung gelesen ruhezeiten nacht stumm erwähnung standort'), render: () => <ChatSettingsPage part="privacy" /> },
      { key: 'chat-pukki', label: 'Pukki', words: kw('pukki assistent überblick briefing erinnerung mails figur lernen'), render: () => <ChatSettingsPage part="pukki" /> },
    ],
  },
  {
    key: 'people', label: t('Personen'), icon: <People20Regular />, pages: [
      { key: 'people', label: t('Kontakte'), words: kw('kontakte adressbuch import export vcf nextcloud'), render: () => <PeopleSettings /> },
    ],
  },
  // Module ohne eigene Einstellungen erscheinen nicht (Hauptschalter: Verwaltung → Module)
  { key: 'tasks', label: t('Aufgaben'), icon: <TaskListSquareLtr20Regular />, pages: [] },
  {
    key: 'notes', label: t('Notizen'), icon: <Note20Regular />, pages: [
      { key: 'notes', label: 'notizi', words: kw('notizen notizi e2ee abgleich'), render: () => <NotesSettings /> },
      { key: 'recipescan', label: t('Rezept-Scan'), words: kw('rezept foto kochbuch scan texterkennung ocr ki'), render: () => <RecipeScanSettings /> },
    ],
  },
  {
    key: 'files', label: t('Dateien'), icon: <Folder20Regular />, pages: [
      { key: 'files', label: t('Speicherplatz'), words: kw('dateien speicherplatz belegt leeren löschen'), render: () => <FilesStorageSettings /> },
      { key: 'file-versions', label: t('Frühere Versionen'), words: kw('fassungen versionen aufbewahrung ausdünnen'), render: () => <FilesVersionSettings /> },
      { key: 'file-trash', label: t('Papierkorb'), words: kw('papierkorb aufbewahrung leeren'), render: () => <FilesTrashSettings /> },
      { key: 'file-links', label: t('Freigabelinks'), words: kw('freigabe links teilen ablauf abrufe'), render: () => <FilesLinkSettings /> },
      { key: 'external-targets', label: t('Externe Ziele'), words: kw('extern ziel webdav onedrive pcloud nextcloud storage box nas einbinden'), render: () => <ExternalTargetsSettings /> },
      // Tresor bei den Dateien: dort ist er auch zu sehen
      { key: 'tresor', label: t('Tresor'), words: kw('tresor verschlüsselt tresor-passwort wiederherstellungscodes wiederherstellungsschlüssel automatisch schließen geräte sperre cryptomator nextcloud webdav'), render: () => <TresorSettings /> },
    ],
  },
  { key: 'gallery', label: t('Galerie'), icon: <Image20Regular />, pages: [
    { key: 'gallery-xmp', label: t('Angaben in den Fotos'), words: kw('xmp sidecar sterne bewertung titel beschreibung markierung farbe schreiben lightroom'), render: () => <GallerySettings /> },
  ] },
  { key: 'audio', label: t('Audiothek'), icon: <MusicNote220Regular />, pages: [] },
  {
    key: 'dms', label: 'DMS', icon: <DocumentText20Regular />, pages: [
      { key: 'dms-sources', label: t('Quellen'), words: kw('dms dokumente quellen scanner überwachter ordner import dateiordner mail mailordner anhänge regeln'),
        render: () => <DmsSourcesSettings /> },
      { key: 'dms-profiles', label: t('Ablageprofile'), words: kw('dms dokumente ablage ablageprofil muster ordner dateiname'), render: () => <DmsProfilesSettings /> },
      { key: 'dms-assign', label: t('Einordnung'), words: kw('dms dokumente ki einordnen texterkennung pdf/a'), render: () => <DmsAssignSettings /> },
      { key: 'dms-paperless', label: t('Übernahme aus paperless'), words: kw('dms paperless übernahme import export probelauf'), render: () => <DmsPaperlessSettings /> },
    ],
  },
]

// Fruehere Adressen (#/settings/general) weiter gueltig
const aliases: Record<string, string> = { general: 'language' }

/** Release-Liste: Seiten und Bereiche, die erst mit einer späteren Version freigeschaltet werden. */
const pageFeature: Record<string, string> = { tresor: 'vault' }
const released = (list: Category[]) => list
  .map(c => ({ ...c, pages: c.pages.filter(p => !pageFeature[p.key] || featureOn(pageFeature[p.key])) }))
  .filter(c => c.pages.length > 0)

/** Gruppen der Module (Hauptschalter der Verwaltung, Server: Features.Modules) - Gruppe und Modul heißen gleich. */
const categoryModule: Record<string, string> = { audio: 'audio', dms: 'dms' }

/** Module: ausgeschaltet (oder nicht freigeschaltet) fehlt die Gruppe; die Hauptschalter stehen in der Verwaltung. */
const withModules = (list: Category[]) => list.filter(c => !categoryModule[c.key] || featureOn(categoryModule[c.key]))

export default function SettingsView() {
  const s = useStyles()
  const cats = released(withModules(categories))
  const pages = cats.flatMap(c => c.pages.map(p => ({ ...p, category: c })))
  const fromHash = location.hash.split('/')[2]
  const initial = pages.find(p => p.key === (aliases[fromHash] ?? fromHash)) ?? pages.find(p => p.key === 'language') ?? pages[0]
  const [page, setPage] = useState(initial.key)
  const [category, setCategory] = useState(initial.category.key)
  const [search, setSearch] = useState('')
  // Handy: Bereiche -> Seiten -> Inhalt, je mit Zurueck; mit Seite in der Adresse gleich deren Inhalt
  const mobile = useIsMobile()
  const [step, setStep] = useState<'cats' | 'pages' | 'page'>(fromHash ? 'page' : 'cats')
  useBackClose(mobile && step !== 'cats', () => setStep('cats'))
  useBackClose(mobile && step === 'page', () => setStep('pages'))

  const go = (key: string) => {
    const p = pages.find(x => x.key === key)!
    setPage(key)
    setCategory(p.category.key)
    // Verlaufseintrag behalten (Zurueck-Taste auf dem Handy)
    // Ein Zusatz hinter der Seite (#/settings/devices/pair) bleibt fuer die Seite selbst stehen
    const sub = location.hash.startsWith('#/settings/' + key + '/') ? location.hash.slice(('#/settings/' + key).length) : ''
    history.replaceState(history.state, '', '#/settings/' + key + sub)
  }

  // Verweis aus einer anderen Seite (#/settings/activity), auch wenn die Einstellungen schon offen, aber ausgeblendet sind.
  // Nur einmal angemeldet: Die Leiste wechselt im selben Ereignis den Bereich, die Seite zeichnet sich neu - ein dabei ab- und
  // wieder angemeldeter Lauscher würde für dieses Ereignis nicht mehr aufgerufen (die alte Seite blieb stehen)
  const onHash = useRef<() => void>(() => {})
  onHash.current = () => {
    const key = location.hash.split('/')[2]
    if (location.hash.startsWith('#/settings') && pages.some(p => p.key === key)) { go(key); setStep('page') }
  }
  useEffect(() => {
    const handle = () => onHash.current()
    window.addEventListener('hashchange', handle)
    return () => window.removeEventListener('hashchange', handle)
  }, [])

  const term = search.trim().toLowerCase()
  const shown = term
    ? pages.filter(p => (p.label + ' ' + p.words + ' ' + p.category.label).toLowerCase().includes(term))
    : (cats.find(c => c.key === category) ?? cats[0]).pages.map(p => ({ ...p, category: cats.find(c => c.key === category) ?? cats[0] }))
  // Seite weggefallen (Modul gerade ausgeschaltet): erste Seite ihrer Gruppe
  const current = pages.find(p => p.key === page) ?? pages.find(p => p.category.key === category) ?? pages[0]

  if (mobile) {
    const cat = cats.find(c => c.key === category) ?? cats[0]
    const pageList = (
      <>
        {shown.length === 0 && <Text className={s.sub} style={{ padding: 10 }}>{t('Nichts gefunden.')}</Text>}
        {shown.map(p => (
          <div key={p.key} className={s.entry} onClick={() => { go(p.key); setStep('page') }} {...pressable(() => { go(p.key); setStep('page') })}>
            <div>
              <div>{p.label}</div>
              {term && <div className={s.sub}>{p.category.label}</div>}
            </div>
          </div>
        ))}
      </>
    )
    return (
      <div className={mergeClasses(s.root, s.rootMobile)}>
        {step === 'cats' && (
          <nav className={mergeClasses(s.cats, s.navMobile)}>
            <Text className={s.title}>{t('Einstellungen')}</Text>
            <SearchBox placeholder={t('Einstellungen suchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ marginBottom: 10 }} />
            {term ? pageList : cats.map(c => (
              <div key={c.key} className={s.entry} onClick={() => { setCategory(c.key); setStep('pages') }} {...pressable(() => { setCategory(c.key); setStep('pages') })}>
                {c.icon}{c.label}
              </div>
            ))}
          </nav>
        )}
        {step === 'pages' && <>
          <BackBar onBack={() => setStep('cats')} title={cat.label} />
          <nav className={mergeClasses(s.pages, s.navMobile)}>{pageList}</nav>
        </>}
        {step === 'page' && <>
          <BackBar onBack={() => setStep(term ? 'cats' : 'pages')} title={current.label} />
          <div className={s.body}>
            <div className={s.inner} key={current.key}>{current.render()}</div>
          </div>
        </>}
      </div>
    )
  }

  return (
    <div className={s.root}>
      <nav className={s.cats}>
        <Text className={s.title}>{t('Einstellungen')}</Text>
        <SearchBox placeholder={t('Einstellungen suchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ marginBottom: 10 }} />
        {cats.map(c => (
          <div key={c.key} className={mergeClasses(s.entry, !term && category === c.key && s.on)}
            aria-current={!term && category === c.key ? 'true' : undefined}
            onClick={() => { setSearch(''); setCategory(c.key); go(c.pages[0].key) }} {...pressable(() => { setSearch(''); setCategory(c.key); go(c.pages[0].key) })}>
            {c.icon}{c.label}
          </div>
        ))}
      </nav>
      <nav className={s.pages}>
        {shown.length === 0 && <Text className={s.sub} style={{ padding: 10 }}>{t('Nichts gefunden.')}</Text>}
        {shown.map(p => (
          <div key={p.key} className={mergeClasses(s.entry, page === p.key && s.on)} aria-current={page === p.key ? 'page' : undefined} onClick={() => go(p.key)} {...pressable(() => go(p.key))}>
            <div>
              <div>{p.label}</div>
              {term && <div className={s.sub}>{p.category.label}</div>}
            </div>
          </div>
        ))}
      </nav>
      <div className={s.body}>
        <div className={s.inner} key={current.key}>{current.render()}</div>
      </div>
    </div>
  )
}
