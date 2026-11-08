import {
  Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuDivider, MenuItem, MenuItemRadio,
  MenuList, MenuPopover, MenuTrigger, Option, Select, Spinner, Switch, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ChevronDown16Regular, Copy20Regular, Delete20Regular, Dismiss20Regular, ImageMultiple20Regular, People20Regular, Sparkle20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import type { Item } from './common'
import type { Meta } from './PhotoFilter'
import { copyText } from '../shell/browser'

/** Fotos eines Albums in der Galerie: Pfad „alb:&lt;Album&gt;/&lt;Schlüssel&gt;“ - Vorschau und Original kommen über das Album. */
export const ALB = 'alb:'
export const isAlb = (i: { path: string }) => i.path.startsWith(ALB)
const partsOf = (i: { path: string }) => { const r = i.path.slice(ALB.length); const s = r.indexOf('/'); return { id: r.slice(0, s), key: r.slice(s + 1) } }
export const albumThumb = (i: Item, size = 256) => { const p = partsOf(i); return `/api/gallery/albums/${p.id}/thumb?` + q({ key: p.key, size, v: i.etag }) }
export const albumFile = (i: Item, inline?: boolean) => { const p = partsOf(i); return `/api/gallery/albums/${p.id}/file?` + q({ key: p.key, inline: inline || undefined }) }
export const albumKey = (i: Item) => partsOf(i).key

export interface AlbumInfo {
  id: number; name: string; kind: 'fixed' | 'smart'; rule?: string | null; owner: string; ownerName?: string | null; mine: boolean
  members?: string[] | null; shared: boolean; count: number; cover?: string | null
}

interface AlbumItem { key: string; owner: string; ownerName?: string | null; mine: boolean; name: string; size: number; mime: string | null; modified: string; etag: string; path?: string | null; meta?: Meta | null }

export const albumsApi = {
  list: () => api.get<AlbumInfo[]>('/api/gallery/albums'),
  create: (name: string, kind: 'fixed' | 'smart' = 'fixed', rule?: string) => api.post<{ id: number }>('/api/gallery/albums', { name, kind, rule }),
  update: (id: number, patch: { name?: string; rule?: string; members?: string[] }) => api.put(`/api/gallery/albums/${id}`, patch),
  remove: (id: number) => api.del(`/api/gallery/albums/${id}`),
  add: (id: number, paths: string[]) => api.post<{ added: number }>(`/api/gallery/albums/${id}/add`, { paths }),
  take: (id: number, keys: string[]) => api.post(`/api/gallery/albums/${id}/remove`, { keys }),
  links: (id: number) => api.get<AlbumLink[]>(`/api/gallery/albums/${id}/links`),
  createLink: (id: number, body: { days?: number; password?: string; download: boolean }) => api.post<AlbumLink>(`/api/gallery/albums/${id}/links`, body),
  deleteLink: (id: number, link: string) => api.del(`/api/gallery/albums/${id}/links/${link}`),
}

export interface AlbumLink { id: string; token: string; expires: string | null; hasPassword: boolean; download: boolean; created: string; views: number; expired: boolean }

/**
 * Album extern zeigen: öffentliche Links (live, nur ansehen) anlegen und löschen - wahlweise mit Ablauf, Passwort und
 * Herunterladen der Originale. Wer den Link hat, sieht auch später dazugekommene Fotos.
 */
export function AlbumLinksDialog({ album, onClose }: { album: AlbumInfo; onClose: () => void }) {
  const { fail, notify } = useApp()
  const [links, setLinks] = useState<AlbumLink[] | null>(null)
  const [days, setDays] = useState('0')
  const [password, setPassword] = useState('')
  const [download, setDownload] = useState(false)
  const [busy, setBusy] = useState(false)
  const load = () => albumsApi.links(album.id).then(setLinks).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const urlOf = (l: AlbumLink) => `${location.origin}/a/${l.token}`
  const copy = async (url: string) => notify(await copyText(url) ? t('Link kopiert') : url)
  const create = async () => {
    setBusy(true)
    try {
      const l = await albumsApi.createLink(album.id, { days: Number(days) || undefined, password: password || undefined, download })
      setPassword(''); await load(); copy(urlOf(l))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ öffentlich zeigen', { name: album.name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              {t('Wer den Link hat, sieht die Fotos im Browser, ohne Anmeldung - auch die, die später dazukommen. Ändern oder löschen kann er nichts.')}
            </Text>
            {links == null ? <Spinner size="tiny" /> : links.map(l => (
              <div key={l.id} style={{ display: 'flex', alignItems: 'center', gap: 8, borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, paddingBottom: 6 }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Text size={200} block truncate wrap={false} style={{ fontFamily: 'monospace' }}>{urlOf(l)}</Text>
                  <Text size={200} style={{ color: l.expired ? tokens.colorPaletteRedForeground1 : tokens.colorNeutralForeground3 }}>
                    {[l.expired ? t('abgelaufen') : l.expires ? t('bis {date}', { date: new Date(l.expires).toLocaleDateString() }) : t('ohne Ablauf'),
                      l.hasPassword && t('mit Passwort'), l.download ? t('Herunterladen erlaubt') : t('nur ansehen'), t('{n}× geöffnet', { n: l.views })].filter(Boolean).join(' · ')}
                  </Text>
                </div>
                <Tooltip content={t('Link kopieren')} relationship="label"><Button appearance="subtle" icon={<Copy20Regular />} onClick={() => copy(urlOf(l))} /></Tooltip>
                <Tooltip content={t('Link löschen')} relationship="label"><Button appearance="subtle" icon={<Delete20Regular />} onClick={async () => { try { await albumsApi.deleteLink(album.id, l.id); load() } catch (e) { fail(e) } }} /></Tooltip>
              </div>
            ))}
            <Text weight="semibold">{t('Neuer Link')}</Text>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
              <Field label={t('Gültig')}>
                <Select value={days} onChange={(_, d) => setDays(d.value)}>
                  <option value="0">{t('ohne Ablauf')}</option><option value="1">{t('1 Tag')}</option><option value="7">{t('7 Tage')}</option>
                  <option value="30">{t('30 Tage')}</option><option value="365">{t('1 Jahr')}</option>
                </Select>
              </Field>
              <Field label={t('Passwort (wahlweise)')}><Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} /></Field>
            </div>
            <Switch checked={download} onChange={(_, d) => setDownload(d.checked)} label={t('Originale herunterladen erlauben')} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Schließen')}</Button>
            <Button appearance="primary" disabled={busy} onClick={create}>{t('Link anlegen und kopieren')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Inhalt eines Albums als Einträge der Galerie, dazu Aufnahmedaten und wem was gehört. */
export async function loadAlbum(id: number): Promise<{ items: Item[]; meta: Record<string, Meta>; mine: Record<string, boolean> }> {
  const r = await api.get<{ items: AlbumItem[] }>(`/api/gallery/albums/${id}/items`)
  const path = (x: AlbumItem) => `${ALB}${id}/${x.key}`
  return {
    items: r.items.map(x => ({ path: path(x), name: x.name, isDir: false, size: x.size, mime: x.mime, modified: x.modified, etag: x.etag })),
    meta: Object.fromEntries(r.items.filter(x => x.meta).map(x => [path(x), { ...x.meta!, key: path(x) }])),
    mine: Object.fromEntries(r.items.map(x => [path(x), x.mine])),
  }
}

/** Auswahl in der Titelzeile der Galerie: alle Fotos oder ein Album; dazu „Neues Album“ und „Neues Smart-Album“. */
export function AlbumPicker({ albums, current, onPick, onNew, onNewSmart }: { albums: AlbumInfo[]; current: number | null; onPick: (id: number | null) => void; onNew: () => void; onNewSmart?: () => void }) {
  const cur = albums.find(a => a.id === current)
  const icon = (a: AlbumInfo) => a.kind === 'smart' ? <Sparkle20Regular /> : a.shared || !a.mine ? <People20Regular /> : <ImageMultiple20Regular />
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Button size="small" appearance={cur ? 'primary' : 'subtle'} icon={<ChevronDown16Regular />} iconPosition="after">{cur ? cur.name : t('Alle Fotos')}</Button>
      </MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ album: [String(current ?? '')] }} onCheckedValueChange={(_, d) => onPick(d.checkedItems[0] ? Number(d.checkedItems[0]) : null)}>
          <MenuItemRadio name="album" value="">{t('Alle Fotos')}</MenuItemRadio>
          {albums.length > 0 && <MenuDivider />}
          {albums.map(a => (
            <MenuItemRadio key={a.id} name="album" value={String(a.id)} icon={icon(a)} secondaryContent={String(a.count)}>
              {a.name}{!a.mine && a.ownerName ? ` · ${a.ownerName}` : ''}
            </MenuItemRadio>
          ))}
          <MenuDivider />
          <MenuItem icon={<Add20Regular />} onClick={onNew}>{t('Neues Album …')}</MenuItem>
          {onNewSmart && <MenuItem icon={<Sparkle20Regular />} onClick={onNewSmart}>{t('Neues Smart-Album …')}</MenuItem>}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
}

/** Name eines Albums eingeben (neu oder umbenennen). */
export function AlbumNameDialog({ title, initial, onClose }: { title: string; initial?: string; onClose: (name: string | null) => void }) {
  const [name, setName] = useState(initial ?? '')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(null)}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            <Field label={t('Name')}><Input autoFocus value={name} maxLength={80} onChange={(_, d) => setName(d.value)} onKeyDown={e => { if (e.key === 'Enter' && name.trim()) onClose(name.trim()) }} /></Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!name.trim()} onClick={() => onClose(name.trim())}>{t('OK')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Album teilen: mit welchen Personen dieses Servers (feste Alben: sie dürfen eigene Fotos dazulegen; smarte: nur ansehen). */
export function AlbumShareDialog({ album, onClose }: { album: AlbumInfo; onClose: (changed: boolean) => void }) {
  const { fail } = useApp()
  const [people, setPeople] = useState<{ id: string; displayName: string }[] | null>(null)
  const [chosen, setChosen] = useState<string[]>(album.members ?? [])
  const [query, setQuery] = useState('')
  // Wie die übrigen Freigabe-Dialoge: nur wer Zugriff hat, darüber „Person hinzufügen“ mit Vorschlägen - auch bei vielen Benutzern
  const qn = query.trim().toLowerCase()
  const matches = (people ?? []).filter(p => !chosen.includes(p.id) && (!qn || p.displayName.toLowerCase().includes(qn)))
  useEffect(() => { api.get<{ id: string; displayName: string }[]>('/api/chat/people').then(setPeople).catch(e => { fail(e); setPeople([]) }) }, [fail])
  const save = async () => {
    try { await albumsApi.update(album.id, { members: chosen }); onClose(true) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ teilen', { name: album.name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              {album.kind === 'smart' ? t('Wer das Album sieht, kann es nur ansehen.') : t('Wer das Album sieht, darf eigene Fotos dazulegen und nur diese wieder herausnehmen.')}
            </Text>
            {people == null && <Spinner size="small" />}
            {people && <>
              <Field label={t('Person hinzufügen')}>
                <Combobox freeform placeholder={t('Name eingeben …')} value={query} selectedOptions={[]}
                  onChange={e => setQuery((e.target as HTMLInputElement).value)}
                  onOptionSelect={(_, d) => { if (d.optionValue) { setChosen(c => [...c, d.optionValue!]); setQuery('') } }}>
                  {matches.slice(0, 8).map(p => <Option key={p.id} value={p.id} text={p.displayName}>{p.displayName}</Option>)}
                  {matches.length === 0 && <Option disabled value="" text="">{qn ? t('Niemand gefunden') : t('Alle haben schon Zugriff')}</Option>}
                  {matches.length > 8 && <Option disabled value="" text="">{t('… und {n} weitere – weitertippen', { n: matches.length - 8 })}</Option>}
                </Combobox>
              </Field>
              <Text weight="semibold">{t('Zugriff haben')}</Text>
              {chosen.length === 0
                ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Noch niemand – füge oben eine Person hinzu.')}</Text>
                : chosen.map(id => (
                  <div key={id} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <Text style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{people.find(p => p.id === id)?.displayName ?? id}</Text>
                    <Tooltip content={t('Zugriff entfernen')} relationship="label">
                      <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={() => setChosen(c => c.filter(x => x !== id))} />
                    </Tooltip>
                  </div>
                ))}
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
