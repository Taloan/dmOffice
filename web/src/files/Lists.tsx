// Papierkorb und Uebersicht der eigenen Freigaben - als Hooks: Daten und Aktionen fuer das Menueband,
// dazu die Liste zum Zeichnen.
import {
  Button, Checkbox, Spinner, Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Copy20Regular, Delete20Regular, LockClosed16Regular, People16Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { locale, t } from '../i18n'
import { useIsMobile } from '../shell/mobile'
import { Item, KindIcon, parentOf } from './common'
import { Link, copyText, linkStatus, linkUrl } from './Dialogs'

const useStyles = makeStyles({
  row: { cursor: 'pointer', userSelect: 'none' },
  sel: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  muted: { color: tokens.colorNeutralForeground3 },
  empty: { color: tokens.colorNeutralForeground3, padding: '12px 4px', display: 'block' },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 0 8px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3, fontSize: '13px' },
})

// ---------------- Papierkorb ----------------

export interface TrashItem { id: number; path: string; name: string; isDir: boolean; size: number; deleted: string; mime: string | null }

export function useTrash(active: boolean, fail: (e: unknown) => void, notify: (m: string) => void, changed: () => void) {
  const [data, setData] = useState<{ items: TrashItem[]; days: number; usage: number } | null>(null)
  const [selected, setSelected] = useState<number[]>([])
  const load = useCallback(() => api.get('/api/files/trash').then(setData).catch(fail), [fail])
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
    if (!ids.length || !confirm(ids.length === 1 ? t('Endgültig löschen? Das lässt sich nicht rückgängig machen.') : t('{n} Elemente endgültig löschen? Das lässt sich nicht rückgängig machen.', { n: ids.length }))) return
    try { await api.post('/api/files/trash/delete', { ids }) } catch (e) { fail(e) }
    setSelected([]); load(); changed()
  }
  const empty = async () => {
    if (!data?.items.length || !confirm(t('Papierkorb leeren? Alles darin wird endgültig gelöscht.'))) return
    try { await api.post('/api/files/trash/empty') } catch (e) { fail(e) }
    setSelected([]); load(); changed()
  }
  return { data, selected, setSelected, sel, restore, purge, empty, load }
}

export function TrashList({ trash }: { trash: ReturnType<typeof useTrash> }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const { data, selected, setSelected } = trash
  if (!data) return <Spinner />
  const toggle = (id: number) => setSelected(sl => sl.includes(id) ? sl.filter(x => x !== id) : [...sl, id])
  return (
    <>
      <div className={s.bar}>
        <span>{data.days > 0 ? t('Wird nach {days} Tagen automatisch gelöscht.', { days: data.days }) : t('Wird nicht automatisch geleert.')}</span>
        <span>·</span>
        <a href="#/settings/file-trash">{t('Einstellungen')}</a>
        <span>· {t('Belegt: {used}', { used: fmtSize(data.usage) })}</span>
      </div>
      {data.items.length === 0 ? <Text className={s.empty}>{t('Der Papierkorb ist leer.')}</Text> : (
        <Table size="small">
          <TableHeader>
            <TableRow>
              <TableHeaderCell style={{ width: 36 }}>
                <Checkbox checked={selected.length === 0 ? false : selected.length === data.items.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
                  onChange={() => setSelected(selected.length === data.items.length ? [] : data.items.map(i => i.id))} />
              </TableHeaderCell>
              <TableHeaderCell>{t('Name')}</TableHeaderCell>
              {!mobile && <TableHeaderCell>{t('Ursprünglicher Ort')}</TableHeaderCell>}
              {!mobile && <TableHeaderCell style={{ width: 170 }}>{t('Gelöscht')}</TableHeaderCell>}
              {!mobile && <TableHeaderCell style={{ width: 100 }}>{t('Größe')}</TableHeaderCell>}
              <TableHeaderCell style={{ width: mobile ? 44 : 140 }} />
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.items.map(i => {
              const isSel = selected.includes(i.id)
              return (
                <TableRow key={i.id} className={mergeClasses(s.row, isSel && s.sel)} aria-selected={isSel}
                  onClick={e => e.ctrlKey || e.metaKey || mobile ? toggle(i.id) : setSelected([i.id])}>
                  <TableCell onClick={e => { e.stopPropagation(); toggle(i.id) }}><Checkbox checked={isSel} aria-label={t('Auswählen')} /></TableCell>
                  <TableCell><TableCellLayout media={<KindIcon item={i} />}
                    description={mobile ? `/${parentOf(i.path)} · ${new Date(i.deleted).toLocaleString(locale)}` : undefined}>{i.name}</TableCellLayout></TableCell>
                  {!mobile && <TableCell className={s.muted}>/{parentOf(i.path)}</TableCell>}
                  {!mobile && <TableCell>{new Date(i.deleted).toLocaleString(locale)}</TableCell>}
                  {!mobile && <TableCell>{fmtSize(i.size)}</TableCell>}
                  <TableCell onClick={e => e.stopPropagation()}>
                    <Button size="small" appearance="subtle" onClick={() => trash.restore([i.id])}>{mobile ? '↺' : t('Wiederherstellen')}</Button>
                  </TableCell>
                </TableRow>
              )
            })}
          </TableBody>
        </Table>
      )}
    </>
  )
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
  if (!links.items || !links.people) return <Spinner />
  if (!links.items.length && !links.people.length) return <Text className={s.empty}>{t('Du hast noch nichts geteilt. Rechtsklick auf eine Datei oder einen Ordner → „Teilen“.')}</Text>
  const revoke = async (l: Link) => {
    if (!confirm(t('Link widerrufen? Wer ihn hat, kommt dann nicht mehr an die Inhalte.'))) return
    try { await api.del('/api/files/links/' + l.id); links.load() } catch (e) { fail(e) }
  }
  return (<>
    {links.people.length > 0 && <>
      <Text weight="semibold" block style={{ padding: '4px 0' }}>{t('Mit Personen geteilt')}</Text>
      <Table size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('Ordner')}</TableHeaderCell>
            <TableHeaderCell>{t('Geteilt mit')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {links.people.map(p => (
            <TableRow key={p.id} className={s.row} onClick={() => onManage({ item: p.item } as Link)}>
              <TableCell>
                <TableCellLayout media={<KindIcon item={p.item} />} description={mobile ? p.members.map(m => m.displayName).join(', ') : '/' + parentOf(p.item.path)}>
                  <a href="#" onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(p.item.path, true) }}>{p.item.name}</a>
                  <People16Regular style={{ marginLeft: 4, verticalAlign: 'middle' }} />
                </TableCellLayout>
              </TableCell>
              {!mobile && <TableCell>{p.members.map(m => `${m.displayName} (${m.readOnly ? t('Lesen') : t('Bearbeiten')})`).join(', ')}</TableCell>}
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
          {!mobile && <TableHeaderCell>{t('Art')}</TableHeaderCell>}
          {!mobile && <TableHeaderCell style={{ width: 120 }}>{t('Läuft ab')}</TableHeaderCell>}
          {!mobile && <TableHeaderCell style={{ width: 110 }}>{t('Downloads')}</TableHeaderCell>}
          <TableHeaderCell style={{ width: mobile ? 80 : 120 }} />
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
                  description={mobile ? desc : it ? '/' + parentOf(it.path) : t('Im Papierkorb – Link ruht')}>
                  {it ? <a href="#" onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(it.path, it.isDir) }}>{it.name || t('Meine Dateien')}</a> : t('(gelöscht)')}
                  {l.hasPassword && <LockClosed16Regular style={{ marginLeft: 4, verticalAlign: 'middle' }} />}
                </TableCellLayout>
              </TableCell>
              {!mobile && <TableCell>{linkStatus(l.mode)}</TableCell>}
              {!mobile && <TableCell style={{ color: l.expired ? tokens.colorPaletteRedForeground1 : undefined }}>
                {l.expires ? new Date(l.expires).toLocaleDateString(locale) : '–'}{l.expired ? ' ' + t('(abgelaufen)') : ''}</TableCell>}
              {!mobile && <TableCell>{l.downloads}</TableCell>}
              <TableCell onClick={e => e.stopPropagation()}>
                <Tooltip content={t('Link kopieren')} relationship="label">
                  <Button size="small" appearance="subtle" icon={<Copy20Regular />} onClick={async () => { if (await copyText(linkUrl(l))) notify(t('Link kopiert')) }} />
                </Tooltip>
                <Tooltip content={t('Widerrufen')} relationship="label">
                  <Button size="small" appearance="subtle" icon={<Delete20Regular />} onClick={() => revoke(l)} />
                </Tooltip>
              </TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
    </>}
  </>)
}
