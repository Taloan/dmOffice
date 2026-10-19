// Einstellungen, die nur die Oberflaeche betreffen (Favoriten, Ordnerfarben, Akzentfarbe, Formate).
// Gespeichert beim Benutzer auf dem Server (/api/settings/ui), damit sie auf jedem Geraet gleich sind;
// eine Kopie im Browser, damit Farbe und Formate schon vor dem ersten Abruf stimmen.
import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { api } from './api'

export interface UiPrefs {
  language?: 'de' | 'en'
  calendarTasks?: boolean
  virtualClosed?: Record<string, boolean>         // Suchordner/Kategorien im Ordnerbereich eingeklappt
  categoryFolders?: boolean                      // Kategorien als Ordner zeigen                         // Aufgaben mit Faelligkeit im Kalender zeigen                           // Sprache der Oberflaeche
  favorites?: string[]                              // "konto|ordnerpfad"
  folderColors?: Record<string, string>
  folderCount?: Record<string, 'unread' | 'total'>
  folderOrder?: Record<string, string[]>           // "konto|elternpfad" -> Reihenfolge der Kinder
  accent?: string                                  // Akzentfarbe, #rrggbb
  dateFormat?: 'dd.MM.yyyy' | 'd.M.yyyy' | 'dd.MM.yy' | 'yyyy-MM-dd'
  timeFormat?: '24' | '12'
  // Ordnerbereich
  showFavorites?: boolean
  pinFavorites?: boolean
  sortFolders?: 'alpha' | 'custom'
  // Nachrichtenliste und Vorschaufenster
  textSize?: 'small' | 'medium' | 'large'
  density?: 'compact' | 'normal' | 'roomy'
  showPreview?: boolean
  showAvatars?: boolean
  readingPane?: 'right' | 'bottom' | 'off'
  folderPane?: boolean
  todoBar?: ('calendar' | 'people' | 'tasks')[]
  ribbonTab?: 'start' | 'view'
  // Die anderen Bereiche: Reiter im Menueband und Navigationsbereich je Bereich ("calendar", "people", ...)
  ribbonTabs?: Record<string, 'start' | 'view'>
  bookmarks?: { name: string; url: string }[]        // eigene Lesezeichen in der linken Leiste (hoechstens 5)
  ribbonLabels?: 'both' | 'icons' | 'text'           // Menueband: Symbole und Text, nur Symbole, nur Text
  sidePane?: Record<string, boolean>
  contactSort?: 'name' | 'family' | 'org'
  taskSort?: 'due' | 'created' | 'title' | 'importance'
  notePreview?: boolean
  fileSort?: 'name' | 'modified' | 'size'
  // Verfassen
  composeFont?: string
  composeSize?: string
  replyAll?: boolean
  sendDelay?: number                               // Sekunden zum Zurueckrufen nach dem Senden (0-30)
  // Kalender
  firstDay?: number
  workStart?: string
  workEnd?: string
  workDays?: number[]
  slotMinutes?: number
  weekNumbers?: boolean
  defaultReminder?: number | null
  // Benachrichtigungen
  notifyMail?: boolean
  notifyCalendar?: boolean
  notifyDesktop?: boolean
  notifySound?: boolean
  // Suchen
  searchScope?: 'inboxAll' | 'all' | 'current'
  searchDeleted?: boolean
  searchRelevance?: boolean
  searchSort?: 'newest' | 'oldest' | 'relevance' | 'from' | 'subject'   // Reihenfolge der Suchtreffer
}

const KEY = 'dmoffice.ui'

function cached(): UiPrefs {
  try { return JSON.parse(localStorage.getItem(KEY) || '{}') } catch { return {} }
}

/** Der aktuelle Stand - fuer Formatierfunktionen ausserhalb von React. */
export let currentPrefs: UiPrefs = cached()

function publish(p: UiPrefs) {
  currentPrefs = p
  try { localStorage.setItem(KEY, JSON.stringify(p)) } catch { /* privater Modus */ }
  window.dispatchEvent(new Event('dmoffice:prefs'))
}

export const PrefsContext = createContext<{ prefs: UiPrefs; update: (patch: Partial<UiPrefs>) => void }>({
  prefs: currentPrefs, update: () => {},
})
export const usePrefs = () => useContext(PrefsContext)

/** Laedt die Einstellungen vom Server und speichert Aenderungen gesammelt zurueck. */
export function usePrefsState() {
  const [prefs, setPrefs] = useState<UiPrefs>(currentPrefs)
  const timer = useRef<number | undefined>(undefined)

  useEffect(() => {
    api.get<UiPrefs>('/api/settings/ui').then(p => {
      // Auf einem anderen Geraet die Sprache gewechselt: einmal neu laden
      setPrefs(p); publish(p)
      if (p.language && p.language !== document.documentElement.lang) location.reload()
    }).catch(() => {})
  }, [])

  const update = useCallback((patch: Partial<UiPrefs>) => {
    setPrefs(prev => {
      const next = { ...prev, ...patch }
      publish(next)
      clearTimeout(timer.current)
      timer.current = window.setTimeout(() => { api.put('/api/settings/ui', next).catch(() => {}) }, 400)
      return next
    })
  }, [])

  return { prefs, update }
}

/** Neu zeichnen, wenn sich die Einstellungen aendern (fuer Teile ausserhalb des Kontexts). */
export function usePrefsVersion(): number {
  const [n, setN] = useState(0)
  useEffect(() => {
    const on = () => setN(x => x + 1)
    window.addEventListener('dmoffice:prefs', on)
    return () => window.removeEventListener('dmoffice:prefs', on)
  }, [])
  return n
}
