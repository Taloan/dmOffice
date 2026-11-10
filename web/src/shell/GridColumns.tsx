import { makeStyles, Menu, MenuDivider, MenuItem, MenuItemCheckbox, MenuList, MenuPopover, mergeClasses, tokens } from '@fluentui/react-components'
import { ArrowDown16Regular, ArrowUp16Regular } from '@fluentui/react-icons'
import { useRef, useState } from 'react'
import { t } from '../i18n'
import { usePrefs } from '../prefs'

/**
 * Spalten einer Tabelle aus CSS-Raster wie im Explorer: Klick auf den Kopf sortiert (noch einmal: umgekehrt), Breite an der
 * rechten Kante ziehen (Doppelklick: Vorgabe), Reihenfolge per Ziehen, Rechtsklick auf die Kopfzeile wählt die Spalten.
 * Je Tabelle (id) in den Einstellungen gemerkt (tableCols, tableSort). Vorlage für jede neue Tabelle.
 */
export interface ColDef { label: () => string; width: number; sort?: string }
export interface ColState { key: string; width: number }
export interface SortState { key: string; desc: boolean }

const DRAG_COL = 'application/x-dmoffice-grid-column'

const useStyles = makeStyles({
  head: { position: 'relative', userSelect: 'none', overflow: 'hidden', whiteSpace: 'nowrap', textOverflow: 'ellipsis', display: 'flex', alignItems: 'center',
    gap: '4px', minWidth: 0, height: '100%' },
  sortable: { cursor: 'pointer', ':hover': { color: tokens.colorNeutralForeground1 } },
  dragging: { opacity: 0.5 },
  over: { boxShadow: `inset 2px 0 0 ${tokens.colorBrandStroke1}` },
  overAfter: { boxShadow: `inset -2px 0 0 ${tokens.colorBrandStroke1}` },
  label: { overflow: 'hidden', textOverflow: 'ellipsis' },
  grip: { position: 'absolute', top: 0, right: 0, bottom: 0, width: '8px', cursor: 'col-resize', zIndex: 1,
    ':hover': { backgroundColor: tokens.colorBrandStroke2 } },
})

/** Spalten, Sortierung und Kopfzeile einer Tabelle. lead: Breite der festen Spalte vorn (etwa Auswahlkasten), '' für keine;
 * tail: letzte Spalte (füllt auf; etwa für Knöpfe am Zeilenende 'minmax(max-content, 1fr)'). */
export function useGridColumns(id: string, defs: Record<string, ColDef>, defaults: string[], lead = '32px', tail = 'minmax(0, 1fr)') {
  const { prefs, update } = usePrefs()
  const [live, setLive] = useState<Record<string, number>>({})
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const clean = (c: ColState[]) => c.filter((x, i, a) => x.key in defs && a.findIndex(y => y.key === x.key) === i)
    .map(x => ({ key: x.key, width: Math.min(900, Math.max(40, Math.round(x.width || defs[x.key].width))) }))
  const initial = defaults.map(k => ({ key: k, width: defs[k].width }))
  const cols = clean(prefs.tableCols?.[id] ?? initial)
  const sort = prefs.tableSort?.[id] ?? null
  const save = (c: ColState[]) => update({ tableCols: { ...prefs.tableCols, [id]: c } })
  const setSort = (key: string) => update({ tableSort: { ...prefs.tableSort, [id]: { key, desc: sort?.key === key ? !sort.desc : false } } })
  const move = (from: string, to: string, after: boolean) => {
    const list = cols.filter(c => c.key !== from)
    const at = list.findIndex(c => c.key === to) + (after ? 1 : 0)
    list.splice(at, 0, cols.find(c => c.key === from)!)
    save(list)
  }
  const toggle = (k: string) => save(cols.some(c => c.key === k) ? cols.filter(c => c.key !== k) : [...cols, { key: k, width: defs[k].width }])
  /** grid-template-columns: feste Spalte vorn, die Spalten, letzte Spalte füllt auf. */
  const template = [lead, ...cols.map(c => `${live[c.key] ?? c.width}px`), tail].filter(Boolean).join(' ')
  const openMenu = (e: React.MouseEvent) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }) }

  const cells = cols.map(c => (
    <ColHead key={c.key} label={defs[c.key].label()} sortable={!!defs[c.key].sort} sorted={!!defs[c.key].sort && sort?.key === defs[c.key].sort} desc={sort?.desc}
      onSort={() => setSort(defs[c.key].sort!)} moveKey={c.key} onMove={move}
      onWidth={w => setLive(l => ({ ...l, [c.key]: w }))}
      onWidthDone={w => { setLive(l => { const n = { ...l }; delete n[c.key]; return n }); save(cols.map(x => x.key === c.key ? { ...x, width: w || defs[c.key].width } : x)) }} />
  ))
  const m = menu
  const columnMenu = m && (
    <Menu open onOpenChange={(_, d) => { if (!d.open) setMenu(null) }}
      positioning={{ target: { getBoundingClientRect: () => ({ x: m.x, y: m.y, left: m.x, top: m.y, right: m.x, bottom: m.y, width: 0, height: 0, toJSON: () => '' }) } as any }}>
      <MenuPopover>
        <MenuList checkedValues={{ cols: cols.map(c => c.key) }}>
          {Object.keys(defs).map(k => <MenuItemCheckbox key={k} name="cols" value={k} onClick={() => toggle(k)}>{defs[k].label()}</MenuItemCheckbox>)}
          <MenuDivider />
          <MenuItem onClick={() => save(initial)}>{t('Standard wiederherstellen')}</MenuItem>
        </MenuList>
      </MenuPopover>
    </Menu>
  )
  return { cols, sort, template, cells, openMenu, columnMenu }
}

function ColHead({ label, sortable, sorted, desc, onSort, onWidth, onWidthDone, moveKey, onMove }: {
  label: string; sortable?: boolean; sorted?: boolean; desc?: boolean; onSort?: () => void
  onWidth: (w: number) => void; onWidthDone: (w: number) => void; moveKey: string; onMove: (from: string, to: string, after: boolean) => void
}) {
  const s = useStyles()
  const cell = useRef<HTMLDivElement>(null)
  const resized = useRef(false)
  const [drag, setDrag] = useState(false)
  // Ziehen über die linke Hälfte setzt die Spalte davor, über die rechte dahinter - so geht auch die letzte Stelle
  const [over, setOver] = useState<'before' | 'after' | null>(null)
  const sideOf = (e: React.DragEvent) => { const r = e.currentTarget.getBoundingClientRect(); return e.clientX > r.left + r.width / 2 ? 'after' as const : 'before' as const }
  const [resizing, setResizing] = useState(false)
  const startResize = (e: React.PointerEvent) => {
    e.preventDefault(); e.stopPropagation()
    setResizing(true)
    const startX = e.clientX, startW = cell.current?.getBoundingClientRect().width ?? 120
    const el = e.currentTarget as HTMLElement
    el.setPointerCapture(e.pointerId)
    let last = startW
    const move = (ev: PointerEvent) => { last = Math.max(40, Math.round(startW + ev.clientX - startX)); onWidth(last) }
    const up = () => { el.removeEventListener('pointermove', move); el.removeEventListener('pointerup', up); resized.current = true; setResizing(false); onWidthDone(last) }
    el.addEventListener('pointermove', move)
    el.addEventListener('pointerup', up)
  }
  return (
    <div ref={cell} role="columnheader" aria-sort={sorted ? (desc ? 'descending' : 'ascending') : undefined}
      className={mergeClasses(s.head, sortable && s.sortable, drag && s.dragging, over === 'before' && s.over, over === 'after' && s.overAfter)}
      onClick={() => { if (resized.current) { resized.current = false; return } if (sortable) onSort?.() }}
      draggable={!resizing} onDragStart={e => {
        // Am Rand: Breite ändern, nicht verschieben
        if (resizing) { e.preventDefault(); return }
        e.dataTransfer.setData(DRAG_COL, moveKey); e.dataTransfer.effectAllowed = 'move'; setDrag(true)
      }}
      onDragEnd={() => setDrag(false)}
      onDragOver={e => { if (e.dataTransfer.types.includes(DRAG_COL)) { e.preventDefault(); setOver(sideOf(e)) } }}
      onDragLeave={() => setOver(null)}
      onDrop={e => { setOver(null); const from = e.dataTransfer.getData(DRAG_COL); if (from && from !== moveKey) { e.preventDefault(); onMove(from, moveKey, sideOf(e) === 'after') } }}>
      <span className={s.label}>{label}</span>
      {sorted && (desc ? <ArrowDown16Regular /> : <ArrowUp16Regular />)}
      <span className={s.grip} onPointerDown={startResize} onClick={e => e.stopPropagation()} onDoubleClick={e => { e.stopPropagation(); onWidthDone(0) }}
        role="separator" aria-orientation="vertical" aria-label={t('Spaltenbreite ändern')} />
    </div>
  )
}
