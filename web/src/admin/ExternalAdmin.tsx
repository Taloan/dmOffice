import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody, Radio, RadioGroup, Select, Switch, Text, tokens, Tooltip } from '@fluentui/react-components'
import { RibbonActions, RibbonButton } from '../shell/Ribbon' // [feature:adminclean]
import { useAdminPart } from './AdminPage' // [feature:adminclean]
import { Add20Regular, Delete20Regular, FolderOpen20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, featureOn, fmtSize, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { AdminCol, AdminTable } from './AdminTable'
import { ServerFolderDialog } from './ServerFolderDialog'
import { TopFolderChoice } from '../files/TopFolderChoice'
import { SftpFields, isSftp } from '../files/SftpFields'
import { ensureSftpTrust } from '../files/SftpTrust' // [feature:sftptrust]
import { AddOneDrive, AddPCloud, type SignInPick } from '../files/ExternalView'
import { askConfirm } from '../shell/context'
import { useAdminSave } from './draft' // [feature:savemode]
import { useRef } from 'react' // [feature:savemode]

interface Folder {
  id: string; name: string; path: string; users: string[]; readOnly: boolean; include?: string[]
  /** Art: folder (Pfad im Container, auch ohne Angabe), webdav (mit sftp://-Adresse SFTP), onedrive, pcloud */
  kind?: string; url?: string | null; user?: string | null; account?: string | null
  /** Neues Passwort oder neuer Schlüssel; leer: das gespeicherte bleibt. Gespeicherte kommen nie zurück, nur ob es eins gibt. */
  password?: string; passwordSet?: boolean; signedIn?: boolean
  /** OneDrive und pCloud: neue Anmeldung und gewählter Ordner, bis gespeichert */
  flow?: string | null; item?: string | null; folder?: string | null
}
type MountKind = 'folder' | 'webdav' | 'sftp' | 'onedrive' | 'pcloud'
const kindOf = (f: Folder): MountKind => f.kind === 'webdav' ? (isSftp(f.url) ? 'sftp' : 'webdav') : f.kind === 'onedrive' || f.kind === 'pcloud' ? f.kind : 'folder'
/** Wo der Mount liegt, zum Anzeigen */
const whereOf = (f: Folder) => kindOf(f) === 'folder' ? f.path : f.account || f.url || ''
const ADMIN_SIGNIN = '/api/admin/external/server'
interface IndexPart { group: string; top: string; count: number; bytes: number; done: number; openBytes: number }
interface IndexState {
  mount: string; level: string; groups: string[]; folders: string[]; maxMb: number; read: string; check: string
  files: number; dirs: number; wanted: number; done: number; errors: number; lastScan: string | null; lastSeconds: number
  bytesPerSecond: number | null; note: string | null; parts: IndexPart[]; checking: boolean; ocr: boolean
}
interface UserRow { id: string; login: string; displayName: string }

/**
 * Verwaltung, zwei Seiten: „Lokale Server-Mounts“ (part server: gibt nur der Admin frei - Pfad im Container, je Benutzer,
 * wahlweise nur lesen) und „Externe Ordner“ (part users: ob Benutzer eigene WebDAV-, OneDrive- und pCloud-Ordner einbinden dürfen,
 * das Heimnetz eigens). Systemweite WebDAV-/OneDrive-/pCloud-Ordner für alle gibt es nicht.
 */
export function ExternalAdmin({ part }: { part: 'server' | 'users' }) {
  const { fail, notify } = useApp()
  const [folders, setFolders] = useState<Folder[] | null>(null)
  // Gespeicherter Stand: Ist der Ort eines Mounts geändert, gilt die Ordnerliste erst nach dem Speichern wieder
  const [saved, setSaved] = useState<Folder[]>([])
  const [webDav, setWebDav] = useState(false)
  const [priv, setPriv] = useState(false)
  const [oneDrive, setOneDrive] = useState(false)
  // Microsoft-Anmeldung auf diesem Server eingerichtet (DMOFFICE_MS_CLIENT_ID nicht „off“)
  const [microsoft, setMicrosoft] = useState(true)
  const [pCloud, setPCloud] = useState(false)
  // Benutzer-Mounts: gespeicherter Stand (für Verwerfen und die Rückfrage) und eingebundene Ordner je Art
  const [sftp, setSftp] = useState(false)
  const [savedSw, setSavedSw] = useState({ webDav: false, priv: false, oneDrive: false, pCloud: false, sftp: false })
  const [mounts, setMounts] = useState<Record<string, number>>({})
  const [users, setUsers] = useState<UserRow[]>([])
  const [dirty, setDirty] = useState(false)
  // [feature:adminclean]
  const tabbed = useAdminPart().part != null
  // [feature:adminclean:else]
  // const tabbed = false
  // [/feature:adminclean]
  // Ordner-Browser für den Pfad des n-ten Ordners
  const [browse, setBrowse] = useState<number | null>(null)
  // Anmeldung bei Microsoft oder pCloud für den n-ten Ordner
  const [signIn, setSignIn] = useState<number | null>(null)
  // Weitere Arten als Pfade im Container (WebDAV, SFTP, OneDrive, pCloud)
  const drivers = featureOn('mountdrivers')
  // Stand der Suche je Ordner (alle 10 Sekunden, solange noch gelesen wird)
  const [states, setStates] = useState<IndexState[]>([])
  const loadStates = () => api.get<IndexState[]>('/api/admin/external/index').then(setStates).catch(() => {})
  useEffect(() => {
    loadStates()
    const timer = setInterval(loadStates, 10000)
    return () => clearInterval(timer)
  }, [])
  const [searchOf, setSearchOf] = useState<string | null>(null)
  // Mounts, bei denen gerade kein Ordner gewählt ist: So darf nicht gespeichert werden, leer hieße alle
  const [noFolder, setNoFolder] = useState<Record<string, boolean>>({})
  useEffect(() => {
    api.get<{ folders: Folder[]; webDavForUsers: boolean; webDavPrivate: boolean; oneDriveForUsers?: boolean; microsoft?: boolean; pCloudForUsers?: boolean; sftpForUsers?: boolean; mounts?: Record<string, number> }>('/api/admin/external')
      .then(r => {
        const sw = { webDav: r.webDavForUsers, priv: (r.webDavForUsers || !!r.sftpForUsers) && r.webDavPrivate, oneDrive: !!r.oneDriveForUsers, pCloud: !!r.pCloudForUsers, sftp: !!r.sftpForUsers }
        setFolders(r.folders); setSaved(r.folders); setWebDav(sw.webDav); setPriv(sw.priv); setOneDrive(sw.oneDrive); setMicrosoft(r.microsoft !== false)
        setPCloud(sw.pCloud); setSftp(sw.sftp); setSavedSw(sw); setMounts(r.mounts ?? {})
      }).catch(fail)
    api.get<UserRow[]>('/api/admin/users').then(setUsers).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // [feature:savemode]
  // Kennung savemode: „Speichern“ und „Verwerfen“ im Menüband statt in der Seite
  const later = useRef<{ dirty: boolean; save: () => Promise<boolean>; discard: () => void }>({ dirty: false, save: async () => true, discard: () => {} })
  const drafting = useAdminSave({ get dirty() { return later.current.dirty }, save: () => later.current.save(), discard: () => later.current.discard() })
  // [feature:savemode:else]
  // const drafting = false
  // [/feature:savemode]
  if (!folders) return null
  const change = (i: number, patch: Partial<Folder>) => { setFolders(f => f!.map((x, n) => (n === i ? { ...x, ...patch } : x))); setDirty(true) }
  const setKind = (i: number, k: MountKind) => change(i, {
    kind: k === 'sftp' ? 'webdav' : k, url: k === 'sftp' ? 'sftp://' : k === 'webdav' ? '' : null, user: '', password: '',
    flow: null, item: null, folder: null, account: null,
  })
  const kinds: [MountKind, string][] = [['folder', t('Pfad im Container')], ['webdav', 'WebDAV'], ...(featureOn('sftp') ? [['sftp', 'SFTP'] as [MountKind, string]] : []),
    ...(microsoft ? [['onedrive', 'OneDrive'] as [MountKind, string]] : []), ['pcloud', 'pCloud']]
  // Ort eines gespeicherten Mounts geändert (Adresse, Benutzer, Zugang, Anmeldung): die Ordnerliste zeigt noch den alten Ort
  const moved = (f: Folder) => {
    const old = saved.find(x => x.id === f.id)
    return !!old && ((old.url ?? '') !== (f.url ?? '') || (old.user ?? '') !== (f.user ?? '') || !!f.password || !!f.flow)
  }
  const picked = (i: number) => (p: SignInPick) => {
    change(i, { flow: p.flow, item: p.item ?? null, folder: p.folder, account: p.account, name: folders[i].name || p.name })
    setSignIn(null)
  }
  const save = async (wd = webDav, pv = priv, od = oneDrive, pc = pCloud, sf = sftp) => {
    try {
      // [feature:sftptrust]
      // Neue oder geänderte SFTP-Ordner: Fingerabdruck vor dem Speichern bestätigen
      for (const f of folders) if (kindOf(f) === 'sftp' && (!f.id || moved(f)) && !await ensureSftpTrust({ url: f.url ?? '' }, { admin: true })) return false
      // [/feature:sftptrust]
      await api.put('/api/admin/external', { folders, webDavForUsers: wd, webDavPrivate: pv, oneDriveForUsers: od, pCloudForUsers: pc, sftpForUsers: sf })
      // Neue Ordner bekommen ihre Kennung vom Server - neu laden, sonst bleiben sie „noch nicht gespeichert“
      const fresh = (await api.get<{ folders: Folder[] }>('/api/admin/external')).folders
      setFolders(fresh); setSaved(fresh); setNoFolder({})
      setDirty(false); notify(t('Gespeichert')); loadStates()
      setSavedSw({ webDav: wd, priv: pv, oneDrive: od, pCloud: pc, sftp: sf })
      return true // [feature:savemode]
    } catch (e) { fail(e) }
    return false // [feature:savemode]
  }
  // Benutzer-Mounts: erst mit „Speichern“, beim Ausschalten mit Rückfrage, wenn Benutzer schon Ordner dieser Art eingebunden haben
  const swDirty = webDav !== savedSw.webDav || priv !== savedSw.priv || oneDrive !== savedSw.oneDrive || pCloud !== savedSw.pCloud || sftp !== savedSw.sftp
  const saveSwitches = async () => {
    const off: string[] = []
    if (savedSw.webDav && !webDav && mounts.webdav) off.push(t('{n} WebDAV-Ordner', { n: mounts.webdav }))
    if (savedSw.oneDrive && !oneDrive && mounts.onedrive) off.push(t('{n} OneDrive-Ordner', { n: mounts.onedrive }))
    if (savedSw.pCloud && !pCloud && mounts.pcloud) off.push(t('{n} pCloud-Ordner', { n: mounts.pcloud }))
    if (savedSw.sftp && !sftp && mounts.sftp) off.push(t('{n} SFTP-Ordner', { n: mounts.sftp }))
    if (off.length && !await askConfirm(t('Eingebundene Ordner sperren?'),
      t('Benutzer haben {list} eingebunden. Diese Ordner werden gesperrt und sind nicht mehr sichtbar. Wird die Einstellung wieder aktiviert, sind sie samt Zugangsdaten wieder verfügbar.', { list: off.join(', ') }),
      // [feature:savemode]
      t('Sperren und speichern'))) return false
    // [feature:savemode:else]
    // t('Sperren und speichern'))) return
    // [/feature:savemode]
    // [feature:savemode]
    return await save(webDav, priv, oneDrive, pCloud, sftp)
    // [feature:savemode:else]
    // await save(webDav, priv, oneDrive, pCloud, sftp)
    // [/feature:savemode]
  }
  const discard = () => { setWebDav(savedSw.webDav); setPriv(savedSw.priv); setOneDrive(savedSw.oneDrive); setPCloud(savedSw.pCloud); setSftp(savedSw.sftp) }
  // [feature:savemode]
  later.current = part === 'users' ? { dirty: swDirty, save: saveSwitches, discard } : {
    dirty,
    save: async () => {
      if (Object.values(noFolder).some(Boolean)) { notify(t('Nicht gespeichert'), t('Bei einem Server-Mount ist kein Ordner gewählt.'), 'warning'); return false }
      const gone = saved.filter(x => !folders.some(f => f.id === x.id)).map(x => x.name || x.path)
      if (gone.length && !(await askConfirm(t('Server-Mounts entfernen?'), t('{list} verschwindet bei den Benutzern unter „Externe Ordner“. Die Dateien am Ort selbst bleiben unberührt.', { list: gone.join(', ') }), t('Entfernen')))) return false
      return save()
    },
    discard: () => { setFolders(saved); setNoFolder({}); setDirty(false) },
  }
  // [/feature:savemode]
  const muted = { color: tokens.colorNeutralForeground3 }
  if (part === 'users') return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 760, marginTop: tabbed ? 0 : 24 }}>
      <Text size={200} style={muted}>{t('Eingebundene Ordner sieht nur der Benutzer, der sie eingebunden hat. Für diese Ordner gibt es keine Versionen, keinen Papierkorb und keine Freigabelinks, und dmOffice sichert sie nicht mit.')}</Text>
      {/* Aus: auch „Heimnetz“ aus - sonst wäre es beim Wiedereinschalten ungefragt wieder an */}
      <Switch checked={webDav} onChange={(_, d) => { setWebDav(d.checked); if (!d.checked && !sftp) setPriv(false) }} label={t('Benutzer dürfen eigene WebDAV-Ordner einbinden')} />
      {featureOn('sftp') && <Switch checked={sftp} onChange={(_, d) => { setSftp(d.checked); if (!d.checked && !webDav) setPriv(false) }} label={t('Benutzer dürfen eigene SFTP-Ordner einbinden')} />}
      {(webDav || sftp) && (
        <>
          <Switch checked={priv} onChange={(_, d) => setPriv(d.checked)} label={t('… auch mit Adressen im Heimnetz')} />
          <Text size={200} style={muted}>{t('Ist die Einstellung deaktiviert, erreichen eigene WebDAV- und SFTP-Ordner und Kalender-Abos nur Adressen im Internet. Ist sie aktiviert, können Benutzer über dmOffice auch Geräte im Heimnetz abfragen.')}</Text>
        </>
      )}
      <Switch checked={oneDrive} disabled={!microsoft && !oneDrive} onChange={(_, d) => setOneDrive(d.checked)}
        label={t('Benutzer dürfen eigene OneDrive-Ordner einbinden (privat oder geschäftlich)')} />
      <Text size={200} style={muted}>{microsoft
        ? t('Ist die Einstellung deaktiviert, sind bestehende OneDrive-Ordner gesperrt.')
        : t('Die Microsoft-Anmeldung ist auf diesem Server abgeschaltet (DMOFFICE_MS_CLIENT_ID).')}</Text>
      <Switch checked={pCloud} onChange={(_, d) => setPCloud(d.checked)}
        label={t('Benutzer dürfen eigene pCloud-Ordner einbinden')} />
      <Text size={200} style={muted}>{t('Jeder Benutzer meldet sich mit einer eigenen App bei pCloud an (Client-ID und Client-Secret beim Einbinden). Ist die Einstellung deaktiviert, sind bestehende pCloud-Ordner gesperrt.')}</Text>
      {!drafting && <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        <Button appearance="primary" disabled={!swDirty} onClick={saveSwitches}>{t('Speichern')}</Button>
        <Button disabled={!swDirty} onClick={discard}>{t('Verwerfen')}</Button>
      </div>}
    </div>
  )
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 760, marginTop: tabbed ? 0 : 24 }}>
      {drivers ? <>
        <Text size={200} style={muted}>{t('Server-Mounts erscheinen bei den Benutzern, für die sie freigegeben sind, unter „Externe Ordner“. Für diese Ordner gibt es keine Versionen, keinen Papierkorb und keine Freigabelinks, und dmOffice sichert sie nicht mit.')}</Text>
        <Text size={200} style={muted}>{t('Pfade im Container müssen in der Container-Vorlage unter /external/<Name> eingebunden sein.')}</Text>
      </> : <>
        <Text size={200} style={muted}>{t('Lokale Server-Mounts erscheinen bei den Benutzern, für die sie freigegeben sind, unter „Externe Ordner“. Für diese Ordner gibt es keine Versionen, keinen Papierkorb und keine Freigabelinks, und dmOffice sichert sie nicht mit.')}</Text>
        <Text size={200} style={muted}>{t('Server-Mounts müssen in der Container-Vorlage unter /external/<Name> eingebunden sein.')}</Text>
      </>}
      {folders.length === 0 && <Text size={200} style={muted}>{t('Noch keiner freigegeben.')}</Text>}
      {folders.map((f, i) => { const kind = kindOf(f); return (
        <div key={f.id || i} style={{ display: 'flex', flexDirection: 'column', gap: 6, padding: 10, border: `1px solid ${f.id ? tokens.colorNeutralStroke2 : tokens.colorBrandStroke1}`, borderRadius: 6 }}>
          {!f.id && <Text size={200} weight="semibold" style={{ color: tokens.colorBrandForeground1 }}>{t('Neu – noch nicht gespeichert')}</Text>}
          {/* Die Art steht fest, sobald gespeichert ist - Galerie, Suche und Vorschauen hängen an der Kennung */}
          {drivers && !f.id && <Field label={t('Art')}>
            <RadioGroup layout="horizontal" value={kind} onChange={(_, d) => setKind(i, d.value as MountKind)}>
              {kinds.map(k => <Radio key={k[0]} value={k[0]} label={k[1]} />)}
            </RadioGroup>
          </Field>}
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <Field label={t('Name')} style={{ flex: '1 1 160px' }}><Input value={f.name} onChange={(_, d) => change(i, { name: d.value })} placeholder={t('z. B. Musik')} /></Field>
            {kind === 'folder' && <Field label={t('Pfad im Container')} style={{ flex: '2 1 240px' }}>
              <Input value={f.path} onChange={(_, d) => change(i, { path: d.value })} placeholder="/external/musik"
                contentAfter={<Button size="small" appearance="transparent" icon={<FolderOpen20Regular />} onClick={() => setBrowse(i)} aria-label={t('Ordner auf dem Server wählen')} title={t('Ordner auf dem Server wählen')} />} />
            </Field>}
            <Tooltip content={t('Entfernen')} relationship="label">
              <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => { setFolders(x => x!.filter((_, n) => n !== i)); setDirty(true) }} />
            </Tooltip>
          </div>
          {kind === 'webdav' && <>
            <Field label={t('Adresse')}><Input value={f.url ?? ''} onChange={(_, d) => change(i, { url: d.value })} placeholder="https://" /></Field>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <Field label={t('Benutzername')} style={{ flex: '1 1 160px' }}><Input value={f.user ?? ''} onChange={(_, d) => change(i, { user: d.value })} autoComplete="off" /></Field>
              <Field label={t('Passwort')} style={{ flex: '1 1 160px' }} hint={f.passwordSet ? t('Gespeichert. Leer lassen, um es zu behalten.') : undefined}>
                <Input type="password" autoComplete="new-password" value={f.password ?? ''} placeholder={f.passwordSet ? '••••••••' : ''} onChange={(_, d) => change(i, { password: d.value })} />
              </Field>
            </div>
          </>}
          {kind === 'sftp' && <SftpFields url={f.url ?? ''} onUrl={v => change(i, { url: v })} user={f.user ?? ''} onUser={v => change(i, { user: v })}
            secret={f.password ?? ''} onSecret={v => change(i, { password: v })} hasSecret={f.passwordSet} admin />}
          {(kind === 'onedrive' || kind === 'pcloud') && <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
            {f.account && <Text size={200} style={muted}>{f.account}</Text>}
            <Button size="small" onClick={() => setSignIn(i)}>{f.signedIn || f.flow
              ? (kind === 'onedrive' ? t('Mit Microsoft neu anmelden') : t('Mit pCloud neu anmelden'))
              : (kind === 'onedrive' ? t('Mit Microsoft anmelden') : t('Mit pCloud anmelden'))}</Button>
          </div>}
          <Switch checked={f.readOnly} onChange={(_, d) => change(i, { readOnly: d.checked })} label={t('Nur lesen')} />
          {f.id
            ? <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
              <Button size="small" onClick={() => setSearchOf(f.id)}>{t('Indexierung konfigurieren')}</Button>
              <Text size={200} style={muted}>{searchSummary(states.find(s => s.mount === f.id))}</Text>
            </div>
            : <Text size={200} style={muted}>{t('Die Indexierung lässt sich nach dem Speichern konfigurieren.')}</Text>}
          {(kind === 'folder' ? f.path.trim().startsWith('/') : !!f.id) && <>
            <Text size={200} style={muted}>{t('In dmOffice erscheinen:')}</Text>
            {kind !== 'folder' && moved(f)
              ? <Text size={200} style={muted}>{t('Die Ordner lassen sich nach dem Speichern wählen.')}</Text>
              : <ServerTop path={kind === 'folder' ? f.path.trim() : undefined} mount={kind === 'folder' ? undefined : f.id} stamp={stampOf(saved.find(x => x.id === f.id))}
                include={f.include ?? []} onChange={include => change(i, { include })} onNone={n => setNoFolder(x => ({ ...x, [f.id || i]: n }))} />}
          </>}
          <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', alignItems: 'center' }}>
            <Text size={200} style={muted}>{t('Sichtbar für:')}</Text>
            <Checkbox label={t('alle')} checked={f.users.length === 0} onChange={(_, d) => change(i, { users: d.checked ? [] : users.map(u => u.id) })} />
            {f.users.length > 0 && users.map(u => (
              <Checkbox key={u.id} label={{ children: u.displayName || u.login, style: { whiteSpace: 'normal', overflowWrap: 'anywhere', minWidth: 0 } }} style={{ minWidth: 0, maxWidth: '100%' }} checked={f.users.includes(u.id)}
                onChange={(_, d) => change(i, { users: d.checked ? [...f.users, u.id] : f.users.filter(x => x !== u.id) })} />
            ))}
          </div>
        </div>
      ) })}
      {/* Erst die Liste ergänzen (dezent, direkt unter den Ordnern), dann einmal speichern - so ist klar, was was tut */}
      {/* Während etwas offen (ungespeichert) ist, kein zweiter Ordner dazu - erst speichern */}
      {/* [feature:adminclean] */}
      {tabbed && <RibbonActions>
        <RibbonButton icon={<Add20Regular />} disabled={dirty} label={t('Neuer Server-Mount')} onClick={() => { setFolders(f => [...f!, { id: '', name: '', path: '', users: [], readOnly: true }]); setDirty(true) }} />
      </RibbonActions>}
      {/* [/feature:adminclean] */}
      {!tabbed && !dirty && <div><Button appearance="subtle" icon={<Add20Regular />} onClick={() => { setFolders(f => [...f!, { id: '', name: '', path: '', users: [], readOnly: true }]); setDirty(true) }}>{t('Weiteren Ordner hinzufügen')}</Button></div>}
      {/* Klebt unten: bei langen Ordnerlisten bleibt „Speichern“ im Blick */}
      {dirty && !drafting && <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap', padding: '8px 0', borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
        position: 'sticky', bottom: 0, background: tokens.colorNeutralBackground1, zIndex: 1 }}>
        <Button appearance="primary" disabled={Object.values(noFolder).some(Boolean)} onClick={() => save()}>{t('Speichern')}</Button>
        {dirty && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground2 }}>{t('Neue und geänderte Ordner werden erst mit „Speichern“ übernommen.')}</Text>}
      </div>}
      {signIn !== null && folders[signIn] && (
        <Dialog open onOpenChange={(_, d) => { if (!d.open) setSignIn(null) }}>
          <DialogSurface style={{ maxWidth: 560 }}>
            {kindOf(folders[signIn]) === 'onedrive'
              ? <AddOneDrive choice={null} base={ADMIN_SIGNIN} onClose={() => setSignIn(null)} onPick={picked(signIn)} />
              : <AddPCloud choice={null} base={ADMIN_SIGNIN} onClose={() => setSignIn(null)} onPick={picked(signIn)} />}
          </DialogSurface>
        </Dialog>
      )}
      {searchOf && <SearchDialog folder={folders.find(x => x.id === searchOf)!} state={states.find(s => s.mount === searchOf)} onClose={() => { setSearchOf(null); loadStates() }} />}
      {browse !== null && folders[browse] && (
        <ServerFolderDialog title={t('Ordner auf dem Server wählen')} start={folders[browse].path} onClose={() => setBrowse(null)}
          onPick={p => { change(browse, { path: p, name: folders[browse].name || p.split('/').filter(Boolean).pop() || '' }); setBrowse(null) }} />
      )}
    </div>
  )
}

const levelName = (level?: string) => level === 'contents' ? t('Auch Inhalte') : level === 'names' ? t('Dateien und Ordner') : t('Nur bereitstellen')

/** Eine Zeile neben dem Knopf: Stufe und letzte Prüfung. */
function searchSummary(s?: IndexState) {
  if (!s) return ''
  const parts = [levelName(s.level)]
  if (s.level !== 'none' && s.checking) parts.push(t('wird gerade geprüft'))
  else if (s.level !== 'none' && s.lastScan) parts.push(t('zuletzt geprüft {when}', { when: when(s.lastScan) }))
  return parts.join(' · ')
}

const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })

function fmtDuration(seconds: number) {
  const h = Math.floor(seconds / 3600), m = Math.round((seconds % 3600) / 60)
  return h > 0 ? t('{h} Std. {m} Min.', { h, m }) : t('{m} Min.', { m })
}

function fmtEstimate(seconds: number) {
  const days = Math.round(seconds / 86400), hours = Math.round(seconds / 3600)
  return days >= 2 ? t('{n} Tage', { n: days }) : hours >= 2 ? t('{n} Stunden', { n: hours }) : t('etwa eine Stunde oder weniger')
}

const GROUPS: { id: string; name: () => string; hint: () => string }[] = [
  { id: 'text', name: () => t('Textdateien'), hint: () => 'TXT, CSV, Markdown, Log, JSON, XML, YAML, HTML, EML, VCF, ICS' },
  { id: 'docs', name: () => t('Dokumente'), hint: () => t('PDF, Word, Excel, PowerPoint') },
  { id: 'images', name: () => t('Bilder und Scans'), hint: () => t('JPG, PNG, TIFF, HEIC, gescannte PDFs. Dauert deutlich länger.') },
]
const STAND_COLS: AdminCol<{ label: string; value: string }>[] = [
  { key: 'label', label: () => t('Angabe'), width: 0, sort: r => r.label, render: r => r.label, fixed: true },
  { key: 'value', label: () => t('Wert'), width: 260, sort: r => r.value, render: r => <div style={{ textAlign: 'right' }}>{r.value}</div>, fixed: true },
]
const CHECK_SECONDS: Record<string, number> = { '1h': 3600, '6h': 21600, daily: 86400, weekly: 604800 }

/** Verwaltung → Lokale Server-Mounts → Suche einstellen: Stufe, Dateigruppen, Ordner, Größe, Zeitplan und Stand je Mount. */
function SearchDialog({ folder, state, onClose }: { folder: Folder; state?: IndexState; onClose: () => void }) {
  const { fail, notify } = useApp()
  const [level, setLevel] = useState(state?.level ?? 'names')
  const [groups, setGroups] = useState<string[]>(state?.groups ?? ['text', 'docs'])
  const [folders, setFolders] = useState<string[]>(state?.folders ?? [])
  const [maxMb, setMaxMb] = useState(state?.maxMb ?? 50)
  const [read, setRead] = useState(state?.read ?? 'continuous')
  const [check, setCheck] = useState(state?.check ?? 'daily')
  const [busy, setBusy] = useState(false)
  const [noFolder, setNoFolder] = useState(false)
  const muted = { color: tokens.colorNeutralForeground3 }
  const row = { display: 'flex', justifyContent: 'space-between', gap: 12, alignItems: 'center', flexWrap: 'wrap' } as const
  const heading = (text: string) => <Text weight="semibold" style={{ display: 'block', marginTop: 16, marginBottom: 4 }}>{text}</Text>
  const inFolders = (p: IndexPart) => folders.length === 0 || folders.includes(p.top)
  const partsOf = (g: string) => (state?.parts ?? []).filter(p => p.group === g && inFolders(p))
  const openBytes = groups.flatMap(partsOf).reduce((sum, p) => sum + p.openBytes, 0)
  const estimate = level === 'contents' && state?.bytesPerSecond && openBytes > 0 ? fmtEstimate(openBytes / state.bytesPerSecond) : null
  const tooSlow = level !== 'none' && !!state?.lastSeconds && !!CHECK_SECONDS[check] && state.lastSeconds > CHECK_SECONDS[check]
  const wanted = state ? state.wanted : 0
  const save = async () => {
    setBusy(true)
    try {
      await api.put(`/api/admin/external/index/${encodeURIComponent(folder.id)}`, { level, groups, folders, maxMb, read, check })
      notify(t('Gespeichert'))
      onClose()
    } catch (e) { fail(e); setBusy(false) }
  }
  const checkNow = async () => {
    try { await api.post(`/api/admin/external/index/${encodeURIComponent(folder.id)}/check`); notify(t('Prüfung gestartet')) } catch (e) { fail(e) }
  }
  const reset = async () => {
    if (!await askConfirm('', t('Alle gewählten Dateien neu auswerten? Bis das fertig ist, findet die Suche weiter die bisherigen Inhalte.'))) return
    try { await api.post(`/api/admin/external/index/${encodeURIComponent(folder.id)}/reset`); notify(t('Wird neu ausgewertet')) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{t('Suche in „{name}“', { name: folder.name })}</DialogTitle>
          <DialogContent>
            <Text size={200} style={{ ...muted, display: 'block' }}>{whereOf(folder)}</Text>
            {heading(t('Was soll die Suche finden?'))}
            <RadioGroup value={level} onChange={(_, d) => setLevel(d.value)}>
              <Radio value="none" label={<>{t('Nichts, nur bereitstellen')}<br /><Text size={200} style={muted}>{t('Dateien sind sichtbar und lassen sich öffnen. Die Suche findet sie nicht.')}</Text></>} />
              <Radio value="names" label={<>{t('Dateien und Ordner')}<br /><Text size={200} style={muted}>{t('Name, Ordner, Dateityp, Größe und Datum.')}</Text></>} />
              <Radio value="contents" label={<>{t('Auch Inhalte')}<br /><Text size={200} style={muted}>{t('Zusätzlich der Text in den gewählten Dateien.')}</Text></>} />
            </RadioGroup>

            {level === 'contents' && <>
              {heading(t('Inhalte von'))}
              {GROUPS.map(g => {
                const parts = partsOf(g.id)
                const count = parts.reduce((sum, p) => sum + p.count, 0), bytes = parts.reduce((sum, p) => sum + p.bytes, 0)
                const noOcr = g.id === 'images' && state?.ocr === false
                return (
                  <div key={g.id} style={{ ...row, alignItems: 'flex-start' }}>
                    <Checkbox disabled={noOcr} checked={groups.includes(g.id)} onChange={(_, d) => setGroups(x => d.checked ? [...x, g.id] : x.filter(y => y !== g.id))}
                      label={<>{g.name()}<br /><Text size={200} style={muted}>{noOcr ? t('Die Texterkennung fehlt auf diesem Server.') : g.hint()}</Text></>} />
                    {state && state.parts.length > 0 && <Text size={200} style={{ ...muted, whiteSpace: 'nowrap', paddingTop: 8 }}>{count.toLocaleString(locale)} · {fmtSize(bytes)}</Text>}
                  </div>
                )
              })}
              {heading(t('Inhalte nur aus diesen Ordnern'))}
              <ServerTop path={kindOf(folder) === 'folder' ? folder.path : undefined} mount={kindOf(folder) === 'folder' ? undefined : folder.id} include={folders} onChange={setFolders} onNone={setNoFolder} />
              <div style={{ ...row, marginTop: 10 }}>
                <Text>{t('Größere Dateien auslassen, ab')}</Text>
                <Select value={String(maxMb)} onChange={(_, d) => setMaxMb(Number(d.value))} style={{ width: 170 }}>
                  {[10, 50, 200, 1024].map(n => <option key={n} value={n}>{fmtSize(n * 1024 * 1024)}</option>)}
                </Select>
              </div>
              <div style={{ ...row, marginTop: 6 }}>
                <Text>{t('Dateien für die Suche auswerten')}</Text>
                <Select value={read} onChange={(_, d) => setRead(d.value)} style={{ width: 250 }}>
                  <option value="continuous">{t('Laufend, bis alle fertig sind')}</option>
                  <option value="night">{t('Nur nachts (22 bis 6 Uhr)')}</option>
                </Select>
              </div>
              {estimate && <MessageBar intent="warning" layout="multiline" style={{ marginTop: 8 }}><MessageBarBody>{t('Das Auswerten dauert mit dieser Auswahl voraussichtlich {dauer}.', { dauer: estimate })}</MessageBarBody></MessageBar>}
            </>}

            {level !== 'none' && <>
              {heading(t('Auf dem Stand halten'))}
              <div style={row}>
                <Text>{t('Alle Dateien prüfen')}</Text>
                <Select value={check} onChange={(_, d) => setCheck(d.value)} style={{ width: 170 }}>
                  <option value="daily">{t('Täglich um 03:00')}</option>
                  <option value="6h">{t('Alle 6 Stunden')}</option>
                  <option value="1h">{t('Stündlich')}</option>
                  <option value="weekly">{t('Wöchentlich')}</option>
                  <option value="manual">{t('Nur von Hand')}</option>
                </Select>
              </div>
              <Text size={200} style={{ ...muted, display: 'block', marginTop: 4 }}>{t('Über dmOffice geänderte Dateien findet die Suche sofort. Außerhalb von dmOffice geänderte Dateien findet sie nach der nächsten Prüfung.')}</Text>
              {tooSlow && <MessageBar intent="warning" layout="multiline" style={{ marginTop: 8 }}><MessageBarBody>{t('Eine Prüfung dauert derzeit länger als dieser Abstand. Die nächste beginnt dann, sobald die laufende fertig ist.')}</MessageBarBody></MessageBar>}

              {heading(t('Stand'))}
              <AdminTable id="mount.search.stand" cols={STAND_COLS} rowKey={r => r.label} rows={[
                { label: t('Zuletzt alle Dateien geprüft'), value: state?.checking ? t('Prüfung läuft') : state?.lastScan ? `${when(state.lastScan)} · ${t('Dauer {d}', { d: fmtDuration(state.lastSeconds) })}` : t('Noch nicht') },
                { label: t('Dateien und Ordner'), value: t('{a} und {b}', { a: (state?.files ?? 0).toLocaleString(locale), b: (state?.dirs ?? 0).toLocaleString(locale) }) },
                ...(level === 'contents' ? [
                  { label: t('Dateien ausgewertet'), value: t('{a} von {b}', { a: (state?.done ?? 0).toLocaleString(locale), b: wanted.toLocaleString(locale) }) },
                  { label: t('Nicht lesbar'), value: (state?.errors ?? 0).toLocaleString(locale) },
                ] : []),
              ]} />
              {state?.note && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground2, display: 'block' }}>{t(state.note)}</Text>}
              <div style={{ display: 'flex', gap: 8, marginTop: 8, flexWrap: 'wrap' }}>
                <Button size="small" onClick={checkNow}>{t('Jetzt prüfen')}</Button>
                {level === 'contents' && <Button size="small" onClick={reset}>{t('Alles neu auswerten')}</Button>}
              </div>
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || (level === 'contents' && noFolder)} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Gespeicherter Ort eines Mounts - ändert er sich, lädt die Ordnerliste neu. */
const stampOf = (f?: Folder) => f ? [f.url, f.user, f.item, f.folder, f.account].join('|') : ''

/** Ordner der obersten Ebene zum Auswählen: eines Pfads im Container (kurz nach dem Tippen geladen) oder eines gespeicherten Server-Mounts anderer Art. */
function ServerTop({ path, mount, stamp, include, onChange, onNone }: { path?: string; mount?: string; stamp?: string; include: string[]; onChange: (include: string[]) => void; onNone?: (none: boolean) => void }) {
  const [dirs, setDirs] = useState<string[] | null>(null)
  const [missing, setMissing] = useState(false)
  useEffect(() => {
    setDirs(null); setMissing(false)
    const timer = setTimeout(() => {
      if (mount) api.get<{ dirs: string[] }>(`${ADMIN_SIGNIN}/${encodeURIComponent(mount)}/top`).then(r => setDirs(r.dirs)).catch(() => setMissing(true))
      else api.get<{ dirs: { name: string }[] }>('/api/admin/server-folders?' + q({ path: path ?? '' }))
        .then(r => setDirs(r.dirs.map(d => d.name))).catch(() => setMissing(true))
    }, mount ? 0 : 400)
    return () => clearTimeout(timer)
  }, [path, mount, stamp])
  if (missing) return <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{mount ? t('Gerade nicht erreichbar.') : t('Den Ordner gibt es auf dem Server nicht.')}</Text>
  return <TopFolderChoice dirs={dirs} include={include} onChange={onChange} onNone={onNone} />
}
