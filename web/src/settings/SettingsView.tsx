import { SearchBox, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import {
  Person20Regular, Settings20Regular, Mail20Regular, CalendarLtr20Regular, People20Regular, Note20Regular,
} from '@fluentui/react-icons'
import { useState } from 'react'
import { PageTitle } from './parts'
import { t } from '../i18n'
import { AccountsSettings } from './AccountsSettings'
import { RulesSettings, SpamSettings } from './RulesSettings'
import { DevicesSettings, NotesSettings } from './DevicesSettings'
import { StatsSettings } from './StatsSettings'
import { SecuritySettings } from './SecuritySettings'
import { AppearanceSettings, InstallSettings, LanguageSettings, NotificationSettings, SearchSettings } from './GeneralPages'
import { ComposeSettings, FolderPaneSettings, MessageListSettings, PreviewPaneSettings } from './MailPages'
import { CategorySettings } from '../mail/Categories'
import { CalendarViewSettings, EventSettings, PeopleSettings } from './CalendarPages'

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', minWidth: 0 },
  cats: { width: '210px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '16px 8px', display: 'flex', flexDirection: 'column', gap: '2px' },
  title: { fontSize: '20px', fontWeight: 600, padding: '0 8px 12px' },
  pages: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '16px 8px', display: 'flex', flexDirection: 'column', gap: '2px', overflowY: 'auto' },
  entry: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    position: 'relative', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  on: {
    fontWeight: 600, backgroundColor: tokens.colorNeutralBackground1Selected,
    '::before': { content: '""', position: 'absolute', left: 0, top: '8px', bottom: '8px', width: '3px', borderRadius: '2px', backgroundColor: tokens.colorBrandBackground },
  },
  sub: { fontSize: '11px', color: tokens.colorNeutralForeground3 },
  body: { flex: 1, overflowY: 'auto', padding: '24px 32px 48px', display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
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
      { key: 'accounts', label: t('E-Mail-Konten'), words: kw('konto imap smtp server passwort signatur sync zeitraum'), render: () => <AccountsSettings /> },
      { key: 'security', label: t('Sicherheit'), words: kw('passwort ändern zwei-faktor 2fa authenticator wiederherstellungscodes'), render: () => <SecuritySettings /> },
      { key: 'devices', label: t('Geräte & Sync'), words: kw('app-passwort caldav carddav webdav handy davx iphone thunderbird notizi'), render: () => <DevicesSettings /> },
    ],
  },
  {
    key: 'general', label: t('Allgemein'), icon: <Settings20Regular />, pages: [
      { key: 'language', label: t('Sprache und Zeit'), words: kw('sprache datumsformat zeitformat zeitzone uhrzeit'), render: () => <LanguageSettings /> },
      { key: 'appearance', label: t('Darstellung'), words: kw('hell dunkel modus design farbe akzent theme'), render: () => <AppearanceSettings /> },
      { key: 'notifications', label: t('Benachrichtigungen'), words: kw('hinweis neue mail erinnerung ton desktop'), render: () => <NotificationSettings /> },
      { key: 'install', label: t('Apps installieren'), words: kw('app installieren pwa apk android download fenster symbol startmenü dock home-bildschirm desktop handy'), render: () => <InstallSettings /> },
      { key: 'search', label: t('Suchen'), words: kw('suchbereich ordner gelöschte relevanz'), render: () => <SearchSettings /> },
      { key: 'stats', label: t('Speicher & Statistik'), words: kw('größe ordnergröße datenbank anzahl statistik'), render: () => <StatsSettings /> },
    ],
  },
  {
    key: 'mail', label: t('E-Mail'), icon: <Mail20Regular />, pages: [
      { key: 'list', label: t('Nachrichtenliste'), words: kw('textgröße abstand vorschau bild avatar liste'), render: () => <MessageListSettings /> },
      { key: 'pane', label: t('Vorschaufenster'), words: kw('lesebereich rechts unten layout gelesen markieren ordner öffnen'), render: () => <PreviewPaneSettings /> },
      { key: 'folders', label: t('Ordnerbereich'), words: kw('favoriten ordner sortieren reihenfolge'), render: () => <FolderPaneSettings /> },
      { key: 'compose', label: t('Verfassen und Antworten'), words: kw('schrift schriftart antworten allen signatur'), render: () => <ComposeSettings /> },
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
    key: 'people', label: t('Personen'), icon: <People20Regular />, pages: [
      { key: 'people', label: t('Kontakte'), words: kw('kontakte adressbuch import export vcf nextcloud'), render: () => <PeopleSettings /> },
    ],
  },
  {
    key: 'notes', label: t('Notizen'), icon: <Note20Regular />, pages: [
      { key: 'notes', label: 'notizi', words: kw('notizen notizi e2ee abgleich'), render: () => <NotesSettings /> },
    ],
  },
]

// Fruehere Adressen (#/settings/general) weiter gueltig
const aliases: Record<string, string> = { general: 'language' }

const allPages = categories.flatMap(c => c.pages.map(p => ({ ...p, category: c })))

export default function SettingsView() {
  const s = useStyles()
  const fromHash = location.hash.split('/')[2]
  const initial = allPages.find(p => p.key === (aliases[fromHash] ?? fromHash)) ?? allPages.find(p => p.key === 'language')!
  const [page, setPage] = useState(initial.key)
  const [category, setCategory] = useState(initial.category.key)
  const [search, setSearch] = useState('')

  const go = (key: string) => {
    const p = allPages.find(x => x.key === key)!
    setPage(key)
    setCategory(p.category.key)
    history.replaceState(null, '', '#/settings/' + key)
  }

  const term = search.trim().toLowerCase()
  const shown = term
    ? allPages.filter(p => (p.label + ' ' + p.words + ' ' + p.category.label).toLowerCase().includes(term))
    : categories.find(c => c.key === category)!.pages.map(p => ({ ...p, category: categories.find(c => c.key === category)! }))
  const current = allPages.find(p => p.key === page)!

  return (
    <div className={s.root}>
      <nav className={s.cats}>
        <Text className={s.title}>{t('Einstellungen')}</Text>
        <SearchBox placeholder={t('Einstellungen suchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ marginBottom: 10 }} />
        {categories.map(c => (
          <div key={c.key} className={mergeClasses(s.entry, !term && category === c.key && s.on)}
            onClick={() => { setSearch(''); setCategory(c.key); go(c.pages[0].key) }}>
            {c.icon}{c.label}
          </div>
        ))}
      </nav>
      <nav className={s.pages}>
        {shown.length === 0 && <Text className={s.sub} style={{ padding: 10 }}>{t('Nichts gefunden.')}</Text>}
        {shown.map(p => (
          <div key={p.key} className={mergeClasses(s.entry, page === p.key && s.on)} onClick={() => go(p.key)}>
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
