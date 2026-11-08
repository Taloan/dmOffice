import { Switch, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { ColDef, useGridColumns } from '../shell/GridColumns'

interface Row { login: string; displayName: string; on: boolean; lastRun: string | null; lastOk: string | null; lastError: string | null; fails: number; running: boolean }

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '10px', maxWidth: '980px', marginTop: '24px' },
  muted: { color: tokens.colorNeutralForeground3 },
  table: { display: 'flex', flexDirection: 'column', overflowX: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  row: { display: 'grid', alignItems: 'center', gap: '8px', padding: '6px 10px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, minHeight: '32px' },
  head: { fontWeight: 600, color: tokens.colorNeutralForeground2, backgroundColor: tokens.colorNeutralBackground2, height: '32px', padding: '0 10px' },
  cell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  bad: { color: tokens.colorPaletteRedForeground1 },
})

const COLS: Record<string, ColDef> = {
  name: { label: () => t('Person'), width: 200, sort: 'name' },
  login: { label: () => t('Anmeldename'), width: 140, sort: 'login' },
  on: { label: () => t('Eingeschaltet'), width: 110, sort: 'on' },
  lastRun: { label: () => t('Zuletzt exportiert'), width: 150, sort: 'lastRun' },
  lastOk: { label: () => t('Zuletzt ohne Fehler'), width: 150, sort: 'lastOk' },
  error: { label: () => t('Fehler'), width: 260, sort: 'error' },
}
const DEFAULT = ['name', 'on', 'lastRun', 'error']
const when = (iso: string | null) => iso ? new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }) : ''

/**
 * Verwaltung → Für Benutzer freigeben → Benutzer-Datenexport: freischalten oder sperren (Vorgabe: frei), dazu der Stand je Person. Ziel und
 * Zugangsdaten der Personen erscheinen hier nicht - jede Person richtet ihre Sicherung selbst ein.
 */
export function PlainBackupAdmin() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [allowed, setAllowed] = useState<boolean | null>(null)
  const [rows, setRows] = useState<Row[]>([])
  const grid = useGridColumns('admin-plainbackup', COLS, DEFAULT, '')
  const load = () => api.get<{ allowed: boolean; users: Row[] }>('/api/admin/plainbackup').then(r => { setAllowed(r.allowed); setRows(r.users) }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (allowed === null) return null
  const set = async (on: boolean) => {
    try { await api.put('/api/admin/plainbackup', { allowed: on }); setAllowed(on); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  const key = grid.sort?.key ?? 'name'
  const value = (r: Row): string | number => {
    switch (key) {
      case 'login': return r.login
      case 'on': return r.on ? 1 : 0
      case 'lastRun': return r.lastRun ?? ''
      case 'lastOk': return r.lastOk ?? ''
      case 'error': return r.lastError ?? ''
      default: return r.displayName || r.login
    }
  }
  const sorted = [...rows].sort((a, b) => {
    const x = value(a), y = value(b)
    const c = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), locale, { sensitivity: 'base' })
    return grid.sort?.desc ? -c : c
  })
  return (
    <div className={s.root}>
      <Text size={200} className={s.muted}>{t('Mit dem Datenexport legt jede Person ihre Daten entschlüsselt und lesbar an einem eigenen Ziel ab, etwa per WebDAV nach pCloud. Eingerichtet wird er unter Einstellungen → Konto → Datenexport. Ziel und Zugangsdaten der Personen erscheinen hier nicht.')}</Text>
      <Switch checked={allowed} onChange={(_, d) => set(d.checked)} label={t('Benutzer dürfen den Datenexport einrichten')} />
      {!allowed && <Text size={200} className={s.muted}>{t('Gesperrt: eingerichtete Datenexporte ruhen, ihre Einstellungen und Ziele bleiben unberührt.')}</Text>}
      <div className={s.table}>
        <div className={`${s.row} ${s.head}`} style={{ gridTemplateColumns: grid.template, minWidth: 'max-content' }} onContextMenu={grid.openMenu}>{grid.cells}</div>
        {grid.columnMenu}
        {sorted.map(r => (
          <div key={r.login} className={s.row} style={{ gridTemplateColumns: grid.template, minWidth: 'max-content' }}>
            {grid.cols.map(c => {
              switch (c.key) {
                case 'login': return <span key={c.key} className={s.cell}>{r.login}</span>
                case 'on': return <span key={c.key} className={s.cell}>{r.running ? t('läuft') : r.on ? t('ja') : t('nein')}</span>
                case 'lastRun': return <span key={c.key} className={s.cell}>{when(r.lastRun)}</span>
                case 'lastOk': return <span key={c.key} className={s.cell}>{when(r.lastOk)}</span>
                case 'error': return <span key={c.key} className={`${s.cell} ${r.lastError ? s.bad : ''}`} title={r.lastError ?? undefined}>
                  {r.lastError ? (r.fails > 1 ? t('{n}-mal nacheinander: ', { n: r.fails }) : '') + r.lastError : ''}</span>
                default: return <span key={c.key} className={s.cell}>{r.displayName || r.login}</span>
              }
            })}
          </div>
        ))}
        {rows.length === 0 && <Text className={s.muted} style={{ padding: 10 }}>{t('Keine Personen.')}</Text>}
      </div>
    </div>
  )
}
