import { useMemo, useState } from 'react'
import { locale } from '../i18n'

type Key = string | number | null | undefined

/** Tabelle nach Klick auf die Spaltenkoepfe sortieren. `columns` liefert je Spalte den Sortierwert; leere Werte stehen am Ende. */
export function useSort<T>(rows: T[], columns: Record<string, (row: T) => Key>, initial?: { column: string; descending?: boolean }) {
  const [state, setState] = useState<{ column: string; descending: boolean } | null>(
    initial ? { column: initial.column, descending: !!initial.descending } : null)

  const sorted = useMemo(() => {
    const get = state && columns[state.column]
    if (!state || !get) return rows
    const dir = state.descending ? -1 : 1
    return rows.map((row, i) => ({ row, i, key: get(row) })).sort((a, b) => {
      const ea = a.key == null || a.key === '', eb = b.key == null || b.key === ''
      if (ea || eb) return ea && eb ? a.i - b.i : ea ? 1 : -1   // Leeres immer ans Ende
      const c = typeof a.key === 'number' && typeof b.key === 'number' ? a.key - b.key
        : String(a.key).localeCompare(String(b.key), locale, { sensitivity: 'base', numeric: true })
      return c !== 0 ? c * dir : a.i - b.i
    }).map(x => x.row)
  }, [rows, state]) // eslint-disable-line react-hooks/exhaustive-deps   // columns ist bei jedem Aufruf neu, aendert aber nichts am Ergebnis

  /** Eigenschaften fuer <TableHeaderCell>: Klick sortiert, zweiter Klick dreht um. */
  const header = (column: string) => ({
    sortable: true as const,
    sortDirection: state?.column === column ? (state.descending ? 'descending' as const : 'ascending' as const) : undefined,
    onClick: () => setState(s => s?.column === column ? { column, descending: !s.descending } : { column, descending: false }),
  })
  return { sorted, header }
}
