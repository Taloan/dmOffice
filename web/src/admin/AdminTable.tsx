import { Checkbox, Menu, MenuDivider, MenuItem, MenuItemCheckbox, MenuList, MenuPopover, Table, TableBody, TableCell, TableHeader, TableRow, tokens } from '@fluentui/react-components'
import { useMemo, useState } from 'react'
import { ColHead } from '../files/FileColumns'
import { tableFrameLook } from '../shell/GridColumns'
import { t, locale } from '../i18n'
import { dblOpen } from '../shell/rowOpen' // [feature:rowdblopen]

export interface AdminCol<R> {
  key: string
  label: () => string
  /** Vorgabebreite in Pixeln; 0 = nimmt den Rest */
  width: number
  /** Wert zum Sortieren (Zahl oder Text); ohne: nicht sortierbar */
  sort?: (r: R) => number | string
  render: (r: R) => React.ReactNode
  /** Nicht ausblendbar (etwa die Aktionsspalte) */
  fixed?: boolean
  /** Eigener Inhalt im Spaltenkopf statt der Beschriftung (etwa „alle auswählen“) */
  head?: () => React.ReactNode
}

/**
 * Auswahlspalte mit Kästchen je Zeile und „alle auswählen“ im Spaltenkopf (bezogen auf die übergebenen, also gefilterten Zeilen).
 * can: nur diese Zeilen sind wählbar.
 */
export function selectCol<R, K extends string | number>({ rows, id, selected, onChange, can }: {
  rows: R[]; id: (r: R) => K; selected: K[]; onChange: (next: K[]) => void; can?: (r: R) => boolean
}): AdminCol<R> {
  const ids = rows.filter(r => !can || can(r)).map(id)
  const n = ids.filter(k => selected.includes(k)).length
  return {
    key: 'sel', label: () => t('Auswahl'), width: 40, fixed: true,
    head: () => <Checkbox checked={ids.length > 0 && n === ids.length ? true : n > 0 ? 'mixed' : false} disabled={ids.length === 0} aria-label={t('Alle auswählen')}
      onClick={e => e.stopPropagation()}
      onChange={() => onChange(n === ids.length ? selected.filter(k => !ids.includes(k)) : [...selected, ...ids.filter(k => !selected.includes(k))])} />,
    render: r => can && !can(r) ? null : <Checkbox checked={selected.includes(id(r))} aria-label={t('Auswählen')}
      onChange={(_, d) => onChange(d.checked ? [...selected, id(r)] : selected.filter(k => k !== id(r)))} />,
  }
}

/** Auswahlspalte für genau einen Eintrag (Bearbeiten, Löschen im Menüband); ohne Kästchen im Kopf. */
export function pickCol<R, K extends string | number>({ id, selected, onChange }: {
  id: (r: R) => K; selected: K | null | undefined; onChange: (next: K | null) => void
}): AdminCol<R> {
  return {
    key: 'sel', label: () => t('Auswahl'), head: () => null, width: 40, fixed: true,
    render: r => <Checkbox checked={selected === id(r)} aria-label={t('Auswählen')} onChange={() => onChange(selected === id(r) ? null : id(r))} />,
  }
}

/**
 * Tabelle der Verwaltung wie im Explorer: Kopf anklicken sortiert, Kante ziehen ändert die Breite, Kopf ziehen ändert die
 * Reihenfolge, Rechtsklick auf den Kopf wählt die Spalten. Breiten, Reihenfolge und Auswahl merkt sich der Browser.
 */
export function AdminTable<R>({ id, cols, rows, rowKey, initialSort, max = 1000, onRowContextMenu, onRowClick, active,
  onRowOpen, // [feature:rowdblopen]
}: {
  id: string; cols: AdminCol<R>[]; rows: R[]; rowKey: (r: R) => string | number
  initialSort?: { col: string; desc: boolean }; max?: number
  /** Rechtsklick auf eine Zeile (Kontextmenü der Zeile); ohne: Menü des Browsers */
  onRowContextMenu?: (e: React.MouseEvent, r: R) => void
  /** Klick in eine Zeile wählt sie aus (Aktionen im Menüband); [active] ist der Schlüssel der gewählten Zeile */
  onRowClick?: (r: R) => void
  active?: string | number | null
  // [feature:rowdblopen]
  /** Doppelklick auf eine Zeile löst ihre Öffnen-Aktion aus (wie „Öffnen“/„Bearbeiten“ im Menüband) */
  onRowOpen?: (r: R) => void
  // [/feature:rowdblopen]
}) {
  const store = 'dmoffice.admin.table.' + id
  const defaults = cols.map(c => ({ key: c.key, width: c.width }))
  const [layout, setLayout] = useState<{ key: string; width: number }[]>(() => {
    try {
      const v = JSON.parse(localStorage.getItem(store) ?? 'null')
      if (Array.isArray(v) && v.every(x => cols.some(c => c.key === x.key))) {
        // Feste Spalten immer dabei
        for (const c of cols) if (c.fixed && !v.some((x: { key: string }) => x.key === c.key)) v.push({ key: c.key, width: c.width })
        return v
      }
    } catch { /* Vorgabe */ }
    return defaults
  })
  const save = (l: typeof layout) => { setLayout(l); try { localStorage.setItem(store, JSON.stringify(l)) } catch { /* nur für jetzt */ } }
  const [sort, setSort] = useState(initialSort ?? { col: '', desc: false })
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const byKey = (k: string) => cols.find(c => c.key === k)!

  const shown = useMemo(() => {
    const c = cols.find(x => x.key === sort.col)
    if (!c?.sort) return rows
    const dir = sort.desc ? -1 : 1
    return [...rows].sort((a, b) => {
      const x = c.sort!(a), y = c.sort!(b)
      return dir * (typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), locale))
    })
  }, [rows, sort, cols])

  const toggle = (k: string) => save(layout.some(x => x.key === k) ? layout.filter(x => x.key !== k) : [...layout, { key: k, width: byKey(k).width }])

  return (
    <>
      {/* Breiter als der Platz: waagerecht scrollen statt die freie Spalte auf null zu drücken */}
      {/* Rahmen wie in allen Tabellen (Vorbild Helpdesk/Finanzen) */}
      <div style={{ overflowX: 'auto', maxWidth: '100%', ...tableFrameLook, flexShrink: 0 }}>
      <Table size="small" style={{ tableLayout: 'fixed', minWidth: layout.reduce((n, l) => n + (l.width || 220), 0) }}>
        <TableHeader onContextMenu={e => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }) }}>
          <TableRow>
            {layout.map(l => {
              const c = byKey(l.key)
              return (
                <ColHead<string> key={l.key} label={c.head ? c.head() : c.label()} width={l.width || undefined} minWidth={l.width ? undefined : 160}
                  sortable={!!c.sort} sorted={sort.col === l.key} desc={sort.desc}
                  onSort={() => setSort(x => ({ col: l.key, desc: x.col === l.key ? !x.desc : false }))}
                  onWidth={w => setLayout(ls => ls.map(x => x.key === l.key ? { ...x, width: w } : x))}
                  onWidthDone={w => save(layout.map(x => x.key === l.key ? { ...x, width: w || c.width } : x))}
                  moveKey={l.key} onMove={(from, to, after) => {
                    const next = layout.filter(x => x.key !== from)
                    next.splice(next.findIndex(x => x.key === to) + (after ? 1 : 0), 0, layout.find(x => x.key === from)!)
                    save(next)
                  }} />
              )
            })}
          </TableRow>
        </TableHeader>
        <TableBody>
          {shown.slice(0, max).map(r => (
            <TableRow key={rowKey(r)} onContextMenu={onRowContextMenu ? e => onRowContextMenu(e, r) : undefined}
              onClick={onRowClick ? () => onRowClick(r) : undefined}
              {...(onRowOpen ? dblOpen(() => onRowOpen(r)) : {})} // [feature:rowdblopen]
              aria-selected={active != null ? rowKey(r) === active : undefined}
              style={active != null && rowKey(r) === active ? { backgroundColor: tokens.colorNeutralBackground1Selected } : undefined}>{layout.map(l => <TableCell key={l.key} style={{ verticalAlign: 'middle', wordBreak: 'break-word' }}>{byKey(l.key).render(r)}</TableCell>)}</TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
      <Menu open={!!menu} onOpenChange={(_, d) => { if (!d.open) setMenu(null) }}
        positioning={{ autoSize: 'height', target: menu ? { getBoundingClientRect: () => ({ x: menu.x, y: menu.y, left: menu.x, top: menu.y, right: menu.x, bottom: menu.y, width: 0, height: 0, toJSON: () => '' }) } as unknown as HTMLElement : undefined }}>
        <MenuPopover>
          <MenuList checkedValues={{ cols: layout.map(x => x.key) }}>
            {cols.filter(c => !c.fixed).map(c => <MenuItemCheckbox key={c.key} name="cols" value={c.key} onClick={() => toggle(c.key)}>{c.label()}</MenuItemCheckbox>)}
            <MenuDivider />
            <MenuItem onClick={() => save(defaults)}>{t('Standard wiederherstellen')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
    </>
  )
}
