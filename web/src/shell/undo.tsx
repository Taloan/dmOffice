import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Portal, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowCounterclockwise20Regular, ChevronUp20Regular, Dismiss20Regular } from '@fluentui/react-icons'
import { useEffect, useState, useSyncExternalStore } from 'react'
import { featureOn } from '../api'
import { t } from '../i18n'
import { isTyping } from './context'
import { useIsMobile } from './mobile'

/**
 * Einheitliches Rückgängig (Kennung undoall): Jede Aktion meldet sich mit Text und Rückgängig-Funktion an (pushUndo).
 * Die letzte erscheint als Leiste unten in der Mitte mit „Rückgängig“ (Vorbild: Dateien), Strg+Z nimmt außerhalb von
 * Eingabefeldern die letzte zurück, die Liste hinter dem Pfeil zeigt die letzten Schritte, solange die Seite offen ist.
 * Jeder Schritt gehört zu dem Bereich, in dem er geschah (Galerie, Dateien, E-Mail …): Rückgängig im Menüband, Strg+Z und die
 * Liste nehmen nur Schritte des Bereichs zurück, der gerade offen ist - nie still etwas aus einem anderen Bereich.
 * Ohne Kennung bleibt alles wie bisher: Termine/Kontakte/Aufgaben/DMS mit Toast (App.undoable), Dateien mit eigener Leiste.
 */
export interface UndoStep { id: number; label: string; run: () => Promise<void>; at: number; area: string }

/** Bereich aus der Adresse, wie die Seitenleiste ihn bestimmt (alte Galerie-Links in den Dateien zählen zur Galerie) */
const areaNow = () => /^#\/?files\/:photos/.test(location.hash) ? 'gallery' : location.hash.replace(/^#\/?/, '').split('/')[0] || 'dashboard'

/** So viele Schritte je Bereich bleiben, solange die Seite offen ist */
export const UNDO_MAX = 10
const SHOW_MS = 8000

let steps: UndoStep[] = []
let shown: UndoStep | null = null
let busy = false
let nextId = 1
let timer: number | undefined
let snapshot: { steps: UndoStep[]; shown: UndoStep | null } = { steps: [], shown }
const listeners = new Set<() => void>()
/** Nur die Schritte des offenen Bereichs; die eingeblendete Leiste nur, solange ihr Bereich offen ist */
const emit = () => {
  const area = areaNow()
  snapshot = { steps: steps.filter(s => s.area === area), shown: shown?.area === area ? shown : null }
  listeners.forEach(l => l())
}
if (typeof window !== 'undefined') window.addEventListener('hashchange', () => emit())
const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l) } }

/** Meldung und Fehler der App - setzt UndoHost */
let report: { done: (label: string) => void; fail: (e: unknown) => void } = { done: () => {}, fail: () => {} }

export const undoAllOn = () => featureOn('undoall')

/** Aktion anmelden: erscheint als Leiste, Strg+Z oder „Rückgängig“ ruft run. */
export function pushUndo(label: string, run: () => Promise<void>) {
  const step = { id: nextId++, label, run, at: Date.now(), area: areaNow() }
  const same = steps.filter(s => s.area === step.area)
  const drop = new Set(same.slice(0, Math.max(0, same.length - (UNDO_MAX - 1))))
  steps = [...steps.filter(s => !drop.has(s)), step]
  shown = step
  clearTimeout(timer)
  timer = window.setTimeout(() => { shown = null; emit() }, SHOW_MS)
  emit()
}

/** Einen Schritt zurücknehmen (ohne id: den letzten des offenen Bereichs). false: es gab nichts. */
export async function undoStep(id?: number): Promise<boolean> {
  if (busy) return true
  const step = id == null ? snapshot.steps[snapshot.steps.length - 1] : steps.find(s => s.id === id)
  if (!step) return false
  steps = steps.filter(s => s !== step)
  if (shown === step || id == null) shown = null
  emit()
  busy = true
  try { await step.run(); report.done(step.label) } catch (e) { report.fail(e) } finally { busy = false }
  return true
}

export const hideUndo = () => { clearTimeout(timer); shown = null; emit() }
/** Maus über der Leiste: stehen lassen; danach wieder die volle Zeit */
const hold = (on: boolean) => { clearTimeout(timer); if (!on && shown) timer = window.setTimeout(() => { shown = null; emit() }, SHOW_MS) }

/** Die Schritte für Menübänder (Knopf „Rückgängig“ nur aktiv, wenn es etwas gibt). */
export function useUndoSteps() {
  return useSyncExternalStore(subscribe, () => snapshot)
}

const useStyles = makeStyles({
  bar: {
    position: 'fixed', left: '50%', transform: 'translateX(-50%)', zIndex: 1000001, display: 'flex', alignItems: 'center', gap: '12px',
    padding: '8px 8px 8px 16px', borderRadius: tokens.borderRadiusLarge, boxShadow: tokens.shadow16, maxWidth: 'calc(100vw - 32px)',
    backgroundColor: tokens.colorNeutralBackgroundInverted, color: tokens.colorNeutralForegroundInverted,
  },
  label: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  btn: { color: tokens.colorBrandForegroundInverted },
  time: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
})

/** Einmal in der App: Leiste an festem Platz und Strg+Z. */
export function UndoHost({ notify, fail }: { notify: (title: string) => void; fail: (e: unknown) => void }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const { steps: list, shown: bar } = useUndoSteps()
  const [menu, setMenu] = useState(false)
  useEffect(() => { report = { done: label => notify(t('Rückgängig: {what}', { what: label })), fail } }, [notify, fail])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // Bereiche mit eigenem Strg+Z (etwa Dateien) haben es schon erledigt; in Eingabefeldern bleibt es beim Text
      if (e.defaultPrevented || !(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey || e.key.toLowerCase() !== 'z' || isTyping(e)) return
      if (!snapshot.steps.length) return
      e.preventDefault()
      undoStep()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
  if (!bar && !menu) return null
  const shownStep = bar ?? list[list.length - 1]
  if (!shownStep) return null
  const time = (at: number) => new Date(at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  return (
    <Portal>
      <div className={s.bar} role="status" onMouseEnter={() => hold(true)} onMouseLeave={() => hold(false)} style={{ bottom: mobile ? 'calc(env(safe-area-inset-bottom, 0px) + 76px)' : 'calc(env(safe-area-inset-bottom, 0px) + 16px)' }}>
        <span className={s.label}>{shownStep.label}</span>
        <Button appearance="transparent" className={s.btn} icon={<ArrowCounterclockwise20Regular />} onClick={() => undoStep(shownStep.id)}>{t('Rückgängig')}</Button>
        {list.length > 1 && (
          <Menu open={menu} onOpenChange={(_, d) => { setMenu(d.open); hold(d.open) }} positioning="above-end">
            <MenuTrigger disableButtonEnhancement>
              <Button appearance="transparent" className={s.btn} icon={<ChevronUp20Regular />} aria-label={t('Letzte Schritte')} title={t('Letzte Schritte')} />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {[...list].reverse().map(x => (
                  <MenuItem key={x.id} icon={<ArrowCounterclockwise20Regular />} secondaryContent={<span className={s.time}>{time(x.at)}</span>}
                    onClick={() => { setMenu(false); undoStep(x.id) }}>{x.label}</MenuItem>
                ))}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        <Button appearance="transparent" className={s.btn} icon={<Dismiss20Regular />} aria-label={t('Schließen')} onClick={() => { setMenu(false); hideUndo() }} />
      </div>
    </Portal>
  )
}
