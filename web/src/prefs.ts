// Einstellungen, die nur die Oberflaeche betreffen (Favoriten, Ordnerfarben, Akzentfarbe, Formate).
// Gespeichert beim Benutzer auf dem Server (/api/settings/ui), damit sie auf jedem Geraet gleich sind;
// eine Kopie im Browser, damit Farbe und Formate schon vor dem ersten Abruf stimmen.
import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { api } from './api'

export interface UiPrefs {
  language?: 'de' | 'en'
  adminHintsDone?: string[]                         // Hinweise nach der Einrichtung, die der Admin abgehakt hat
  zoneHintDone?: string                             // "Gerät|Konto": zu diesem Unterschied der Zeitzonen wurde schon gefragt
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
  bookmarks?: { name: string; url: string }[]        // eigene Lesezeichen in der linken Leiste (hoechstens 5)
  ribbonLabels?: 'both' | 'icons' | 'text'           // Menueband: Symbole und Text, nur Symbole, nur Text
  ribbonHide?: Record<string, boolean>               // Menueband je Bereich nur beim Darueberfahren zeigen (wie Office)
  sidePane?: Record<string, boolean>
  contactSort?: 'name' | 'family' | 'org'
  taskSort?: 'due' | 'created' | 'title' | 'importance'
  notePreview?: boolean
  /** Hinweis auf die notizi-App in den Notizen weggeklickt */
  notiziTipHidden?: boolean
  /** Notizliste: eigene Reihenfolge (wie in notizi), Geaendert, Erstellt oder Titel */
  noteSort?: 'manual' | 'modified' | 'created' | 'title'
  noteSortDesc?: boolean
  fileSort?: 'name' | 'modified' | 'size' | 'kind' | 'created' | 'ext' | 'taken'
  /** Dateien: absteigend sortieren (Klick auf denselben Spaltenkopf) */
  fileSortDesc?: boolean
  /** Ansicht Fotos: eigene Reihenfolge, Vorgabe Aufnahmedatum (sonst Aenderungsdatum), neueste zuerst */
  photoSort?: 'name' | 'modified' | 'size' | 'taken'
  photoSortDesc?: boolean
  /** Ansicht Fotos: nach Monat (Vorgabe), Tag oder Jahr gruppieren */
  photoGroup?: 'none' | 'day' | 'month' | 'year' | 'source' | 'category' | 'tag'
  /** Galerie: Filterleiste wie in Lightroom offen, und welche Eigenschaft in ihren vier Spalten steht (PhotoFilter) */
  photoFilter?: boolean
  /** Galerie: JPG und RAW gleichen Namens als ein Stapel (Vorgabe an) */
  photoStack?: boolean
  /** Galerie: Kacheln eingepasst (ganzes Bild) statt ausgefüllt (zugeschnitten) */
  photoFit?: boolean
  /** Galerie: bis zu drei Angaben unter jedem Bild (TILE_INFOS) */
  photoTileInfo?: string[]
  photoTileLabel?: boolean                            // Galerie: Farbmarkierung als farbiger Rahmen um das Bild (zählt nicht zu den drei Bildinfos)
  /** Filterleiste der Galerie: sichtbare Zeilen je Spalte (2..8) */
  photoFilterRows?: number
  photoFilterCols?: string[]
  photoQuick?: { flags: string[]; stars: number; labels: string[] }  // Galerie: schnelle Filter-Leiste (gezeigte Markierungen P/X/U, Sterne ab, Farben) - bleibt erhalten
  /** Audiothek: Liste oder Kacheln je Bereich ("tracks", "albums", "artists", "genres") */
  audioView?: Record<string, 'list' | 'tiles'>
  /** DMS: gespeicherte virtuelle Bäume (Name und Ebenen) */
  dmsTrees?: { name: string; levels: string[] }[]
  /** DMS: Dokumentliste als Liste oder Kacheln, Kachelgröße stufenlos (Regler wie in der Galerie) */
  dmsView?: 'list' | 'tiles'
  /** Tabellen (GridColumns): Spalten mit Breite und Sortierung je Tabelle */
  tableCols?: Record<string, { key: string; width: number }[]>
  tableSort?: Record<string, { key: string; desc: boolean }>
  dmsTilePx?: number
  /** Zuletzt gewählter Bereich im DMS (der Tresor nur, wenn er beim Öffnen schon offen ist) */
  dmsArea?: 'files' | 'tresor'
  /** Dateien: Detailbereich rechts offen */
  fileDetails?: boolean
  /** Dateien: Details (Tabelle) oder Kacheln (grosse Symbole, Bildvorschau) */
  fileView?: 'details' | 'tiles'
  /** Dateien gruppieren: keine, nach Art oder nach Aenderungsdatum */
  fileGroup?: 'none' | 'kind' | 'date'
  /** Dateien: Favoriten stehen immer oben (beim Gruppieren als eigene Gruppe) */
  fileFavTop?: boolean
  /** Ordner vor den Dateien (Vorgabe: ja) - zusammen mit Favoriten oben: erst die Ordner, dann die Favoriten */
  fileFolderTop?: boolean
  /** Detailansicht: sichtbare Spalten in dieser Reihenfolge, mit Breite (FileColumns) */
  fileCols?: { key: string; width: number }[]
  /** Detailansicht: Breite der Spalte Name (leer: was uebrig bleibt) */
  fileNameWidth?: number
  /** Reihenfolge der Bereiche in der Leiste links (mit Umschalt/Strg verschoben); fehlende kommen hinten dran */
  moduleOrder?: string[]
  /** Kachelgroesse in der Dateiablage */
  fileTile?: 'small' | 'medium' | 'large'
  fileTilePx?: number                                  // Kachelgröße stufenlos (Schieberegler wie in Lightroom); ohne: aus fileTile
  /** Dateien: Namen mit führendem Punkt (.trash, .obsidian, .stfolder ...) zeigen; Vorgabe aus */
  fileHidden?: boolean
  /** Breiten der Bereiche (Ordnerleiste, Liste, Details …) je Tab, in px (shell/Splitter.tsx) */
  paneWidths?: Record<string, number>
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
  listSort?: 'newest' | 'oldest' | 'from' | 'subject' | 'size' | 'attachment' | 'unread' | 'flagged'   // Reihenfolge der Mailliste
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
