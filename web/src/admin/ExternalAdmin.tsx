import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody, Radio, RadioGroup, Select, Switch, Text, tokens, Tooltip } from '@fluentui/react-components'
import { Add20Regular, Delete20Regular, FolderOpen20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { AdminCol, AdminTable } from './AdminTable'
import { ServerFolderDialog } from './ServerFolderDialog'
import { TopFolderChoice } from '../files/TopFolderChoice'
import { askConfirm } from '../shell/context'

interface Folder { id: string; name: string; path: string; users: string[]; readOnly: boolean; include?: string[] }
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
  const [webDav, setWebDav] = useState(false)
  const [priv, setPriv] = useState(false)
  const [oneDrive, setOneDrive] = useState(false)
  // Microsoft-Anmeldung auf diesem Server eingerichtet (DMOFFICE_MS_CLIENT_ID nicht „off“)
  const [microsoft, setMicrosoft] = useState(true)
  const [pCloud, setPCloud] = useState(false)
  const [users, setUsers] = useState<UserRow[]>([])
  const [dirty, setDirty] = useState(false)
  // Ordner-Browser für den Pfad des n-ten Ordners
  const [browse, setBrowse] = useState<number | null>(null)
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
    api.get<{ folders: Folder[]; webDavForUsers: boolean; webDavPrivate: boolean; oneDriveForUsers?: boolean; microsoft?: boolean; pCloudForUsers?: boolean }>('/api/admin/external')
      .then(r => {
        setFolders(r.folders); setWebDav(r.webDavForUsers); setPriv(r.webDavPrivate); setOneDrive(!!r.oneDriveForUsers); setMicrosoft(r.microsoft !== false)
        setPCloud(!!r.pCloudForUsers)
      }).catch(fail)
    api.get<UserRow[]>('/api/admin/users').then(setUsers).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!folders) return null
  const change = (i: number, patch: Partial<Folder>) => { setFolders(f => f!.map((x, n) => (n === i ? { ...x, ...patch } : x))); setDirty(true) }
  const save = async (wd = webDav, pv = priv, od = oneDrive, pc = pCloud) => {
    try {
      await api.put('/api/admin/external', { folders, webDavForUsers: wd, webDavPrivate: pv, oneDriveForUsers: od, pCloudForUsers: pc })
      // Neue Ordner bekommen ihre Kennung vom Server - neu laden, sonst bleiben sie „noch nicht gespeichert“
      setFolders((await api.get<{ folders: Folder[] }>('/api/admin/external')).folders)
      setDirty(false); notify(t('Gespeichert')); loadStates()
    } catch (e) { fail(e) }
  }
  const muted = { color: tokens.colorNeutralForeground3 }
  if (part === 'users') return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 760, marginTop: 24 }}>
      <Text size={200} style={muted}>{t('Benutzer können eigene Ordner bei einem WebDAV-Dienst, bei OneDrive oder bei pCloud einbinden, soweit es hier erlaubt ist. Ein solcher Ordner erscheint nur bei dem Benutzer, der ihn eingebunden hat, in der Dateiablage unter „Externe Ordner“; Zugangsdaten und Anmeldung bleiben bei ihm. Die Dateien bleiben an ihrem Speicherort: Versionen, Papierkorb und Freigabelinks gibt es dort nicht, und dmOffice sichert sie nicht mit.')}</Text>
      <Text size={200} style={muted}>{t('Die Schalter gelten sofort.')}</Text>
      <Switch checked={webDav} onChange={(_, d) => { setWebDav(d.checked); save(d.checked, priv) }} label={t('Benutzer dürfen eigene WebDAV-Ordner einbinden (Nextcloud, Storage Box, NAS …)')} />
      {webDav && (
        <>
          <Switch checked={priv} onChange={(_, d) => { setPriv(d.checked); save(webDav, d.checked) }} label={t('… auch mit Adressen im Heimnetz')} />
          <Text size={200} style={muted}>{t('Ohne diesen Schalter erreichen eigene WebDAV-Ordner nur Adressen im Internet – sonst könnte jemand über dmOffice Geräte im Heimnetz (Router, NAS-Verwaltung) abfragen. Nur einschalten, wenn alle Benutzer vertrauenswürdig sind.')}</Text>
        </>
      )}
      <Switch checked={oneDrive} disabled={!microsoft && !oneDrive} onChange={(_, d) => { setOneDrive(d.checked); save(webDav, priv, d.checked) }}
        label={t('Benutzer dürfen eigene OneDrive-Ordner einbinden (privat oder geschäftlich)')} />
      <Text size={200} style={muted}>{microsoft
        ? t('Anmeldung über die Microsoft-App-Registrierung des Servers, mit Zugriff auf die Dateien des Kontos. Ausgeschaltet sind bestehende OneDrive-Ordner gesperrt.')
        : t('Die Microsoft-Anmeldung ist auf diesem Server abgeschaltet (DMOFFICE_MS_CLIENT_ID).')}</Text>
      <Switch checked={pCloud} onChange={(_, d) => { setPCloud(d.checked); save(webDav, priv, oneDrive, d.checked) }}
        label={t('Benutzer dürfen eigene pCloud-Ordner einbinden')} />
      <Text size={200} style={muted}>{t('Jeder Benutzer meldet sich mit einer eigenen App bei pCloud an (Client-ID und Client-Secret beim Einbinden). Ausgeschaltet sind bestehende pCloud-Ordner gesperrt.')}</Text>
    </div>
  )
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 760, marginTop: 24 }}>
      <Text size={200} style={muted}>{t('Lokale Server-Mounts erscheinen bei den Benutzern, für die sie freigegeben sind, in der Dateiablage unter „Externe Ordner“. Die Dateien bleiben an ihrem Speicherort: Versionen, Papierkorb und Freigabelinks gibt es dort nicht, und dmOffice sichert sie nicht mit. Galerie, Audiothek und Suche lesen jede Datei nur einmal für alle Benutzer.')}</Text>
      <Text size={200} style={muted}>{t('Eingebunden werden sie über die Container-Vorlage: Der Pfad im Container muss unter /external/<Name> liegen, zum Beispiel die Unraid-Freigabe /mnt/user/musik unter /external/musik. Danach lassen sie sich hier freigeben. Benutzer können keine eigenen Serverpfade eintragen.')}</Text>
      {folders.length === 0 && <Text size={200} style={muted}>{t('Noch keiner freigegeben.')}</Text>}
      {folders.map((f, i) => (
        <div key={f.id || i} style={{ display: 'flex', flexDirection: 'column', gap: 6, padding: 10, border: `1px solid ${f.id ? tokens.colorNeutralStroke2 : tokens.colorBrandStroke1}`, borderRadius: 6 }}>
          {!f.id && <Text size={200} weight="semibold" style={{ color: tokens.colorBrandForeground1 }}>{t('Neu – noch nicht gespeichert')}</Text>}
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <Field label={t('Name')} style={{ flex: '1 1 160px' }}><Input value={f.name} onChange={(_, d) => change(i, { name: d.value })} placeholder={t('z. B. Musik')} /></Field>
            <Field label={t('Pfad im Container')} style={{ flex: '2 1 240px' }}>
              <Input value={f.path} onChange={(_, d) => change(i, { path: d.value })} placeholder="/external/musik"
                contentAfter={<Button size="small" appearance="transparent" icon={<FolderOpen20Regular />} onClick={() => setBrowse(i)} aria-label={t('Ordner auf dem Server wählen')} title={t('Ordner auf dem Server wählen')} />} />
            </Field>
            <Tooltip content={t('Entfernen')} relationship="label">
              <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => { setFolders(x => x!.filter((_, n) => n !== i)); setDirty(true) }} />
            </Tooltip>
          </div>
          <Switch checked={f.readOnly} onChange={(_, d) => change(i, { readOnly: d.checked })} label={t('Nur lesen')} />
          {f.id
            ? <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
              <Button size="small" onClick={() => setSearchOf(f.id)}>{t('Indexierung konfigurieren')}</Button>
              <Text size={200} style={muted}>{searchSummary(states.find(s => s.mount === f.id))}</Text>
            </div>
            : <Text size={200} style={muted}>{t('Die Indexierung lässt sich nach dem Speichern konfigurieren.')}</Text>}
          {f.path.trim().startsWith('/') && <>
            <Text size={200} style={muted}>{t('In dmOffice erscheinen:')}</Text>
            <ServerTop path={f.path.trim()} include={f.include ?? []} onChange={include => change(i, { include })} onNone={n => setNoFolder(x => ({ ...x, [f.id || i]: n }))} />
          </>}
          <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', alignItems: 'center' }}>
            <Text size={200} style={muted}>{t('Sichtbar für:')}</Text>
            <Checkbox label={t('alle')} checked={f.users.length === 0} onChange={(_, d) => change(i, { users: d.checked ? [] : users.map(u => u.id) })} />
            {f.users.length > 0 && users.map(u => (
              <Checkbox key={u.id} label={u.displayName || u.login} checked={f.users.includes(u.id)}
                onChange={(_, d) => change(i, { users: d.checked ? [...f.users, u.id] : f.users.filter(x => x !== u.id) })} />
            ))}
          </div>
        </div>
      ))}
      {/* Erst die Liste ergänzen (dezent, direkt unter den Ordnern), dann einmal speichern - so ist klar, was was tut */}
      {/* Während etwas offen (ungespeichert) ist, kein zweiter Ordner dazu - erst speichern */}
      {!dirty && <div><Button appearance="subtle" icon={<Add20Regular />} onClick={() => { setFolders(f => [...f!, { id: '', name: '', path: '', users: [], readOnly: true }]); setDirty(true) }}>{t('Weiteren Ordner hinzufügen')}</Button></div>}
      {dirty && <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap', paddingTop: 8, borderTop: `1px solid ${tokens.colorNeutralStroke2}` }}>
        <Button appearance="primary" disabled={Object.values(noFolder).some(Boolean)} onClick={() => save()}>{t('Speichern')}</Button>
        {dirty && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground2 }}>{t('Neue und geänderte Ordner werden erst mit „Speichern“ übernommen.')}</Text>}
      </div>}
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
            <Text size={200} style={{ ...muted, display: 'block' }}>{folder.path}</Text>
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
              <ServerTop path={folder.path} include={folders} onChange={setFolders} onNone={setNoFolder} />
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

/** Ordner der obersten Ebene eines Serverordners zum Auswählen (kurz nach dem Tippen geladen). */
function ServerTop({ path, include, onChange, onNone }: { path: string; include: string[]; onChange: (include: string[]) => void; onNone?: (none: boolean) => void }) {
  const [dirs, setDirs] = useState<string[] | null>(null)
  const [missing, setMissing] = useState(false)
  useEffect(() => {
    setDirs(null); setMissing(false)
    const timer = setTimeout(() => {
      api.get<{ dirs: { name: string }[] }>('/api/admin/server-folders?' + q({ path }))
        .then(r => setDirs(r.dirs.map(d => d.name))).catch(() => setMissing(true))
    }, 400)
    return () => clearTimeout(timer)
  }, [path])
  if (missing) return <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Den Ordner gibt es auf dem Server nicht.')}</Text>
  return <TopFolderChoice dirs={dirs} include={include} onChange={onChange} onNone={onNone} />
}
