import { makeStyles, Menu, MenuDivider, MenuItem, MenuItemCheckbox, MenuList, MenuPopover, mergeClasses, TableHeaderCell, tokens } from '@fluentui/react-components'
import { ArrowDown16Regular, ArrowUp16Regular } from '@fluentui/react-icons'
import { useRef, useState } from 'react'
import { t } from '../i18n'

/**
 * Spalten der Detailansicht wie im Explorer: Breite an der Kante ziehen, Reihenfolge per Ziehen, Spalten per Rechtsklick
 * auf die Kopfzeile (oder Ansicht → Spalten) ein- und ausblenden. Gespeichert in den Einstellungen (fileCols, fileNameWidth).
 */
export type SortKey = 'name' | 'modified' | 'size' | 'kind' | 'created' | 'ext' | 'taken'
export type ColKey = 'modified' | 'created' | 'kind' | 'ext' | 'mime' | 'size' | 'place' | 'shared' | 'favorite'
  | 'taken' | 'camera' | 'lens' | 'exposure' | 'dims' | 'duration' | 'rating' | 'keywords'
/** Spalten aus den Aufnahmedaten (FileMeta) - nur wenn eine davon zu sehen ist, werden sie geladen. */
export const META_COLS: ColKey[] = ['taken', 'camera', 'lens', 'exposure', 'dims', 'duration', 'rating', 'keywords']
export interface ColPref { key: ColKey; width: number }

export const COLS: Record<ColKey, { label: () => string; width: number; sort?: SortKey }> = {
  modified: { label: () => t('Geändert'), width: 160, sort: 'modified' },
  created: { label: () => t('Erstellt'), width: 160, sort: 'created' },
  kind: { label: () => t('Art'), width: 110, sort: 'kind' },
  ext: { label: () => t('Dateiendung'), width: 100, sort: 'ext' },
  mime: { label: () => t('Dateityp'), width: 190 },
  size: { label: () => t('Größe'), width: 100, sort: 'size' },
  place: { label: () => t('Ort'), width: 220 },
  shared: { label: () => t('Geteilt'), width: 140 },
  favorite: { label: () => t('Favorit'), width: 76 },
  taken: { label: () => t('Aufnahmedatum'), width: 160, sort: 'taken' },
  camera: { label: () => t('Kamera'), width: 160 },
  lens: { label: () => t('Objektiv'), width: 180 },
  exposure: { label: () => t('Belichtung'), width: 200 },
  dims: { label: () => t('Abmessungen'), width: 120 },
  duration: { label: () => t('Dauer'), width: 80 },
  rating: { label: () => t('Bewertung'), width: 90 },
  keywords: { label: () => t('Stichwörter'), width: 180 },
}
export const DEFAULT_COLS: ColPref[] = [{ key: 'modified', width: 160 }, { key: 'kind', width: 110 }, { key: 'size', width: 100 }]
const DRAG_COL = 'application/x-dmoffice-column'

/** Gespeicherte Spalten bereinigen: nur bekannte, ohne Doppelte, Breiten in vernuenftigen Grenzen. */
export const cleanCols = (cols: ColPref[] | undefined): ColPref[] =>
  (cols ?? DEFAULT_COLS).filter((c, i, a) => c.key in COLS && a.findIndex(x => x.key === c.key) === i)
    .map(c => ({ key: c.key, width: Math.min(800, Math.max(50, Math.round(c.width || COLS[c.key].width))) }))

const useStyles = makeStyles({
  // Schmale Spalte: Überschrift mit „…“ statt in die nächste hinein
  head: { position: 'relative', userSelect: 'none', overflow: 'hidden', whiteSpace: 'nowrap', '& .fui-TableHeaderCell__button': { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' } },
  sortable: { cursor: 'pointer' },
  dragging: { opacity: 0.5 },
  over: { boxShadow: `inset 2px 0 0 ${tokens.colorBrandStroke1}` },
  overAfter: { boxShadow: `inset -2px 0 0 ${tokens.colorBrandStroke1}` },
  grip: {
    position: 'absolute', top: 0, right: '-3px', bottom: 0, width: '7px', cursor: 'col-resize', zIndex: 1,
    ':hover': { backgroundColor: tokens.colorBrandStroke2 },
  },
})

/** Ein Spaltenkopf: sortieren per Klick, Breite an der rechten Kante, Reihenfolge per Ziehen (wenn moveable). */
export function ColHead<K extends string = ColKey>({ label, width, minWidth, sortable, sorted, desc, onSort, onWidth, onWidthDone, moveKey, onMove }: {
  label: string; width?: number; minWidth?: number; sortable?: boolean; sorted?: boolean; desc?: boolean; onSort?: () => void
  onWidth: (w: number) => void; onWidthDone: (w: number) => void
  /** after: rechts von der Spalte `to` abgelegt - so geht auch die letzte Stelle */
  moveKey?: K; onMove?: (from: K, to: K, after: boolean) => void
}) {
  const s = useStyles()
  const cell = useRef<HTMLDivElement>(null)
  const resized = useRef(false)
  const [drag, setDrag] = useState(false)
  const [over, setOver] = useState<'before' | 'after' | null>(null)
  const sideOf = (e: React.DragEvent) => { const r = e.currentTarget.getBoundingClientRect(); return e.clientX > r.left + r.width / 2 ? 'after' as const : 'before' as const }
  const startResize = (e: React.PointerEvent) => {
    e.preventDefault(); e.stopPropagation()
    const startX = e.clientX, startW = cell.current?.getBoundingClientRect().width ?? width ?? 120
    const el = e.currentTarget as HTMLElement
    el.setPointerCapture(e.pointerId)
    let last = startW
    const move = (ev: PointerEvent) => { last = Math.max(50, Math.round(startW + ev.clientX - startX)); onWidth(last) }
    const up = () => { el.removeEventListener('pointermove', move); el.removeEventListener('pointerup', up); resized.current = true; onWidthDone(last) }
    el.addEventListener('pointermove', move)
    el.addEventListener('pointerup', up)
  }
  return (
    <TableHeaderCell ref={cell} style={width ? { width, minWidth: width, maxWidth: width } : minWidth ? { minWidth } : undefined}
      className={mergeClasses(s.head, sortable && s.sortable, drag && s.dragging, over === 'before' && s.over, over === 'after' && s.overAfter)}
      aria-sort={sorted ? (desc ? 'descending' : 'ascending') : undefined}
      onClick={() => { if (resized.current) { resized.current = false; return } if (sortable) onSort?.() }}
      draggable={!!moveKey}
      onDragStart={moveKey ? e => { e.dataTransfer.setData(DRAG_COL, moveKey); e.dataTransfer.effectAllowed = 'move'; setDrag(true) } : undefined}
      onDragEnd={() => setDrag(false)}
      onDragOver={moveKey ? e => { if (e.dataTransfer.types.includes(DRAG_COL)) { e.preventDefault(); setOver(sideOf(e)) } } : undefined}
      onDragLeave={() => setOver(null)}
      onDrop={moveKey ? e => { setOver(null); const from = e.dataTransfer.getData(DRAG_COL) as K; if (from && from !== moveKey) { e.preventDefault(); onMove?.(from, moveKey, sideOf(e) === 'after') } } : undefined}>
      {label}{sorted && (desc ? <ArrowDown16Regular style={{ marginLeft: 4 }} /> : <ArrowUp16Regular style={{ marginLeft: 4 }} />)}
      <span className={s.grip} onPointerDown={startResize} onClick={e => e.stopPropagation()} onDoubleClick={e => { e.stopPropagation(); onWidthDone(0) }}
        role="separator" aria-orientation="vertical" aria-label={t('Spaltenbreite ändern')} />
    </TableHeaderCell>
  )
}

/** Auswahl der Spalten: als Menue an einer Stelle (Rechtsklick auf die Kopfzeile) oder an einem Knopf (Menueband). */
export function ColumnMenu({ cols, onChange, at, onClose, trigger, labels }: {
  cols: ColPref[]; onChange: (c: ColPref[]) => void
  at?: { x: number; y: number } | null; onClose?: () => void; trigger?: JSX.Element
  /** Abweichende Namen je Ansicht, wie im Spaltenkopf */
  labels?: Partial<Record<ColKey, string>>
}) {
  const shown = cols.map(c => c.key)
  const toggle = (k: ColKey) => onChange(shown.includes(k) ? cols.filter(c => c.key !== k) : [...cols, { key: k, width: COLS[k].width }])
  const list = (
    <MenuList checkedValues={{ cols: shown }}>
      {(Object.keys(COLS) as ColKey[]).map(k => (
        <MenuItemCheckbox key={k} name="cols" value={k} onClick={() => toggle(k)}>{labels?.[k] ?? COLS[k].label()}</MenuItemCheckbox>
      ))}
      <MenuDivider />
      <MenuItem onClick={() => onChange(DEFAULT_COLS)}>{t('Standard wiederherstellen')}</MenuItem>
    </MenuList>
  )
  if (trigger) return <Menu>{trigger}<MenuPopover>{list}</MenuPopover></Menu>
  return (
    <Menu open={!!at} onOpenChange={(_, d) => { if (!d.open) onClose?.() }}
      positioning={{ target: at ? { getBoundingClientRect: () => ({ x: at.x, y: at.y, left: at.x, top: at.y, right: at.x, bottom: at.y, width: 0, height: 0, toJSON: () => '' }) } as any : undefined }}>
      <MenuPopover>{list}</MenuPopover>
    </Menu>
  )
}
