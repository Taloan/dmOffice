// Papierkorb und Uebersicht der eigenen Freigaben - als Hooks: Daten und Aktionen fuer das Menueband,
// dazu die Liste zum Zeichnen.
import { rightLabel, rightOf } from '../calendar/ShareDialog'
import {
  Button, Checkbox, Input, Spinner, Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Copy20Regular, Delete20Regular, Dismiss20Regular, LockClosed16Regular, People16Regular, Search20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { locale, t } from '../i18n'
import { useIsMobile } from '../shell/mobile'
import { Item, KindIcon, parentOf } from './common'
import { Link, copyText, linkStatus, linkUrl } from './Dialogs'
import { askConfirm } from '../shell/context'
import { ColDef, useGridColumns } from '../shell/GridColumns'

const useStyles = makeStyles({
  trashGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '10px', padding: '8px 12px' },
  trashTile: { position: 'relative', display: 'flex', flexDirection: 'column', alignItems: 'stretch', gap: '4px', padding: '6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  trashThumb: { aspectRatio: '4 / 3', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground3, display: 'flex', alignItems: 'center', justifyContent: 'center' },
  trashImg: { width: '100%', height: '100%', objectFit: 'cover', display: 'block' },
  trashCheck: { position: 'absolute', top: '8px', left: '8px', zIndex: 1 },
  row: { cursor: 'pointer', userSelect: 'none' },
  sel: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  muted: { color: tokens.colorNeutralForeground3 },
  empty: { color: tokens.colorNeutralForeground3, padding: '12px 4px', display: 'block' },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 0 8px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3, fontSize: '13px' },
  // Tabellen wie im Explorer (shell/GridColumns), breiter als der Platz: waagerecht scrollen
  grid: { overflowX: 'auto', fontSize: tokens.fontSizeBase300 },
  gridRow: { display: 'grid', alignItems: 'center', columnGap: '8px', minHeight: '34px', padding: '0 8px', minWidth: 'max-content', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  gridHead: { fontWeight: 600, color: tokens.colorNeutralForeground2, backgroundColor: tokens.colorNeutralBackground2, minHeight: '32px', ':hover': { backgroundColor: tokens.colorNeutralBackground2 } },
  cell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  nameCell: { display: 'flex', alignItems: 'center', gap: '8px', minWidth: 0 },
})

const placeOf = (path: string) => { const p = parentOf(path); return p ? '/' + p : t('Meine Dateien') }
const compare = (x: string | number, y: string | number) => typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), locale, { sensitivity: 'base', numeric: true })

const TRASH_COLS: Record<string, ColDef> = {
  name: { label: () => t('Name'), width: 320, sort: 'name' },
  place: { label: () => t('Ursprünglicher Ort'), width: 240, sort: 'place' },
  deleted: { label: () => t('Gelöscht'), width: 170, sort: 'deleted' },
  size: { label: () => t('Größe'), width: 100, sort: 'size' },
}
const SHARE_COLS: Record<string, ColDef> = {
  name: { label: () => t('Ordner'), width: 320, sort: 'name' },
  place: { label: () => t('Ort'), width: 220, sort: 'place' },
  members: { label: () => t('Geteilt mit'), width: 360, sort: 'members' },
}
const LINK_COLS: Record<string, ColDef> = {
  name: { label: () => t('Name'), width: 320, sort: 'name' },
  place: { label: () => t('Ort'), width: 220, sort: 'place' },
  kind: { label: () => t('Art'), width: 160, sort: 'kind' },
  expires: { label: () => t('Läuft ab'), width: 120, sort: 'expires' },
  uses: { label: () => t('Abrufe'), width: 90, sort: 'uses' },
}

// ---------------- Papierkorb ----------------

/** matches: bei einer Suche die Treffer in einem gelöschten Ordner (Pfade darin) */
export interface TrashItem { id: number; path: string; name: string; isDir: boolean; size: number; deleted: string; mime: string | null; matches?: string[] | null }

/** query: Suche im Papierkorb (Namen, Ort, Inhalt gelöschter Ordner) */
export function useTrash(active: boolean, fail: (e: unknown) => void, notify: (m: string) => void, changed: () => void, query = '') {
  const [data, setData] = useState<{ items: TrashItem[]; days: number; usage: number; query?: string } | null>(null)
  const [selected, setSelected] = useState<number[]>([])
  const load = useCallback(() => api.get<{ items: TrashItem[]; days: number; usage: number }>('/api/files/trash' + (query ? '?' + q({ q: query }) : ''))
    .then(d => setData({ ...d, query })).catch(fail), [fail, query])
  useEffect(() => { if (active) { setSelected([]); load() } }, [active, load])
  const sel = data?.items.filter(i => selected.includes(i.id)) ?? []

  const restore = async (ids = selected) => {
    if (!ids.length) return
    try {
      const r = await api.post<{ restored: string[] }>('/api/files/trash/restore', { ids })
      notify(r.restored.length === 1 ? t('Wiederhergestellt: {path}', { path: '/' + r.restored[0] }) : t('{n} wiederhergestellt', { n: r.restored.length }))
    } catch (e) { fail(e) }
    setSelected([]); load(); changed()
  }
  const purge = async (ids = selected) => {
    if (!ids.length || !await askConfirm('', ids.length === 1 ? t('Endgültig löschen? Das lässt sich nicht rückgängig machen.') : t('{n} Elemente endgültig löschen? Das lässt sich nicht rückgängig machen.', { n: ids.length }))) return
    try { await api.post('/api/files/trash/delete', { ids }) } catch (e) { fail(e) }
    setSelected([]); load(); changed()
  }
  const empty = async () => {
    if (!data?.items.length || !await askConfirm('', t('Papierkorb leeren? Alles darin wird endgültig gelöscht.'))) return
    try { await api.post('/api/files/trash/empty') } catch (e) { fail(e) }
    setSelected([]); load(); changed()
  }
  return { data, selected, setSelected, sel, restore, purge, empty, load }
}

/** Papierkorb als Liste oder (Ansicht → Kacheln) mit Vorschaubildern - wie die anderen Bereiche. */
/** search/onSearch: Suchfeld (die Suche selbst läuft in useTrash) */
export function TrashList({ trash, tiles = false, search, onSearch }: { trash: ReturnType<typeof useTrash>; tiles?: boolean; search?: string; onSearch?: (v: string) => void }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const { data, selected, setSelected } = trash
  const grid = useGridColumns('files-trash', TRASH_COLS, ['name', 'place', 'deleted', 'size'], '24px', 'minmax(130px, 1fr)')
  if (!data) return <Spinner />
  const toggle = (id: number) => setSelected(sl => sl.includes(id) ? sl.filter(x => x !== id) : [...sl, id])
  const valueOf = (i: TrashItem): string | number => grid.sort?.key === 'place' ? placeOf(i.path) : grid.sort?.key === 'deleted' ? i.deleted : grid.sort?.key === 'size' ? i.size : i.name
  const rows = grid.sort ? [...data.items].sort((a, b) => (grid.sort!.desc ? -1 : 1) * compare(valueOf(a), valueOf(b))) : data.items
  return (
    <>
      <div className={s.bar}>
        <span>{data.days > 0 ? t('Wird nach {days} Tagen automatisch gelöscht.', { days: data.days }) : t('Wird nicht automatisch geleert.')}</span>
        <span>·</span>
        <a href="#/settings/file-trash">{t('Einstellungen')}</a>
        <span>· {t('Belegt: {used}', { used: fmtSize(data.usage) })}</span>
        {onSearch && <Input size="small" style={{ marginLeft: 'auto', width: 260 }} value={search ?? ''} onChange={(_, d) => onSearch(d.value)}
          placeholder={t('Im Papierkorb suchen')} aria-label={t('Im Papierkorb suchen')} contentBefore={<Search20Regular />}
          onKeyDown={e => { if (e.key === 'Escape') onSearch('') }}
          contentAfter={search ? <Button size="small" appearance="transparent" icon={<Dismiss20Regular />} onClick={() => onSearch('')} aria-label={t('Suche löschen')} /> : undefined} />}
      </div>
      {data.items.length === 0 ? <Text className={s.empty}>{data.query ? t('Nichts gefunden für „{q}“.', { q: data.query }) : t('Der Papierkorb ist leer.')}</Text> : tiles ? (
        <div className={s.trashGrid}>
          {data.items.map(i => {
            const isSel = selected.includes(i.id)
            return (
              <div key={i.id} className={mergeClasses(s.trashTile, isSel && s.sel)} aria-selected={isSel} title={`${i.name}\n${placeOf(i.path)} · ${new Date(i.deleted).toLocaleString(locale)} · ${fmtSize(i.size)}`}
                onClick={e => e.ctrlKey || e.metaKey || mobile ? toggle(i.id) : setSelected([i.id])}>
                <span className={s.trashCheck} onClick={e => { e.stopPropagation(); toggle(i.id) }}><Checkbox checked={isSel} aria-label={t('Auswählen')} /></span>
                <div className={s.trashThumb}><TrashThumb item={i} /></div>
                <Text size={200} truncate wrap={false} block>{i.name}</Text>
                {i.matches && i.matches.length > 0 && <Text size={100} className={s.muted} truncate wrap={false} block title={i.matches.join(', ')}>{t('enthält {names}', { names: i.matches.map(m => m.split('/').pop()).join(', ') })}</Text>}
                <Button size="small" appearance="subtle" onClick={e => { e.stopPropagation(); trash.restore([i.id]) }}>{t('Wiederherstellen')}</Button>
              </div>
            )
          })}
        </div>
      ) : mobile ? (
        <Table size="small">
          <TableHeader>
            <TableRow>
              <TableHeaderCell style={{ width: 36 }}>
                <Checkbox checked={selected.length === 0 ? false : selected.length === data.items.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
                  onChange={() => setSelected(selected.length === data.items.length ? [] : data.items.map(i => i.id))} />
              </TableHeaderCell>
              <TableHeaderCell>{t('Name')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 44 }} />
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.items.map(i => {
              const isSel = selected.includes(i.id)
              return (
                <TableRow key={i.id} className={mergeClasses(s.row, isSel && s.sel)} aria-selected={isSel} onClick={() => toggle(i.id)}>
                  <TableCell onClick={e => { e.stopPropagation(); toggle(i.id) }}><Checkbox checked={isSel} aria-label={t('Auswählen')} /></TableCell>
                  <TableCell><TableCellLayout media={<KindIcon item={i} />}
                    description={[`${placeOf(i.path)} · ${new Date(i.deleted).toLocaleString(locale)}`,
                      i.matches?.length ? t('enthält {names}', { names: i.matches.join(', ') }) : ''].filter(Boolean).join(' · ')}>{i.name}</TableCellLayout></TableCell>
                  <TableCell onClick={e => e.stopPropagation()}>
                    <Button size="small" appearance="subtle" aria-label={t('Wiederherstellen')} onClick={() => trash.restore([i.id])}>↺</Button>
                  </TableCell>
                </TableRow>
              )
            })}
          </TableBody>
        </Table>
      ) : (
        <div className={s.grid} role="table">
          <div className={mergeClasses(s.gridRow, s.gridHead)} role="row" style={{ gridTemplateColumns: grid.template }} onContextMenu={grid.openMenu}>
            <Checkbox checked={selected.length === 0 ? false : selected.length === data.items.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
              onChange={() => setSelected(selected.length === data.items.length ? [] : data.items.map(i => i.id))} />
            {grid.cells}
            <span />
          </div>
          {grid.columnMenu}
          {rows.map(i => {
            const isSel = selected.includes(i.id)
            return (
              <div key={i.id} role="row" className={mergeClasses(s.gridRow, s.row, isSel && s.sel)} aria-selected={isSel} style={{ gridTemplateColumns: grid.template }}
                onClick={e => e.ctrlKey || e.metaKey ? toggle(i.id) : setSelected([i.id])}>
                <span onClick={e => { e.stopPropagation(); toggle(i.id) }}><Checkbox checked={isSel} aria-label={t('Auswählen')} /></span>
                {grid.cols.map(c => {
                  switch (c.key) {
                    case 'place': return <span key={c.key} className={mergeClasses(s.cell, s.muted)} title={placeOf(i.path)}>{placeOf(i.path)}</span>
                    case 'deleted': return <span key={c.key} className={s.cell}>{new Date(i.deleted).toLocaleString(locale)}</span>
                    case 'size': return <span key={c.key} className={s.cell}>{fmtSize(i.size)}</span>
                    default: return <span key={c.key} className={s.nameCell} title={i.matches?.length ? t('enthält {names}', { names: i.matches.join(', ') }) : i.name}><KindIcon item={i} /><span className={s.cell}>{i.name}</span></span>
                  }
                })}
                <span onClick={e => e.stopPropagation()}><Button size="small" appearance="subtle" onClick={() => trash.restore([i.id])}>{t('Wiederherstellen')}</Button></span>
              </div>
            )
          })}
        </div>
      )}
    </>
  )
}

/** Vorschaubild eines gelöschten Fotos - nur, wenn es vor dem Löschen schon eines gab; sonst das Symbol. */
function TrashThumb({ item }: { item: TrashItem }) {
  const s = useStyles()
  const [failed, setFailed] = useState(false)
  const image = (item.mime ?? '').startsWith('image/') || (item.mime ?? '').startsWith('video/') || /\.(heic|arw|cr[23]|nef|raf|dng|orf|rw2)$/i.test(item.name)
  if (!image || failed || item.isDir) return <KindIcon item={item} large />
  return <img className={s.trashImg} src={`/api/files/trash/${item.id}/thumb?size=256`} alt="" loading="lazy" onError={() => setFailed(true)} />
}

// ---------------- Freigaben ----------------

/** Ein mit Personen geteilter Ordner (eigene Freigabe). */
export interface PeopleShare { id: number; item: Item; members: { id: string; login: string; displayName: string; readOnly: boolean }[] }

/** Meine Freigaben: Links und Personen. */
export function useLinks(active: boolean, fail: (e: unknown) => void) {
  const [items, setItems] = useState<Link[] | null>(null)
  const [people, setPeople] = useState<PeopleShare[] | null>(null)
  const load = useCallback(() => {
    api.get<{ items: Link[] }>('/api/files/links').then(d => setItems(d.items)).catch(fail)
    api.get<{ items: PeopleShare[] }>('/api/files/shares').then(d => setPeople(d.items)).catch(fail)
  }, [fail])
  useEffect(() => { if (active) load() }, [active, load])
  return { items, people, load }
}

export function LinksList({ links, onOpen, onManage, notify, fail }: {
  links: ReturnType<typeof useLinks>; onOpen: (path: string, isDir: boolean) => void; onManage: (l: Link) => void
  notify: (m: string) => void; fail: (e: unknown) => void
}) {
  const s = useStyles()
  const mobile = useIsMobile()
  const shareGrid = useGridColumns('files-shared-people', SHARE_COLS, ['name', 'place', 'members'], '', 'minmax(0, 1fr)')
  const linkGrid = useGridColumns('files-shared-links', LINK_COLS, ['name', 'place', 'kind', 'expires', 'uses'], '', 'minmax(80px, 1fr)')
  if (!links.items || !links.people) return <Spinner />
  if (!links.items.length && !links.people.length) return <Text className={s.empty}>{t('Du hast noch nichts geteilt. Rechtsklick auf eine Datei oder einen Ordner → „Teilen“.')}</Text>
  const revoke = async (l: Link) => {
    if (!await askConfirm('', t('Link widerrufen? Wer ihn hat, kommt dann nicht mehr an die Inhalte.'))) return
    try { await api.del('/api/files/links/' + l.id); links.load() } catch (e) { fail(e) }
  }
  const membersOf = (p: PeopleShare) => p.members.map(m => `${m.displayName} (${rightLabel()[rightOf(m)]})`).join(', ')
  const shareValue = (p: PeopleShare): string | number => shareGrid.sort?.key === 'place' ? placeOf(p.item.path) : shareGrid.sort?.key === 'members' ? membersOf(p) : p.item.name
  const linkValue = (l: Link): string | number => {
    switch (linkGrid.sort?.key) {
      case 'place': return l.item ? placeOf(l.item.path) : ''
      case 'kind': return linkStatus(l.mode)
      case 'expires': return l.expires ?? ''
      case 'uses': return l.downloads
      default: return l.item?.name ?? ''
    }
  }
  const sharesShown = shareGrid.sort ? [...links.people].sort((a, b) => (shareGrid.sort!.desc ? -1 : 1) * compare(shareValue(a), shareValue(b))) : links.people
  const linksShown = linkGrid.sort ? [...links.items].sort((a, b) => (linkGrid.sort!.desc ? -1 : 1) * compare(linkValue(a), linkValue(b))) : links.items
  const linkName = (l: Link) => {
    const it = l.item
    return <>
      {it ? <a href="#" onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(it.path, it.isDir) }}>{it.name || t('Meine Dateien')}</a> : t('(gelöscht)')}
      {l.hasPassword && <LockClosed16Regular style={{ marginLeft: 4, verticalAlign: 'middle' }} />}
    </>
  }
  const linkActions = (l: Link) => <>
    <Tooltip content={t('Link kopieren')} relationship="label">
      <Button size="small" appearance="subtle" icon={<Copy20Regular />} onClick={async () => { if (await copyText(linkUrl(l))) notify(t('Link kopiert')) }} />
    </Tooltip>
    <Tooltip content={t('Widerrufen')} relationship="label">
      <Button size="small" appearance="subtle" icon={<Delete20Regular />} onClick={() => revoke(l)} />
    </Tooltip>
  </>
  if (!mobile) return (<>
    {links.people.length > 0 && <>
      <Text weight="semibold" block style={{ padding: '4px 0' }}>{t('Mit Personen geteilt')}</Text>
      <div className={s.grid} role="table">
        <div className={mergeClasses(s.gridRow, s.gridHead)} role="row" style={{ gridTemplateColumns: shareGrid.template }} onContextMenu={shareGrid.openMenu}>{shareGrid.cells}<span /></div>
        {shareGrid.columnMenu}
        {sharesShown.map(p => (
          <div key={p.id} role="row" className={mergeClasses(s.gridRow, s.row)} style={{ gridTemplateColumns: shareGrid.template }} onClick={() => onManage({ item: p.item } as Link)}>
            {shareGrid.cols.map(c => {
              switch (c.key) {
                case 'place': return <span key={c.key} className={mergeClasses(s.cell, s.muted)}>{placeOf(p.item.path)}</span>
                case 'members': return <span key={c.key} className={s.cell} title={membersOf(p)}>{membersOf(p)}</span>
                default: return <span key={c.key} className={s.nameCell}><KindIcon item={p.item} /><span className={s.cell}>
                  <a href="#" onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(p.item.path, true) }}>{p.item.name}</a></span><People16Regular /></span>
              }
            })}
            <span />
          </div>
        ))}
      </div>
    </>}
    {links.items.length > 0 && <>
      {links.people.length > 0 && <Text weight="semibold" block style={{ padding: '12px 0 4px' }}>{t('Per Link geteilt')}</Text>}
      <div className={s.grid} role="table">
        <div className={mergeClasses(s.gridRow, s.gridHead)} role="row" style={{ gridTemplateColumns: linkGrid.template }} onContextMenu={linkGrid.openMenu}>{linkGrid.cells}<span /></div>
        {linkGrid.columnMenu}
        {linksShown.map(l => {
          const it = l.item
          return (
            <div key={l.id} role="row" className={mergeClasses(s.gridRow, s.row)} style={{ gridTemplateColumns: linkGrid.template }} onClick={() => it && onManage(l)}>
              {linkGrid.cols.map(c => {
                switch (c.key) {
                  case 'place': return <span key={c.key} className={mergeClasses(s.cell, s.muted)}>{it ? placeOf(it.path) : t('Im Papierkorb – Link ruht')}</span>
                  case 'kind': return <span key={c.key} className={s.cell}>{linkStatus(l.mode)}</span>
                  case 'expires': return <span key={c.key} className={s.cell} style={{ color: l.expired ? tokens.colorPaletteRedForeground1 : undefined }}>{l.expires ? new Date(l.expires).toLocaleDateString(locale) : '–'}{l.expired ? ' ' + t('(abgelaufen)') : ''}</span>
                  case 'uses': return <span key={c.key} className={s.cell}>{l.downloads}</span>
                  default: return <span key={c.key} className={s.nameCell}>{it ? <KindIcon item={it} /> : <Delete20Regular />}<span className={s.cell}>{linkName(l)}</span></span>
                }
              })}
              <span onClick={e => e.stopPropagation()} style={{ whiteSpace: 'nowrap' }}>{linkActions(l)}</span>
            </div>
          )
        })}
      </div>
    </>}
  </>)
  return (<>
    {links.people.length > 0 && <>
      <Text weight="semibold" block style={{ padding: '4px 0' }}>{t('Mit Personen geteilt')}</Text>
      <Table size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('Ordner')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {links.people.map(p => (
            <TableRow key={p.id} className={s.row} onClick={() => onManage({ item: p.item } as Link)}>
              <TableCell>
                <TableCellLayout media={<KindIcon item={p.item} />} description={p.members.map(m => m.displayName).join(', ')}>
                  <a href="#" onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(p.item.path, true) }}>{p.item.name}</a>
                  <People16Regular style={{ marginLeft: 4, verticalAlign: 'middle' }} />
                </TableCellLayout>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </>}
    {links.items.length > 0 && <>
    {links.people.length > 0 && <Text weight="semibold" block style={{ padding: '12px 0 4px' }}>{t('Per Link geteilt')}</Text>}
    <Table size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>{t('Name')}</TableHeaderCell>
          <TableHeaderCell style={{ width: 80 }} />
        </TableRow>
      </TableHeader>
      <TableBody>
        {links.items.map(l => {
          const it = l.item
          const desc = [linkStatus(l.mode), l.expires ? t('bis {date}', { date: new Date(l.expires).toLocaleDateString(locale) }) : null, l.expired ? t('abgelaufen') : null].filter(Boolean).join(' · ')
          return (
            <TableRow key={l.id} className={s.row} onClick={() => it && onManage(l)}>
              <TableCell>
                <TableCellLayout media={it ? <KindIcon item={it} /> : <Delete20Regular />}
                  description={desc}>
                  {linkName(l)}
                </TableCellLayout>
              </TableCell>
              <TableCell onClick={e => e.stopPropagation()}>{linkActions(l)}</TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
    </>}
  </>)
}
