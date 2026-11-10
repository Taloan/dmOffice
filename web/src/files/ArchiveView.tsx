// Archiv als Ordner (ZIP, 7z, RAR, TAR, TAR.GZ): blättern, Dateien öffnen, entpacken; ZIPs auch ändern. Änderungen sammeln
// sich auf dem Server, bis „Speichern“ ein neues ZIP schreibt (das alte bleibt als Version) oder „Verwerfen“ sie löscht.
import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Checkbox, Field, Input, makeStyles, Menu, MenuDivider, MenuItem, MenuItemCheckbox,
  MenuList, MenuPopover, mergeClasses, MessageBar, MessageBarActions, MessageBarBody, Spinner, Table, TableBody, TableCell, TableCellLayout,
  TableHeader, TableHeaderCell, TableRow, Text, tokens, Toolbar, ToolbarButton,
} from '@fluentui/react-components'
import {
  ArrowClockwise20Regular, ArrowDownload20Regular, ArrowUpload20Regular, Delete20Regular, FolderAdd20Regular, FolderArrowUp20Regular,
  LockClosed20Regular, Open20Regular, Rename20Regular, Save20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, fmtSize, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { askConfirm, askText, ContextMenu, isTyping, useMultiSelect } from '../shell/context'
import { ColHead } from './FileColumns'
import { fmtWhen, Item, KindIcon, kindLabel, kindOf, Source, viewKind } from './common'
import { Viewer } from './Viewer'
import { entriesOf, readEntries, Upload } from './FilesView'

interface Row { path: string; name: string; isDir: boolean; size: number; packed: number; modified: string | null; added: boolean; changed: boolean; mime: string | null }
interface Listing {
  path: string; name: string; format: string; label: string; encrypted: boolean; unlocked: boolean; readOnly: string | null
  bomb: boolean; unsafe: number; total: number; count: number; dir: string; items: Row[]
  pending: { changes: number; started: string; touched: string; expires: string; stale: boolean } | null
}

/** Endungen, die sich als Archiv öffnen lassen. */
export function isArchive(name: string): boolean {
  const n = name.toLowerCase()
  return /\.(zip|7z|rar|tar|tgz)$/.test(n) || n.endsWith('.tar.gz')
}

type ArcCol = 'modified' | 'size' | 'packed' | 'kind'
const ARC_COLS: Record<ArcCol, { label: () => string; width: number }> = {
  modified: { label: () => t('Geändert'), width: 160 },
  size: { label: () => t('Größe'), width: 100 },
  packed: { label: () => t('Gepackt'), width: 100 },
  kind: { label: () => t('Art'), width: 110 },
}
interface ArcColPref { key: ArcCol; width: number }
const DEFAULT_ARC_COLS: ArcColPref[] = [{ key: 'modified', width: 160 }, { key: 'kind', width: 110 }, { key: 'size', width: 100 }, { key: 'packed', width: 100 }]
const COLS_KEY = 'dm.archiveCols'
function loadCols(): { cols: ArcColPref[]; name?: number } {
  try {
    const v = JSON.parse(localStorage.getItem(COLS_KEY) ?? 'null')
    if (v && Array.isArray(v.cols)) return { cols: v.cols.filter((c: ArcColPref) => c.key in ARC_COLS), name: v.name }
  } catch { /* gesperrt oder kaputt: Standard */ }
  return { cols: DEFAULT_ARC_COLS }
}
function storeCols(v: { cols: ArcColPref[]; name?: number }) { try { localStorage.setItem(COLS_KEY, JSON.stringify(v)) } catch { /* egal */ } }

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 },
  head: { display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 16px', flexWrap: 'wrap' },
  bars: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '0 16px 6px' },
  list: { flex: 1, overflow: 'auto', padding: '0 16px 16px', outline: 'none' },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-6px' },
  table: { tableLayout: 'fixed' },
  row: { cursor: 'default', userSelect: 'none' },
  selected: { backgroundColor: tokens.colorNeutralBackground1Selected, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  added: { color: tokens.colorPaletteGreenForeground1, fontSize: '12px', marginLeft: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
  lock: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '420px', padding: '24px 16px' },
})

const join = (a: string, b: string) => (a ? a + '/' + b : b)
const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')
const nameOf = (p: string) => p.slice(p.lastIndexOf('/') + 1)

/**
 * Ein Archiv im Dateibrowser. file: Pfad des Archivs in der Ablage, dir: Ordner darin. onLeave: zurück in einen Ordner der Ablage.
 * onPending meldet offene Änderungen (Anzahl, 0 = keine) - FilesView fragt damit beim Verlassen nach.
 */
export function ArchiveView({ file, dir, onDir, onLeave, onPending }: {
  file: string; dir: string; onDir: (d: string) => void; onLeave: (folder: string) => void; onPending: (n: number) => void
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<Listing | null>(null)
  const [locked, setLocked] = useState<{ wrong: boolean } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [viewer, setViewer] = useState<{ list: Item[]; index: number } | null>(null)
  const [stamp, setStamp] = useState(0)
  const fileInput = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  const load = useCallback(() => {
    api.get<Listing>('/api/files/archive?' + q({ path: file, dir }))
      .then(r => { setData(r); setLocked(null); setError(null); onPending(r.pending?.changes ?? 0); setStamp(n => n + 1) })
      .catch(e => {
        if (e instanceof ApiError && e.data?.password) { setLocked({ wrong: false }); return }
        // Ordner gibt es (nach Umbenennen) nicht mehr: nach oben
        if (e instanceof ApiError && e.status === 404 && dir) { onDir(parentOf(dir)); return }
        // Nicht lesbar (beschädigt, ZIP-Bombe im Verzeichnis …): Meldung statt endlosem Warten
        if (!data) setError(e instanceof Error ? e.message : String(e)); else fail(e)
      })
  }, [file, dir, data]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { load() }, [file, dir]) // eslint-disable-line react-hooks/exhaustive-deps

  // Offene Änderungen: Schließen des Tabs nachfragen (sie liegen auf dem Server, gehen also nicht verloren - aber vergessen werden sollen sie nicht)
  const pending = data?.pending?.changes ?? 0
  useEffect(() => {
    if (!pending) return
    const on = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', on)
    return () => window.removeEventListener('beforeunload', on)
  }, [pending])

  // Fehler mit Passwort-Kennzeichen: Archiv ist (wieder) gesperrt, etwa nach einem Neustart des Servers
  const failed = (e: unknown) => {
    if (e instanceof ApiError && e.data?.password) { setLocked({ wrong: false }); return }
    fail(e)
  }
  const unlock = async () => {
    setBusy(true)
    try { await api.post('/api/files/archive/unlock', { path: file, password }); setPassword(''); load() }
    catch (e) { if (e instanceof ApiError && e.data?.password) setLocked({ wrong: true }); else fail(e) }
    finally { setBusy(false) }
  }

  // ---------------- Sortieren und Spalten ----------------
  const [sort, setSort] = useState<{ key: 'name' | ArcCol; desc: boolean }>({ key: 'name', desc: false })
  const [cfg, setCfg] = useState(loadCols)
  const [colMenu, setColMenu] = useState<{ x: number; y: number } | null>(null)
  const saveCfg = (v: { cols: ArcColPref[]; name?: number }) => { setCfg(v); storeCols(v) }
  const rows = useMemo(() => {
    const d = sort.desc ? -1 : 1
    const cmp = (a: Row, b: Row) => sort.key === 'modified' ? (a.modified ?? '').localeCompare(b.modified ?? '')
      : sort.key === 'size' ? a.size - b.size : sort.key === 'packed' ? a.packed - b.packed
      : sort.key === 'kind' ? kindLabel(kindOf(a)).localeCompare(kindLabel(kindOf(b)), locale)
      : a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })
    return [...(data?.items ?? [])].sort((a, b) => Number(b.isDir) - Number(a.isDir) || d * cmp(a, b) || a.name.localeCompare(b.name, locale))
  }, [data, sort])
  const sortBy = (key: 'name' | ArcCol) => setSort(x => ({ key, desc: x.key === key ? !x.desc : false }))
  const moveCol = (from: ArcCol, to: ArcCol, after: boolean) => {
    const f = cfg.cols.find(c => c.key === from), rest = cfg.cols.filter(c => c.key !== from)
    const at = rest.findIndex(c => c.key === to) + (after ? 1 : 0)
    if (f && rest.some(c => c.key === to)) saveCfg({ ...cfg, cols: [...rest.slice(0, at), f, ...rest.slice(at)] })
  }
  const width = (k: ArcCol | 'name', w: number, store: boolean) => {
    const next = k === 'name' ? { ...cfg, name: w || undefined } : { ...cfg, cols: cfg.cols.map(c => (c.key === k ? { ...c, width: w || ARC_COLS[k].width } : c)) }
    if (store) saveCfg(next); else setCfg(next)
  }

  // ---------------- Auswahl ----------------
  const { selected, setSelected, click } = useMultiSelect<string>(rows.map(r => r.path))
  useEffect(() => setSelected([]), [file, dir]) // eslint-disable-line react-hooks/exhaustive-deps
  const picked = rows.filter(r => selected.includes(r.path))
  const canEdit = !!data && !data.readOnly

  // ---------------- Öffnen ----------------
  const source: Source = useMemo(() => ({
    download: (i, inline) => '/api/files/archive/entry?' + q({ path: file, entry: i.path, inline: inline || undefined, v: i.etag }),
    thumb: (i, size = 256) => '/api/files/archive/thumb?' + q({ path: file, entry: i.path, size, v: i.etag }),
  }), [file])
  const asItem = (r: Row): Item => ({ path: r.path, name: r.name, isDir: r.isDir, size: r.size, mime: r.mime, modified: r.modified ?? '', etag: `${stamp}` })
  const download = (r: Row) => {
    const a = document.createElement('a')
    a.href = source.download(asItem(r))
    a.download = r.name
    a.click()
  }
  const open = (r: Row) => {
    if (r.isDir) { onDir(r.path); return }
    if (data?.encrypted && !data.unlocked) { setLocked({ wrong: false }); return }
    if (data?.bomb) { fail(new Error(t('Das Archiv wird nicht ausgepackt: Die angegebenen Größen sehen nach einer ZIP-Bombe aus.'))); return }
    const it = asItem(r)
    if (!viewKind(it)) { download(r); return }
    const list = rows.filter(x => !x.isDir).map(asItem).filter(x => viewKind(x))
    setViewer({ list, index: Math.max(0, list.findIndex(x => x.path === r.path)) })
  }

  // ---------------- Ändern ----------------
  const run = async (f: () => Promise<unknown>) => {
    setBusy(true)
    try { await f(); load() } catch (e) { failed(e) } finally { setBusy(false) }
  }
  const upload = async (files: Upload[]) => {
    if (!files.length || !canEdit) return
    const names = new Set(rows.map(r => r.name))
    const clash = files.filter(f => !f.rel.includes('/') && names.has(f.rel))
    let conflict: string | undefined
    if (clash.length) {
      if (!await askConfirm('', clash.length === 1 ? t('„{name}“ gibt es im Archiv schon. Ersetzen?', { name: clash[0].rel })
        : t('{n} Dateien gibt es im Archiv schon. Ersetzen?', { n: clash.length }), t('Ersetzen'))) return
      conflict = 'replace'
    }
    await run(async () => {
      const form = new FormData()
      for (const f of files) form.append('file', f.file, f.rel)
      await api.form('/api/files/archive/upload?' + q({ path: file, dir, conflict }), form)
      notify(t('Zum Archiv hinzugefügt'), t('Gespeichert wird beim Verlassen oder mit „Speichern“.'))
    })
  }
  const mkdir = async () => {
    const n = await askText(t('Neuer Ordner'))
    if (n) await run(() => api.post('/api/files/archive/mkdir', { path: file, entry: join(dir, n) }))
  }
  const rename = async (r: Row) => {
    const n = await askText(t('Umbenennen'), r.name)
    if (n && n !== r.name) await run(() => api.post('/api/files/archive/move', { path: file, from: r.path, to: join(dir, n) }))
  }
  const remove = async (list: Row[]) => {
    if (!list.length) return
    const text = list.length === 1 ? t('„{name}“ aus dem Archiv entfernen?', { name: list[0].name }) : t('{n} Einträge aus dem Archiv entfernen?', { n: list.length })
    if (await askConfirm('', text, t('Entfernen'))) await run(() => api.post('/api/files/archive/delete', { path: file, entries: list.map(r => r.path) }))
  }
  const extract = async (list: Row[]) => {
    const stem = nameOf(file).replace(/\.(tar\.gz|tgz|zip|7z|rar|tar)$/i, '')
    const target = await askText(t('Entpacken nach'), join(parentOf(file), stem), t('Ordner in der Dateiablage'), t('Entpacken'))
    if (!target) return
    setBusy(true)
    try {
      const r = await api.post<{ extracted: number; failed: { entry: string; error: string }[]; target: string }>('/api/files/archive/extract',
        { path: file, dir, entries: list.map(x => x.path), target })
      if (r.failed.length) notify(t('{n} Dateien entpackt, {m} nicht', { n: r.extracted, m: r.failed.length }), r.failed.slice(0, 3).map(f => `${nameOf(f.entry)}: ${f.error}`).join('\n'), 'warning')
      else notify(t('{n} Dateien entpackt', { n: r.extracted }), r.target)
    } catch (e) { failed(e) } finally { setBusy(false) }
  }
  const save = async (copy = false) => {
    setBusy(true)
    try {
      const r = await api.post<{ path: string; saved: boolean }>('/api/files/archive/save', { path: file, copy })
      notify(t('Archiv gespeichert'), copy ? r.path : t('Der vorige Stand liegt unter Versionen.'))
      onPending(0)
      if (copy) onLeave(parentOf(r.path)); else load()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.data?.conflict && !copy) {
        if (await askConfirm('', e.message, t('Als Kopie speichern'))) await save(true)
      } else failed(e)
    } finally { setBusy(false) }
  }
  const discard = async () => {
    if (!await askConfirm('', t('Alle Änderungen im Archiv verwerfen?'), t('Verwerfen'))) return
    await run(() => api.post('/api/files/archive/discard', { path: file }))
  }
  const saveText = async (i: Item, text: string): Promise<Item> => {
    const r = await api.post<Row>('/api/files/archive/text', { path: file, entry: i.path, text, create: false })
    load()
    return { ...i, size: r.size, etag: `${Date.now()}` }
  }

  const keys = (e: React.KeyboardEvent) => {
    if (isTyping(e)) return
    if (e.key === 'Delete' && canEdit && picked.length) { e.preventDefault(); remove(picked) }
    else if (e.key === 'F2' && canEdit && picked.length === 1) { e.preventDefault(); rename(picked[0]) }
    else if (e.key === 'Enter' && picked.length === 1) { e.preventDefault(); open(picked[0]) }
    else if (e.key === 'Backspace' && dir) { e.preventDefault(); onDir(parentOf(dir)) }
    else if ((e.ctrlKey || e.metaKey) && e.key === 'a') { e.preventDefault(); setSelected(rows.map(r => r.path)) }
  }

  // ---------------- Kontextmenü ----------------
  const [ctx, setCtx] = useState<{ x: number; y: number; rows: Row[] } | null>(null)
  const menuFor = (e: React.MouseEvent, r: Row) => {
    e.preventDefault(); e.stopPropagation()
    const list = selected.includes(r.path) ? picked : [r]
    if (!selected.includes(r.path)) setSelected([r.path])
    setCtx({ x: e.clientX, y: e.clientY, rows: list })
  }
  const at = (p: { x: number; y: number }) => ({ getBoundingClientRect: () => ({ x: p.x, y: p.y, left: p.x, top: p.y, right: p.x, bottom: p.y, width: 0, height: 0, toJSON: () => '' }) }) as any

  // ---------------- Darstellung ----------------
  const folders = parentOf(file) ? parentOf(file).split('/') : []
  const inner = dir ? dir.split('/') : []
  const crumbs = (
    <Breadcrumb size="small" aria-label={t('Pfad')}>
      <BreadcrumbItem><BreadcrumbButton onClick={() => onLeave('')}>{t('Meine Dateien')}</BreadcrumbButton></BreadcrumbItem>
      {folders.map((p, n) => (
        <span key={'f' + n} style={{ display: 'contents' }}>
          <BreadcrumbDivider />
          <BreadcrumbItem><BreadcrumbButton onClick={() => onLeave(folders.slice(0, n + 1).join('/'))}>{p}</BreadcrumbButton></BreadcrumbItem>
        </span>
      ))}
      <BreadcrumbDivider />
      <BreadcrumbItem><BreadcrumbButton icon={<KindIcon item={{ name: nameOf(file), isDir: false }} />} onClick={() => onDir('')} current={!inner.length}>{nameOf(file)}</BreadcrumbButton></BreadcrumbItem>
      {inner.map((p, n) => (
        <span key={'a' + n} style={{ display: 'contents' }}>
          <BreadcrumbDivider />
          <BreadcrumbItem><BreadcrumbButton onClick={() => onDir(inner.slice(0, n + 1).join('/'))} current={n === inner.length - 1}>{p}</BreadcrumbButton></BreadcrumbItem>
        </span>
      ))}
    </Breadcrumb>
  )

  if (error && !data) return (
    <div className={s.root}>
      <div className={s.head}>{crumbs}</div>
      <div className={s.lock}>
        <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>
        <div><Button onClick={() => onLeave(parentOf(file))}>{t('Zurück')}</Button></div>
      </div>
    </div>
  )

  if (locked) return (
    <div className={s.root}>
      <div className={s.head}>{crumbs}</div>
      <form className={s.lock} onSubmit={e => { e.preventDefault(); if (password) unlock() }}>
        <Text weight="semibold"><LockClosed20Regular style={{ verticalAlign: 'text-bottom', marginRight: 6 }} />{t('Das Archiv ist verschlüsselt.')}</Text>
        <Field label={t('Passwort')} validationState={locked.wrong ? 'error' : 'none'} validationMessage={locked.wrong ? t('Das Passwort stimmt nicht.') : undefined}>
          <Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} autoFocus autoComplete="off" />
        </Field>
        <Text size={200} className={s.muted}>{t('Das Passwort bleibt nur für diese Sitzung im Arbeitsspeicher des Servers, höchstens 30 Minuten ohne Zugriff.')}</Text>
        <div style={{ display: 'flex', gap: 8 }}>
          <Button appearance="primary" type="submit" disabled={busy || !password}>{t('Öffnen')}</Button>
          <Button onClick={() => (data ? setLocked(null) : onLeave(parentOf(file)))}>{t('Abbrechen')}</Button>
        </div>
      </form>
    </div>
  )

  const shown = cfg.cols
  return (
    <div className={mergeClasses(s.root, dragging && s.drop)}
      onDragOver={e => { if (canEdit && e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
      onDragLeave={e => { if (e.currentTarget === e.target) setDragging(false) }}
      onDrop={async e => {
        if (!e.dataTransfer.types.includes('Files')) return
        e.preventDefault(); setDragging(false)
        if (!canEdit) return
        const entries = entriesOf(e.dataTransfer)
        const files = entries.length ? await readEntries(entries) : Array.from(e.dataTransfer.files).map(f => ({ file: f, rel: f.name }))
        upload(files)
      }}>
      <div className={s.head}>
        {crumbs}
        <div style={{ flex: 1 }} />
        <Toolbar size="small">
          {canEdit && <ToolbarButton icon={<ArrowUpload20Regular />} disabled={busy} onClick={() => fileInput.current?.click()}>{t('Hinzufügen')}</ToolbarButton>}
          {canEdit && <ToolbarButton icon={<FolderAdd20Regular />} disabled={busy} onClick={mkdir}>{t('Neuer Ordner')}</ToolbarButton>}
          <ToolbarButton icon={<FolderArrowUp20Regular />} disabled={busy || !data || data.bomb} onClick={() => extract(picked)}>
            {picked.length ? t('Markierte entpacken') : t('Alles entpacken')}
          </ToolbarButton>
          <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={load} aria-label={t('Aktualisieren')} title={t('Aktualisieren')} />
        </Toolbar>
        <input ref={fileInput} type="file" multiple hidden onChange={e => { if (e.target.files) upload(Array.from(e.target.files).map(f => ({ file: f, rel: f.name }))); e.target.value = '' }} />
      </div>
      <div className={s.bars}>
        {data?.pending && (
          <MessageBar intent="warning">
            <MessageBarBody>
              {data.pending.stale ? t('Änderungen im Archiv – das Archiv wurde inzwischen anderweitig geändert, Speichern legt eine Kopie an.')
                : t('Änderungen im Archiv – noch nicht gespeichert.')}{' '}
              <span className={s.muted}>{t('Ohne Speichern verworfen am {when}.', { when: fmtWhen(data.pending.expires, locale) })}</span>
            </MessageBarBody>
            <MessageBarActions>
              <Button appearance="primary" size="small" icon={<Save20Regular />} disabled={busy} onClick={() => save(data.pending!.stale)}>{t('Speichern')}</Button>
              <Button size="small" disabled={busy} onClick={discard}>{t('Verwerfen')}</Button>
            </MessageBarActions>
          </MessageBar>
        )}
        {data?.encrypted && !data.unlocked && (
          <MessageBar intent="info">
            <MessageBarBody>{t('Verschlüsselt – Namen sind lesbar, zum Öffnen, Entpacken und Ändern wird das Passwort gebraucht.')}</MessageBarBody>
            <MessageBarActions><Button size="small" icon={<LockClosed20Regular />} onClick={() => setLocked({ wrong: false })}>{t('Entsperren')}</Button></MessageBarActions>
          </MessageBar>
        )}
        {data?.readOnly && <MessageBar intent={data.bomb || data.unsafe ? 'error' : 'info'}><MessageBarBody>{data.readOnly}</MessageBarBody></MessageBar>}
        {data && data.unsafe > 0 && <MessageBar intent="error"><MessageBarBody>{t('{n} Einträge mit unzulässigen Pfaden werden nicht angezeigt und nicht entpackt.', { n: data.unsafe })}</MessageBarBody></MessageBar>}
      </div>
      <div ref={listRef} className={s.list} tabIndex={0} onKeyDown={keys} onClick={e => { if (e.target === e.currentTarget) setSelected([]) }}>
        {!data ? <Spinner style={{ marginTop: 24 }} /> : (
          <Table size="small" aria-label={nameOf(file)} className={s.table} onContextMenu={e => { if ((e.target as HTMLElement).closest('thead')) { e.preventDefault(); e.stopPropagation(); setColMenu({ x: e.clientX, y: e.clientY }) } }}>
            <TableHeader>
              <TableRow>
                {/* Kontrollkästchen wie in „Meine Dateien“: alle bzw. keine; je Zeile dazunehmen oder herausnehmen */}
                <TableHeaderCell style={{ width: 36 }}>
                  <Checkbox aria-label={t('Alle markieren')} checked={rows.length > 0 && picked.length === rows.length ? true : picked.length ? 'mixed' : false}
                    onChange={() => setSelected(picked.length === rows.length ? [] : rows.map(x => x.path))} />
                </TableHeaderCell>
                <ColHead label={t('Name')} width={cfg.name} minWidth={220} sortable sorted={sort.key === 'name'} desc={sort.desc} onSort={() => sortBy('name')}
                  onWidth={w => width('name', w, false)} onWidthDone={w => width('name', w, true)} />
                {shown.map(c => (
                  <ColHead<ArcCol> key={c.key} label={ARC_COLS[c.key].label()} width={c.width} sortable sorted={sort.key === c.key} desc={sort.desc} onSort={() => sortBy(c.key)}
                    onWidth={w => width(c.key, w, false)} onWidthDone={w => width(c.key, w, true)} moveKey={c.key} onMove={moveCol} />
                ))}
                {cfg.name ? <TableHeaderCell /> : null}
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map(r => (
                <TableRow key={r.path} className={mergeClasses(s.row, selected.includes(r.path) && s.selected)} aria-selected={selected.includes(r.path)}
                  onClick={e => click(e, r.path)} onDoubleClick={() => open(r)} onContextMenu={e => menuFor(e, r)}>
                  <TableCell onClick={e => e.stopPropagation()} onDoubleClick={e => e.stopPropagation()}>
                    <Checkbox aria-label={r.name} checked={selected.includes(r.path)}
                      onChange={(_, d) => setSelected(sel => d.checked ? [...sel.filter(x => x !== r.path), r.path] : sel.filter(x => x !== r.path))} />
                  </TableCell>
                  <TableCell>
                    <TableCellLayout truncate media={<KindIcon item={{ name: r.name, isDir: r.isDir }} />}>
                      {r.name}{(r.added || r.changed) && <span className={s.added}>{r.changed ? t('geändert') : t('neu')}</span>}
                    </TableCellLayout>
                  </TableCell>
                  {shown.map(c => (
                    <TableCell key={c.key}>
                      {c.key === 'modified' ? (r.modified ? fmtWhen(r.modified, locale) : '')
                        : c.key === 'size' ? (r.isDir ? '' : fmtSize(r.size))
                        : c.key === 'packed' ? (r.isDir || !r.packed ? '' : fmtSize(r.packed))
                        : kindLabel(kindOf(r))}
                    </TableCell>
                  ))}
                  {cfg.name ? <TableCell /> : null}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {data && !rows.length && <Text className={s.muted} style={{ display: 'block', padding: 16 }}>{canEdit ? t('Leer. Dateien hierher ziehen oder „Hinzufügen“.') : t('Leer.')}</Text>}
      </div>

      {/* Spalten wählen (Rechtsklick auf die Kopfzeile) */}
      {colMenu && <Menu key={`${colMenu.x},${colMenu.y}`} open onOpenChange={(_, d) => { if (!d.open) setColMenu(null) }} positioning={{ target: at(colMenu), position: 'below', align: 'start', offset: 2 }}>
        <MenuPopover>
          <MenuList checkedValues={{ cols: cfg.cols.map(c => c.key) }}>
            {(Object.keys(ARC_COLS) as ArcCol[]).map(k => (
              <MenuItemCheckbox key={k} name="cols" value={k} onClick={() => saveCfg({ ...cfg, cols: cfg.cols.some(c => c.key === k) ? cfg.cols.filter(c => c.key !== k) : [...cfg.cols, { key: k, width: ARC_COLS[k].width }] })}>
                {ARC_COLS[k].label()}
              </MenuItemCheckbox>
            ))}
            <MenuDivider />
            <MenuItem onClick={() => saveCfg({ cols: DEFAULT_ARC_COLS })}>{t('Standard wiederherstellen')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>}

      {/* Kontextmenü der Einträge */}
      <ContextMenu at={ctx} onClose={() => setCtx(null)}>
        {ctx && <>
          {ctx.rows.length === 1 && <MenuItem icon={<Open20Regular />} onClick={() => open(ctx.rows[0])}>{t('Öffnen')}</MenuItem>}
          {ctx.rows.length === 1 && !ctx.rows[0].isDir && !data?.bomb && <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(ctx.rows[0])}>{t('Herunterladen')}</MenuItem>}
          {!data?.bomb && <MenuItem icon={<FolderArrowUp20Regular />} onClick={() => extract(ctx.rows)}>{t('Entpacken nach …')}</MenuItem>}
          {canEdit && ctx.rows.length === 1 && <MenuItem icon={<Rename20Regular />} onClick={() => rename(ctx.rows[0])}>{t('Umbenennen')}</MenuItem>}
          {canEdit && <><MenuDivider /><MenuItem icon={<Delete20Regular />} onClick={() => remove(ctx.rows)}>{t('Aus dem Archiv entfernen')}</MenuItem></>}
        </>}
      </ContextMenu>

      {viewer && (
        <Viewer items={viewer.list} index={viewer.index} source={source} onIndex={i => setViewer(v => v && { ...v, index: i })}
          onClose={() => { setViewer(null); listRef.current?.focus() }}
          onDownload={i => { const r = rows.find(x => x.path === i.path); if (r) download(r) }}
          onSave={canEdit ? saveText : undefined} />
      )}
    </div>
  )
}
