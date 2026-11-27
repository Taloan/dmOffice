import { makeStyles, mergeClasses, Spinner, Text, tokens } from '@fluentui/react-components'
import { Folder48Regular } from '@fluentui/react-icons'
import { useEffect, useState, type ReactNode } from 'react'
import { fmtSize } from '../api'
import { locale, t } from '../i18n'
import { Item, viewKind } from './common'
import { PdfPagesView } from './Viewer'

/** Zwei Fenster im Dateibereich (Kennung dualpane): beide Fenster sind vollständige Dateiansichten (FilesView, das rechte mit pane);
 *  hier Rahmen, Aktivieren und die Tasten wie im Midnight Commander. */

/** Zusätzliche Kennungen beim Ziehen: aus welchem Fenster der Zug kommt (das andere nimmt ihn dann auch auf der freien Fläche an) */
export const DRAG_PANE1 = 'application/x-dmoffice-pane1'
export const DRAG_PANE2 = 'application/x-dmoffice-pane2'

const useStyles = makeStyles({
  box: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0, border: '2px solid transparent', borderRadius: tokens.borderRadiusMedium, boxSizing: 'border-box' },
  on: { border: `2px solid ${tokens.colorBrandStroke1}` },
  quick: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0, padding: '8px 12px', gap: '8px' },
  quickHead: { display: 'flex', alignItems: 'baseline', gap: '12px', minWidth: 0 },
  quickBody: { flex: 1, minHeight: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'auto', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2 },
  quickText: { alignSelf: 'stretch', margin: 0, padding: '8px 12px', whiteSpace: 'pre-wrap', wordBreak: 'break-word', fontFamily: tokens.fontFamilyMonospace, fontSize: '12px', width: '100%', boxSizing: 'border-box' },
})

/** Rahmen eines Fensters: aktiv mit farbigem Rand; Klick oder Fokus darin macht es aktiv. Ohne zwei Fenster: nur der Inhalt. */
export function PaneBox({ on, active, onActivate, children }: { on: boolean; active: boolean; onActivate: () => void; children: ReactNode }) {
  const s = useStyles()
  if (!on) return <>{children}</>
  return <div className={mergeClasses(s.box, active && s.on)} onMouseDownCapture={e => activateOnly(e, active, onActivate)} onFocusCapture={onActivate}>{children}</div>
}

/**
 * Klick in ein inaktives Fenster macht es nur aktiv: Trifft er die freie Fläche (keine Zeile, kein Knopf), bleibt die Auswahl dort
 * stehen - sonst hebt der Klick sie auf, und wer zwischen den Fenstern wechselt, verliert seine Markierungen (wie im Commander nicht).
 */
function activateOnly(e: React.MouseEvent, active: boolean, onActivate: () => void) {
  if (active) return
  onActivate()
  const el = e.target as HTMLElement
  if (el.closest('[data-path], button, input, a, [role="treeitem"], [role="menuitem"], [role="option"], [role="tab"]')) return
  // Nur der Klick, der zu diesem Drücken gehört - kommt keiner (etwa beim Ziehen), verfällt die Sperre
  const stop = new AbortController()
  window.addEventListener('click', ev => { ev.stopPropagation(); ev.preventDefault(); stop.abort() }, { capture: true, signal: stop.signal })
  window.addEventListener('mouseup', () => setTimeout(() => stop.abort(), 0), { capture: true, once: true })
}

/** Tasten für zwei Fenster wie im Midnight Commander (nur mit den Commander-Tasten); true, wenn verbraucht */
export interface DualKeys {
  /** Tab: anderes Fenster aktiv */
  onSwitch: () => void
  /** F10: zwei Fenster ein/aus */
  onToggle: () => void
  /** Alt+I: das andere Fenster auf diesen Ordner */
  onOtherHere: (path: string) => void
  /** Alt+O: diesen Ordner im anderen Fenster öffnen */
  onOtherOpen: (path: string) => void
  /** Strg+U: Ordner der beiden Fenster tauschen */
  onSwap: () => void
  /** Strg+Q: im anderen Fenster eine Schnellansicht der aktuellen Datei ein/aus */
  onQuick: () => void
}
export function dualKey(e: React.KeyboardEvent, k: DualKeys, here: string | null, dir: string | null): boolean {
  const ctrl = e.ctrlKey || e.metaKey
  const done = () => { e.preventDefault(); return true }
  if (e.key === 'F10' && !ctrl && !e.altKey) { k.onToggle(); return done() }
  if (e.key === 'Tab' && !ctrl && !e.altKey && !e.shiftKey) { k.onSwitch(); return done() }
  if (e.altKey && !ctrl && e.code === 'KeyI') { if (here !== null) k.onOtherHere(here); return done() }
  if (e.altKey && !ctrl && e.code === 'KeyO') { if (dir !== null) k.onOtherOpen(dir); return done() }
  if (ctrl && !e.altKey && !e.shiftKey && e.code === 'KeyU') { k.onSwap(); return done() }
  if (ctrl && !e.altKey && !e.shiftKey && e.code === 'KeyQ') { k.onQuick(); return done() }
  return false
}

/** Text-Schnellansicht: höchstens so viel lesen */
const QUICK_TEXT = 200_000

/**
 * Schnellansicht im anderen Fenster (Strg+Q wie im Midnight Commander): zeigt die Datei unter dem Cursor bzw. die markierte Datei
 * des aktiven Fensters - Bilder, PDF, Video, Ton und Text; sonst Name, Größe und Datum.
 */
export function QuickView({ item, src }: { item: Item | null; src: (i: Item) => string }) {
  const s = useStyles()
  const kind = item ? viewKind(item) : null
  const url = item && !item.isDir ? src(item) : null
  const [text, setText] = useState<string | null>(null)
  useEffect(() => {
    setText(null)
    if (!url || (kind !== 'text' && kind !== 'markdown')) return
    let alive = true
    fetch(url, { credentials: 'same-origin' }).then(r => r.ok ? r.text() : Promise.reject())
      .then(x => { if (alive) setText(x.length > QUICK_TEXT ? x.slice(0, QUICK_TEXT) + '\n…' : x) })
      .catch(() => { if (alive) setText('') })
    return () => { alive = false }
  }, [url, kind])
  const muted = { color: tokens.colorNeutralForeground3 }
  if (!item) return <div className={s.quick}><Text style={muted}>{t('Schnellansicht: keine Datei gewählt.')}</Text></div>
  const when = item.modified ? new Date(item.modified).toLocaleString(locale) : ''
  return (
    <div className={s.quick} aria-label={t('Schnellansicht')}>
      <div className={s.quickHead}>
        <Text weight="semibold" truncate wrap={false} style={{ minWidth: 0 }}>{item.name}</Text>
        <Text size={200} style={{ ...muted, whiteSpace: 'nowrap' }}>{[item.isDir ? t('Ordner') : fmtSize(item.size), when].filter(Boolean).join(' · ')}</Text>
      </div>
      <div className={s.quickBody}>
        {item.isDir ? <Folder48Regular style={muted} />
          : kind === 'image' ? <img src={url!} alt={item.name} style={{ maxWidth: '100%', maxHeight: '100%', objectFit: 'contain' }} />
          // PDF als Seitenbilder: den PDF-Betrachter des Browsers blockiert Vivaldi im Rahmen unter der CSP der Seite
          : kind === 'pdf' ? <PdfPagesView url={url!} title={item.name} />
          : kind === 'video' ? <video src={url!} controls style={{ maxWidth: '100%', maxHeight: '100%' }} />
          : kind === 'audio' ? <audio src={url!} controls />
          : kind === 'text' || kind === 'markdown' ? (text === null ? <Spinner size="small" /> : <pre className={s.quickText}>{text}</pre>)
          : <Text style={muted}>{t('Für diese Datei gibt es keine Schnellansicht.')}</Text>}
      </div>
    </div>
  )
}
