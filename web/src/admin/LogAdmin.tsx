import { Button, Dropdown, Input, Option, Table, TableBody, TableCell, TableHeader, TableRow, Text, tokens } from '@fluentui/react-components'
import { ArrowClockwise20Regular, Copy20Regular, Search20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { ColHead } from '../files/FileColumns'
import { copyText } from '../shell/browser'
import { t, locale } from '../i18n'

type Entry = { id: number; at: string; level: string; category: string; message: string; exception: string | null }
type Col = 'at' | 'level' | 'category' | 'message'
const LABEL: Record<Col, () => string> = { at: () => t('Zeit'), level: () => t('Stufe'), category: () => t('Bereich'), message: () => t('Meldung') }
const WIDTH: Record<Col, number> = { at: 160, level: 90, category: 160, message: 0 }
const STORE = 'dmoffice.admin.log.cols'

/**
 * Systemprotokoll: Warnungen und Fehler des Servers seit dem Start, dazu gebremste (429) und fehlgeschlagene (5xx) Anfragen.
 * Spalten wie im Explorer: Kopf anklicken sortiert, Kante ziehen ändert die Breite, Kopf ziehen ändert die Reihenfolge.
 */
export function LogAdmin() {
  const { fail, notify } = useApp()
  const [data, setData] = useState<{ since: string; entries: Entry[] } | null>(null)
  const [level, setLevel] = useState<'all' | 'Error' | 'Warning'>('all')
  const [query, setQuery] = useState('')
  const [sort, setSort] = useState<{ col: Col; desc: boolean }>({ col: 'at', desc: true })
  const [cols, setCols] = useState<{ key: Col; width: number }[]>(() => {
    try { const v = JSON.parse(localStorage.getItem(STORE) ?? 'null'); if (Array.isArray(v) && v.length === 4) return v } catch { /* Vorgabe */ }
    return (['at', 'level', 'category', 'message'] as Col[]).map(key => ({ key, width: WIDTH[key] }))
  })
  const saveCols = (c: typeof cols) => { setCols(c); try { localStorage.setItem(STORE, JSON.stringify(c)) } catch { /* nur für jetzt */ } }
  const load = () => api.get<{ since: string; entries: Entry[] }>('/api/admin/log').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    const list = (data?.entries ?? []).filter(e => (level === 'all' || (level === 'Error' ? e.level === 'Error' || e.level === 'Critical' : e.level === 'Warning'))
      && (!q || (e.message + ' ' + e.category + ' ' + (e.exception ?? '')).toLowerCase().includes(q)))
    const dir = sort.desc ? -1 : 1
    return [...list].sort((a, b) => dir * (sort.col === 'at' ? a.id - b.id : String(a[sort.col]).localeCompare(String(b[sort.col]), locale)))
  }, [data, level, query, sort])

  const levelText = (l: string) => l === 'Error' || l === 'Critical' ? t('Fehler') : l === 'Warning' ? t('Warnung') : l
  const cell = (e: Entry, c: Col) => {
    if (c === 'at') return new Date(e.at).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'medium' })
    if (c === 'level') return <span style={{ color: e.level === 'Warning' ? tokens.colorPaletteDarkOrangeForeground1 : tokens.colorPaletteRedForeground1 }}>{levelText(e.level)}</span>
    if (c === 'category') return e.category
    return <span style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{e.message}{e.exception ? '\n' + e.exception : ''}</span>
  }
  const copy = async () => {
    const text = shown.map(e => `${e.at}\t${e.level}\t${e.category}\t${e.message}${e.exception ? ' | ' + e.exception : ''}`).join('\n')
    if (await copyText(text)) notify(t('Kopiert'))
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <Text>{t('Warnungen und Fehler des Servers seit dem letzten Start, dazu gebremste (429) und fehlgeschlagene (5xx) Anfragen. Das vollständige Protokoll steht im Container (docker logs).')}</Text>
      {data && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Server gestartet {when} · {n} Einträge', { when: new Date(data.since).toLocaleString(locale), n: data.entries.length })}</Text>}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <Dropdown value={level === 'all' ? t('Alle') : levelText(level)} selectedOptions={[level]} onOptionSelect={(_, d) => setLevel(d.optionValue as typeof level)} style={{ minWidth: 140 }}>
          <Option value="all">{t('Alle')}</Option>
          <Option value="Error">{t('Fehler')}</Option>
          <Option value="Warning">{t('Warnung')}</Option>
        </Dropdown>
        <Input contentBefore={<Search20Regular />} placeholder={t('Durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ minWidth: 220 }} />
        <Button icon={<ArrowClockwise20Regular />} onClick={load}>{t('Aktualisieren')}</Button>
        <Button icon={<Copy20Regular />} onClick={copy} disabled={!shown.length}>{t('Kopieren')}</Button>
      </div>
      {data && (shown.length === 0 ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Keine Einträge.')}</Text> : (
        <Table size="small" style={{ tableLayout: 'fixed' }}>
          <TableHeader>
            <TableRow>
              {cols.map(c => (
                <ColHead<Col> key={c.key} label={LABEL[c.key]()} width={c.width || undefined} minWidth={c.key === 'message' ? 200 : undefined}
                  sortable sorted={sort.col === c.key} desc={sort.desc}
                  onSort={() => setSort(x => ({ col: c.key, desc: x.col === c.key ? !x.desc : c.key === 'at' }))}
                  onWidth={w => setCols(cs => cs.map(x => x.key === c.key ? { ...x, width: w } : x))}
                  onWidthDone={w => saveCols(cols.map(x => x.key === c.key ? { ...x, width: w || WIDTH[c.key] } : x))}
                  moveKey={c.key} onMove={(from, to, after) => {
                    const next = cols.filter(x => x.key !== from)
                    next.splice(next.findIndex(x => x.key === to) + (after ? 1 : 0), 0, cols.find(x => x.key === from)!)
                    saveCols(next)
                  }} />
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.slice(0, 1000).map(e => (
              <TableRow key={e.id}>{cols.map(c => <TableCell key={c.key} style={{ verticalAlign: 'top' }}>{cell(e, c.key)}</TableCell>)}</TableRow>
            ))}
          </TableBody>
        </Table>
      ))}
    </div>
  )
}
