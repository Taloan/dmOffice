import { Menu, MenuDivider, MenuItem, MenuItemCheckbox, MenuList, MenuPopover, Table, TableBody, TableCell, TableHeader, TableRow } from '@fluentui/react-components'
import { useMemo, useState } from 'react'
import { ColHead } from '../files/FileColumns'
import { t, locale } from '../i18n'

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
}

/**
 * Tabelle der Verwaltung wie im Explorer: Kopf anklicken sortiert, Kante ziehen ändert die Breite, Kopf ziehen ändert die
 * Reihenfolge, Rechtsklick auf den Kopf wählt die Spalten. Breiten, Reihenfolge und Auswahl merkt sich der Browser.
 */
export function AdminTable<R>({ id, cols, rows, rowKey, initialSort, max = 1000 }: {
  id: string; cols: AdminCol<R>[]; rows: R[]; rowKey: (r: R) => string | number
  initialSort?: { col: string; desc: boolean }; max?: number
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
      <div style={{ overflowX: 'auto', maxWidth: '100%' }}>
      <Table size="small" style={{ tableLayout: 'fixed', minWidth: layout.reduce((n, l) => n + (l.width || 220), 0) }}>
        <TableHeader onContextMenu={e => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }) }}>
          <TableRow>
            {layout.map(l => {
              const c = byKey(l.key)
              return (
                <ColHead<string> key={l.key} label={c.label()} width={l.width || undefined} minWidth={l.width ? undefined : 160}
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
            <TableRow key={rowKey(r)}>{layout.map(l => <TableCell key={l.key} style={{ verticalAlign: 'top', wordBreak: 'break-word' }}>{byKey(l.key).render(r)}</TableCell>)}</TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
      <Menu open={!!menu} onOpenChange={(_, d) => { if (!d.open) setMenu(null) }}
        positioning={{ target: menu ? { getBoundingClientRect: () => ({ x: menu.x, y: menu.y, left: menu.x, top: menu.y, right: menu.x, bottom: menu.y, width: 0, height: 0, toJSON: () => '' }) } as unknown as HTMLElement : undefined }}>
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
