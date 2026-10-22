import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, FluentProvider, Input,
  Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, makeStyles, tokens,
} from '@fluentui/react-components'
import { Color20Regular, Copy20Regular, FolderArrowRight20Regular } from '@fluentui/react-icons'
import { useCallback, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { darkTheme, lightTheme, storedTheme } from '../theme'
import { t } from '../i18n'

// Rechtsklick-Menues wie in Windows/Outlook - fuer alle Bereiche dieselben Bausteine und dieselbe Reihenfolge:
//   Oeffnen · Neu … · Bearbeiten … · Umbenennen · Farbe · (Bereichseigenes) · Verschieben nach · Kopieren nach ·
//   Importieren/Exportieren · Loeschen (immer zuletzt, durch Strich getrennt)

export interface At { x: number; y: number }

/** Merkt sich, wo und fuer was das Kontextmenue offen ist. */
export function useContextMenu<T>() {
  const [ctx, setCtx] = useState<{ at: At; data: T } | null>(null)
  const open = useCallback((e: { clientX: number; clientY: number; preventDefault: () => void; stopPropagation: () => void }, data: T) => {
    e.preventDefault()
    e.stopPropagation()
    setCtx({ at: { x: e.clientX, y: e.clientY }, data })
  }, [])
  const close = useCallback(() => setCtx(null), [])
  return { ctx, open, close }
}

const pointAt = (at: At) => ({ getBoundingClientRect: () => ({ x: at.x, y: at.y, top: at.y, left: at.x, bottom: at.y, right: at.x, width: 0, height: 0 }) })

/** Menue an der Mausposition. Ein Klick auf einen Eintrag schliesst es. */
export function ContextMenu({ at, onClose, children }: { at: At | null | undefined; onClose: () => void; children: React.ReactNode }) {
  if (!at) return null
  return (
    <Menu key={`${at.x},${at.y}`} open onOpenChange={(_, d) => !d.open && onClose()} positioning={{ target: pointAt(at) as any, position: 'below', align: 'start', offset: 2 }}>
      <MenuPopover><MenuList>{children}</MenuList></MenuPopover>
    </Menu>
  )
}

const useStyles = makeStyles({
  swatches: { display: 'grid', gridTemplateColumns: 'repeat(5, 24px)', gap: '6px', padding: '8px' },
  swatch: { width: '24px', height: '24px', borderRadius: '50%', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', padding: 0 },
  on: { outline: `2px solid ${tokens.colorNeutralForeground1}`, outlineOffset: '1px' },
})

/** Farben fuer Kalender, Listen und Adressbuecher (wie im Dialog). */
export const collectionColors = ['#0F6CBD', '#13A10E', '#CA5010', '#C239B3', '#E3008C', '#986F0B', '#038387', '#8764B8', '#D13438', '#69797E']

/** Untermenue "Farbe" mit Farbfeldern. */
export function ColorMenu({ colors = collectionColors, current, onPick, label, square, none }: {
  colors?: string[]; current?: string | null; onPick: (c: string | null) => void; label?: string; square?: boolean; none?: boolean
}) {
  const s = useStyles()
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement><MenuItem icon={<Color20Regular />}>{label ?? t('Farbe')}</MenuItem></MenuTrigger>
      <MenuPopover>
        <div className={s.swatches}>
          {colors.map(c => (
            <button key={c} className={`${s.swatch} ${current?.toUpperCase() === c.toUpperCase() ? s.on : ''}`} style={{ background: c, borderRadius: square ? 6 : undefined }}
              title={c} aria-label={c} onClick={() => onPick(c)} />
          ))}
        </div>
        {none && <MenuList><MenuItem onClick={() => onPick(null)}>{t('Keine Farbe')}</MenuItem></MenuList>}
      </MenuPopover>
    </Menu>
  )
}

export interface Target { id: string | number; label: string; disabled?: boolean; depth?: number }

/** Untermenue "Verschieben nach" bzw. "Kopieren nach" mit den moeglichen Zielen. */
export function TargetMenu({ kind, targets, onPick, disabled }: {
  kind: 'move' | 'copy'; targets: Target[]; onPick: (id: string | number) => void; disabled?: boolean
}) {
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <MenuItem icon={kind === 'move' ? <FolderArrowRight20Regular /> : <Copy20Regular />} disabled={disabled || targets.length === 0}>
          {kind === 'move' ? t('Verschieben nach') : t('Kopieren nach')}
        </MenuItem>
      </MenuTrigger>
      <MenuPopover style={{ maxHeight: 420, overflowY: 'auto' }}>
        <MenuList>
          {targets.map(x => (
            <MenuItem key={x.id} disabled={x.disabled} onClick={() => onPick(x.id)}>{' '.repeat(4 * (x.depth ?? 0))}{x.label}</MenuItem>
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
}

/** Kleiner Dialog fuer einen Namen (Umbenennen, Neuer Ordner) - statt prompt(). null: abgebrochen. */
export function askText(title: string, initial = '', label = t('Name'), ok = t('OK')): Promise<string | null> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    const done = (r: string | null) => { root.unmount(); host.remove(); resolve(r) }
    const dark = storedTheme() === 'dark' || (storedTheme() === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
    function Ask() {
      const [value, setValue] = useState(initial)
      return (
        <Dialog open onOpenChange={(_, x) => !x.open && done(null)}>
          <DialogSurface style={{ maxWidth: 400 }}>
            <form onSubmit={e => { e.preventDefault(); if (value.trim()) done(value.trim()) }}>
              <DialogBody>
                <DialogTitle>{title}</DialogTitle>
                <DialogContent>
                  <Field label={label}>
                    <Input value={value} onChange={(_, d) => setValue(d.value)} autoFocus onFocus={e => e.currentTarget.select()} />
                  </Field>
                </DialogContent>
                <DialogActions>
                  <Button onClick={() => done(null)}>{t('Abbrechen')}</Button>
                  <Button appearance="primary" type="submit" disabled={!value.trim()}>{ok}</Button>
                </DialogActions>
              </DialogBody>
            </form>
          </DialogSurface>
        </Dialog>
      )
    }
    root.render(<FluentProvider theme={dark ? darkTheme : lightTheme}><Ask /></FluentProvider>)
  })
}

/** Tippt der Benutzer gerade (Feld, Editor)? Dann gelten die Tastenkuerzel der Liste nicht. */
export function isTyping(e: { target: EventTarget | null }): boolean {
  const el = e.target as HTMLElement | null
  if (!el) return false
  return el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName) || !!el.closest?.('[role=dialog],[role=menu],[role=listbox]')
}

/**
 * Mehrfachauswahl wie im Explorer: Klick waehlt einen, Strg+Klick schaltet um, Umschalt+Klick waehlt den Bereich.
 * order: die Eintraege in der angezeigten Reihenfolge.
 */
export function useMultiSelect<K extends string | number>(order: K[]) {
  const [selected, setSelected] = useState<K[]>([])
  const anchor = useRef<K | null>(null)
  const orderRef = useRef(order)
  orderRef.current = order
  const click = useCallback((e: { ctrlKey: boolean; metaKey: boolean; shiftKey: boolean }, id: K) => {
    const list = orderRef.current
    if (e.ctrlKey || e.metaKey) {
      setSelected(sel => sel.includes(id) ? sel.filter(x => x !== id) : [...sel, id])
      anchor.current = id
    } else if (e.shiftKey && anchor.current != null && list.includes(anchor.current)) {
      const a = list.indexOf(anchor.current), b = list.indexOf(id)
      setSelected(list.slice(Math.min(a, b), Math.max(a, b) + 1))
    } else {
      setSelected([id])
      anchor.current = id
    }
  }, [])
  /** Rechtsklick: ausserhalb der Auswahl waehlt er genau diesen Eintrag (wie Outlook). */
  const pick = useCallback((id: K) => setSelected(sel => (sel.includes(id) ? sel : (anchor.current = id, [id]))), [])
  const all = useCallback(() => setSelected(orderRef.current), [])
  const clear = useCallback(() => setSelected([]), [])
  /** Pfeiltasten: naechster/voriger Eintrag, mit Umschalt die Auswahl erweitern. */
  const step = useCallback((dir: 1 | -1, extend: boolean) => {
    const list = orderRef.current
    if (!list.length) return null
    setSelected(sel => {
      const last = sel.length ? sel[sel.length - 1] : null
      const i = last == null ? (dir > 0 ? -1 : list.length) : list.indexOf(last)
      const next = list[Math.max(0, Math.min(list.length - 1, i + dir))]
      if (!extend) { anchor.current = next; return [next] }
      const a = anchor.current != null && list.includes(anchor.current) ? list.indexOf(anchor.current) : i
      const b = list.indexOf(next)
      // Der zuletzt erreichte Eintrag steht hinten - von dort geht es weiter
      return [...list.slice(Math.min(a, b), Math.max(a, b) + 1).filter(x => x !== next), next]
    })
  }, [])
  return { selected, setSelected, click, pick, all, clear, step }
}
