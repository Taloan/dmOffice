import {
  Avatar, Button, Menu, MenuDivider, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Spinner, Text, Tooltip,
  makeStyles, mergeClasses, tokens, CounterBadge,
} from '@fluentui/react-components'
import {
  bundleIcon, Mail24Filled, Mail24Regular, CalendarLtr24Filled, CalendarLtr24Regular, People24Filled, People24Regular,
  TaskListSquareLtr24Filled, TaskListSquareLtr24Regular, Note24Filled, Note24Regular, Folder24Filled, Folder24Regular,
  Settings24Regular, Settings24Filled, ShieldPersonRegular, ShieldPersonFilled, SignOut20Regular, WeatherMoon20Regular,
  ArrowSync20Regular,
} from '@fluentui/react-icons'
import { Component, lazy, Suspense, useEffect, useRef, useState } from 'react'
import { e2eForget } from '../e2e'
import { api } from '../api'
import { useApp } from '../App'
import { t, tError } from '../i18n'
import { storedTheme, storeTheme, ThemeChoice } from '../theme'
import { usePoll } from './hooks'
import { Notifier } from './Notifier'
import { Bookmarks } from './Bookmarks'

// Nach einem Update gibt es die Teilstuecke des alten Builds nicht mehr - dann einmal neu laden
function load<T>(f: () => Promise<T>): () => Promise<T> {
  return () => f().catch(err => {
    if (!sessionStorage.getItem('dmoffice.reloaded')) {
      sessionStorage.setItem('dmoffice.reloaded', '1')
      location.reload()
      return new Promise<T>(() => {})
    }
    throw err
  })
}
const MailView = lazy(load(() => import('../mail/MailView')))
const CalendarView = lazy(load(() => import('../calendar/CalendarView')))
const ContactsView = lazy(load(() => import('../contacts/ContactsView')))
const TasksView = lazy(load(() => import('../tasks/TasksView')))
const NotesView = lazy(load(() => import('../notes/NotesView')))
const FilesView = lazy(load(() => import('../files/FilesView')))
const SettingsView = lazy(load(() => import('../settings/SettingsView')))
const AdminView = lazy(load(() => import('../admin/AdminView')))

class Boundary extends Component<{ children: React.ReactNode; resetKey: string }, { error: Error | null }> {
  state = { error: null as Error | null }
  static getDerivedStateFromError(error: Error) { return { error } }
  componentDidUpdate(prev: { resetKey: string }) { if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null }) }
  render() {
    if (!this.state.error) return this.props.children
    return (
      <div style={{ margin: 'auto', textAlign: 'center', maxWidth: 480 }}>
        <Text size={500} block>{t('Hier ist etwas schiefgegangen.')}</Text>
        <Text block style={{ margin: '8px 0 16px', color: tokens.colorNeutralForeground3 }}>{tError(this.state.error.message)}</Text>
        <Button appearance="primary" onClick={() => location.reload()}>{t('Neu laden')}</Button>
      </div>
    )
  }
}

const MailIcon = bundleIcon(Mail24Filled, Mail24Regular)
const CalIcon = bundleIcon(CalendarLtr24Filled, CalendarLtr24Regular)
const PeopleIcon = bundleIcon(People24Filled, People24Regular)
const TaskIcon = bundleIcon(TaskListSquareLtr24Filled, TaskListSquareLtr24Regular)
const NoteIcon = bundleIcon(Note24Filled, Note24Regular)
const FilesIcon = bundleIcon(Folder24Filled, Folder24Regular)
const SettingsIcon = bundleIcon(Settings24Filled, Settings24Regular)
const AdminIcon = bundleIcon(ShieldPersonFilled, ShieldPersonRegular)

export type Module = 'mail' | 'calendar' | 'contacts' | 'tasks' | 'notes' | 'files' | 'settings' | 'admin'

const modules: { key: Module; label: string; icon: any }[] = [
  { key: 'mail', label: t('E-Mail'), icon: MailIcon },
  { key: 'calendar', label: t('Kalender'), icon: CalIcon },
  { key: 'contacts', label: t('Kontakte'), icon: PeopleIcon },
  { key: 'tasks', label: t('Aufgaben'), icon: TaskIcon },
  { key: 'notes', label: t('Notizen'), icon: NoteIcon },
  { key: 'files', label: t('Dateien'), icon: FilesIcon },
]

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', height: '100%', backgroundColor: tokens.colorNeutralBackground3 },
  // Kopfzeile neutral wie in Office (dunkel: Anthrazit) - die Signaturfarbe traegt nur das Logo
  top: {
    height: '48px', flexShrink: 0, display: 'flex', alignItems: 'center', gap: '8px', padding: '0 12px',
    backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground1,
  },
  brand: { display: 'flex', alignItems: 'center', gap: '8px', fontWeight: 600, fontSize: '16px', width: '220px' },
  spacer: { flex: 1 },
  topButton: { color: tokens.colorNeutralForeground2, minWidth: 'auto', ':hover': { color: tokens.colorNeutralForeground1, backgroundColor: tokens.colorNeutralBackground3Hover } },
  body: { flex: 1, display: 'flex', minHeight: 0 },
  rail: {
    width: '56px', flexShrink: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px', paddingTop: '8px',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  railButton: {
    width: '44px', height: '44px', borderRadius: tokens.borderRadiusLarge, position: 'relative', color: tokens.colorNeutralForeground2,
    ':hover': { backgroundColor: tokens.colorNeutralBackground3Hover },
  },
  // Alle Bereichssymbole in der eingestellten Akzentfarbe
  tinted: { color: tokens.colorBrandForeground1, ':hover': { color: tokens.colorBrandForeground1 } },
  railActive: {
    color: tokens.colorBrandForeground1, backgroundColor: tokens.colorNeutralBackground1,
    '::before': { content: '""', position: 'absolute', left: '-6px', top: '12px', bottom: '12px', width: '3px', borderRadius: '2px', backgroundColor: tokens.colorBrandBackground },
  },
  badge: { position: 'absolute', top: '2px', right: '0px', pointerEvents: 'none' },
  main: {
    flex: 1, minWidth: 0, display: 'flex', backgroundColor: tokens.colorNeutralBackground1, borderTopLeftRadius: tokens.borderRadiusXLarge,
    overflow: 'hidden', boxShadow: tokens.shadow4,
  },
  loading: { margin: 'auto' },
})

function currentModule(): Module {
  const h = location.hash.replace(/^#\/?/, '').split('/')[0] as Module
  return [...modules.map(m => m.key), 'settings', 'admin'].includes(h) ? h : 'mail'
}

export function Shell() {
  const s = useStyles()
  // Fensterrahmen der installierten App (Titelleiste, Fensterknoepfe) in der Farbe der Kopfleiste -
  // folgt Akzentfarbe und Hell/Dunkel, die sich auch in den Einstellungen aendern koennen
  const headerRef = useRef<HTMLElement>(null)
  useEffect(() => {
    const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')
    const sync = () => {
      const c = headerRef.current && getComputedStyle(headerRef.current).backgroundColor
      if (meta && c && meta.content !== c) meta.content = c
    }
    sync()
    const timer = setInterval(sync, 1500)
    return () => clearInterval(timer)
  }, [])
  const { me } = useApp()
  const [module, setModule] = useState<Module>(currentModule())
  const [theme, setTheme] = useState<ThemeChoice>(storedTheme())
  const status = usePoll(() => api.get('/api/mail/status'), 20000, 'dmoffice:mailstatus')

  useEffect(() => { sessionStorage.removeItem('dmoffice.reloaded') }, [])
  useEffect(() => {
    const onHash = () => setModule(currentModule())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const go = (m: Module) => { location.hash = '#/' + m }
  const unread = status ? Object.values<any>(status.accounts).reduce((n, a) => n + (a.unread || 0), 0) : 0
  const syncing = status ? Object.values<any>(status.accounts).some(a => a.running) : false

  useEffect(() => {
    document.title = unread > 0 ? `(${unread}) dmOffice` : 'dmOffice'
    // Als App installiert (Edge/Chrome): Zahl auf dem Symbol in der Taskleiste bzw. auf dem Startbildschirm
    const nav = navigator as Navigator & { setAppBadge?: (n?: number) => Promise<void>; clearAppBadge?: () => Promise<void> }
    if (unread > 0) nav.setAppBadge?.(unread).catch(() => {})
    else nav.clearAppBadge?.().catch(() => {})
  }, [unread])

  const chooseTheme = (choice: ThemeChoice) => {
    setTheme(choice)
    storeTheme(choice)
    api.put('/api/settings/general', { theme: choice }).catch(() => {})
  }
  const logout = async () => {
    e2eForget()
    await api.post('/api/auth/logout')
    location.hash = ''
    location.reload()
  }

  return (
    <div className={s.root}>
      <Notifier status={status} />
      <header className={mergeClasses(s.top, 'dm-titlebar')} ref={headerRef}>
        <div className={s.brand}>
          <img src="/favicon.svg" width={22} height={22} alt="" />
          <span className="dm-brand-name">dmOffice</span>
        </div>
        <div className={s.spacer} />
        <Tooltip content={syncing ? t('Wird synchronisiert …') : t('Jetzt synchronisieren')} relationship="label">
          <Button appearance="transparent" className={s.topButton}
            icon={syncing ? <Spinner size="extra-tiny" /> : <ArrowSync20Regular />}
            onClick={() => api.post('/api/mail/sync')} aria-label={syncing ? t('Wird synchronisiert …') : t('Jetzt synchronisieren')} />
        </Tooltip>
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Tooltip content={t('Darstellung')} relationship="label">
              <Button appearance="transparent" className={s.topButton} icon={<WeatherMoon20Regular />} />
            </Tooltip>
          </MenuTrigger>
          <MenuPopover>
            <MenuList checkedValues={{ theme: [theme] }} onCheckedValueChange={(_, d) => chooseTheme(d.checkedItems[0] as ThemeChoice)}>
              <MenuItemRadio name="theme" value="system">{t('Wie das System')}</MenuItemRadio>
              <MenuItemRadio name="theme" value="light">{t('Hell')}</MenuItemRadio>
              <MenuItemRadio name="theme" value="dark">{t('Dunkel')}</MenuItemRadio>
            </MenuList>
          </MenuPopover>
        </Menu>
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Button appearance="transparent" className={s.topButton} aria-label={t('Konto')}>
              <Avatar name={me.displayName} size={28} color="brand" />
            </Button>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              <MenuItem disabled><Text weight="semibold">{me.displayName}</Text> <Text size={200}>({me.login})</Text></MenuItem>
              <MenuDivider />
              <MenuItem icon={<Settings24Regular />} onClick={() => go('settings')}>{t('Einstellungen')}</MenuItem>
              <MenuItem icon={<SignOut20Regular />} onClick={logout}>{t('Abmelden')}</MenuItem>
            </MenuList>
          </MenuPopover>
        </Menu>
      </header>
      <div className={s.body}>
        <nav className={s.rail}>
          {modules.filter(m => m.key !== 'files' || me.files !== false).map(m => (
            <Tooltip key={m.key} content={m.label} relationship="label" positioning="after">
              {/* Zaehler neben dem Knopf, nicht darin - sonst wird der Knopf breiter als die Leiste */}
              <div style={{ position: 'relative' }}>
                <Button appearance="subtle" className={mergeClasses(s.railButton, module === m.key && s.railActive, s.tinted)}
                  icon={<m.icon />} onClick={() => go(m.key)} aria-current={module === m.key ? 'page' : undefined} />
                {m.key === 'mail' && unread > 0 && <CounterBadge className={s.badge} count={unread} size="small" overflowCount={999} />}
              </div>
            </Tooltip>
          ))}
          <Bookmarks />
          <div className={s.spacer} />
          {me.isAdmin && (
            <Tooltip content={t('Verwaltung')} relationship="label" positioning="after">
              <Button appearance="subtle" className={mergeClasses(s.railButton, module === 'admin' && s.railActive, s.tinted)} icon={<AdminIcon />} onClick={() => go('admin')} />
            </Tooltip>
          )}
          <Tooltip content={t('Einstellungen')} relationship="label" positioning="after">
            <Button appearance="subtle" className={mergeClasses(s.railButton, module === 'settings' && s.railActive, s.tinted)} icon={<SettingsIcon />} onClick={() => go('settings')} />
          </Tooltip>
          <div style={{ height: 8 }} />
        </nav>
        <main className={s.main}>
          <Boundary resetKey={module}>
          <Suspense fallback={<Spinner className={s.loading} />}>
            {module === 'mail' && <MailView status={status} />}
            {module === 'calendar' && <CalendarView />}
            {module === 'contacts' && <ContactsView />}
            {module === 'tasks' && <TasksView />}
            {module === 'notes' && <NotesView />}
            {module === 'files' && me.files !== false && <FilesView />}
            {module === 'settings' && <SettingsView />}
            {module === 'admin' && me.isAdmin && <AdminView />}
          </Suspense>
          </Boundary>
        </main>
      </div>
    </div>
  )
}
