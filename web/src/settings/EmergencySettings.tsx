import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions,
  MessageBarBody, Select, Switch, Tab, TabList, Text, makeStyles, tokens, MenuItem, MenuDivider,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowDownload20Regular, ArrowUp20Regular, Copy20Regular, Delete20Regular, Dismiss20Regular, Edit20Regular, Folder20Regular,
  Key20Regular, Print20Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
import QRCode from 'qrcode'
import { api, featureOn, fmtSize, publicOrigin, q } from '../api'
import { Item, KindIcon, parentOf } from '../files/common'
import { RichEditor, RichEditorHandle, smallImage } from '../shell/RichEditor'
import { requestTresorOpen, tresorState } from '../tresor/tresor'
import { e2eForget } from '../e2e'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'
import { Hint, Section } from './parts'
import { ContextMenu, askConfirm, useContextMenu } from '../shell/context'
import { CategorySelect } from '../shell/CategorySelect'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { printHtml } from '../print'

type State = 'open' | 'denied' | 'granted' | 'withdrawn' | 'revoked'
interface Req { id: string; requester?: string; requesterName?: string; state: State; created: string; due: string; decided?: string | null; decidedByName?: string | null }
interface Mine {
  id: string; name: string; account: boolean; admin: boolean; waitDays: number; requesters: string[]; extraVetoers: string[]; noVeto: string[]
  contacts: Contact[]
  folders: { id: number; path: string | null }[]; vetoers: { id: string; name: string; fromShare: boolean }[]
  requests: Req[]; log: { at: string; who: string; what: string }[]
  hasKey: boolean; hasText: boolean; files: PkgFile[]
}
/** Benutzer ohne Konto: fordert über den eigenen Link mit dem Notfallcode an; nach der Frist legt dmOffice ein Konto an */
interface Contact { id?: string; name: string; email?: string; token?: string; userId?: string | null; passwordSet?: boolean }
interface PkgFile { id: string; name: string; path?: string; size: number; copied: string; missing: boolean }
interface ForMe { id: string; name: string; ownerName: string; waitDays: number; content: string; account: boolean; sealedContent: boolean; hasKey: boolean; request: Req | null }
interface ToDecide { id: string; packageName: string; ownerName: string; own: boolean; requesterName: string; due: string; content: string }
interface Data { mine: Mine[]; forMe: ForMe[]; toDecide: ToDecide[]; canMail: boolean }
interface Person { id: string; login: string; displayName: string }
interface FolderOpt { id: number; path: string; members: string[]; shareable?: boolean }

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '20px', maxWidth: '760px' },
  card: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '12px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
  focus: { outline: `2px solid ${tokens.colorBrandStroke1}` },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  pick: { maxHeight: '200px', overflowY: 'auto', display: 'flex', flexDirection: 'column', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '4px 8px' },
  editor: { display: 'flex', flexDirection: 'column', height: '260px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden' },
  chosen: { display: 'flex', flexDirection: 'column', gap: '2px' },
  chosenRow: { display: 'flex', alignItems: 'center', gap: '8px', padding: '2px 0' },
  grow: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  browse: { height: '320px', overflowY: 'auto', display: 'flex', flexDirection: 'column', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '4px' },
  browseRow: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  code: { fontFamily: 'Consolas, monospace', fontSize: '22px', letterSpacing: '1px', textAlign: 'center', padding: '8px', userSelect: 'all' },
  flow: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: '8px' },
  step: { display: 'flex', flexDirection: 'column', gap: '4px', padding: '10px 12px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2, position: 'relative' },
  stepNo: { width: '22px', height: '22px', borderRadius: '50%', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '12px', fontWeight: 600, backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand },
  person: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 0', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  doc: { width: '100%', height: '50vh', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: '#fff' },
})

const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const linkOf = (id: string) => `${publicOrigin()}/#/notfall/${id}`
const contactLink = (c: Contact) => `${publicOrigin()}/n/${c.token}`
const days = (n: number) => (n === 1 ? t('1 Tag') : t('{n} Tage', { n }))
/** Tresor öffnen (der Schlüssel des Notfallcodes liegt dort); false mit verständlicher Meldung */
const tresorFor = async (fail: (e: unknown) => void) => {
  if (await requestTresorOpen()) return true
  fail(new Error((await tresorState().catch(() => null))?.exists === false
    ? t('Der Notfallcode eines Notfallpakets wird in deinem Tresor gespeichert. Bitte richte zuerst unter Sicherheit → Tresor einen Tresor ein.')
    : t('Für den Notfallcode muss dein Tresor geöffnet sein.')))
  return false
}
/** Das rote Abzeichen oben gleich neu abfragen */
export const EMERGENCY_CHANGED = 'dmoffice:emergency'

/** Einstellungen → Notfallzugang: eigene Pakete, was fuer mich eingerichtet ist, und Anfragen, bei denen ich widersprechen kann. */
export function EmergencySettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<Data | null>(null)
  const [people, setPeople] = useState<Person[]>([])
  const [edit, setEdit] = useState<Mine | 'new' | null>(null)
  const [showLog, setShowLog] = useState<string | null>(null)
  const [guideOf, setGuideOf] = useState<{ id: string; code?: string } | null>(null)
  const [asking, setAsking] = useState<ForMe | null>(null)
  const [opening, setOpening] = useState<ForMe | null>(null)
  // Rechtsklick auf ein eigenes Notfallpaket (Kennung ctxall): dieselben Aktionen wie die Knöpfe
  const menu = useContextMenu<Mine>()
  const ctxall = featureOn('ctxall')
  // Aus dem Link #/notfall/<Kennung>: dieses Paket hervorheben
  const [focus] = useState(() => location.hash.split('/')[3] ?? '')
  const load = () => api.get<Data>('/api/emergency').then(setData).catch(fail)
  useEffect(() => { load(); api.get<Person[]>('/api/pim/people').then(setPeople).catch(() => {}) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const nameOf = (id: string) => { const p = people.find(x => x.id === id); return p ? p.displayName || p.login : '?' }
  const act = async (url: string, done?: string, body?: unknown) => {
    try { await api.post(url, body); if (done) notify(done); load(); window.dispatchEvent(new Event(EMERGENCY_CHANGED)); return true } catch (e) { fail(e); return false }
  }
  if (!data) return null

  const copyLink = (p: Mine) => { copyText(linkOf(p.id)); notify(t('Link kopiert')) }
  const showGuide = (p: Mine) => setGuideOf({ id: p.id })
  // Ältere Pakete ohne Notfallcode: nachträglich erstellen
  const makeCode = async (p: Mine) => {
    if (!await tresorFor(fail)) return
    try { const r = await api.post<{ code: string }>(`/api/emergency/packages/${p.id}/keys`); load(); setGuideOf({ id: p.id, code: r.code }) } catch (e) { fail(e) }
  }
  const removePkg = async (p: Mine) => {
    if (!await askConfirm('', t('„{name}“ löschen? Gewährter Zugriff wird zurückgenommen.', { name: p.name }))) return
    try { await api.del(`/api/emergency/packages/${p.id}`); load() } catch (e) { fail(e) }
  }

  const status = (r: Req | null, account: boolean, id: string, waitDays: number) => {
    const pf = data.forMe.find(x => x.id === id)
    const ask = pf && !pf.hasKey
      ? <Text className={s.muted}>{t('Für dieses Notfallpaket gibt es noch keinen Notfallcode. Deshalb kann es nicht angefordert werden. Bitte wende dich an {owner}.', { owner: pf.ownerName })}</Text>
      : <div><Button appearance="primary" onClick={() => pf && setAsking(pf)}>{t('Notfallzugang anfordern')}</Button></div>
    if (!r || r.state === 'withdrawn') return ask
    if (r.state === 'open') return <div className={s.row}>
      <Text>{t('Angefordert – ohne Widerspruch Zugriff ab {when}.', { when: when(r.due) })}</Text>
      <Button onClick={() => act(`/api/emergency/requests/${r.id}/withdraw`)}>{t('Zurückziehen')}</Button>
    </div>
    if (r.state === 'denied') return <>
      <MessageBar intent="success"><MessageBarBody>{t('Kein Notfall – bestätigt von {name}.', { name: r.decidedByName ?? '?' })}</MessageBarBody></MessageBar>
      {ask}
    </>
    if (r.state === 'revoked') return <>
      <Text className={s.muted}>{t('Zurückgenommen von {name}.', { name: r.decidedByName ?? '?' })}</Text>
      {ask}
    </>
    const p = pf
    return <div className={s.row}>
      <Text weight="semibold">{t('Zugriff gewährt.')}</Text>
      {p?.sealedContent && <Button appearance="primary" icon={<Key20Regular />} onClick={() => setOpening(p)}>{t('Notfalltext und Dateien öffnen')}</Button>}
      {account && <Button appearance="primary" onClick={async () => {
        if (!await askConfirm('', t('Du wirst in diesem Browser im Konto angemeldet. Um wieder du selbst zu sein, meldest du dich danach ab.'))) return
        // Den eigenen Umschlag verwerfen: die Notfallsitzung laeuft ohne (der Server laesst keinen zu)
        try { await api.post(`/api/auth/emergency/${r.id}`); e2eForget(); location.hash = ''; location.reload() } catch (e) { fail(e) }
      }}>{t('Konto öffnen')}</Button>}
    </div>
  }

  return (
    <div className={s.page}>
      <Text>{t('Mit einem Notfallpaket kannst du festlegen, wer im Notfall worauf Zugriff erhält: Ordner, Dateien, einen Notfalltext oder dein ganzes Konto. Der Zugriff wird erst gewährt, wenn innerhalb einer Frist niemand bestätigt, dass kein Notfall vorliegt.')}</Text>

      {data.toDecide.length > 0 && (
        <Section title={t('Offene Anfragen')}>
          {data.toDecide.map(r => (
            <MessageBar key={r.id} intent="error" layout="multiline">
              <MessageBarBody>
                {r.own
                  ? t('{who} hat deinen Notfallzugang „{name}“ angefordert. Ohne Widerspruch bekommt {who} am {when} Zugriff auf {content}.', { who: r.requesterName, name: r.packageName, when: when(r.due), content: r.content })
                  : t('{who} hat den Notfallzugang „{name}“ von {owner} angefordert. Ohne Widerspruch bekommt {who} am {when} Zugriff auf {content}.', { who: r.requesterName, name: r.packageName, owner: r.ownerName, when: when(r.due), content: r.content })}
              </MessageBarBody>
              <MessageBarActions><Button appearance="primary" onClick={() => act(`/api/emergency/requests/${r.id}/deny`, t('Danke – kein Notfall bestätigt'))}>{t('Kein Notfall')}</Button></MessageBarActions>
            </MessageBar>
          ))}
        </Section>
      )}

      {data.forMe.length > 0 && (
        <Section title={t('Für dich eingerichtet')}>
          {data.forMe.map(p => (
            <div key={p.id} className={s.card + (p.id === focus ? ' ' + s.focus : '')}>
              <Text weight="semibold">{p.name} <span className={s.muted}>· {t('von {owner}', { owner: p.ownerName })}</span></Text>
              <Text size={200} className={s.muted}>{t('{content} · Frist {days}', { content: p.content, days: days(p.waitDays) })}</Text>
              {status(p.request, p.account, p.id, p.waitDays)}
            </div>
          ))}
        </Section>
      )}

      {focus && !data.forMe.some(p => p.id === focus) && !data.mine.some(p => p.id === focus) && (
        <MessageBar intent="warning"><MessageBarBody>{t('Diesen Notfallzugang gibt es nicht oder er ist nicht für dich eingerichtet.')}</MessageBarBody></MessageBar>
      )}

      <Section title={t('So läuft ein Notfall ab')}>
        <Flow />
      </Section>

      <Section title={t('Deine Notfallpakete')} last>
        {data.mine.length === 0 && <Text className={s.muted}>{t('Noch keine.')}</Text>}
        {data.mine.map(p => (
          <div key={p.id} className={s.card + (p.id === focus ? ' ' + s.focus : '')} onContextMenu={ctxall ? e => menu.open(e, p) : undefined}>
            <div className={s.row}>
              <Text weight="semibold" style={{ flex: 1 }}>{p.name}</Text>
              <Button size="small" icon={<Copy20Regular />} onClick={() => copyLink(p)}>{t('Link kopieren')}</Button>
              {p.hasKey && <Button size="small" icon={<Print20Regular />} onClick={() => showGuide(p)}>{t('Anleitungen')}</Button>}
              <Button size="small" icon={<Edit20Regular />} onClick={() => setEdit(p)}>{t('Bearbeiten')}</Button>
              <Button size="small" icon={<Delete20Regular />} aria-label={t('Löschen')} onClick={() => removePkg(p)} />
            </div>
            {!p.hasKey && <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>{t('Für dieses Notfallpaket gibt es noch keinen Notfallcode. Ohne Notfallcode kann das Paket nicht angefordert werden.')}</MessageBarBody>
              <MessageBarActions><Button size="small" appearance="primary" icon={<Key20Regular />} onClick={() => makeCode(p)}>{t('Notfallcode erstellen')}</Button></MessageBarActions>
            </MessageBar>}
            <Text size={200}>{[
              ...(p.hasText ? [t('Notfalltext')] : []),
              ...p.files.map(f => f.name),
              ...p.folders.map(f => f.path ?? '?'),
              ...(p.account ? [t('ganzes Konto')] : []),
              ...(p.admin ? [t('Verwaltung')] : []),
            ].join(' · ')}</Text>
            {p.files.filter(f => f.missing).map(f => <Text key={f.id} size={200} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>
              {t('„{name}“ gibt es in der Ablage nicht mehr – im Paket bleibt die Kopie vom {when}.', { name: f.name, when: when(f.copied) })}</Text>)}
            <Text size={200} className={s.muted}>
              {t('Anfragen: {names} · Widersprechen: du{others} · Frist {days}', {
                names: [...p.requesters.filter(id => !p.contacts.some(c => c.userId === id)).map(nameOf), ...p.contacts.map(c => c.name)].join(', '), others: p.vetoers.length ? ', ' + p.vetoers.map(v => v.name).join(', ') : '', days: days(p.waitDays),
              })}
            </Text>
            {p.contacts.map(c => (
              <div key={c.id} className={s.row}>
                <Text size={200} style={{ flex: 1 }}>{c.name} <span className={s.muted}>{c.email ? ' · ' + c.email : ''} · {c.userId
                  ? (c.passwordSet ? t('Konto angelegt') : t('Konto angelegt, Passwort noch nicht festgelegt'))
                  : t('ohne Konto, fordert über den eigenen Link an')}</span></Text>
                {c.token && <Button size="small" icon={<Copy20Regular />} onClick={() => { copyText(contactLink(c)); notify(t('Link kopiert')) }}>{t('Link für {name} kopieren', { name: c.name })}</Button>}
              </div>
            ))}
            {p.requests.filter(r => r.state === 'open' || r.state === 'granted').map(r => (
              <div key={r.id} className={s.row}>
                <Text size={200} style={{ flex: 1 }}>{r.state === 'open'
                  ? t('{who} hat angefragt – Zugriff ab {when}', { who: r.requesterName ?? '?', when: when(r.due) })
                  : t('{who} hat Zugriff seit {when}', { who: r.requesterName ?? '?', when: when(r.decided ?? r.due) })}</Text>
                {r.state === 'open'
                  ? <Button size="small" appearance="primary" onClick={() => act(`/api/emergency/requests/${r.id}/deny`, t('Danke – kein Notfall bestätigt'))}>{t('Kein Notfall')}</Button>
                  : <Button size="small" onClick={async () => { if (await askConfirm('', t('Zugriff zurücknehmen?'))) act(`/api/emergency/requests/${r.id}/revoke`, t('Zurückgenommen')) }}>{t('Zurücknehmen')}</Button>}
              </div>
            ))}
            {p.log.length > 0 && <div>
              <Button size="small" appearance="transparent" onClick={() => setShowLog(showLog === p.id ? null : p.id)}>{showLog === p.id ? t('Protokoll ausblenden') : t('Protokoll')}</Button>
              {showLog === p.id && p.log.map((l, i) => <Text key={i} block size={200} className={s.muted}>{when(l.at)} · {l.who} · {l.what}</Text>)}
            </div>}
          </div>
        ))}
        {menu.ctx && (() => {
          const p = menu.ctx.data
          return (
            <ContextMenu at={menu.ctx.at} onClose={menu.close}>
              <MenuItem icon={<Edit20Regular />} onClick={() => setEdit(p)}>{t('Bearbeiten')}</MenuItem>
              <MenuItem icon={<Copy20Regular />} onClick={() => copyLink(p)}>{t('Link kopieren')}</MenuItem>
              {p.hasKey
                ? <MenuItem icon={<Print20Regular />} onClick={() => showGuide(p)}>{t('Anleitungen')}</MenuItem>
                : <MenuItem icon={<Key20Regular />} onClick={() => makeCode(p)}>{t('Notfallcode erstellen')}</MenuItem>}
              {p.log.length > 0 && <MenuItem onClick={() => setShowLog(showLog === p.id ? null : p.id)}>{showLog === p.id ? t('Protokoll ausblenden') : t('Protokoll')}</MenuItem>}
              <MenuDivider />
              <MenuItem icon={<Delete20Regular />} onClick={() => removePkg(p)}>{t('Löschen')}</MenuItem>
            </ContextMenu>
          )
        })()}
        <RibbonActions><RibbonButton primary icon={<Add20Regular />} label={t('Notfallpaket anlegen')} onClick={() => setEdit('new')} /></RibbonActions>
      </Section>

      {edit && <PackageDialog pkg={edit === 'new' ? null : edit} people={people} canMail={data.canMail} onClose={(saved, made) => {
        setEdit(null)
        if (saved) load()
        if (made) setGuideOf(made)
      }} />}
      {guideOf && data.mine.find(p => p.id === guideOf.id) && <GuideDialog pkg={data.mine.find(p => p.id === guideOf.id)!} code={guideOf.code} fresh={!!guideOf.code}
        people={people} onClose={() => setGuideOf(null)} />}
      {asking && <RequestDialog pkg={asking} onClose={() => setAsking(null)}
        onAsk={async code => { if (await act(`/api/emergency/packages/${asking.id}/request`, t('Angefordert'), { code })) setAsking(null) }} />}
      {opening && <OpenDialog pkg={opening} onClose={() => setOpening(null)} />}
    </div>
  )
}

function PackageDialog({ pkg, people, canMail, onClose }: { pkg: Mine | null; people: Person[]; canMail: boolean; onClose: (saved: boolean, made?: { id: string; code: string }) => void }) {
  const s = useStyles()
  const { fail, me } = useApp()
  const [name, setName] = useState(pkg?.name ?? '')
  const [folders, setFolders] = useState<FolderOpt[]>([])
  const [files, setFiles] = useState<PkgFile[]>(pkg?.files ?? [])
  const [added, setAdded] = useState<Item[]>([])
  const [account, setAccount] = useState(pkg?.account ?? false)
  const [admin, setAdmin] = useState(pkg?.admin ?? false)
  const [requesters, setRequesters] = useState<string[]>(pkg?.requesters ?? [])
  const [contacts, setContacts] = useState<Contact[]>(pkg?.contacts ?? [])
  const [newName, setNewName] = useState('')
  const [newMail, setNewMail] = useState('')
  // Die E-Mail-Adresse ist freiwillig: darüber gehen nur Hinweise, angefordert wird mit dem Notfallcode
  const mailOk = !newMail.trim() || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(newMail.trim())
  const addContact = () => {
    if (!newName.trim() || !mailOk) return
    if (newMail.trim() && contacts.some(c => c.email?.toLowerCase() === newMail.trim().toLowerCase())) { fail(new Error(t('Diese Adresse ist schon eingetragen.'))); return }
    setContacts([...contacts, { name: newName.trim(), email: newMail.trim() }]); setNewName(''); setNewMail('')
  }
  // Konten, die dmOffice für Personen ohne Konto angelegt hat, stehen bei ihnen (nicht doppelt in der Liste)
  const fromContacts = contacts.map(c => c.userId).filter((x): x is string => !!x)
  const [extra, setExtra] = useState<string[]>(pkg?.extraVetoers ?? [])
  const [noVeto, setNoVeto] = useState<string[]>(pkg?.noVeto ?? [])
  const [wait, setWait] = useState(pkg?.waitDays ?? 7)
  const [picker, setPicker] = useState<'folder' | 'file' | null>(null)
  const [busy, setBusy] = useState(false)
  // Notfalltext: liegt verschlüsselt im Paket, lesbar mit dem Schlüssel aus dem Tresor (der Dialog „Tresor öffnen“ kommt von selbst)
  const editor = useRef<RichEditorHandle>(null)
  const [text, setText] = useState<string | null>(pkg?.hasText ? null : '')
  const [textFailed, setTextFailed] = useState(false)
  const [textChanged, setTextChanged] = useState(false)
  const loadText = () => {
    if (!pkg?.hasText) return
    setTextFailed(false)
    api.get<{ html: string }>(`/api/emergency/packages/${pkg.id}/text`).then(r => setText(r.html)).catch(e => { setTextFailed(true); fail(e) })
  }
  useEffect(loadText, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Gewählte Ordner mit Pfad und Mitgliedern ihrer Freigabe
  const loadFolders = (ids: number[]) => {
    if (ids.length === 0) { setFolders([]); return }
    api.get<FolderOpt[]>('/api/emergency/folders?' + q({ ids: ids.join(',') })).then(setFolders).catch(fail)
  }
  useEffect(() => loadFolders(pkg?.folders.map(f => f.id) ?? []), []) // eslint-disable-line react-hooks/exhaustive-deps
  // Wer die gewaehlten Ordner schon geteilt bekommt, darf widersprechen - ausser, du nimmst ihn heraus
  const fromShares = useMemo(() => [...new Set(folders.flatMap(f => f.members))], [folders])
  const nameOf = (id: string) => { const p = people.find(x => x.id === id); return p ? p.displayName || p.login : '?' }
  const toggle = (list: string[], set: (v: string[]) => void, id: string, on: boolean) => set(on ? [...list, id] : list.filter(x => x !== id))
  // Personen alphabetisch; zum Hinzufügen nur, wer noch nicht gewählt ist
  const byName = (a: Person, b: Person) => (a.displayName || a.login).localeCompare(b.displayName || b.login)
  const choices = (skip: string[]) => people.filter(p => !skip.includes(p.id)).sort(byName).map(p => ({ id: p.id, label: p.displayName || p.login, extra: p.displayName ? ' · ' + p.login : '' }))
  const personRow = (id: string, onRemove: () => void, note?: string) => <div key={id} className={s.chosenRow}>
    <Text className={s.grow}>{nameOf(id)}{note && <span className={s.muted}> · {note}</span>}</Text>
    <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Entfernen')} onClick={onRemove} />
  </div>
  const hasText = () => (editor.current?.text() ?? text ?? '').trim().length > 0 || /<img/i.test(editor.current?.html() ?? '')
  // Schon angelegt (etwa beim ersten Speichern, dann scheiterte etwas danach): weiter dasselbe Paket
  const savedId = useRef(pkg?.id)
  const save = async () => {
    setBusy(true)
    try {
      const html = editor.current?.html() ?? ''
      const content = hasText() || files.length > 0 || added.length > 0
      // Jedes Paket bekommt einen Notfallcode, unabhängig vom Inhalt: sein Schlüssel kommt in den Tresor - vorher öffnen
      const needKey = !pkg?.hasKey
      if (needKey && !await tresorFor(fail)) return
      const r = await api.put<{ id: string }>('/api/emergency/packages', {
        id: savedId.current, name, folders: folders.map(f => f.id), account, admin, requesters, extraVetoers: extra, noVeto, waitDays: wait, content,
        contacts: contacts.map(c => ({ id: c.id, name: c.name, email: c.email })),
      })
      const id = savedId.current = savedId.current ?? r.id
      // Danach gleich die Anleitungen zum Ausdrucken
      let made: { id: string; code: string } | undefined
      if (needKey) made = { id, code: (await api.post<{ code: string }>(`/api/emergency/packages/${id}/keys`)).code }
      if (textChanged && (pkg?.hasKey || made)) await api.put(`/api/emergency/packages/${id}/text`, { html: hasText() ? html : '' })
      for (const f of pkg?.files ?? []) if (!files.some(x => x.id === f.id)) await api.del(`/api/emergency/packages/${id}/files/${f.id}`)
      if (added.length > 0) {
        const res = await api.post<{ failed: string[] }>(`/api/emergency/packages/${id}/files`, { ids: added.map(i => i.fileId) })
        if (res.failed.length) fail(new Error(res.failed.join('\n')))
      }
      onClose(true, made)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  // Schritte wie bei einem Assistenten; alle bleiben geladen (der Notfalltext geht beim Wechseln nicht verloren)
  const STEPS = [['text', t('Notfalltext')], ['content', t('Inhalt')], ['people', t('Berechtigte')]] as const
  const [step, setStep] = useState(0)
  const pane = (i: number) => ({ display: step === i ? 'flex' : 'none', flexDirection: 'column' as const, gap: 14 })
  const last = step === STEPS.length - 1
  const nothing = folders.length === 0 && files.length === 0 && added.length === 0 && !account && !admin && !textChanged && !pkg?.hasText
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{pkg ? t('Notfallpaket bearbeiten') : t('Notfallpaket anlegen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14, minHeight: 460 }}>
            <TabList selectedValue={step} onTabSelect={(_, d) => setStep(d.value as number)}>
              {STEPS.map(([k, label], i) => <Tab key={k} value={i}>{`${i + 1}. ${label}`}</Tab>)}
            </TabList>
            <div style={pane(0)}>
            <Field label={t('Name')}><Input value={name} onChange={(_, d) => setName(d.value)} placeholder={t('z. B. Familie')} /></Field>
            <Field label={t('Notfalltext')}>
              {text == null
                ? <div className={s.row}>
                  <Text className={s.muted}>{textFailed ? t('Zum Bearbeiten des Notfalltexts den Tresor öffnen.') : t('Wird geladen …')}</Text>
                  {textFailed && <Button size="small" onClick={loadText}>{t('Tresor öffnen')}</Button>}
                </div>
                : <div className={s.editor}><RichEditor ref={editor} initial={text} headings onChange={() => setTextChanged(true)} onImage={smallImage} /></div>}
              <Hint>{t('Im Notfalltext kannst du festhalten, was im Notfall zu tun ist, zum Beispiel wo wichtige Unterlagen liegen und an wen man sich wenden kann. Bilder können eingefügt werden, auch aus der Zwischenablage. Der Notfalltext wird verschlüsselt gespeichert und kann nur mit dem Notfallcode geöffnet werden.')}</Hint>
            </Field>
            </div>
            <div style={pane(1)}>
            <Field label={t('Dateien')}>
              <div className={s.chosen}>
                {files.map(f => <div key={f.id} className={s.chosenRow}>
                  <KindIcon item={{ name: f.name, isDir: false }} />
                  <Text className={s.grow} title={f.path}>{f.name}</Text>
                  <Text size={200} className={s.muted}>{f.missing ? t('Herkunft fehlt – Kopie vom {when}', { when: when(f.copied) }) : fmtSize(f.size)}</Text>
                  <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Entfernen')} onClick={() => setFiles(files.filter(x => x.id !== f.id))} />
                </div>)}
                {added.map(i => <div key={i.path} className={s.chosenRow}>
                  <KindIcon item={i} />
                  <Text className={s.grow} title={i.path}>{i.name}</Text>
                  <Text size={200} className={s.muted}>{fmtSize(i.size)}</Text>
                  <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Entfernen')} onClick={() => setAdded(added.filter(x => x.path !== i.path))} />
                </div>)}
                <div><Button size="small" icon={<Add20Regular />} onClick={() => setPicker('file')}>{t('Dateien hinzufügen')}</Button></div>
              </div>
              <Hint>{t('Das Paket enthält eine verschlüsselte Kopie. Wird die Datei in der Ablage ersetzt, erneuert dmOffice die Kopie.')}</Hint>
            </Field>
            <Field label={t('Ordner')}>
              <div className={s.chosen}>
                {folders.map(f => <div key={f.id} className={s.chosenRow}>
                  <Folder20Regular />
                  <Text className={s.grow}>{f.path}</Text>
                  <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Entfernen')} onClick={() => setFolders(folders.filter(x => x.id !== f.id))} />
                </div>)}
                <div><Button size="small" icon={<Add20Regular />} onClick={() => setPicker('folder')}>{t('Ordner hinzufügen')}</Button></div>
              </div>
              <Hint>{t('Zum Lesen geteilt, keine Kopie – immer der aktuelle Stand.')}</Hint>
            </Field>
            <Switch checked={account} onChange={(_, d) => setAccount(d.checked)} label={t('Das ganze Konto (Mails, Kalender, Kontakte, Dateien – ohne Sicherheitseinstellungen)')} />
            {(me.isAdmin || admin) && <Switch checked={admin} disabled={!me.isAdmin} onChange={(_, d) => setAdmin(d.checked)} label={t('Verwaltung (Admin-Rechte)')} />}
            </div>
            <div style={pane(2)}>
            <Field label={t('Wer das Paket anfordern darf')}>
              <div className={s.chosen}>
                {people.length === 0 && contacts.length === 0 && <Text size={200} className={s.muted}>{t('Außer dir gibt es auf diesem Server noch keine weiteren Benutzer.')}</Text>}
                {requesters.filter(id => !fromContacts.includes(id)).map(id => personRow(id, () => toggle(requesters, setRequesters, id, false)))}
                {choices(requesters).length > 0 && <CategorySelect size="small" value={null} style={{ maxWidth: 320 }} placeholder={t('Benutzer hinzufügen – zum Suchen den Namen eintippen')}
                  items={choices(requesters)} onChange={id => { toggle(requesters, setRequesters, id, true); setExtra(extra.filter(x => x !== id)) }} />}
              </div>
            </Field>
            <Field label={t('Benutzer ohne Konto')}>
              <div className={s.chosen}>
                {contacts.map((c, i) => <div key={c.id ?? 'neu' + i} className={s.chosenRow}>
                  <Text className={s.grow}>{c.name} <span className={s.muted}>{c.email ? ' · ' + c.email : ''}{c.userId ? ' · ' + t('Konto angelegt') : ''}</span></Text>
                  <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Entfernen')} onClick={() => {
                    setContacts(contacts.filter(x => x !== c))
                    if (c.userId) setRequesters(requesters.filter(x => x !== c.userId))
                  }} />
                </div>)}
                <div className={s.row}>
                  <Input size="small" value={newName} onChange={(_, d) => setNewName(d.value)} placeholder={t('Name')} style={{ maxWidth: 200 }}
                    onKeyDown={e => { if (e.key === 'Enter') addContact() }} />
                  <Input size="small" type="email" value={newMail} onChange={(_, d) => setNewMail(d.value)} placeholder={t('E-Mail-Adresse (freiwillig)')} style={{ maxWidth: 240 }}
                    onKeyDown={e => { if (e.key === 'Enter') addContact() }} />
                  <Button size="small" icon={<Add20Regular />} disabled={!newName.trim() || !mailOk} onClick={addContact}>{t('Hinzufügen')}</Button>
                </div>
              </div>
              <Hint>{t('Für jeden hier eingetragenen Benutzer erstellt dmOffice einen eigenen Link. Mit diesem Link und dem Notfallcode kann der Benutzer das Notfallpaket ohne Anmeldung anfordern. Nach Ablauf der Frist legt dmOffice für ihn ein Konto an.')}
                {' '}{canMail
                  ? t('Ist eine E-Mail-Adresse eingetragen, erhält der Benutzer zusätzlich Hinweise per Mail.')
                  : t('Hinweise per Mail kann dmOffice nur senden, wenn in deinem Konto ein Mailkonto eingerichtet ist.')}</Hint>
            </Field>
            <Field label={t('Wer widersprechen darf')}>
              <Text size={200}>{t('Du selbst kannst jederzeit widersprechen. Außerdem können alle Benutzer widersprechen, mit denen die gewählten Ordner geteilt sind, und alle Benutzer, die du hier hinzufügst.')}</Text>
              <div className={s.chosen}>
                {fromShares.length === 0 && !extra.some(id => !requesters.includes(id)) && <Text size={200} className={s.muted}>{t('Außer dir kann bisher niemand widersprechen.')}</Text>}
                {fromShares.map(id => <Checkbox key={id} label={nameOf(id) + ' · ' + t('Ordner geteilt')} checked={!noVeto.includes(id)}
                  onChange={(_, d) => toggle(noVeto, setNoVeto, id, !d.checked)} />)}
                {extra.filter(id => !fromShares.includes(id) && !requesters.includes(id)).map(id => personRow(id, () => toggle(extra, setExtra, id, false)))}
                {choices([...fromShares, ...requesters, ...extra]).length > 0 && <CategorySelect size="small" value={null} style={{ maxWidth: 320 }}
                  placeholder={t('Benutzer hinzufügen – zum Suchen den Namen eintippen')} items={choices([...fromShares, ...requesters, ...extra])} onChange={id => toggle(extra, setExtra, id, true)} />}
              </div>
            </Field>
            <Field label={t('Frist für einen Widerspruch')}>
              <Select value={String(wait)} onChange={(_, d) => setWait(Number(d.value))}>
                {[1, 2, 3, 5, 7, 14, 30].map(n => <option key={n} value={n}>{n === 1 ? t('1 Tag') : t('{n} Tage', { n })}</option>)}
              </Select>
            </Field>
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            {step > 0 && <Button onClick={() => setStep(step - 1)}>{t('Zurück')}</Button>}
            {!last && <Button appearance={pkg ? 'secondary' : 'primary'} onClick={() => setStep(step + 1)}>{t('Weiter')}</Button>}
            {(last || pkg) && <Button appearance="primary" onClick={save} disabled={busy || !name.trim() || requesters.length + contacts.length === 0 || nothing}
              title={!name.trim() ? t('Bitte einen Namen angeben.') : requesters.length + contacts.length === 0 ? t('Bitte wähle mindestens einen Benutzer, der das Paket anfordern darf.') : nothing ? t('Das Paket ist noch leer.') : undefined}>{t('Speichern')}</Button>}
          </DialogActions>
        </DialogBody>
      {picker && <AblagePicker mode={picker} onClose={() => setPicker(null)} onPick={list => {
        setPicker(null)
        if (picker === 'folder') loadFolders([...new Set([...folders.map(f => f.id), ...list.map(i => i.fileId!)])])
        else setAdded(a => [...a, ...list.filter(i => !a.some(x => x.path === i.path) && !files.some(f => f.path === i.path))])
      }} />}
      </DialogSurface>
    </Dialog>
  )
}


/** Ordnerbrowser der eigenen Ablage: Ordner („Diesen Ordner“ oder Unterordner ankreuzen) oder Dateien wählen. */
function AblagePicker({ mode, onClose, onPick }: { mode: 'folder' | 'file'; onClose: () => void; onPick: (items: Item[]) => void }) {
  const s = useStyles()
  const [path, setPath] = useState('')
  const [items, setItems] = useState<Item[] | null>(null)
  const [here, setHere] = useState<Item | null>(null)
  const [chosen, setChosen] = useState<Item[]>([])
  useEffect(() => {
    setItems(null)
    // Nur Eigenes: keine geteilten Ordner anderer, nicht der Tresor
    api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => setItems(d.items.filter(i => !i.owner && !i.path.startsWith('dmVAULT') && !i.path.startsWith('tresor:'))))
      .catch(() => setItems([]))
    if (path) api.get<{ items: Item[] }>('/api/files/?' + q({ path: parentOf(path) })).then(d => setHere(d.items.find(i => i.path === path) ?? null)).catch(() => setHere(null))
    else setHere(null)
  }, [path])
  const toggle = (i: Item) => setChosen(c => (c.some(x => x.path === i.path) ? c.filter(x => x.path !== i.path) : [...c, i]))
  const shown = (items ?? []).filter(i => i.isDir || mode === 'file')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{mode === 'folder' ? t('Ordner hinzufügen') : t('Dateien hinzufügen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div className={s.row}>
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('folder|Nach oben')} />
              <Folder20Regular /><Text className={s.grow}>{path || t('Meine Dateien')}</Text>
              {mode === 'folder' && here && <Checkbox label={t('Diesen Ordner')} checked={chosen.some(x => x.path === here.path)} onChange={() => toggle(here)} />}
            </div>
            <div className={s.browse}>
              {items == null && <Text className={s.muted}>{t('Wird geladen …')}</Text>}
              {items != null && shown.length === 0 && <Text className={s.muted}>{mode === 'folder' ? t('Keine Unterordner.') : t('Dieser Ordner ist leer.')}</Text>}
              {shown.map(i => <div key={i.path} className={s.browseRow} onClick={() => i.isDir ? setPath(i.path) : toggle(i)}>
                {(mode === 'folder' || !i.isDir) && <Checkbox checked={chosen.some(x => x.path === i.path)} onClick={e => e.stopPropagation()} onChange={() => toggle(i)} />}
                <KindIcon item={i} /><Text className={s.grow}>{i.name}</Text>
                {!i.isDir && <Text size={200} className={s.muted}>{fmtSize(i.size)}</Text>}
              </div>)}
            </div>
            <Text size={200} className={s.muted}>{chosen.length ? chosen.map(c => c.name).join(', ') : mode === 'folder' ? t('Ordner öffnen mit Klick, wählen mit dem Kästchen.') : t('Ordner öffnen mit Klick, Dateien wählen mit Klick.')}</Text>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={chosen.length === 0} onClick={() => onPick(chosen)}>{chosen.length > 1 ? t('{n} hinzufügen', { n: chosen.length }) : t('Hinzufügen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Ablaufdiagramm: vier Schritte eines Notfalls, für alle Inhalte gleich */
function Flow() {
  const s = useStyles()
  const steps = [
    [t('Vorbereiten'), t('Du legst ein Notfallpaket an und gibst jedem Berechtigten seine Anleitung mit dem Link und dem Notfallcode, am besten ausgedruckt.')],
    [t('Anfordern'), t('Im Notfall öffnet ein Berechtigter seinen Link und fordert den Zugriff mit dem Notfallcode an.')],
    [t('Frist'), t('Alle, die widersprechen dürfen, werden täglich im Chat benachrichtigt. Mit „Kein Notfall“ kann jeder von ihnen die Anforderung beenden.')],
    [t('Zugriff'), t('Widerspricht bis zum Ende der Frist niemand, erhält der Berechtigte Zugriff. Notfalltext und Dateien öffnet er mit demselben Notfallcode.')],
  ]
  return <>
    <div className={s.flow}>
      {steps.map(([title, text], i) => <div key={i} className={s.step}>
        <div className={s.row}><span className={s.stepNo}>{i + 1}</span><Text weight="semibold">{title}</Text></div>
        <Text size={200}>{text}</Text>
      </div>)}
    </div>
    <Hint>{t('Benutzer ohne Konto auf diesem Server gehen denselben Weg ohne Anmeldung. Nach Ablauf der Frist legt dmOffice für sie ein Konto an. Das Passwort für dieses Konto legen sie über ihren Link mit dem Notfallcode fest.')}</Hint>
  </>
}

interface Guide { key: string; name: string; link: string; contact: boolean }

/** Anleitungen zum Ausdrucken: je Berechtigtem Link, Notfallcode und die Schritte */
function GuideDialog({ pkg, code: given, fresh, people, onClose }: { pkg: Mine; code?: string; fresh: boolean; people: Person[]; onClose: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [code, setCode] = useState(given ?? '')
  const [qrs, setQrs] = useState<Record<string, string>>({})
  useEffect(() => {
    if (given) return
    (async () => {
      if (!await tresorFor(fail)) { onClose(); return }
      try { setCode((await api.get<{ code: string }>(`/api/emergency/packages/${pkg.id}/code`)).code) } catch (e) { fail(e); onClose() }
    })()
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const contactUsers = new Set(pkg.contacts.map(c => c.userId).filter(Boolean) as string[])
  const guides: Guide[] = useMemo(() => [
    ...pkg.requesters.filter(id => !contactUsers.has(id)).map(id => {
      const p = people.find(x => x.id === id)
      return { key: id, name: p ? p.displayName || p.login : '?', link: linkOf(pkg.id), contact: false }
    }),
    ...pkg.contacts.filter(c => c.token).map(c => ({ key: c.id ?? c.name, name: c.name, link: contactLink(c), contact: true })),
  ], [pkg, people]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const links = [...new Set([code, ...guides.map(g => g.link)])].filter(Boolean)
    Promise.all(links.map(l => QRCode.toDataURL(l, { width: 300, margin: 1 }).then(u => [l, u] as const).catch(() => [l, ''] as const)))
      .then(r => setQrs(Object.fromEntries(r)))
  }, [code, guides])

  const sealed = pkg.hasText || pkg.files.length > 0
  const esc = (x: string) => x.replace(/&/g, '&amp;').replace(/</g, '&lt;')
  const page = (g: Guide) => {
    const steps = g.contact
      ? [t('Öffne den Link. Eine Anmeldung ist dafür nicht nötig.'),
        t('Gib den Notfallcode ein und fordere den Zugriff an.'),
        t('Widerspricht bis zum Ende der Frist niemand, legt dmOffice für dich ein Konto an. Über denselben Link kannst du dann mit dem Notfallcode dein Passwort festlegen und dich anmelden.')]
      : [t('Öffne den Link und melde dich mit deinem Konto an.'),
        t('Wähle „Notfallzugang anfordern“ und gib den Notfallcode ein.'),
        t('Widerspricht bis zum Ende der Frist niemand, erhältst du Zugriff.')]
    if (pkg.folders.length > 0) steps.push(t('Die Ordner des Notfallpakets findest du danach in der Ablage unter „Mit mir geteilt“.'))
    if (sealed) steps.push(t('Notfalltext und Dateien kannst du unter Einstellungen → Notfallzugang mit dem Notfallcode öffnen.'))
    if (pkg.account) steps.push(t('Über „Konto öffnen“ unter Einstellungen → Notfallzugang kannst du dich im Konto anmelden.'))
    return `<section style="page-break-after:always;font-family:sans-serif;padding:24px">`
      + `<h2>${esc(t('Notfallpaket „{name}“', { name: pkg.name }))}</h2>`
      + `<p>${esc(t('Für {name}', { name: g.name }))}</p>`
      + `<h3>${esc(t('Link'))}</h3><p style="word-break:break-all">${esc(g.link)}</p>${qrs[g.link] ? `<img src="${qrs[g.link]}" width="180">` : ''}`
      + `<h3>${esc(t('Notfallcode'))}</h3><p style="font:26px Consolas,monospace;letter-spacing:1px">${esc(code)}</p>`
      + `<h3>${esc(t('So gehst du im Notfall vor'))}</h3><ol>${steps.map(x => `<li style="margin-bottom:6px">${esc(x)}</li>`).join('')}</ol>`
      + `<p>${esc(t('Bewahre diese Anleitung sicher auf. Wer den Link und den Notfallcode kennt, kann das Notfallpaket anfordern.'))}</p></section>`
  }
  const print = (list: Guide[]) => {
    void printHtml(`<!doctype html><meta charset="utf-8"><title>${esc(t('Anleitungen'))}</title><body style="margin:0">${list.map(page).join('')}</body>`)
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('Anleitungen für „{name}“', { name: pkg.name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {fresh && <MessageBar intent="success"><MessageBarBody>{t('Der Notfallcode wurde erstellt.')}</MessageBarBody></MessageBar>}
            <Text>{t('Jeder Berechtigte braucht im Notfall seinen Link und den Notfallcode. Am besten druckst du für jeden Berechtigten seine Anleitung aus und gibst sie ihm. Die Anleitung erklärt auch die einzelnen Schritte.')}</Text>
            {code ? <>
              <div className={s.code}>{code}</div>
              {qrs[code] && <img src={qrs[code]} alt="" style={{ width: 160, alignSelf: 'center' }} />}
            </> : <Text className={s.muted}>{t('Lädt …')}</Text>}
            <Field label={t('Berechtigte')}>
              <div className={s.chosen}>
                {guides.length === 0 && <Text className={s.muted}>{t('Für dieses Notfallpaket ist noch niemand berechtigt.')}</Text>}
                {guides.map(g => <div key={g.key} className={s.person}>
                  <Text className={s.grow}>{g.name} <span className={s.muted}>· {g.contact ? t('ohne Konto') : t('mit Konto')}</span></Text>
                  <Button size="small" icon={<Copy20Regular />} onClick={() => copyText(g.link)} title={t('Link kopieren')} />
                  <Button size="small" icon={<Print20Regular />} disabled={!code} onClick={() => print([g])}>{t('Anleitung drucken')}</Button>
                </div>)}
              </div>
            </Field>
            <Hint>{t('Den Notfallcode und die Anleitungen kann dmOffice dir jederzeit wieder anzeigen, solange dein Tresor geöffnet ist.')}</Hint>
          </DialogContent>
          <DialogActions>
            {guides.length > 1 && <Button icon={<Print20Regular />} disabled={!code} onClick={() => print(guides)}>{t('Alle drucken')}</Button>}
            <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Anfordern mit dem Notfallcode (Benutzer mit Konto) */
function RequestDialog({ pkg, onClose, onAsk }: { pkg: ForMe; onClose: () => void; onAsk: (code: string) => void }) {
  const [code, setCode] = useState('')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{t('Notfallzugang anfordern')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Text>{t('Liegt ein Notfall vor, kannst du hier mit dem Notfallcode den Zugriff auf „{name}“ anfordern. Danach werden alle benachrichtigt, die widersprechen dürfen. Widerspricht innerhalb von {n} niemand, erhältst du Zugriff.', { name: pkg.name, n: pkg.waitDays === 1 ? t('einem Tag') : t('{n} Tagen', { n: pkg.waitDays }) })}</Text>
            <Field label={t('Notfallcode')} hint={t('Den Notfallcode findest du auf der Anleitung, die du von {owner} erhalten hast.', { owner: pkg.ownerName })}>
              <Input value={code} onChange={(_, d) => setCode(d.value)} placeholder="XXXX-XXXX-XXXX-XXXX-XXXX" autoFocus
                onKeyDown={e => { if (e.key === 'Enter' && code.trim()) onAsk(code) }} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={!code.trim()} onClick={() => onAsk(code)}>{t('Anfordern')}</Button>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Nach der Freigabe: mit dem Notfallcode Notfalltext (zuerst) und Dateien öffnen. */
function OpenDialog({ pkg, onClose }: { pkg: ForMe; onClose: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [code, setCode] = useState('')
  const [opened, setOpened] = useState<{ html: string; files: PkgFile[] } | null>(null)
  const open = async () => {
    try { setOpened(await api.post<{ html: string; files: PkgFile[] }>(`/api/emergency/packages/${pkg.id}/open`, { code })) } catch (e) { fail(e) }
  }
  const download = async (f: PkgFile) => {
    try {
      const r = await window.fetch(`/api/emergency/packages/${pkg.id}/files/${f.id}`, { credentials: 'same-origin', headers: { 'X-Notfallcode': code } })
      if (!r.ok) throw new Error((await r.json().catch(() => null))?.error ?? t('Herunterladen fehlgeschlagen.'))
      const url = URL.createObjectURL(await r.blob())
      const a = document.createElement('a'); a.href = url; a.download = f.name; a.click()
      setTimeout(() => URL.revokeObjectURL(url), 10_000)
    } catch (e) { fail(e) }
  }
  // Der Text des Besitzers in einem abgeschotteten Rahmen: keine Skripte, keine Formulare
  const doc = opened ? `<!doctype html><meta charset="utf-8"><style>body{font-family:'Segoe UI',sans-serif;font-size:14px;color:#222;margin:16px}img{max-width:100%}</style>${opened.html}` : ''
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ von {owner}', { name: pkg.name, owner: pkg.ownerName })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {!opened ? <>
              <Field label={t('Notfallcode')} hint={t('Den Notfallcode findest du auf der Anleitung, die du erhalten hast.')}>
                <Input value={code} onChange={(_, d) => setCode(d.value)} placeholder="XXXX-XXXX-XXXX-XXXX-XXXX" onKeyDown={e => { if (e.key === 'Enter' && code.trim()) open() }} />
              </Field>
            </> : <>
              {opened.html ? <iframe className={s.doc} sandbox="" srcDoc={doc} title={t('Notfalltext')} /> : <Text className={s.muted}>{t('Kein Notfalltext.')}</Text>}
              {opened.files.length > 0 && <Field label={t('Dateien')}>
                <div className={s.chosen}>
                  {opened.files.map(f => <div key={f.id} className={s.chosenRow}>
                    <KindIcon item={{ name: f.name, isDir: false }} />
                    <Text className={s.grow}>{f.name}</Text>
                    <Text size={200} className={s.muted}>{fmtSize(f.size)} · {t('Stand {when}', { when: when(f.copied) })}</Text>
                    <Button size="small" icon={<ArrowDownload20Regular />} onClick={() => download(f)}>{t('Herunterladen')}</Button>
                  </div>)}
                </div>
              </Field>}
            </>}
          </DialogContent>
          <DialogActions>
            {!opened && <Button appearance="primary" disabled={!code.trim()} onClick={open}>{t('Öffnen')}</Button>}
            <Button onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
