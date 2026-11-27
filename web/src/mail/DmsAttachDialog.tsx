import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Search20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { locale, t } from '../i18n'
import { ColDef, tableFrameLook, useGridColumns } from '../shell/GridColumns'
import { useIsMobile } from '../shell/mobile'

/** Dokument aus dem DMS, wie die Liste es liefert (nur, was die Auswahl braucht) */
interface Doc { id: number; title: string; originalName: string; docDate: string | null; imported: string; correspondent: string | null; size: number
  state: string; trashed: boolean; path: string | null; fileId: number; mount: string | null }

/** Zum Anfügen: „dms:Nummer“ liest der Server beim Senden aus dem DMS (auch aus einem externen Ordner) */
export interface DmsPicked { path: string; name: string; size: number }

const useStyles = makeStyles({
  surface: { width: 'min(900px, calc(100vw - 24px))', maxWidth: 'none', height: 'min(640px, calc(100vh - 48px))', display: 'flex', flexDirection: 'column' },
  body: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 },
  content: { display: 'flex', flexDirection: 'column', gap: '10px', flex: 1, minHeight: 0 },
  table: { flex: 1, minHeight: 0, overflow: 'auto', ...tableFrameLook },
  row: { display: 'grid', alignItems: 'center', gap: '8px', padding: '4px 10px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, minHeight: '32px', cursor: 'default',
    userSelect: 'none', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  head: { position: 'sticky', top: 0, zIndex: 1, backgroundColor: tokens.colorNeutralBackground2, fontWeight: tokens.fontWeightSemibold, ':hover': { backgroundColor: tokens.colorNeutralBackground2 } },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  cell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  num: { textAlign: 'right' },
  muted: { color: tokens.colorNeutralForeground3 },
})

const COLS: Record<string, ColDef> = {
  title: { label: () => t('Titel'), width: 320, sort: 'title' },
  correspondent: { label: () => t('Korrespondent'), width: 180, sort: 'correspondent' },
  date: { label: () => t('Dokumentdatum'), width: 120, sort: 'date' },
  imported: { label: () => t('Importiert'), width: 140, sort: 'imported' },
  size: { label: () => t('Größe'), width: 90, sort: 'size' },
}

const fmtDay = (d: string | null) => d ? new Date(d + 'T12:00:00').toLocaleDateString(locale) : ''
const fmtImported = (d: string) => d ? new Date(d).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }) : ''
const fileName = (d: Doc) => (d.path ?? d.originalName).split('/').pop() || d.originalName

/** Dokumente aus dem DMS anfügen (Kennung maildms): Suche über Titel, Text und Angaben, Tabelle wie im Explorer, mehrere ankreuzen.
 *  Nur fertig abgelegte Dokumente des offenen Bereichs; Doppelklick fügt dieses eine an. */
export function DmsAttachDialog({ onPick, onClose }: { onPick: (items: DmsPicked[]) => void; onClose: () => void }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const [text, setText] = useState('')
  const [rows, setRows] = useState<Doc[] | null>(null)
  const [picked, setPicked] = useState<Map<number, Doc>>(new Map())
  const names = useRef<Promise<Map<string, string>> | null>(null)
  const seq = useRef(0)
  const grid = useGridColumns('mail-attach-dms', COLS, ['title', 'correspondent', 'date', 'size'])
  useEffect(() => {
    // Erst wenn das Tippen ruht; eine ältere, später eintreffende Antwort überschreibt die neuere nicht
    const n = ++seq.current
    const h = setTimeout(async () => {
      try {
        names.current ??= api.get<{ id: string; name: string }[]>('/api/dms/lists').then(l => new Map(l.map(e => [e.id, e.name]))).catch(() => new Map())
        const [r, map] = await Promise.all([api.get<{ items: Doc[] }>('/api/dms/docs?' + q({ limit: 300, sort: 'imported', desc: true, q: text.trim() || undefined })), names.current])
        if (n !== seq.current) return
        setRows(r.items.filter(d => !d.trashed && d.state === 'done' && (d.fileId !== 0 || d.mount != null))
          .map(d => ({ ...d, correspondent: d.correspondent ? map.get(d.correspondent) ?? '' : '' })))
      } catch { if (n === seq.current) setRows([]) }
    }, text ? 250 : 0)
    return () => clearTimeout(h)
  }, [text])
  const key = grid.sort?.key ?? 'imported'
  const desc = grid.sort ? grid.sort.desc : true
  const sorted = useMemo(() => [...(rows ?? [])].sort((a, b) => {
    const v = (d: Doc): string | number => key === 'size' ? d.size : key === 'date' ? `${d.docDate ?? ''}|${d.imported}` : key === 'imported' ? d.imported
      : key === 'correspondent' ? d.correspondent ?? '' : d.title || d.originalName
    const x = v(a), y = v(b)
    const c = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), locale, { sensitivity: 'base', numeric: true })
    return desc ? -c : c
  }), [rows, key, desc])
  const toggle = (d: Doc) => setPicked(p => { const n = new Map(p); if (n.has(d.id)) n.delete(d.id); else n.set(d.id, d); return n })
  const take = (list: Doc[]) => onPick(list.map(d => ({ path: `dms:${d.id}`, name: fileName(d), size: d.size })))
  const cell = (d: Doc, k: string) => k === 'correspondent' ? d.correspondent : k === 'date' ? fmtDay(d.docDate) : k === 'imported' ? fmtImported(d.imported)
    : k === 'size' ? <div className={s.num}>{fmtSize(d.size)}</div> : d.title || d.originalName
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface className={s.surface}>
        <DialogBody className={s.body}>
          <DialogTitle>{t('Aus dem DMS')}</DialogTitle>
          <DialogContent className={s.content}>
            <Input autoFocus contentBefore={<Search20Regular />} placeholder={t('Suchen')} value={text} onChange={(_, d) => setText(d.value)}
              onKeyDown={e => { if (e.key === 'Enter' && sorted.length === 1) { e.preventDefault(); take(sorted) } }} />
            <div className={s.table}>
              {rows == null ? <Spinner size="small" style={{ margin: 16 }} /> : <>
                {!mobile && <div className={`${s.row} ${s.head}`} style={{ gridTemplateColumns: grid.template, minWidth: 'max-content' }} onContextMenu={grid.openMenu}>
                  <span />{grid.cells}
                </div>}
                {!mobile && grid.columnMenu}
                {sorted.map(d => (
                  <div key={d.id} className={`${s.row} ${picked.has(d.id) ? s.sel : ''}`} onClick={() => toggle(d)} onDoubleClick={() => take([d])}
                    style={{ gridTemplateColumns: mobile ? '32px minmax(0, 1fr) auto' : grid.template, minWidth: mobile ? undefined : 'max-content' }}>
                    <Checkbox checked={picked.has(d.id)} onClick={e => e.stopPropagation()} onChange={() => toggle(d)} aria-label={d.title || d.originalName} />
                    {mobile ? <>
                      <span style={{ minWidth: 0 }}><div className={s.cell}>{d.title || d.originalName}</div>
                        <Text size={200} className={s.muted} block truncate wrap={false}>{[fmtDay(d.docDate), d.correspondent].filter(Boolean).join(' · ')}</Text></span>
                      <span className={s.muted}>{fmtSize(d.size)}</span>
                    </> : grid.cols.map(c => { const v = cell(d, c.key); return <span key={c.key} className={s.cell} title={typeof v === 'string' ? v : undefined}>{v}</span> })}
                  </div>
                ))}
                {sorted.length === 0 && <Text className={s.muted} style={{ padding: 10 }}>{t('Keine passenden Dokumente.')}</Text>}
              </>}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!picked.size} onClick={() => take([...picked.values()])}>{t('Anfügen ({n})', { n: picked.size })}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
