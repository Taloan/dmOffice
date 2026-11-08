import { Button, makeStyles, mergeClasses, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Select, Text, tokens } from '@fluentui/react-components'
import { ChevronDown16Regular, Dismiss16Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { locale, t } from '../i18n'

/** Aufnahmedaten eines Fotos oder Videos (Server: FileMeta) - key ist der Pfad (Galerie) oder der Name (ein Ordner). */
export interface Meta {
  key: string; taken?: number | null; camera?: string | null; lens?: string | null; focal?: number | null; aperture?: number | null
  exposure?: number | null; iso?: number | null; width?: number | null; height?: number | null; duration?: number | null
  rating?: number | null; title?: string | null; keywords?: string | null; description?: string | null
  /** Farbmarkierung (Lightroom, Capture One, darktable): red, yellow, green, blue, purple, orange, pink, gray oder ein eigener Name */
  label?: string | null
  /** Markierung wie Lightroom: 1 ausgewählt (P), -1 abgelehnt (X), sonst ohne (U) */
  pick?: number | null
  /** Aufnahmeort (Atlas) */
  lat?: number | null; lon?: number | null
}
interface Named { name: string; modified: string; path?: string }

// Anzeige wie in Lightroom und im Explorer
export const fmtExposure = (x: number) => x >= 1 ? `${+x.toFixed(1)} s` : `1/${Math.round(1 / x)} s`
export const fmtAperture = (x: number) => `f/${+x.toFixed(1)}`
export const fmtDuration = (s: number) => { const m = Math.floor(s / 60), r = Math.round(s % 60); return m >= 60 ? `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}` }
/** „1/250 s · f/2.8 · ISO 200 · 35 mm“ */
export const exposureLine = (m?: Meta) => !m ? '' : [m.exposure && fmtExposure(m.exposure), m.aperture && fmtAperture(m.aperture), m.iso && `ISO ${m.iso}`, m.focal && `${m.focal} mm`].filter(Boolean).join(' · ')
export const dimsOf = (m?: Meta) => m?.width && m.height ? `${m.width} × ${m.height}` : ''

const num = (x: number, d = 1) => x.toLocaleString(locale, { maximumFractionDigits: d })

/** Farbmarkierungen: Farbe für Rahmen und Punkt, Name zum Anzeigen; eigene Namen (Lightroom-Farbsätze) grau. */
const LABELS: Record<string, [string, () => string]> = {
  red: ['#E5484D', () => t('Rot')], yellow: ['#F5C400', () => t('Gelb')], green: ['#30A46C', () => t('Grün')], blue: ['#0090FF', () => t('Blau')],
  purple: ['#8E4EC6', () => t('Violett')], orange: ['#F76B15', () => t('Orange')], pink: ['#D6409F', () => t('Pink')], gray: ['#8B8D98', () => t('Grau')],
}
export const labelColor = (v?: string | null) => !v ? undefined : LABELS[v]?.[0] ?? '#8B8D98'
export const labelName = (v: string) => LABELS[v]?.[1]() ?? v

/** Quelle eines Fotos wie auf dem Server (AlbumApi.SourceOf): eigene Ablage („own“) oder ein Galerie-Ordner („ext:<Kennung>“). */
export const sourceOf = (path?: string) => {
  if (!path?.startsWith('ext:')) return 'own'
  const slash = path.indexOf('/', 4)
  return slash > 4 ? path.slice(0, slash) : 'own'
}
/** Namen der Galerie-Ordner zum Anzeigen („Storage › RAW › PRI“); kommen beim Öffnen der Galerie vom Server. */
const sourceNames = new Map<string, string>()
export const setSourceNames = (list: { id: string; name: string }[]) => { sourceNames.clear(); for (const x of list) sourceNames.set('ext:' + x.id, x.name) }
/** Werte, die es immer gibt, auch ohne passendes Foto: bei der Quelle alle eingerichteten (ein leerer Ordner soll wählbar bleiben). */
export const knownValues = (attr: FilterAttr): string[] => attr === 'source' ? ['own', ...sourceNames.keys()] : []

/**
 * Angaben über den Bildern der Galerie (Ansicht → Bildinfos, höchstens drei) wie im Kamera-Importer: je Angabe eine Zeile,
 * links und rechts - Datum | Uhrzeit, Blende Zeit ISO | Brennweite, Objektiv | Kamera. Zu lang wird gekürzt.
 */
export const TILE_INFOS: Record<string, { label: () => string; value: (i: Named, m?: Meta) => [string, string?] }> = {
  name: { label: () => t('Dateiname'), value: i => [i.name] },
  taken: { label: () => t('Datum und Uhrzeit'), value: (i, m) => {
    const d = new Date(m?.taken ?? i.modified)
    return [d.toLocaleDateString(locale, { day: '2-digit', month: '2-digit', year: 'numeric' }), d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })]
  } },
  exposure: { label: () => t('Belichtung, ISO, Blende'), value: (_, m) => !m ? [''] : [
    // ISO kurz als „i600“ (Vorgabe) - die Zeile ist schmal
    [m.aperture && 'f' + num(m.aperture), m.exposure && fmtExposure(m.exposure).replace(' s', 's'), m.iso && 'i' + m.iso].filter(Boolean).join(' '),
    m.focal ? num(m.focal, 0) + 'mm' : undefined] },
  camera: { label: () => t('Kamera und Objektiv'), value: (_, m) => [m?.camera ?? '', m?.lens ?? undefined] },
  title: { label: () => t('Titel'), value: (_, m) => [m?.title ?? ''] },
  keywords: { label: () => t('Stichwörter'), value: (_, m) => [m?.keywords?.split('\n').join(', ') ?? ''] },
  rating: { label: () => t('Bewertung'), value: (_, m) => [m?.rating ? '★'.repeat(m.rating) : ''] },
  dims: { label: () => t('Abmessungen'), value: (_, m) => [dimsOf(m)] },
}
/** Aufnahmezeit, sonst Änderungszeit - so sortiert und gruppiert die Galerie. */
export const takenOf = (i: Named, m?: Meta) => m?.taken ?? Date.parse(i.modified)

// Wonach eine Spalte der Filterleiste unterscheidet; '' ist „Unbekannt“
export type FilterAttr = 'year' | 'month' | 'camera' | 'lens' | 'focal' | 'aperture' | 'exposure' | 'iso' | 'keyword' | 'title' | 'description' | 'rating' | 'label' | 'pick' | 'source' | 'type' | 'orientation'
type Order = 'desc' | 'num' | 'alpha'
const ext = (n: string) => { const i = n.lastIndexOf('.'); return i > 0 ? n.slice(i + 1).toUpperCase() : '' }
export const ATTRS: Record<FilterAttr, { label: () => string; values: (i: Named, m?: Meta) => string[]; show?: (v: string) => string; order: Order }> = {
  year: { label: () => t('Jahr'), values: (i, m) => [String(new Date(takenOf(i, m)).getFullYear())], order: 'desc' },
  month: {
    label: () => t('Monat'), order: 'desc',
    values: (i, m) => { const d = new Date(takenOf(i, m)); return [`${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`] },
    show: v => new Date(+v.slice(0, 4), +v.slice(5) - 1, 1).toLocaleDateString(locale, { month: 'long', year: 'numeric' }),
  },
  camera: { label: () => t('Kamera'), values: (_, m) => [m?.camera ?? ''], order: 'alpha' },
  lens: { label: () => t('Objektiv'), values: (_, m) => [m?.lens ?? ''], order: 'alpha' },
  focal: { label: () => t('Brennweite'), values: (_, m) => [m?.focal ? String(m.focal) : ''], show: v => `${v} mm`, order: 'num' },
  aperture: { label: () => t('Blende'), values: (_, m) => [m?.aperture ? String(m.aperture) : ''], show: v => fmtAperture(+v), order: 'num' },
  exposure: { label: () => t('Belichtungszeit'), values: (_, m) => [m?.exposure ? String(m.exposure) : ''], show: v => fmtExposure(+v), order: 'num' },
  iso: { label: () => t('ISO'), values: (_, m) => [m?.iso ? String(m.iso) : ''], show: v => `ISO ${v}`, order: 'num' },
  keyword: { label: () => t('Stichwort'), values: (_, m) => m?.keywords ? m.keywords.split('\n') : [''], order: 'alpha' },
  title: { label: () => t('Titel'), values: (_, m) => [m?.title?.trim() ?? ''], show: v => v === '' ? t('Ohne Titel') : v, order: 'alpha' },
  description: { label: () => t('Beschreibung'), values: (_, m) => [m?.description?.trim() ?? ''], show: v => v === '' ? t('Ohne Beschreibung') : v, order: 'alpha' },
  rating: { label: () => t('Bewertung'), values: (_, m) => [String(m?.rating ?? 0)], show: v => v === '0' ? t('Ohne Bewertung') : '★'.repeat(+v), order: 'desc' },
  label: { label: () => t('Farbmarkierung'), values: (_, m) => [m?.label ?? ''], show: v => v === '' ? t('Ohne Farbmarkierung') : labelName(v), order: 'alpha' },
  pick: { label: () => t('Markierung'), values: (_, m) => [m?.pick === 1 ? 'P' : m?.pick === -1 ? 'X' : 'U'], show: v => v === 'P' ? t('Ausgewählt') : v === 'X' ? t('Abgelehnt') : t('Ohne Markierung'), order: 'alpha' },
  source: { label: () => t('Quelle'), values: i => [sourceOf(i.path)], show: v => v === 'own' ? t('Eigene Ablage') : sourceNames.get(v) ?? v.slice(4), order: 'alpha' },
  type: { label: () => t('Dateityp'), values: i => [ext(i.name)], order: 'alpha' },
  orientation: {
    label: () => t('Format'), order: 'alpha',
    values: (_, m) => [!m?.width || !m.height ? '' : m.width > m.height ? 'quer' : m.width < m.height ? 'hoch' : 'quadrat'],
    show: v => v === 'quer' ? t('Querformat') : v === 'hoch' ? t('Hochformat') : t('Quadratisch'),
  },
}
export const PRESETS: { label: () => string; cols: FilterAttr[] }[] = [
  { label: () => t('Datum, Kamera, Objektiv, Stichwort'), cols: ['year', 'camera', 'lens', 'keyword'] },
  { label: () => t('Aufnahme: Brennweite, Blende, Belichtung, ISO'), cols: ['focal', 'aperture', 'exposure', 'iso'] },
  { label: () => t('Zeit und Art: Jahr, Monat, Bewertung, Dateityp'), cols: ['year', 'month', 'rating', 'type'] },
  { label: () => t('Auswahl: Bewertung, Farbmarkierung, Jahr, Stichwort'), cols: ['rating', 'label', 'year', 'keyword'] },
]
export const DEFAULT_FILTER_COLS: FilterAttr[] = PRESETS[0].cols
export const cleanFilterCols = (c?: string[]): FilterAttr[] => c?.length === 4 && c.every(x => x in ATTRS) ? c as FilterAttr[] : DEFAULT_FILTER_COLS

const passes = <T extends Named>(i: T, m: Meta | undefined, cols: FilterAttr[], sel: string[][], upTo: number) => {
  for (let k = 0; k < upTo; k++) {
    if (!sel[k]?.length) continue
    const vals = ATTRS[cols[k]].values(i, m)
    if (!vals.some(v => sel[k].includes(v))) return false
  }
  return true
}
/** Was durch alle vier Spalten passt. */
export const filterPhotos = <T extends Named>(items: T[], metaOf: (i: T) => Meta | undefined, cols: FilterAttr[], sel: string[][]) =>
  sel.some(s => s.length) ? items.filter(i => passes(i, metaOf(i), cols, sel, cols.length)) : items

const useStyles = makeStyles({
  bar: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '8px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground2 },
  top: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  cols: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: '8px' },
  col: { display: 'flex', flexDirection: 'column', minWidth: 0, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1 },
  list: { overflowY: 'auto', position: 'relative' },
  grip: { height: '6px', margin: '0 -12px -8px', cursor: 'ns-resize', touchAction: 'none', ':hover': { backgroundColor: tokens.colorNeutralStroke2 } },
  row: {
    // Feste Höhe (ROW_PX): sonst ergibt sie sich aus Schrift und Abstand und passt nicht zum Einrasten beim Ziehen
    display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', height: '24px', boxSizing: 'border-box', padding: '0 8px',
    cursor: 'pointer', fontSize: tokens.fontSizeBase200, userSelect: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  on: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  name: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  count: { color: tokens.colorNeutralForeground3, flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
})

/**
 * Filterleiste wie in der Lightroom-Bibliothek: vier Spalten, je eine Eigenschaft (Jahr, Kamera, Objektiv …) mit Anzahl.
 * Klick wählt einen Wert, Strg-Klick mehrere; jede Spalte zählt nur, was die Spalten links davon durchlassen.
 */
/** Höhe einer Zeile in den Spalten (Pixel, wie height in styles.row) - für 2 bis 8 sichtbare Zeilen beim Ziehen. */
const ROW_PX = 24

/** Kopf der Filterleiste: Voreinstellung, Filter aufheben, Anzahl - steht in der Titelzeile der Galerie (spart eine Zeile). */
export function PhotoFilterTop({ total, cols: _cols, onCols, sel, onSel, shown, pending, onSaveSmart }: {
  total: number; cols: FilterAttr[]; onCols: (c: FilterAttr[]) => void; sel: string[][]; onSel: (s: string[][]) => void; shown: number; pending: number
  /** Gesetzter Filter als Smart-Album speichern */
  onSaveSmart?: () => void
}) {
  const s = useStyles()
  const active = sel.some(x => x.length)
  return (
    <div className={s.top}>
      <Menu>
        <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle" icon={<ChevronDown16Regular />} iconPosition="after">{t('Voreinstellung')}</Button></MenuTrigger>
        <MenuPopover><MenuList>
          {PRESETS.map((p, n) => <MenuItem key={n} onClick={() => { onCols(p.cols); onSel([[], [], [], []]) }}>{p.label()}</MenuItem>)}
        </MenuList></MenuPopover>
      </Menu>
      {active && <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} onClick={() => onSel([[], [], [], []])}>{t('Filter aufheben')}</Button>}
      {active && onSaveSmart && <Button size="small" appearance="subtle" onClick={onSaveSmart}>{t('Als Smart-Album speichern')}</Button>}
      <Text size={200} className={s.muted}>{active ? t('{n} von {total}', { n: shown, total }) : t('{total} Fotos und Videos', { total })}</Text>
      {pending > 0 && <Text size={200} className={s.muted}>· {t('Aufnahmedaten von {n} Dateien werden noch gelesen', { n: pending })}</Text>}
    </div>
  )
}

export function PhotoFilterBar<T extends Named>({ items, metaOf, facets, cols, onCols, sel, onSel, rows, onRows }: {
  items: T[]; metaOf: (i: T) => Meta | undefined; cols: FilterAttr[]; onCols: (c: FilterAttr[]) => void
  /** Werte mit Anzahl schon vom Server gezählt (Zeitleiste der Galerie) - dann nicht über items */
  facets?: { total: number; values: [string, number][] }[] | null
  sel: string[][]; onSel: (s: string[][]) => void
  /** Sichtbare Zeilen je Spalte (2..8); am unteren Rand ziehen ändert es */
  rows: number; onRows: (n: number) => void
}) {
  const s = useStyles()
  const [drag, setDrag] = useState<number | null>(null)
  const visible = drag ?? rows
  // Gewählter Wert sichtbar: Ändert sich die Auswahl (auch von außen, etwa aus dem Detailbereich), rollt jede Spalte ihren ersten
  // gewählten Wert in den sichtbaren Bereich - einmal je Auswahl, sobald die Werte da sind; danach bleibt das Rollen beim Benutzer
  const bar = useRef<HTMLDivElement>(null)
  // je Spalte: für welche Auswahl schon gerollt wurde
  const shown = useRef<string[]>([])
  useEffect(() => {
    bar.current?.querySelectorAll<HTMLElement>('[role=listbox]').forEach((list, k) => {
      const key = cols[k] + '|' + JSON.stringify(sel[k] ?? [])
      if (shown.current[k] === key) return
      if (!sel[k]?.length) { shown.current[k] = key; return }
      const on = list.querySelector<HTMLElement>('[aria-selected=true]')
      if (!on) return
      if (on.offsetTop < list.scrollTop || on.offsetTop + on.offsetHeight > list.scrollTop + list.clientHeight)
        list.scrollTop = Math.max(0, on.offsetTop - (list.clientHeight - on.offsetHeight) / 2)
      shown.current[k] = key
    })
  })
  const column = (k: number) => {
    const a = ATTRS[cols[k]]
    const counts = new Map<string, number>()
    let total = 0
    if (facets) {
      const f = facets[k]
      total = f?.total ?? 0
      for (const [v, n] of f?.values ?? []) counts.set(v, n)
    } else
      for (const i of items) {
        const m = metaOf(i)
        if (!passes(i, m, cols, sel, k)) continue
        total++
        for (const v of new Set(a.values(i, m))) counts.set(v, (counts.get(v) ?? 0) + 1)
      }
    for (const v of knownValues(cols[k])) if (!counts.has(v)) counts.set(v, 0)
    const values = [...counts.entries()].sort(([x], [y]) => (x === '') !== (y === '') ? (x === '' ? 1 : -1)
      : a.order === 'desc' ? y.localeCompare(x, locale, { numeric: true }) : a.order === 'num' ? +x - +y : x.localeCompare(y, locale, { sensitivity: 'base' }))
    const pick = (v: string | null, multi: boolean) => {
      const next = sel.map(x => [...x])
      next[k] = v === null ? [] : multi ? (next[k].includes(v) ? next[k].filter(x => x !== v) : [...next[k], v]) : next[k].length === 1 && next[k][0] === v ? [] : [v]
      onSel(next)
    }
    return (
      <div key={k} className={s.col}>
        <Select size="small" appearance="filled-lighter" value={cols[k]} aria-label={t('Eigenschaft der Spalte')}
          onChange={(_, d) => { onCols(cols.map((c, j) => j === k ? d.value as FilterAttr : c)); onSel(sel.map((x, j) => j === k ? [] : x)) }}>
          {(Object.keys(ATTRS) as FilterAttr[]).map(x => <option key={x} value={x}>{ATTRS[x].label()}</option>)}
        </Select>
        <div className={s.list} role="listbox" aria-multiselectable style={{ maxHeight: visible * ROW_PX }}>
          <div role="option" aria-selected={!sel[k].length} className={mergeClasses(s.row, !sel[k].length && s.on)} onClick={() => pick(null, false)}>
            <span className={s.name}>{t('Alle')}</span><span className={s.count}>{total}</span>
          </div>
          {values.map(([v, n]) => {
            const on = sel[k].includes(v)
            return (
              <div key={v} role="option" aria-selected={on} className={mergeClasses(s.row, on && s.on)} onClick={e => pick(v, e.ctrlKey || e.metaKey)}
                title={t('Strg-Klick wählt mehrere')}>
                <span className={s.name}>{v === '' ? t('Unbekannt') : a.show ? a.show(v) : v}</span><span className={s.count}>{n}</span>
              </div>
            )
          })}
        </div>
      </div>
    )
  }
  return (
    <div className={s.bar} ref={bar}>
      <div className={s.cols}>{cols.map((_, k) => column(k))}</div>
      {/* Höhe: am unteren Rand ziehen, 2 bis 8 Zeilen */}
      <div className={s.grip} role="separator" aria-orientation="horizontal" title={t('Ziehen ändert die Höhe der Filterleiste')}
        onPointerDown={e => {
          const y0 = e.clientY, r0 = rows
          const el = e.currentTarget
          el.setPointerCapture(e.pointerId)
          let cur = r0
          const move = (ev: PointerEvent) => { cur = Math.max(2, Math.min(8, r0 + Math.round((ev.clientY - y0) / ROW_PX))); setDrag(cur) }
          const up = () => { el.removeEventListener('pointermove', move); el.removeEventListener('pointerup', up); setDrag(null); if (cur !== r0) onRows(cur) }
          el.addEventListener('pointermove', move); el.addEventListener('pointerup', up)
        }} />
    </div>
  )
}

// ---------------- Markierung, Sterne, Farbe: schnelle Filter-Leiste und Setzen (wie Lightroom) ----------------

export interface QuickFilter { flags: string[]; stars: number; labels: string[] }
export const QUICK_DEFAULT: QuickFilter = { flags: [], stars: 0, labels: [] }
/** Die fünf Farben wie in Lightroom (Tasten 6–9 für die ersten vier) */
export const MARK_LABELS = ['red', 'yellow', 'green', 'blue', 'purple']

/** Als Spalten einer Filterregel (wie die Filterleiste und Smart-Alben, auch auf dem Server); null: filtert nichts. */
export function quickRule(q?: QuickFilter): { cols: FilterAttr[]; sel: string[][] } | null {
  const v = q ?? QUICK_DEFAULT
  const cols: FilterAttr[] = [], sel: string[][] = []
  // Keine oder alle drei gewählt: alle zeigen
  if (v.flags.length > 0 && v.flags.length < 3) { cols.push('pick'); sel.push(v.flags) }
  if (v.stars > 0) { cols.push('rating'); sel.push(['1', '2', '3', '4', '5'].filter(x => +x >= v.stars)) }
  if (v.labels.length) { cols.push('label'); sel.push(v.labels) }
  return cols.length ? { cols, sel } : null
}

const useQuick = makeStyles({
  bar: { display: 'flex', alignItems: 'center', gap: '4px', flexWrap: 'wrap', padding: '4px 8px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  grp: { display: 'flex', alignItems: 'center', gap: '2px', paddingRight: '8px', marginRight: '4px', borderRight: `1px solid ${tokens.colorNeutralStroke2}`, ':last-child': { borderRight: 'none' } },
  lbl: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, marginRight: '4px' },
  btn: { minWidth: '26px', height: '26px', padding: '0 6px', fontSize: tokens.fontSizeBase200 },
  on: { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2, border: `1px solid ${tokens.colorBrandStroke2}` },
  dot: { width: '12px', height: '12px', borderRadius: '50%', display: 'inline-block' },
})

/** Filter-Leiste oben: welche Markierungen gezeigt werden, Sterne ab, Farben. */
export function PhotoQuickBar({ value, onChange, inline }: { value?: QuickFilter; onChange: (q: QuickFilter) => void; inline?: boolean }) {
  const s = useQuick()
  const v = value ?? QUICK_DEFAULT
  const flag = (f: string) => onChange({ ...v, flags: v.flags.includes(f) ? v.flags.filter(x => x !== f) : [...v.flags, f] })
  // Nichts gedrückt: alle; sonst nur die gedrückten Markierungen
  const flagTitle: Record<string, string> = { P: t('Ausgewählte zeigen'), X: t('Abgelehnte zeigen'), U: t('Ohne Markierung zeigen') }
  // Kürzel und Farben wie in der Leiste unten, der Name steht im Tooltip
  const flagColor: Record<string, string | undefined> = { P: tokens.colorPaletteGreenForeground1, X: tokens.colorPaletteRedForeground1, U: undefined }
  // Eingeklappt, solange nichts gefiltert wird: auf dem Handy nähme die Leiste sonst ein Drittel des Bildschirms
  const [open, setOpen] = useState(false)
  if (!open && !quickRule(v)) return <Button size="small" appearance="subtle" style={{ fontWeight: 400 }} aria-expanded={false} onClick={() => setOpen(true)}>{t('Nach Markierung, Sternen, Farbe filtern')}</Button>
  return (
    <div className={s.bar} style={inline ? { borderBottom: 'none', padding: 0 } : undefined} role="toolbar" aria-label={t('Filter-Leiste')}>
      <div className={s.grp}><span className={s.lbl}>{t('Markierung')}</span>
        {['P', 'X', 'U'].map(f => <Button key={f} size="small" appearance="subtle" className={mergeClasses(s.btn, v.flags.includes(f) && s.on)} title={flagTitle[f]} aria-pressed={v.flags.includes(f)}
          style={v.flags.includes(f) ? undefined : { color: flagColor[f] }} onClick={() => flag(f)}>{f}</Button>)}
      </div>
      <div className={s.grp}><span className={s.lbl}>{t('Sterne ab')}</span>
        {[1, 2, 3, 4, 5].map(n => <Button key={n} size="small" appearance="subtle" className={mergeClasses(s.btn, v.stars >= n && s.on)} title={t('ab {n} Sternen', { n })} aria-pressed={v.stars === n}
          onClick={() => onChange({ ...v, stars: v.stars === n ? 0 : n })}>★</Button>)}
      </div>
      <div className={s.grp}><span className={s.lbl}>{t('Farbe')}</span>
        {MARK_LABELS.map(l => <Button key={l} size="small" appearance="subtle" className={mergeClasses(s.btn, v.labels.includes(l) && s.on)} title={labelName(l)} aria-pressed={v.labels.includes(l)}
          onClick={() => onChange({ ...v, labels: v.labels.includes(l) ? v.labels.filter(x => x !== l) : [...v.labels, l] })}><span className={s.dot} style={{ background: labelColor(l) }} /></Button>)}
      </div>
      {quickRule(v) && <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} onClick={() => onChange(QUICK_DEFAULT)}>{t('Alle zeigen')}</Button>}
    </div>
  )
}

/** Setter-Leiste unten: Markierung, Sterne, Farbe (und Löschen) für die gewählten Fotos. */
export function PhotoSetBar({ count, onPick, onRating, onLabel, onDelete }: {
  count: number; onPick: (p: number) => void; onRating: (r: number) => void; onLabel: (l: string) => void; onDelete?: () => void
}) {
  const s = useQuick()
  return (
    <div className={s.bar} style={{ borderBottom: 'none', borderTop: `1px solid ${tokens.colorNeutralStroke2}` }} role="toolbar" aria-label={t('Markieren')}>
      <span className={s.lbl} style={{ marginRight: 8 }}>{count ? t('{n} gewählt', { n: count }) : t('Nichts gewählt')}</span>
      <div className={s.grp}>
        <Button size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('Ausgewählt (P)')} style={{ color: tokens.colorPaletteGreenForeground1 }} onClick={() => onPick(1)}>P</Button>
        <Button size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('Abgelehnt (X)')} style={{ color: tokens.colorPaletteRedForeground1 }} onClick={() => onPick(-1)}>X</Button>
        <Button size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('Ohne Markierung (U)')} onClick={() => onPick(0)}>U</Button>
      </div>
      <div className={s.grp}>
        {[1, 2, 3, 4, 5].map(n => <Button key={n} size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('{n} Sterne ({n})', { n })} onClick={() => onRating(n)}>★</Button>)}
        <Button size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('Ohne Bewertung (0)')} onClick={() => onRating(0)}>0</Button>
      </div>
      <div className={s.grp}>
        {MARK_LABELS.map((l, k) => <Button key={l} size="small" appearance="subtle" className={s.btn} disabled={!count} title={labelName(l) + (k < 4 ? ` (${k + 6})` : '')} onClick={() => onLabel(l)}><span className={s.dot} style={{ background: labelColor(l) }} /></Button>)}
        <Button size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('Ohne Farbe')} onClick={() => onLabel('')}>–</Button>
      </div>
      {onDelete && <div className={s.grp}><Button size="small" appearance="subtle" className={s.btn} disabled={!count} title={t('Löschen (Entf)')} style={{ color: tokens.colorPaletteRedForeground1 }} onClick={onDelete}>{t('Löschen')}</Button></div>}
    </div>
  )
}
