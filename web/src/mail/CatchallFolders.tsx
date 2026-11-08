import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ChevronDown16Regular, ChevronRight16Regular, Copy20Regular, Delete20Regular, Edit20Regular, Globe20Regular, LockClosed20Regular, LockOpen20Regular, NumberSymbol20Regular, PersonMail20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, Folder } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { copyText } from '../shell/browser'
import { askConfirm } from '../shell/context'
import { usePrefs } from '../prefs'
import type { AliasItem, AliasState } from './AliasDialog'
import type { Selection } from './MailView'

/** Nach jeder Änderung an Catchall-Aliasen (Sperren, Anlegen, Übernehmen): Seitenleiste, Liste und Einstellungen laden neu. */
export const CATCHALL_CHANGED = 'dmoffice:catchall'
export const catchallChanged = () => window.dispatchEvent(new Event(CATCHALL_CHANGED))

/** Stand der Catchall-Aliase (Mail/CatchallAliasesApi.cs), neu bei refresh und nach CATCHALL_CHANGED. */
export function useCatchall(refresh?: unknown): AliasState | null {
  const [st, setSt] = useState<AliasState | null>(null)
  const load = useCallback(() => { api.get<AliasState>('/api/mail/aliases').then(setSt).catch(() => {}) }, [])
  useEffect(() => { load() }, [load, refresh])
  useEffect(() => {
    window.addEventListener(CATCHALL_CHANGED, load)
    return () => window.removeEventListener(CATCHALL_CHANGED, load)
  }, [load])
  return st
}

/** Sperren oder entsperren mit einem Klick (wie eingestellt: Junk oder löschen). */
export async function setAliasBlocked(address: string, block: boolean) {
  await api.put('/api/mail/aliases/item', { address, block })
  catchallChanged()
}

/** Alias löschen nach Rückfrage: Mails aus seinem Ordner in den Ordner der Domain, sein Ordner wird gelöscht. false: abgebrochen. */
export async function deleteAlias(address: string): Promise<boolean> {
  if (!await askConfirm(t('{a} löschen?', { a: address }),
    t('Die Mails im Ordner von {a} kommen in den Ordner der Domain, der Ordner von {a} wird gelöscht. Neue Mails an {a} landen im Ordner der Domain.', { a: address }),
    t('Löschen'))) return false
  await api.del('/api/mail/aliases/item?address=' + encodeURIComponent(address))
  catchallChanged()
  return true
}

/** Unbekannte Adresse als Alias übernehmen: Ordner anlegen, alle ihre Mails aus Posteingang und Sammelordner der Domain dorthin. */
export async function adoptAlias(address: string, note = '') {
  const at = address.lastIndexOf('@')
  const r = await api.post<AliasItem & { moved?: number }>('/api/mail/aliases', { name: address.slice(0, at), domain: address.slice(at + 1), note })
  catchallChanged()
  return r
}

/** Gehört dieser Ordner zu den Catchall-Aliasen (Domain-Ordner oder darunter)? Dann zeigt ihn nur der eigene Abschnitt. */
export function isCatchallFolder(st: AliasState | null, account: string, f: Folder): boolean {
  if (!st || st.config.account !== account) return false
  return st.domains.some(d => !!d.folder && (f.path === d.folder || f.path.startsWith(d.folder + f.delimiter)))
}

/** Name mit Notiz in Klammern: „shop (Notiz)“, ohne Notiz nur der Name. */
export const withNote = (name: string, note?: string | null) => note?.trim() ? `${name} (${note.trim()})` : name

/** Titel über der Mailliste für einen Catchall-Ordner: beim Alias die Adresse mit Notiz, beim Domain-Ordner die Domain; sonst null. */
export function catchallTitle(st: AliasState | null, account: string, path: string): string | null {
  if (!st || st.config.account !== account) return null
  const a = st.items.find(i => i.folder === path)
  if (a) return withNote(a.address, a.note)
  return st.domains.find(d => d.folder === path)?.domain ?? null
}

const useStyles = makeStyles({
  head: {
    display: 'flex', alignItems: 'center', gap: '4px', padding: '6px 6px', cursor: 'pointer', borderRadius: tokens.borderRadiusMedium,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  item: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 8px 6px 4px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected, fontWeight: 600 },
  twisty: { width: '16px', display: 'inline-flex', flexShrink: 0 },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  unread: { color: tokens.colorBrandForeground1, fontWeight: 600, fontSize: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
})

/** Abschnitt „Catchall-Aliase“ im Ordnerbereich (wie Kategorien): je Domain ihr Ordner, darunter je Alias seiner, gesperrte mit Schloss. */
export function CatchallSection({ st, folders, selection, onSelect }: {
  st: AliasState | null; folders: Record<string, Folder[]>; selection: Selection | null; onSelect: (s: Selection) => void
}) {
  const s = useStyles()
  const { notify, fail } = useApp()
  const { prefs, update } = usePrefs()
  // Kontextmenü: an einem Alias (a), an einer Domain oder am Abschnitt - key ist der Schlüssel der Ordneranzahl
  const [menu, setMenu] = useState<{ a?: AliasItem; key: string; def: 'unread' | 'total'; x: number; y: number } | null>(null)
  const closed = prefs.virtualClosed ?? {}
  const toggle = (k: string) => update({ virtualClosed: { ...closed, [k]: !closed[k] } })
  const account = st?.config.account
  if (!st || !account || st.domains.length === 0) return null
  const list = folders[account] || []
  const folderOf = (p?: string | null) => (p ? list.find(f => f.path === p) : undefined)

  // Ordneranzahl wie bei allen Ordnern (Kontextmenü „Ordneranzahl ändern“): ungelesen oder gesamt, Schlüssel Konto|Pfad;
  // zugeklappt zählt eine Domain mit all ihren Aliasen, der zugeklappte Abschnitt alles
  const modeOf = (key: string, def: 'unread' | 'total') => prefs.folderCount?.[key] ?? def
  const sum = (list: (Folder | undefined)[], mode: 'unread' | 'total') => list.reduce((n, f) => n + (f ? (mode === 'total' ? f.total : f.unread) : 0), 0)
  const badge = (n: number, mode: 'unread' | 'total', title?: string) => n > 0
    ? <span className={mode === 'total' ? s.muted : s.unread} style={{ fontSize: 12 }} title={title}>{n.toLocaleString(locale)}</span> : null
  const openMenu = (e: React.MouseEvent, m: { a?: AliasItem; key: string; def: 'unread' | 'total' }) => {
    e.preventDefault(); e.stopPropagation(); setMenu({ ...m, x: e.clientX, y: e.clientY })
  }

  const row = (key: string, folder: Folder | undefined, icon: JSX.Element, name: string, depth: number, count: React.ReactNode,
    opts: { kids?: boolean; closedKey?: string; alias?: AliasItem; menu?: { key: string; def: 'unread' | 'total' } } = {}) => (
    <div key={key} className={mergeClasses(s.item, !!folder && selection?.account === account && selection.folder === folder.path && s.active)}
      style={{ paddingLeft: 4 + depth * 14 }} title={opts.alias?.note || undefined}
      onClick={() => folder && onSelect({ account, folder: folder.path })}
      onContextMenu={opts.menu ? e => openMenu(e, { a: opts.alias, ...opts.menu! }) : undefined}>
      <span className={s.twisty} onClick={e => { if (opts.kids && opts.closedKey) { e.stopPropagation(); toggle(opts.closedKey) } }}>
        {opts.kids && (closed[opts.closedKey!] ? <ChevronRight16Regular /> : <ChevronDown16Regular />)}
      </span>
      {icon}
      <span className={mergeClasses(s.name, !folder && s.muted)}>{name}</span>
      {count}
    </div>
  )

  const note = async (a: AliasItem) => {
    const n = prompt(t('Notiz zu {a}:', { a: a.address }), a.note)
    if (n == null) return
    try { await api.put('/api/mail/aliases/item', { address: a.address, note: n }); catchallChanged() } catch (e) { fail(e) }
  }

  return (
    <div style={{ marginBottom: 10 }}>
      <div className={s.head} onClick={() => toggle('catchall')} onContextMenu={e => openMenu(e, { key: account + '|catchall', def: 'unread' })}>
        {closed.catchall ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
        <Text weight="semibold" style={{ flex: 1 }}>{t('Catchall-Aliase')}</Text>
        {closed.catchall && (() => {
          const m = modeOf(account + '|catchall', 'unread')
          return badge(sum([...st.domains.map(d => folderOf(d.folder)), ...st.items.map(a => folderOf(a.folder))], m), m)
        })()}
      </div>
      {!closed.catchall && st.domains.map(d => {
        const items = st.items.filter(i => i.address.endsWith('@' + d.domain))
        const k = 'catchall:' + d.domain
        const df = folderOf(d.folder)
        // Domain-Ordner: Mails an Adressen ohne Alias - wie bisher mit der Gesamtzahl, sofern nicht anders eingestellt
        const dk = account + '|' + (d.folder ?? k), dm = modeOf(dk, 'total')
        const shut = items.length > 0 && !!closed[k]
        const dn = shut ? sum([df, ...items.map(a => folderOf(a.folder))], dm) : sum([df], dm)
        return (
          <div key={d.domain}>
            {row(k, df, <Globe20Regular />, d.domain, 0, badge(dn, dm, shut ? undefined : t('Mails an Adressen ohne Alias')),
              { kids: items.length > 0, closedKey: k, menu: { key: dk, def: 'total' } })}
            {!closed[k] && items.map(a => {
              const f = folderOf(a.folder), ak = account + '|' + (a.folder ?? a.address), am = modeOf(ak, 'unread')
              return row(a.address, f, a.blocked ? <LockClosed20Regular color={tokens.colorPaletteRedForeground1} /> : <PersonMail20Regular />,
                withNote(a.address.slice(0, a.address.lastIndexOf('@')), a.note), 1, badge(sum([f], am), am), { alias: a, menu: { key: ak, def: 'unread' } })
            })}
          </div>
        )
      })}
      {menu && (
        <Menu open onOpenChange={(_, d) => !d.open && setMenu(null)}
          positioning={{ target: { getBoundingClientRect: () => ({ x: menu.x, y: menu.y, top: menu.y, left: menu.x, bottom: menu.y, right: menu.x, width: 0, height: 0 }) } as any }}>
          <MenuPopover>
            <MenuList>
              <Menu>
                <MenuTrigger disableButtonEnhancement><MenuItem icon={<NumberSymbol20Regular />}>{t('Ordneranzahl ändern')}</MenuItem></MenuTrigger>
                <MenuPopover>
                  <MenuList checkedValues={{ count: [modeOf(menu.key, menu.def)] }} onCheckedValueChange={(_, d) => {
                    update({ folderCount: { ...(prefs.folderCount ?? {}), [menu.key]: d.checkedItems[0] as 'unread' | 'total' } }); setMenu(null)
                  }}>
                    <MenuItemRadio name="count" value="unread">{t('Anzahl ungelesener Elemente anzeigen')}</MenuItemRadio>
                    <MenuItemRadio name="count" value="total">{t('Gesamtzahl der Elemente anzeigen')}</MenuItemRadio>
                  </MenuList>
                </MenuPopover>
              </Menu>
              {menu.a && (() => {
                const a = menu.a
                return <>
                  <MenuItem icon={a.blocked ? <LockOpen20Regular /> : <LockClosed20Regular />}
                    onClick={() => setAliasBlocked(a.address, !a.blocked).catch(fail)}>{a.blocked ? t('Entsperren') : t('Sperren')}</MenuItem>
                  <MenuItem icon={<Edit20Regular />} onClick={() => note(a)}>{t('Notiz ändern …')}</MenuItem>
                  <MenuItem icon={<Copy20Regular />} onClick={async () => { if (await copyText(a.address)) notify(t('{a} kopiert', { a: a.address })) }}>{t('Adresse kopieren')}</MenuItem>
                  <MenuItem icon={<Delete20Regular />} onClick={() => deleteAlias(a.address).catch(fail)}>{t('Löschen …')}</MenuItem>
                </>
              })()}
            </MenuList>
          </MenuPopover>
        </Menu>
      )}
    </div>
  )
}

/** „über alias@domain“ an einer Mail, mit Knopf: Sperren, Entsperren oder (unbekannte Adresse) Übernehmen. */
export function AliasHint({ address, state, sender, className }: {
  address: string; state?: 'open' | 'blocked' | 'unknown' | null
  /** Name des Absenders - Vorschlag für die Notiz beim Anlegen */
  sender?: string | null; className?: string
}) {
  const { fail } = useApp()
  const [now, setNow] = useState(state ?? 'open')
  const [busy, setBusy] = useState(false)
  const [adopt, setAdopt] = useState(false)
  useEffect(() => setNow(state ?? 'open'), [state, address])
  const act = async (e: React.MouseEvent) => {
    e.stopPropagation()
    if (now === 'unknown') { setAdopt(true); return }
    setBusy(true)
    try {
      await setAliasBlocked(address, now !== 'blocked')
      setNow(now === 'blocked' ? 'open' : 'blocked')
    } catch (err) { fail(err) } finally { setBusy(false) }
  }
  return (
    <span className={className} title={t('An den Catchall-Alias {a}', { a: address })} style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}
      onClick={e => e.stopPropagation()}>
      {now === 'blocked' && <LockClosed20Regular style={{ width: 14, height: 14 }} color={tokens.colorPaletteRedForeground1} />}
      <span>{now === 'unknown' ? t('An unbekannte Adresse {a}', { a: address }) : t('über {a}', { a: address })}</span>
      <a role="button" href="#" onClick={e => { e.preventDefault(); if (!busy) act(e) }} style={{ color: tokens.colorBrandForegroundLink, textDecoration: 'none' }}>
        {now === 'unknown' ? t('Als Alias anlegen') : now === 'blocked' ? t('Entsperren') : t('Sperren')}
      </a>
      {adopt && <AdoptAliasDialog address={address} sender={sender} onClose={ok => { setAdopt(false); if (ok) setNow('open') }} />}
    </span>
  )
}

/** Unbekannte Adresse als Alias anlegen: Notiz abfragen (vorbelegt mit dem Absendernamen), dann Ordner anlegen und alle Mails
 * an diese Adresse aus dem Sammelordner der Domain dorthin verschieben. */
export function AdoptAliasDialog({ address, sender, onClose }: { address: string; sender?: string | null; onClose: (ok: boolean) => void }) {
  const { notify, fail } = useApp()
  const [note, setNote] = useState(sender ?? '')
  const [busy, setBusy] = useState(false)
  const save = async () => {
    setBusy(true)
    try {
      const r = await adoptAlias(address, note.trim())
      notify(t('Alias {a} angelegt', { a: address }), r.moved ? t('{n} Mails in den Ordner verschoben', { n: r.moved }) : undefined)
      onClose(true)
    } catch (e) { fail(e); setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 460 }} onClick={e => e.stopPropagation()}>
        <DialogBody>
          <DialogTitle>{t('Als Alias anlegen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text weight="semibold">{address}</Text>
            <Field label={t('Wofür?')} hint={t('Der Alias bekommt einen eigenen Ordner; alle Mails an diese Adresse wandern aus dem Sammelordner dorthin.')}>
              <Input value={note} autoFocus onChange={(_, d) => setNote(d.value)} onKeyDown={e => { if (e.key === 'Enter') save() }} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={busy} onClick={save}>{t('Anlegen')}</Button>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
