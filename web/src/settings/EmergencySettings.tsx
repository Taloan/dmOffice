import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions,
  MessageBarBody, Select, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowDownload20Regular, ArrowUp20Regular, Copy20Regular, Delete20Regular, Dismiss20Regular, Edit20Regular, Folder20Regular,
  Key20Regular, Print20Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
import QRCode from 'qrcode'
import { api, fmtSize, q } from '../api'
import { Item, KindIcon, parentOf } from '../files/common'
import { RichEditor, RichEditorHandle } from '../shell/RichEditor'
import { requestTresorOpen, tresorState } from '../tresor/tresor'
import { e2eForget } from '../e2e'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'
import { Hint, PageTitle, Section } from './parts'
import { askConfirm } from '../shell/context'

type State = 'open' | 'denied' | 'granted' | 'withdrawn' | 'revoked'
interface Req { id: string; requester?: string; requesterName?: string; state: State; created: string; due: string; decided?: string | null; decidedByName?: string | null }
interface Mine {
  id: string; name: string; account: boolean; admin: boolean; waitDays: number; requesters: string[]; extraVetoers: string[]; noVeto: string[]
  folders: { id: number; path: string | null }[]; vetoers: { id: string; name: string; fromShare: boolean }[]
  requests: Req[]; log: { at: string; who: string; what: string }[]
  hasKey: boolean; hasText: boolean; files: PkgFile[]
}
interface PkgFile { id: string; name: string; path?: string; size: number; copied: string; missing: boolean }
interface ForMe { id: string; name: string; ownerName: string; waitDays: number; content: string; account: boolean; sealedContent: boolean; request: Req | null }
interface ToDecide { id: string; packageName: string; ownerName: string; own: boolean; requesterName: string; due: string; content: string }
interface Data { mine: Mine[]; forMe: ForMe[]; toDecide: ToDecide[] }
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
  doc: { width: '100%', height: '50vh', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: '#fff' },
})

const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const linkOf = (id: string) => `${location.origin}/#/notfall/${id}`
const days = (n: number) => (n === 1 ? t('1 Tag') : t('{n} Tage', { n }))
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
  const [codeOf, setCodeOf] = useState<{ name: string; code: string } | null>(null)
  const [opening, setOpening] = useState<ForMe | null>(null)
  // Aus dem Link #/notfall/<Kennung>: dieses Paket hervorheben
  const [focus] = useState(() => location.hash.split('/')[3] ?? '')
  const load = () => api.get<Data>('/api/emergency').then(setData).catch(fail)
  useEffect(() => { load(); api.get<Person[]>('/api/pim/people').then(setPeople).catch(() => {}) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const nameOf = (id: string) => { const p = people.find(x => x.id === id); return p ? p.displayName || p.login : '?' }
  const act = async (url: string, done?: string) => {
    try { await api.post(url); if (done) notify(done); load(); window.dispatchEvent(new Event(EMERGENCY_CHANGED)) } catch (e) { fail(e) }
  }
  if (!data) return null

  const status = (r: Req | null, account: boolean, id: string, waitDays: number) => {
    const ask = <div><Button appearance="primary" onClick={async () => {
      if (await askConfirm('', t('Alle, die widersprechen dürfen, werden benachrichtigt. Bestätigt niemand innerhalb von {n} Tagen, dass kein Notfall vorliegt, bekommst du Zugriff.', { n: waitDays })))
        act(`/api/emergency/packages/${id}/request`, t('Angefordert'))
    }}>{t('Notfallzugang anfordern')}</Button></div>
    if (!r || r.state === 'withdrawn') return ask
    if (r.state === 'open') return <div className={s.row}>
      <Text>{t('Angefordert – ohne Widerspruch Zugriff ab {when}.', { when: when(r.due) })}</Text>
      <Button onClick={() => act(`/api/emergency/requests/${r.id}/withdraw`)}>{t('Zurückziehen')}</Button>
    </div>
    if (r.state === 'denied') return <>
      <MessageBar intent="success"><MessageBarBody>{t('Alles gut, es gibt keinen Notfall – bestätigt von {name}.', { name: r.decidedByName ?? '?' })}</MessageBarBody></MessageBar>
      {ask}
    </>
    if (r.state === 'revoked') return <>
      <Text className={s.muted}>{t('Zurückgenommen von {name}.', { name: r.decidedByName ?? '?' })}</Text>
      {ask}
    </>
    const p = data.forMe.find(x => x.id === id)
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
      <PageTitle>{t('Notfallzugang')}</PageTitle>
      <Text>{t('Wer im Notfall worauf Zugriff bekommt – erst wenn innerhalb der Frist niemand bestätigt, dass kein Notfall vorliegt.')}</Text>

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

      <Section title={t('Deine Notfallpakete')} last>
        {data.mine.length === 0 && <Text className={s.muted}>{t('Noch keine.')}</Text>}
        {data.mine.map(p => (
          <div key={p.id} className={s.card + (p.id === focus ? ' ' + s.focus : '')}>
            <div className={s.row}>
              <Text weight="semibold" style={{ flex: 1 }}>{p.name}</Text>
              <Button size="small" icon={<Copy20Regular />} onClick={() => { copyText(linkOf(p.id)); notify(t('Link kopiert')) }}>{t('Link kopieren')}</Button>
              {p.hasKey && <Button size="small" icon={<Key20Regular />} onClick={async () => {
                try { setCodeOf({ name: p.name, code: (await api.get<{ code: string }>(`/api/emergency/packages/${p.id}/code`)).code }) } catch (e) { fail(e) }
              }}>{t('Notfallcode')}</Button>}
              <Button size="small" icon={<Edit20Regular />} onClick={() => setEdit(p)}>{t('Bearbeiten')}</Button>
              <Button size="small" icon={<Delete20Regular />} aria-label={t('Löschen')} onClick={async () => {
                if (!await askConfirm('', t('„{name}“ löschen? Gewährter Zugriff wird zurückgenommen.', { name: p.name }))) return
                try { await api.del(`/api/emergency/packages/${p.id}`); load() } catch (e) { fail(e) }
              }} />
            </div>
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
                names: p.requesters.map(nameOf).join(', '), others: p.vetoers.length ? ', ' + p.vetoers.map(v => v.name).join(', ') : '', days: days(p.waitDays),
              })}
            </Text>
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
        <div><Button icon={<Add20Regular />} onClick={() => setEdit('new')}>{t('Notfallpaket anlegen')}</Button></div>
        <Hint>{t('Den Link schickst du den Personen, die anfragen dürfen – sie melden sich an und fordern an. Vorher sieht niemand etwas.')}</Hint>
      </Section>

      {edit && <PackageDialog pkg={edit === 'new' ? null : edit} people={people} onClose={(saved, made) => {
        setEdit(null)
        if (saved) load()
        if (made) setCodeOf(made)
      }} />}
      {codeOf && <CodeDialog name={codeOf.name} code={codeOf.code} fresh={false} onClose={() => setCodeOf(null)} />}
      {opening && <OpenDialog pkg={opening} onClose={() => setOpening(null)} />}
    </div>
  )
}

function PackageDialog({ pkg, people, onClose }: { pkg: Mine | null; people: Person[]; onClose: (saved: boolean, code?: { name: string; code: string }) => void }) {
  const s = useStyles()
  const { fail, me } = useApp()
  const [name, setName] = useState(pkg?.name ?? '')
  const [folders, setFolders] = useState<FolderOpt[]>([])
  const [files, setFiles] = useState<PkgFile[]>(pkg?.files ?? [])
  const [added, setAdded] = useState<Item[]>([])
  const [account, setAccount] = useState(pkg?.account ?? false)
  const [admin, setAdmin] = useState(pkg?.admin ?? false)
  const [requesters, setRequesters] = useState<string[]>(pkg?.requesters ?? [])
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
  const hasText = () => (editor.current?.text() ?? text ?? '').trim().length > 0 || /<img/i.test(editor.current?.html() ?? '')
  // Schon angelegt (etwa beim ersten Speichern, dann scheiterte etwas danach): weiter dasselbe Paket
  const savedId = useRef(pkg?.id)
  const save = async () => {
    setBusy(true)
    try {
      const html = editor.current?.html() ?? ''
      const content = hasText() || files.length > 0 || added.length > 0
      // Text oder Dateien zum ersten Mal: der Paketschlüssel kommt in den Tresor - vorher öffnen (oder sagen, dass es ihn braucht)
      const needKey = (textChanged && hasText() || added.length > 0) && !pkg?.hasKey
      if (needKey && !await requestTresorOpen()) {
        fail(new Error((await tresorState().catch(() => null))?.exists === false
          ? t('Notfalltext und Dateien liegen verschlüsselt im Paket, ihr Schlüssel im Tresor. Bitte zuerst unter Sicherheit → Tresor einen Tresor einrichten.')
          : t('Zum Speichern von Notfalltext und Dateien den Tresor öffnen.')))
        return
      }
      const r = await api.put<{ id: string }>('/api/emergency/packages', {
        id: savedId.current, name, folders: folders.map(f => f.id), account, admin, requesters, extraVetoers: extra, noVeto, waitDays: wait, content,
      })
      const id = savedId.current = savedId.current ?? r.id
      // Der Notfallcode kommt einmal zum Ausdrucken
      let made: { name: string; code: string } | undefined
      if (needKey) made = { name, code: (await api.post<{ code: string }>(`/api/emergency/packages/${id}/keys`)).code }
      if (textChanged && (pkg?.hasKey || made)) await api.put(`/api/emergency/packages/${id}/text`, { html: hasText() ? html : '' })
      for (const f of pkg?.files ?? []) if (!files.some(x => x.id === f.id)) await api.del(`/api/emergency/packages/${id}/files/${f.id}`)
      if (added.length > 0) {
        const res = await api.post<{ failed: string[] }>(`/api/emergency/packages/${id}/files`, { ids: added.map(i => i.fileId) })
        if (res.failed.length) fail(new Error(res.failed.join('\n')))
      }
      onClose(true, made)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const nothing = folders.length === 0 && files.length === 0 && added.length === 0 && !account && !admin && !textChanged && !pkg?.hasText
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{pkg ? t('Notfallpaket bearbeiten') : t('Notfallpaket anlegen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <Field label={t('Name')}><Input value={name} onChange={(_, d) => setName(d.value)} placeholder={t('z. B. Familie')} /></Field>
            <Field label={t('Notfalltext')}>
              {text == null
                ? <div className={s.row}>
                  <Text className={s.muted}>{textFailed ? t('Zum Bearbeiten des Notfalltexts den Tresor öffnen.') : t('Wird geladen …')}</Text>
                  {textFailed && <Button size="small" onClick={loadText}>{t('Tresor öffnen')}</Button>}
                </div>
                : <div className={s.editor}><RichEditor ref={editor} initial={text} headings onChange={() => setTextChanged(true)} onImage={smallImage} /></div>}
              <Hint>{t('Was im Paket ist und was zu tun ist: Zugangsdaten, wo was liegt, wen man anruft. Bilder lassen sich einfügen, auch aus der Zwischenablage. Verschlüsselt, zu öffnen mit dem Notfallcode.')}</Hint>
            </Field>
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
            <Field label={t('Wer anfragen darf')}>
              <div className={s.pick}>
                {people.map(p => <Checkbox key={p.id} label={p.displayName || p.login} checked={requesters.includes(p.id)}
                  onChange={(_, d) => toggle(requesters, setRequesters, p.id, !!d.checked)} />)}
              </div>
            </Field>
            <Field label={t('Wer widersprechen darf')}>
              <Text size={200}>{t('Du immer. Dazu alle, mit denen die gewählten Ordner geteilt sind, und wen du hier wählst.')}</Text>
              <div className={s.pick}>
                {fromShares.length === 0 && people.every(p => requesters.includes(p.id)) && <Text size={200} className={s.muted}>{t('Niemand sonst.')}</Text>}
                {fromShares.map(id => <Checkbox key={id} label={nameOf(id) + ' · ' + t('Ordner geteilt')} checked={!noVeto.includes(id)}
                  onChange={(_, d) => toggle(noVeto, setNoVeto, id, !d.checked)} />)}
                {people.filter(p => !fromShares.includes(p.id) && !requesters.includes(p.id)).map(p => <Checkbox key={p.id} label={p.displayName || p.login} checked={extra.includes(p.id)}
                  onChange={(_, d) => toggle(extra, setExtra, p.id, !!d.checked)} />)}
              </div>
            </Field>
            <Field label={t('Frist für einen Widerspruch')}>
              <Select value={String(wait)} onChange={(_, d) => setWait(Number(d.value))}>
                {[1, 2, 3, 5, 7, 14, 30].map(n => <option key={n} value={n}>{n === 1 ? t('1 Tag') : t('{n} Tage', { n })}</option>)}
              </Select>
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save} disabled={busy || !name.trim() || requesters.length === 0 || nothing}>{t('Speichern')}</Button>
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

/** Bild für den Notfalltext: höchstens 1600 px, als JPEG (steht im verschlüsselten Text). */
async function smallImage(file: File): Promise<string> {
  const url = URL.createObjectURL(file)
  try {
    const img = await new Promise<HTMLImageElement>((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = url })
    const f = Math.min(1, 1600 / Math.max(img.width, img.height))
    const c = document.createElement('canvas')
    c.width = Math.round(img.width * f); c.height = Math.round(img.height * f)
    c.getContext('2d')!.drawImage(img, 0, 0, c.width, c.height)
    return c.toDataURL('image/jpeg', 0.85)
  } finally { URL.revokeObjectURL(url) }
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
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('Nach oben')} />
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

/** Notfallcode zum Ausdrucken (oder als Foto des QR-Codes) - für die Personen, die anfragen dürfen. */
function CodeDialog({ name, code, onClose }: { name: string; code: string; fresh: boolean; onClose: () => void }) {
  const s = useStyles()
  const [qr, setQr] = useState<string | null>(null)
  useEffect(() => { QRCode.toDataURL(code, { width: 360, margin: 1 }).then(setQr).catch(() => setQr(null)) }, [code])
  const print = () => {
    const w = window.open('', '_blank', 'width=640,height=800')
    if (!w) return
    const esc = (x: string) => x.replace(/&/g, '&amp;').replace(/</g, '&lt;')
    w.document.write(`<!doctype html><meta charset="utf-8"><title>${esc(t('Notfallcode'))}</title><body style="font-family:sans-serif;padding:32px">`
      + `<h2>${esc(t('Notfallcode für „{name}“', { name }))}</h2>`
      + `<p style="font:28px Consolas,monospace;letter-spacing:1px">${esc(code)}</p>${qr ? `<img src="${qr}" width="240">` : ''}`
      + `<p>${esc(t('Im Notfall unter {url} anmelden, unter Einstellungen → Notfallzugang den Zugang anfordern und nach Ablauf der Frist mit diesem Code Notfalltext und Dateien öffnen.', { url: location.origin }))}</p>`
      + `<p>${esc(t('Gut aufbewahren. Ohne den Code lässt sich das Paket nicht öffnen.'))}</p></body>`)
    w.document.close(); w.focus(); w.print()
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Notfallcode für „{name}“', { name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10, alignItems: 'stretch' }}>
            <Text>{t('Diesen Code bekommen die Personen, die anfragen dürfen – ausgedruckt oder als Foto. Mit ihm öffnen sie Notfalltext und Dateien.')}</Text>
            <div className={s.code}>{code}</div>
            {qr && <img src={qr} alt="" style={{ width: 200, alignSelf: 'center' }} />}
            <Hint>{t('dmOffice zeigt ihn dir jederzeit wieder, solange dein Tresor offen ist.')}</Hint>
          </DialogContent>
          <DialogActions>
            <Button icon={<Print20Regular />} onClick={print}>{t('Drucken')}</Button>
            <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
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
              <Field label={t('Notfallcode')} hint={t('Steht auf dem Ausdruck, den du bekommen hast.')}>
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
