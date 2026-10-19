import {
  Button, CounterBadge, Menu, MenuDivider, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Text, Tooltip,
  makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  ChevronDown16Regular, ChevronRight16Regular, Mail20Regular, Send20Regular, Drafts20Regular, Delete20Regular, ShieldError20Regular,
  Archive20Regular, Folder20Regular, FolderAdd20Regular, Warning16Filled, Star20Regular, StarOff20Regular,
  Rename20Regular, Color20Regular, NumberSymbol20Regular, Broom20Regular, MailRead20Regular, ArrowUp20Regular, ArrowDown20Regular,
  Open20Regular, DataBarVertical20Regular, Flag20Regular, Clock20Regular, CalendarClock20Regular,
} from '@fluentui/react-icons'
import { useRef, useState } from 'react'
import { Account, api, Folder } from '../api'
import { cleanupDays, cleanupLabel } from '../settings/AccountsSettings'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import type { Selection } from './MailView'
import { locale, t, lang } from '../i18n'
import { VirtualFolders } from './VirtualFolders'

const useStyles = makeStyles({
  root: { flex: 1, overflowY: 'auto', padding: '8px 6px' },
  account: {
    display: 'flex', alignItems: 'center', gap: '4px', padding: '6px 6px', cursor: 'pointer', borderRadius: tokens.borderRadiusMedium,
    fontWeight: 600, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  item: {
    display: 'flex', alignItems: 'center', gap: '8px', height: '32px', paddingRight: '8px', borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer', color: tokens.colorNeutralForeground1, userSelect: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover }, fontWeight: 600 },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}` },
  // Einfuegemarke beim Umsortieren per Ziehen
  lineBefore: { boxShadow: `inset 0 2px 0 ${tokens.colorBrandStroke1}` },
  lineAfter: { boxShadow: `inset 0 -2px 0 ${tokens.colorBrandStroke1}` },
  name: { flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { fontSize: '11px', color: tokens.colorNeutralForeground3, marginLeft: '4px' },
  unread: { color: tokens.colorBrandForeground1, fontWeight: 600, fontSize: '12px' },
  total: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  twisty: { width: '16px', display: 'inline-flex', flexShrink: 0 },
  icon: { color: tokens.colorNeutralForeground2, display: 'inline-flex' },
  head: { fontWeight: 600, color: tokens.colorNeutralForeground2 },
  swatches: { display: 'grid', gridTemplateColumns: 'repeat(5, 24px)', gap: '6px', padding: '8px' },
  swatch: { width: '24px', height: '24px', borderRadius: '50%', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', padding: 0 },
})

const specialNames: Record<string, string> = {
  inbox: t('Posteingang'), flagged: t('Markiert'), snoozed: lang === 'en' ? 'Snoozed' : 'Wiedervorlage', sent: t('Gesendete Elemente'), drafts: t('Entwürfe'),
  trash: t('Gelöschte Elemente'), junk: t('Junk-E-Mail'), archive: t('Archiv'),
}
const specialOrder = ['inbox', 'drafts', 'sent', 'snoozed', 'flagged', 'archive', 'junk', 'trash']
const folderColors = ['#D13438', '#CA5010', '#C19C00', '#13A10E', '#038387', '#0F6CBD', '#8764B8', '#C239B3', '#69797E']

export const folderLabel = (f?: Folder | null) => (f ? (f.special && specialNames[f.special]) || f.name : '')

function FolderIcon({ special }: { special: string | null }) {
  switch (special) {
    case 'inbox': return <Mail20Regular />
    case 'flagged': return <Flag20Regular />
    case 'snoozed': return <Clock20Regular />
    case 'sent': return <Send20Regular />
    case 'drafts': return <Drafts20Regular />
    case 'trash': return <Delete20Regular />
    case 'junk': return <ShieldError20Regular />
    case 'archive': return <Archive20Regular />
    default: return <Folder20Regular />
  }
}

interface Node { folder: Folder; parent: string; children: Node[] }

/** Ordnerbaum: Sonderordner vorne wie in Outlook, die anderen von A bis Z oder in eigener Reihenfolge. */
function tree(account: string, folders: Folder[], order: Record<string, string[]> | undefined, custom: boolean): Node[] {
  const byPath = new Map<string, Node>()
  folders.forEach(f => byPath.set(f.path, { folder: f, parent: '', children: [] }))
  const roots: Node[] = []
  for (const n of byPath.values()) {
    const d = n.folder.delimiter
    const parentPath = n.folder.path.includes(d) ? n.folder.path.slice(0, n.folder.path.lastIndexOf(d)) : ''
    const parent = parentPath ? byPath.get(parentPath) : undefined
    // Sonderordner unter INBOX (INBOX.Sent) trotzdem oben zeigen, wie Outlook
    if (parent && !(n.folder.special && parent.folder.special === 'inbox')) { n.parent = parentPath; parent.children.push(n) }
    else roots.push(n)
  }
  const sort = (list: Node[], parent: string) => {
    const own = custom ? order?.[account + '|' + parent] ?? [] : []
    list.sort((a, b) => {
      const ai = a.folder.special ? specialOrder.indexOf(a.folder.special) : 99
      const bi = b.folder.special ? specialOrder.indexOf(b.folder.special) : 99
      if (ai !== bi) return ai - bi
      const ao = own.indexOf(a.folder.path), bo = own.indexOf(b.folder.path)
      if (ao >= 0 || bo >= 0) return (ao < 0 ? 1e6 : ao) - (bo < 0 ? 1e6 : bo)
      return a.folder.name.localeCompare(b.folder.name, locale)
    })
    list.forEach(n => sort(n.children, n.folder.path))
  }
  sort(roots, '')
  return roots
}

interface Props {
  accounts: Account[]
  folders: Record<string, Folder[]>
  selection: Selection | null
  status: any
  onSelect: (s: Selection) => void
  onDrop: (account: string, target: string, ids: number[]) => void
  onChanged: () => void
}

export function FolderPane({ accounts, folders, selection, status, onSelect, onDrop, onChanged }: Props) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const { prefs, update } = usePrefs()
  const [closed, setClosed] = useState<Record<string, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem('dmoffice.folders.closed') || '{}') } catch { return {} }
  })
  const [dropTarget, setDropTarget] = useState<string | null>(null)
  const [cleanup, setCleanup] = useState<Record<string, number>>({})
  const [menu, setMenu] = useState<{ account: string; folder: Folder | null; x: number; y: number; fav?: boolean } | null>(null)

  const favorites = prefs.favorites ?? []
  const custom = (prefs.sortFolders ?? 'custom') === 'custom'
  const trees = Object.fromEntries(accounts.map(a => [a.id, tree(a.id, folders[a.id] || [], prefs.folderOrder, custom)]))

  const toggle = (key: string) => {
    const next = { ...closed, [key]: !closed[key] }
    setClosed(next)
    try { localStorage.setItem('dmoffice.folders.closed', JSON.stringify(next)) } catch { /* egal */ }
  }

  const create = async (account: string, parent: string) => {
    const name = prompt(t('Name des neuen Ordners:'))
    if (!name) return
    try { await api.post(`/api/mail/${account}/folders`, { path: parent, newName: name }); setTimeout(onChanged, 1500) } catch (e) { fail(e) }
  }
  const rename = async (account: string, f: Folder) => {
    const name = prompt(t('Neuer Name:'), f.name)
    if (!name || name === f.name) return
    try { await api.post(`/api/mail/${account}/folders/rename`, { path: f.path, newName: name }); setTimeout(onChanged, 1500) } catch (e) { fail(e) }
  }
  const markRead = async (account: string, f: Folder) => {
    try { await api.post(`/api/mail/${account}/folders/read`, { path: f.path }); onChanged() } catch (e) { fail(e) }
  }
  const empty = async (account: string, f: Folder) => {
    const permanent = f.special === 'trash' || f.special === 'junk'
    const vars = { n: f.total, folder: folderLabel(f) }
    if (!confirm(permanent
      ? (f.total === 1 ? t('1 Element in „{folder}“ endgültig löschen?', vars) : t('Alle {n} Elemente in „{folder}“ endgültig löschen?', vars))
      : (f.total === 1 ? t('1 Element aus „{folder}“ in die gelöschten Elemente verschieben?', vars)
        : t('Alle {n} Elemente aus „{folder}“ in die gelöschten Elemente verschieben?', vars)))) return
    try {
      const r = await api.post(`/api/mail/${account}/folders/empty`, { path: f.path })
      notify(r.permanent
        ? (r.count === 1 ? t('1 Element gelöscht') : t('{n} Elemente gelöscht', { n: r.count }))
        : (r.count === 1 ? t('1 Element verschoben') : t('{n} Elemente verschoben', { n: r.count })))
      onChanged()
    } catch (e) { fail(e) }
  }
  const cleanupOf = (account: string, special: string) => {
    const a = accounts.find(x => x.id === account)
    return cleanup[`${account}:${special}`] ?? (special === 'trash' ? a?.trashDays : a?.junkDays) ?? 0
  }
  const setAutoClean = async (account: string, special: string, days: number) => {
    try {
      const r = await api.post(`/api/mail/${account}/folders/cleanup`, { special, days })
      setCleanup(c => ({ ...c, [`${account}:${special}`]: days }))
      notify(days === 0 ? t('Wird nicht mehr automatisch geleert')
        : r.count ? t('Automatisch leeren {when} – {n} alte Elemente gelöscht', { when: cleanupLabel(days), n: r.count })
        : t('Wird {when} automatisch geleert', { when: cleanupLabel(days) }))
      if (r.count) onChanged()
    } catch (e) { fail(e) }
  }
  const remove = async (account: string, f: Folder) => {
    if (!confirm(t('Ordner „{name}“ samt Inhalt auf dem Server löschen?', { name: f.name }))) return
    try { await api.post(`/api/mail/${account}/folders/delete`, { path: f.path }); setTimeout(onChanged, 1500) } catch (e) { fail(e) }
  }

  const key = (account: string, f: Folder) => account + '|' + f.path
  const toggleFavorite = (account: string, f: Folder) => {
    const k = key(account, f)
    update({ favorites: favorites.includes(k) ? favorites.filter(x => x !== k) : [...favorites, k] })
  }
  const setColor = (account: string, f: Folder, color: string | null) => {
    const next = { ...(prefs.folderColors ?? {}) }
    if (color) next[key(account, f)] = color
    else delete next[key(account, f)]
    update({ folderColors: next })
  }
  const setCount = (account: string, f: Folder, mode: 'unread' | 'total') =>
    update({ folderCount: { ...(prefs.folderCount ?? {}), [key(account, f)]: mode } })

  /** Die eigenen Ordner neben f (gleicher Elternordner), in der angezeigten Reihenfolge. */
  const siblingsOf = (account: string, path: string): Node[] => {
    const find = (list: Node[]): Node[] | null => {
      if (list.some(n => n.folder.path === path)) return list
      for (const n of list) { const r = find(n.children); if (r) return r }
      return null
    }
    return (find(trees[account]) ?? []).filter(n => !n.folder.special)
  }

  /** Ordner unter seinen Geschwistern verschieben - eigene Reihenfolge, wie in Outlook. */
  const shift = (account: string, f: Folder, by: -1 | 1) => {
    const siblings = siblingsOf(account, f.path)
    const i = siblings.findIndex(n => n.folder.path === f.path)
    const j = i + by
    if (i < 0 || j < 0 || j >= siblings.length) return
    const paths = siblings.map(n => n.folder.path);
    [paths[i], paths[j]] = [paths[j], paths[i]]
    const parent = siblings[i].parent
    update({ folderOrder: { ...(prefs.folderOrder ?? {}), [account + '|' + parent]: paths }, sortFolders: 'custom' })
  }

  /** Ordner per Ziehen vor oder hinter einen Geschwisterordner setzen. */
  const placeFolder = (account: string, path: string, target: string, after: boolean) => {
    const siblings = siblingsOf(account, path)
    if (!siblings.some(n => n.folder.path === target) || path === target) return
    const paths = siblings.map(n => n.folder.path).filter(p => p !== path)
    paths.splice(paths.indexOf(target) + (after ? 1 : 0), 0, path)
    update({ folderOrder: { ...(prefs.folderOrder ?? {}), [account + '|' + siblings[0].parent]: paths }, sortFolders: 'custom' })
  }

  /** Favorit per Ziehen oder Menue umsetzen. */
  const placeFavorite = (k: string, target: string, after: boolean) => {
    if (k === target) return
    const list = favorites.filter(x => x !== k)
    list.splice(list.indexOf(target) + (after ? 1 : 0), 0, k)
    update({ favorites: list })
  }
  const shiftFavorite = (k: string, by: -1 | 1) => {
    const i = favorites.indexOf(k), j = i + by
    if (i < 0 || j < 0 || j >= favorites.length) return
    const list = [...favorites];
    [list[i], list[j]] = [list[j], list[i]]
    update({ favorites: list })
  }

  // Gezogener Ordner - waehrend des Ziehens gibt der Browser die Daten nicht heraus, nur beim Ablegen
  const dragged = useRef<{ account: string; path: string; fav: boolean } | null>(null)
  const [insert, setInsert] = useState<{ rowKey: string; after: boolean } | null>(null)

  const openInTab = (account: string, f: Folder) =>
    window.open(`${location.pathname}#/mail/${encodeURIComponent(account)}/${encodeURIComponent(f.path)}`, '_blank')

  const counter = (account: string, f: Folder) => {
    const mode = prefs.folderCount?.[key(account, f)] ?? (f.special === 'drafts' || f.special === 'snoozed' ? 'total' : 'unread')
    if (mode === 'total') return f.total > 0 ? <span className={s.total}>{f.total.toLocaleString(locale)}</span> : null
    if (f.unread <= 0 || f.special === 'sent' || f.special === 'trash') return null
    return f.special === 'inbox' ? <CounterBadge count={f.unread} size="small" overflowCount={9999} /> : <span className={s.unread}>{f.unread}</span>
  }

  const row = (account: string, f: Folder, depth: number, opts: { kids?: number; fav?: boolean } = {}) => {
    const k = key(account, f)
    const rowKey = (opts.fav ? 'fav:' : '') + k
    const active = selection?.account === account && selection.folder === f.path
    const color = prefs.folderColors?.[k]
    return (
      <div key={rowKey}
        className={mergeClasses(s.item, active && s.active, dropTarget === rowKey && s.drop,
          insert?.rowKey === rowKey && (insert.after ? s.lineAfter : s.lineBefore))}
        style={{ paddingLeft: 4 + depth * 14 }}
        onClick={() => f.selectable && onSelect({ account, folder: f.path })}
        onContextMenu={e => { e.preventDefault(); setMenu({ account, folder: f, x: e.clientX, y: e.clientY, fav: !!opts.fav }) }}
        draggable={!!opts.fav || !f.special}
        onDragStart={e => {
          dragged.current = { account, path: f.path, fav: !!opts.fav }
          e.dataTransfer.setData('application/x-dmoffice-folder', k)
          e.dataTransfer.effectAllowed = 'move'
        }}
        onDragEnd={() => { dragged.current = null; setInsert(null) }}
        onDragOver={e => {
          const d = dragged.current
          if (d && e.dataTransfer.types.includes('application/x-dmoffice-folder')) {
            // Umsortieren: nur Favorit zu Favorit, oder Ordner zu Geschwisterordner; Standardordner bleiben fest
            const ok = d.fav ? !!opts.fav
              : !opts.fav && !f.special && d.account === account && siblingsOf(account, d.path).some(n => n.folder.path === f.path)
            if (!ok) return
            e.preventDefault()
            const r = e.currentTarget.getBoundingClientRect()
            setInsert({ rowKey, after: e.clientY > r.top + r.height / 2 })
            return
          }
          if (f.selectable && e.dataTransfer.types.includes('application/x-dmoffice-mail')) { e.preventDefault(); setDropTarget(rowKey) }
        }}
        onDragLeave={() => { setDropTarget(t => (t === rowKey ? null : t)); setInsert(x => (x?.rowKey === rowKey ? null : x)) }}
        onDrop={e => {
          e.preventDefault()
          setDropTarget(null)
          const d = dragged.current
          if (d && e.dataTransfer.types.includes('application/x-dmoffice-folder')) {
            const after = insert?.rowKey === rowKey && insert.after
            if (d.fav) placeFavorite(d.account + '|' + d.path, k, after)
            else placeFolder(account, d.path, f.path, after)
            dragged.current = null
            setInsert(null)
            return
          }
          const data = JSON.parse(e.dataTransfer.getData('application/x-dmoffice-mail') || '{}')
          if (data.account === account && data.ids?.length) onDrop(account, f.path, data.ids)
        }}>
        <span className={s.twisty} onClick={e => { if (opts.kids) { e.stopPropagation(); toggle(k) } }}>
          {!!opts.kids && (closed[k] ? <ChevronRight16Regular /> : <ChevronDown16Regular />)}
        </span>
        <span className={s.icon} style={color ? { color } : undefined}><FolderIcon special={f.special} /></span>
        <span className={s.name}>
          {folderLabel(f)}
          {opts.fav && accounts.length > 1 && <span className={s.sub}>{accounts.find(a => a.id === account)?.name}</span>}
        </span>
        {counter(account, f)}
      </div>
    )
  }

  const renderNode = (account: string, n: Node, depth: number): JSX.Element => (
    <div key={account + '|' + n.folder.path}>
      {row(account, n.folder, depth, { kids: n.children.length })}
      {!closed[account + '|' + n.folder.path] && n.children.map(c => renderNode(account, c, depth + 1))}
    </div>
  )

  const favoriteRows = favorites.map(k => {
    const [account, ...rest] = k.split('|')
    const f = folders[account]?.find(x => x.path === rest.join('|'))
    return f ? row(account, f, 0, { fav: true }) : null
  }).filter(Boolean)

  const favSection = (prefs.showFavorites ?? true) && favoriteRows.length > 0 && (
    <div style={{ marginBottom: 10 }}>
      <div className={s.account} onClick={() => toggle('favorites')}>
        {closed.favorites ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
        <Text weight="semibold" style={{ flex: 1 }}>{t('Favoriten')}</Text>
      </div>
      {!closed.favorites && favoriteRows}
    </div>
  )

  const mf = menu?.folder ?? null
  const virtual = mf?.special === 'flagged'
  const mk = menu && mf ? key(menu.account, mf) : ''
  const countMode = mf ? prefs.folderCount?.[mk] ?? (mf.special === 'drafts' ? 'total' : 'unread') : 'unread'

  return (
    <div className={s.root} onClick={() => setMenu(null)}>
      {(prefs.pinFavorites ?? true) && favSection}
      {accounts.map(a => {
        const list = folders[a.id] || []
        const err = status?.accounts?.[a.id]?.error
        return (
          <div key={a.id} style={{ marginBottom: 10 }}>
            <div className={s.account} onClick={() => toggle(a.id)}
              onContextMenu={e => { e.preventDefault(); setMenu({ account: a.id, folder: null, x: e.clientX, y: e.clientY }) }}>
              {closed[a.id] ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
              <Text weight="semibold" truncate style={{ flex: 1 }} title={a.email}>{a.name || a.email}</Text>
              {err && <Tooltip content={err} relationship="description"><Warning16Filled color={tokens.colorPaletteRedForeground1} /></Tooltip>}
              <Tooltip content={t('Neuer Ordner')} relationship="label">
                <Button appearance="transparent" size="small" icon={<FolderAdd20Regular />} onClick={e => { e.stopPropagation(); create(a.id, '') }} />
              </Tooltip>
            </div>
            {!closed[a.id] && (list.length === 0
              ? <Text size={200} style={{ paddingLeft: 24, color: tokens.colorNeutralForeground3 }}>{err ? t('Keine Verbindung') : t('Wird geladen …')}</Text>
              : trees[a.id].map(n => renderNode(a.id, n, 0)))}
          </div>
        )
      })}
      <VirtualFolders accounts={accounts} folders={folders} selection={selection} onSelect={onSelect} refresh={status} />
      {!(prefs.pinFavorites ?? true) && favSection}

      {menu && (
        <Menu open onOpenChange={(_, d) => !d.open && setMenu(null)}
          positioning={{ target: { getBoundingClientRect: () => ({ x: menu.x, y: menu.y, top: menu.y, left: menu.x, bottom: menu.y, right: menu.x, width: 0, height: 0 }) } as any }}>
          <MenuPopover>
            <MenuList>
              {mf ? (
                <>
                  <MenuItem disabled className={s.head}>
                    {mf.total === 1
                      ? t('1 Element ({u} ungelesen)', { u: mf.unread.toLocaleString(locale) })
                      : t('{n} Elemente ({u} ungelesen)', { n: mf.total.toLocaleString(locale), u: mf.unread.toLocaleString(locale) })}
                  </MenuItem>
                  <MenuDivider />
                  <MenuItem icon={favorites.includes(mk) ? <StarOff20Regular /> : <Star20Regular />} onClick={() => toggleFavorite(menu.account, mf)}>
                    {favorites.includes(mk) ? t('Aus Favoriten entfernen') : t('Zu Favoriten hinzufügen')}
                  </MenuItem>
                  <MenuDivider />
                  <MenuItem icon={<FolderAdd20Regular />} disabled={virtual} onClick={() => create(menu.account, mf.path)}>{t('Neuer Unterordner …')}</MenuItem>
                  <MenuItem icon={<Rename20Regular />} disabled={!!mf.special} onClick={() => rename(menu.account, mf)}>{t('Umbenennen')}</MenuItem>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement><MenuItem icon={<Color20Regular />}>{t('Farbe ändern')}</MenuItem></MenuTrigger>
                    <MenuPopover>
                      <div className={s.swatches}>
                        {folderColors.map(c => <button key={c} className={s.swatch} style={{ background: c }} title={c} onClick={() => { setColor(menu.account, mf, c); setMenu(null) }} />)}
                      </div>
                      <MenuList><MenuItem onClick={() => { setColor(menu.account, mf, null); setMenu(null) }}>{t('Keine Farbe')}</MenuItem></MenuList>
                    </MenuPopover>
                  </Menu>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement><MenuItem icon={<NumberSymbol20Regular />}>{t('Ordneranzahl ändern')}</MenuItem></MenuTrigger>
                    <MenuPopover>
                      <MenuList checkedValues={{ count: [countMode] }} onCheckedValueChange={(_, d) => { setCount(menu.account, mf, d.checkedItems[0] as any); setMenu(null) }}>
                        <MenuItemRadio name="count" value="unread">{t('Anzahl ungelesener Elemente anzeigen')}</MenuItemRadio>
                        <MenuItemRadio name="count" value="total">{t('Gesamtzahl der Elemente anzeigen')}</MenuItemRadio>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                  <MenuItem icon={<Broom20Regular />} disabled={virtual || !mf.selectable || mf.total === 0} onClick={() => empty(menu.account, mf)}>{t('Leeren')}</MenuItem>
                  {(mf.special === 'trash' || mf.special === 'junk') && (
                    <Menu>
                      <MenuTrigger disableButtonEnhancement><MenuItem icon={<CalendarClock20Regular />}>{t('Automatisch leeren')}</MenuItem></MenuTrigger>
                      <MenuPopover>
                        <MenuList checkedValues={{ days: [String(cleanupOf(menu.account, mf.special))] }}
                          onCheckedValueChange={(_, d) => { setAutoClean(menu.account, mf.special!, Number(d.checkedItems[0])); setMenu(null) }}>
                          {cleanupDays.map(d => <MenuItemRadio key={d} name="days" value={String(d)}>{d === 0 ? t('Nie') : t('Elemente älter als {n} Tage', { n: d })}</MenuItemRadio>)}
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  )}
                  <MenuDivider />
                  <MenuItem icon={<MailRead20Regular />} disabled={virtual || !mf.selectable || mf.unread === 0} onClick={() => markRead(menu.account, mf)}>{t('Alle als gelesen markieren')}</MenuItem>
                  <MenuDivider />
                  {menu.fav && <MenuItem icon={<ArrowUp20Regular />} disabled={favorites.indexOf(mk) <= 0} onClick={() => shiftFavorite(mk, -1)}>{t('In Favoriten nach oben')}</MenuItem>}
                  {menu.fav && <MenuItem icon={<ArrowDown20Regular />} disabled={favorites.indexOf(mk) >= favorites.length - 1} onClick={() => shiftFavorite(mk, 1)}>{t('In Favoriten nach unten')}</MenuItem>}
                  <MenuItem icon={<ArrowUp20Regular />} disabled={!!mf.special} onClick={() => shift(menu.account, mf, -1)}>{t('In Liste nach oben verschieben')}</MenuItem>
                  <MenuItem icon={<ArrowDown20Regular />} disabled={!!mf.special} onClick={() => shift(menu.account, mf, 1)}>{t('In Liste nach unten verschieben')}</MenuItem>
                  <MenuDivider />
                  <MenuItem icon={<Open20Regular />} disabled={!mf.selectable} onClick={() => openInTab(menu.account, mf)}>{t('In neuer Registerkarte öffnen')}</MenuItem>
                  <MenuItem icon={<DataBarVertical20Regular />} onClick={() => { location.hash = '#/settings/stats' }}>{t('Statistik …')}</MenuItem>
                  {!mf.special && <MenuItem icon={<Delete20Regular />} onClick={() => remove(menu.account, mf)}>{t('Ordner löschen')}</MenuItem>}
                </>
              ) : (
                <>
                  <MenuItem icon={<FolderAdd20Regular />} onClick={() => create(menu.account, '')}>{t('Neuer Ordner …')}</MenuItem>
                  <MenuItem icon={<DataBarVertical20Regular />} onClick={() => { location.hash = '#/settings/stats' }}>{t('Statistik …')}</MenuItem>
                </>
              )}
            </MenuList>
          </MenuPopover>
        </Menu>
      )}
    </div>
  )
}
