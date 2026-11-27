import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, FluentProvider } from '@fluentui/react-components'
import { ArrowUndo20Regular, Save20Regular } from '@fluentui/react-icons'
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { createRoot } from 'react-dom/client'
import { RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { activeTheme, storedTheme } from '../theme'
import { t } from '../i18n'
import { featureOn } from '../api' // [feature:adminclean]

// Kennung savemode: In der Verwaltung wird bewusst gespeichert. Jede Seite sammelt ihre Änderungen lokal und
// meldet sie hier an; „Speichern“ und „Verwerfen“ stehen im Menüband, wer die Seite oder die Verwaltung
// mit offenen Änderungen verlässt, wird gefragt. Ein gemeinsamer Baustein für alle Seiten der Verwaltung.

interface Entry {
  /** Gibt es ungespeicherte Änderungen? */
  dirty: boolean
  /** Speichert; false = abgelehnt (Rückfrage verneint) oder fehlgeschlagen, die Änderungen bleiben stehen. */
  save: () => Promise<boolean>
  /** Verwirft die Änderungen (zurück zum gespeicherten Stand). */
  discard: () => void
  // [feature:adminclean]
  /** Kennung adminclean: Abschnitt im gewählten Reiter nicht zu sehen - zählt nicht als „etwas zum Speichern“ */
  off?: boolean
  // [/feature:adminclean]
}

/** Sammelstelle der Seiten (eine je Verwaltung). */
export class AdminDraftHost {
  private entries = new Map<number, { current: Entry }>()
  private listeners = new Set<() => void>()
  private version = 0
  put(id: number, e: { current: Entry }) { this.entries.set(id, e); this.changed() }
  drop(id: number) { if (this.entries.delete(id)) this.changed() }
  changed() { this.version++; this.listeners.forEach(l => l()) }
  subscribe = (l: () => void) => { this.listeners.add(l); return () => { this.listeners.delete(l) } }
  snapshot = () => this.version
  get dirty() { return [...this.entries.values()].some(e => e.current.dirty) }
  /** Kennung adminclean: Hat die gezeigte Seite überhaupt etwas zum Speichern? */
  get any() { return [...this.entries.values()].some(e => !e.current.off) } // [feature:adminclean]
  /** Alle geänderten Seiten nacheinander speichern; hält an, sobald eine ablehnt. */
  async saveAll() {
    for (const e of [...this.entries.values()]) if (e.current.dirty && !(await e.current.save())) return false
    return true
  }
  discardAll() { for (const e of this.entries.values()) if (e.current.dirty) e.current.discard() }
  /** Weiter nur, wenn nichts offen ist oder nach „Änderungen speichern“/„Verwerfen“; „Zurück“ bleibt. */
  async leave(): Promise<boolean> {
    if (!this.dirty) return true
    const r = await askLeave()
    if (r === 'save') return this.saveAll()
    if (r === 'discard') { this.discardAll(); return true }
    return false
  }
}

const HostCtx = createContext<AdminDraftHost | null>(null)
export const AdminDraftProvider = HostCtx.Provider

let seq = 0

/**
 * Meldet die Änderungen einer Seite an. Liefert true, wenn bewusst gespeichert wird (Kennung savemode, Verwaltung mit
 * Menüband) - dann sammelt die Seite ihre Änderungen lokal und zeigt keine eigenen Speichern-Knöpfe.
 * own: Sammelstelle direkt (für die Verwaltung selbst, die außerhalb ihres Providers liegt).
 */
export function useAdminSave(e: Entry, own?: AdminDraftHost | null): boolean {
  const host = useContext(HostCtx) ?? own ?? null
  const ref = useRef(e)
  ref.current = e
  const id = useMemo(() => ++seq, [])
  useEffect(() => {
    if (!host) return
    host.put(id, ref)
    return () => host.drop(id)
  }, [host, id])
  // Nach jedem Zeichnen: hat sich „geändert“ gedreht, erfährt es das Menüband (dirty darf ein Getter sein)
  const last = useRef(false)
  // [feature:adminclean]
  const lastOff = useRef(e.off)
  useEffect(() => {
    const d = ref.current.dirty
    if (d !== last.current || ref.current.off !== lastOff.current) { last.current = d; lastOff.current = ref.current.off; host?.changed() }
  })
  // [feature:adminclean:else]
  // useEffect(() => {
  //   const d = ref.current.dirty
  //   if (d !== last.current) { last.current = d; host?.changed() }
  // })
  // [/feature:adminclean]
  return !!host
}

/**
 * Entwurf für einen gespeicherten Stand: value ist der Entwurf oder (ohne Änderung) der gespeicherte Stand.
 * commit speichert und liefert false, wenn es nicht geklappt hat; confirm fragt vorher nach (große Auswirkung).
 */
export function useAdminDraft<T>(saved: T, commit: (v: T) => Promise<boolean | void>, confirm?: (from: T, to: T) => Promise<boolean>, off?: boolean) {
  const [draft, setDraft] = useState<T | null>(null)
  const dirty = draft !== null && JSON.stringify(draft) !== JSON.stringify(saved)
  const on = useAdminSave({
    dirty,
    save: async () => {
      if (draft === null) return true
      if (confirm && !(await confirm(saved, draft))) return false
      if ((await commit(draft)) === false) return false
      setDraft(null)
      return true
    },
    discard: () => setDraft(null),
    off, // [feature:adminclean]
  })
  const value = draft ?? saved
  const set = useCallback((v: T) => setDraft(v), [])
  return { on, value, set, dirty }
}

/** Speichern und Verwerfen im Menüband; grau, solange nichts geändert ist. */
export function AdminSaveButtons({ host }: { host: AdminDraftHost }) {
  useSyncExternalStore(host.subscribe, host.snapshot)
  const [busy, setBusy] = useState(false)
  const dirty = host.dirty
  const shown = !featureOn('adminclean') || host.any // [feature:adminclean]
  // Seite neu laden oder schließen mit offenen Änderungen: der Browser fragt nach
  useEffect(() => {
    if (!dirty) return
    const warn = (ev: BeforeUnloadEvent) => { ev.preventDefault(); ev.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])
  if (!shown) return null // [feature:adminclean]
  return <>
    <RibbonButton icon={<Save20Regular />} label={t('Speichern')} primary={dirty} disabled={!dirty || busy}
      onClick={async () => { setBusy(true); try { await host.saveAll() } finally { setBusy(false) } }} />
    <RibbonButton icon={<ArrowUndo20Regular />} label={t('Verwerfen')} disabled={!dirty || busy} onClick={() => host.discardAll()} />
    <RibbonDivider />
  </>
}

/** Verlassen mit offenen Änderungen: Änderungen speichern, Verwerfen oder Zurück (bleiben). */
function askLeave(): Promise<'save' | 'discard' | null> {
  return new Promise(resolve => {
    const el = document.createElement('div')
    document.body.appendChild(el)
    const root = createRoot(el)
    const done = (r: 'save' | 'discard' | null) => { root.unmount(); el.remove(); resolve(r) }
    const dark = storedTheme() === 'dark' || (storedTheme() === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
    root.render(
      <FluentProvider theme={activeTheme(dark)}>
        <Dialog open onOpenChange={(_, x) => !x.open && done(null)}>
          <DialogSurface style={{ maxWidth: 540 }}>
            <DialogBody>
              <DialogTitle>{t('Ungespeicherte Änderungen')}</DialogTitle>
              <DialogContent>{t('Auf dieser Seite gibt es Änderungen, die noch nicht gespeichert sind.')}</DialogContent>
              <DialogActions style={{ whiteSpace: 'nowrap' }}>
                <Button onClick={() => done(null)}>{t('Zurück')}</Button>
                <Button onClick={() => done('discard')}>{t('Verwerfen')}</Button>
                <Button appearance="primary" onClick={() => done('save')} autoFocus>{t('Änderungen speichern')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      </FluentProvider>)
  })
}
