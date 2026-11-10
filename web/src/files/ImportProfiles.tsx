import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Radio, RadioGroup,
  Spinner, SpinButton, Switch, Text, Textarea, Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ArrowSync20Regular, ArrowUp20Regular, Delete20Regular, Dismiss16Regular, Edit20Regular, Folder20Filled, FolderAdd20Regular, FolderOpen20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { askConfirm } from '../shell/context'

interface Mount { id: string; name: string; readOnly?: boolean; available?: boolean }
/** Quellordner: in der eigenen Ablage (mount leer) oder in einem externen Ordner. */
interface Src { mount: string; path: string }
interface Profile {
  id: string; name: string; sources: Src[]; mount: string; mountName?: string | null; target: string; folders: string; names: string; title: string; description: string
  copy: boolean; every: number; since: string | null; active: boolean; lastRun: string | null; error: string | null; lastCount: number; total: number
  pending: number; running: boolean
}
interface Data { profiles: Profile[]; mounts: Mount[]; folderTokens: string[]; nameTokens: string[]; textTokens: string[] }
interface Preview { sample: string | null; folder: string; name: string; title: string; description: string }

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
  section: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  head: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, textTransform: 'uppercase', letterSpacing: '0.05em', color: tokens.colorNeutralForeground2 },
  line: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  grow: { flex: 1, minWidth: '160px' },
  chips: { display: 'flex', flexWrap: 'wrap', gap: '4px' },
  chip: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, minWidth: 0, padding: '2px 8px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  preview: { fontStyle: 'italic', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, wordBreak: 'break-all' },
  source: { display: 'flex', alignItems: 'center', gap: '6px' },
  browse: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, height: '260px', overflowY: 'auto', padding: '4px' },
  dir: { display: 'flex', alignItems: 'center', gap: '6px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
})

const show = (path: string) => path ? path.split('/').join(' › ') : t('Oberste Ebene')
const mountName = (id: string, mounts: Mount[]) => id ? mounts.find(m => m.id === id)?.name ?? id : t('Eigene Ablage')
const srcName = (x: Src, mounts: Mount[]) => mountName(x.mount, mounts) + (x.path ? ' › ' + show(x.path) : '')
const where = (p: Profile, mounts: Mount[]) =>
  (p.mount ? (p.mountName ?? mounts.find(m => m.id === p.mount)?.name ?? p.mount) : t('Eigene Ablage')) + (p.target ? ' › ' + show(p.target) : '')

/** Galerie → Importprofile: Fotos aus Ordnern der eigenen Ablage nach Mustern in einen Zielordner einsortieren. */
export function ImportProfilesDialog({ onClose }: { onClose: (changed: boolean) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [data, setData] = useState<Data | null>(null)
  const [edit, setEdit] = useState<Profile | 'new' | null>(null)
  const [changed, setChanged] = useState(false)
  const load = () => api.get<Data>('/api/gallery/imports').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Während ein Profil läuft, den Stand nachladen
  useEffect(() => {
    if (!data?.profiles.some(p => p.running)) return
    const h = setTimeout(() => { load(); setChanged(true) }, 3000)
    return () => clearTimeout(h)
  }, [data]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!data) return null
  const act = async (f: () => Promise<unknown>) => { try { await f(); setChanged(true); load() } catch (e) { fail(e) } }
  const remove = async (p: Profile) => {
    if (await askConfirm(t('Importprofil löschen'), t('Das Importprofil „{name}“ wird gelöscht. Bereits einsortierte Fotos bleiben, wo sie sind.', { name: p.name }), t('Löschen')))
      act(() => api.del(`/api/gallery/imports/${p.id}`))
  }
  const status = (p: Profile) => p.running ? t('Wird einsortiert …')
    : p.error ? t('Fehler: {error}', { error: p.error })
    : p.lastRun ? t('Zuletzt geprüft {when}: {n} Fotos einsortiert, insgesamt {total}', { when: new Date(p.lastRun).toLocaleString(), n: p.lastCount, total: p.total })
    : p.active ? t('Wird bald zum ersten Mal geprüft.') : t('Noch nicht geprüft.')
  return (
    <>
      <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(changed) }}>
        <DialogSurface style={{ maxWidth: 660 }}>
          <DialogBody>
            <DialogTitle>{t('Importprofile')}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>{t('Ein Importprofil sortiert neue Fotos aus Ordnern der eigenen Ablage oder aus externen Ordnern, etwa aus dem automatischen Hochladen der App, in einen Zielordner ein. Ordnerstruktur und Dateinamen folgen den Mustern des Profils. RAW, JPG und XMP-Datei eines Fotos bleiben zusammen.')}</Text>
              {data.profiles.length === 0 && <Text className={s.muted}>{t('Noch keine Importprofile.')}</Text>}
              {data.profiles.map(p => (
                <div key={p.id} className={s.row}>
                  <div className={s.name}>
                    <Text weight="semibold">{p.name}</Text>
                    <Text size={200} className={s.muted}>{p.sources.map(x => srcName(x, data.mounts)).join(', ')} → {where(p, data.mounts)}</Text>
                    <Text size={200} className={s.muted}>
                      {status(p)}{p.pending > 0 && !p.running ? ' · ' + t('{n} Fotos noch offen', { n: p.pending }) : ''}
                    </Text>
                  </div>
                  {p.running && <Spinner size="tiny" />}
                  <Tooltip content={t('Im Hintergrund regelmäßig prüfen und einsortieren')} relationship="label">
                    <Switch checked={p.active} onChange={(_, d) => act(() => api.put(`/api/gallery/imports/${p.id}`, { ...p, active: d.checked, language: lang }))} />
                  </Tooltip>
                  <Tooltip content={t('Jetzt einsortieren')} relationship="label">
                    <Button appearance="subtle" icon={<ArrowSync20Regular />} disabled={p.running} onClick={() => act(() => api.post(`/api/gallery/imports/${p.id}/run`))} />
                  </Tooltip>
                  <Tooltip content={t('Bearbeiten')} relationship="label">
                    <Button appearance="subtle" icon={<Edit20Regular />} onClick={() => setEdit(p)} />
                  </Tooltip>
                  <Tooltip content={t('Löschen')} relationship="label">
                    <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(p)} />
                  </Tooltip>
                </div>
              ))}
            </DialogContent>
            <DialogActions>
              <Button icon={<Add20Regular />} onClick={() => setEdit('new')}>{t('Neues Importprofil')}</Button>
              <Button appearance="primary" onClick={() => onClose(changed)}>{t('Schließen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {edit && <EditProfile data={data} profile={edit === 'new' ? null : edit} onClose={saved => { setEdit(null); if (saved) { setChanged(true); load() } }} />}
    </>
  )
}

/** Eingabefeld für ein Muster mit Platzhaltern zum Anklicken (an der Schreibmarke eingefügt) und Vorschau darunter. */
export function PatternField({ label, value, onChange, tokens: list, preview, multiline }: {
  label: string; value: string; onChange: (v: string) => void; tokens: string[]; preview: string | undefined; multiline?: boolean
}) {
  const s = useStyles()
  const ref = useRef<HTMLInputElement & HTMLTextAreaElement>(null)
  const insert = (token: string) => {
    const el = ref.current
    const text = `{${token}}`
    const at = el?.selectionStart ?? value.length
    const end = el?.selectionEnd ?? at
    onChange(value.slice(0, at) + text + value.slice(end))
    requestAnimationFrame(() => { el?.focus(); el?.setSelectionRange(at + text.length, at + text.length) })
  }
  return (
    <Field label={label}>
      {multiline
        ? <Textarea ref={ref} className={s.mono} value={value} onChange={(_, d) => onChange(d.value)} resize="vertical" />
        : <Input ref={ref} className={s.mono} value={value} onChange={(_, d) => onChange(d.value)} />}
      <Text className={s.preview}>→ {preview ?? ''}</Text>
      <div className={s.chips}>
        {list.map(x => <Button key={x} size="small" className={s.chip} onClick={() => insert(x)}>{`{${x}}`}</Button>)}
      </div>
    </Field>
  )
}

function EditProfile({ data, profile, onClose }: { data: Data; profile: Profile | null; onClose: (saved: boolean) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [f, setF] = useState(() => profile ? { ...profile } : {
    name: '', sources: [] as Src[], mount: '', target: '', folders: '{exif_year}/{exif_month}/{exif_day}', names: '{exif_datetime}_{original_name}_{camera_make}_{camera_model}',
    title: '', description: '', copy: false, every: 10, since: null as string | null, active: true,
  })
  const set = (patch: Partial<typeof f>) => setF(x => ({ ...x, ...patch }))
  const [pick, setPick] = useState<'source' | 'target' | null>(null)
  // Ort des nächsten Quellordners: eigene Ablage oder ein externer Ordner
  const [srcMount, setSrcMount] = useState('')
  const writable = data.mounts.filter(m => !m.readOnly)
  const extSources = f.sources.filter(x => x.mount)
  // Aus einem Ordner nur zum Lesen lässt sich nur kopieren
  const mustCopy = extSources.some(x => data.mounts.find(m => m.id === x.mount)?.readOnly)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [busy, setBusy] = useState(false)
  // Vorschau vom Server (dieselbe Rechnung wie beim Einsortieren), kurz nach dem letzten Tastendruck
  useEffect(() => {
    const h = setTimeout(() => {
      api.post<Preview>('/api/gallery/imports/preview', { sources: f.sources, folders: f.folders, names: f.names, title: f.title, description: f.description, language: lang })
        .then(setPreview).catch(() => setPreview(null))
    }, 300)
    return () => clearTimeout(h)
  }, [f.sources, f.folders, f.names, f.title, f.description])
  const save = async () => {
    setBusy(true)
    try {
      const body = { ...f, copy: f.copy || mustCopy, since: f.since || null, language: lang }
      if (profile) await api.put(`/api/gallery/imports/${profile.id}`, body)
      else await api.post('/api/gallery/imports', body)
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const today = new Date().toISOString().slice(0, 10)
  const targetFull = (f.mount ? data.mounts.find(m => m.id === f.mount)?.name ?? f.mount : t('Eigene Ablage')) + (f.target ? ' › ' + show(f.target) : '')
  return (
    <>
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{profile ? t('Importprofil bearbeiten') : t('Neues Importprofil')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Name')} required><Input value={f.name} onChange={(_, d) => set({ name: d.value })} /></Field>

            <div className={s.section}>
              <Text className={s.head}>{t('Quelle')}</Text>
              <Text size={200} className={s.muted}>{t('Ordner der eigenen Ablage oder externe Ordner, jeweils mit allen Unterordnern.')}</Text>
              {f.sources.map(x => (
                <div key={x.mount + ':' + x.path} className={s.source}>
                  <Folder20Filled color="#E8A33D" /><Text className={s.grow}>{srcName(x, data.mounts)}</Text>
                  <Tooltip content={t('Quellordner entfernen')} relationship="label">
                    <Button appearance="subtle" size="small" icon={<Dismiss16Regular />} onClick={() => set({ sources: f.sources.filter(y => y !== x) })} />
                  </Tooltip>
                </div>
              ))}
              <div className={s.line}>
                {data.mounts.length > 0 && <Dropdown className={s.grow} value={mountName(srcMount, data.mounts)} selectedOptions={[srcMount]}
                  onOptionSelect={(_, d) => setSrcMount(d.optionValue ?? '')}>
                  <Option value="">{t('Eigene Ablage')}</Option>
                  {data.mounts.map(m => <Option key={m.id} value={m.id} text={m.name}>{m.readOnly ? t('{name} (nur zum Lesen)', { name: m.name }) : m.name}</Option>)}
                </Dropdown>}
                <Button icon={<FolderOpen20Regular />} onClick={() => setPick('source')}>{t('Quellordner hinzufügen')}</Button>
              </div>
              {/* Abstand nur für externe Ordner - Ordner der eigenen Ablage meldet der Server sofort (FolderWatch) */}
              {extSources.length > 0 && <div className={s.line}>
                <Text>{t('Externe Ordner: alle')}</Text>
                <SpinButton style={{ width: 90 }} min={5} max={1440} value={f.every} onChange={(_, d) => set({ every: d.value ?? (Number(d.displayValue) || f.every) })} />
                <Text>{t('Minuten auf neue Dateien prüfen')}</Text>
              </div>}
              <Text size={200} className={s.muted}>{t('Ordner der eigenen Ablage werden sofort geprüft, sobald dort eine Datei fertig gespeichert ist. Externe Ordner werden im eingestellten Abstand geprüft.')}</Text>
              <div className={s.line}>
                <Checkbox checked={!!f.since} label={t('Nur Fotos, aufgenommen ab')} onChange={(_, d) => set({ since: d.checked ? (f.since || today) : null })} />
                <Input type="date" disabled={!f.since} value={f.since ?? today} onChange={(_, d) => set({ since: d.value || null })} />
              </div>
              <Checkbox checked={f.active} label={t('Überwachung aktiv: auch nach einem Neustart des Servers im Hintergrund einsortieren')} onChange={(_, d) => set({ active: !!d.checked })} />
              <RadioGroup layout="horizontal" value={f.copy || mustCopy ? 'copy' : 'move'} onChange={(_, d) => set({ copy: d.value === 'copy' })}>
                <Radio value="move" disabled={mustCopy} label={t('In den Zielordner verschieben')} />
                <Radio value="copy" label={t('In den Zielordner kopieren (die Quelle bleibt)')} />
              </RadioGroup>
              {mustCopy && <Text size={200} className={s.muted}>{t('Ein Quellordner ist nur zum Lesen eingebunden. Daraus lässt sich nur kopieren.')}</Text>}
              {!f.copy && !mustCopy && extSources.length > 0 && <Text size={200} className={s.muted}>
                {t('Beim Verschieben aus einem externen Ordner wird die Quelle erst gelöscht, wenn die Datei am Ziel geprüft ist. Einen Papierkorb gibt es dort nicht.')}
              </Text>}
            </div>

            <div className={s.section}>
              <Text className={s.head}>{t('Zielordner')}</Text>
              <div className={s.line}>
                <Dropdown className={s.grow} value={mountName(f.mount, data.mounts)} selectedOptions={[f.mount]}
                  onOptionSelect={(_, d) => set({ mount: d.optionValue ?? '', target: '' })}>
                  <Option value="">{t('Eigene Ablage')}</Option>
                  {writable.map(m => <Option key={m.id} value={m.id}>{m.name}</Option>)}
                </Dropdown>
                <Button icon={<Folder20Filled color="#E8A33D" />} onClick={() => setPick('target')}>{t('Durchsuchen')}</Button>
              </div>
              <Text>{targetFull}</Text>
              <PatternField label={t('Ordnerstruktur-Muster')} value={f.folders} onChange={v => set({ folders: v })} tokens={data.folderTokens} preview={preview?.folder} />
            </div>

            <div className={s.section}>
              <Text className={s.head}>{t('Dateiname')}</Text>
              <PatternField label={t('Dateinamen-Muster (ohne Dateiendung)')} value={f.names} onChange={v => set({ names: v })} tokens={data.nameTokens} preview={preview?.name} />
              <Text size={200} className={s.muted}>{t('Liegt am Ziel schon eine Datei mit diesem Namen und gleichem Inhalt, gilt das Foto als einsortiert. Bei anderem Inhalt wird „_2“, „_3“ … angehängt.')}</Text>
            </div>

            <div className={s.section}>
              <Text className={s.head}>{t('Metadaten')}</Text>
              <PatternField label={t('Titel (Platzhalter wie beim Dateinamen)')} value={f.title} onChange={v => set({ title: v })} tokens={data.textTokens} preview={preview?.title} />
              <PatternField label={t('Beschreibung (Platzhalter wie beim Dateinamen)')} value={f.description} onChange={v => set({ description: v })} tokens={data.textTokens} preview={preview?.description} multiline />
              <Text size={200} className={s.muted}>{t('Titel und Beschreibung stehen beim JPG in der Datei, beim RAW in der XMP-Datei daneben. Leer: es wird nichts geschrieben.')}</Text>
            </div>
            <Text size={200} className={s.muted}>
              {preview?.sample ? t('Vorschau mit „{name}“.', { name: preview.sample.slice(preview.sample.lastIndexOf('/') + 1) }) : t('Vorschau mit einem Beispielfoto.')}
            </Text>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !f.name.trim() || f.sources.length === 0} onClick={save}>{t('Profil speichern')}</Button>
          </DialogActions>
        </DialogBody>
        {pick && <BrowseDialog mount={pick === 'source' ? srcMount : f.mount} start={pick === 'target' ? f.target : ''}
          title={pick === 'source' ? t('Quellordner wählen') : t('Zielordner wählen')} needPath={pick === 'source' && !srcMount}
          onPick={path => {
            if (pick === 'source') { if (!f.sources.some(x => x.mount === srcMount && x.path === path)) set({ sources: [...f.sources, { mount: srcMount, path }] }) }
            else set({ target: path })
            setPick(null)
          }}
          onClose={() => setPick(null)} />}
      </DialogSurface>
    </Dialog>
    </>
  )
}

/** Ordner wählen: in der eigenen Ablage (mount leer) oder in einem externen Ordner. */
/** needPath: die oberste Ebene ist keine Wahl (Quellordner). */
export function BrowseDialog({ mount, start, title, needPath, onPick, onClose }: { mount: string; start: string; title: string; needPath?: boolean; onPick: (path: string) => void; onClose: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [path, setPath] = useState(start)
  const [dirs, setDirs] = useState<string[] | null>(null)
  const [reload, setReload] = useState(0)
  // Neuer Ordner direkt hier, ohne Umweg über den Bereich Dateien
  const [naming, setNaming] = useState<string | null>(null)
  const create = async () => {
    const name = naming?.trim()
    if (!name || /[\\/]/.test(name) || name === '.' || name === '..') return
    const target = path ? `${path}/${name}` : name
    try {
      if (mount) await api.post('/api/files/external/mkdir', { mount, path: target })
      else await api.post('/api/files/mkdir', { path: target })
      setNaming(null)
      setPath(target)
      setReload(r => r + 1)
    } catch (e) { fail(e) }
  }
  useEffect(() => {
    setDirs(null)
    const names = (items: { name: string; isDir: boolean; owner?: string | null }[]) =>
      items.filter(i => i.isDir && !i.owner && !i.name.startsWith('.') && !i.name.startsWith('@')).map(i => i.name).sort((a, b) => a.localeCompare(b))
    const req = mount
      ? api.get<{ items: { name: string; isDir: boolean }[] }>('/api/files/external/list?' + q({ mount, path }))
      : api.get<{ items: { name: string; isDir: boolean; owner?: string | null }[] }>('/api/files?' + q({ path: path || undefined }))
    req.then(r => setDirs(names(r.items))).catch(e => { setDirs([]); fail(e) })
  }, [mount, path, reload]) // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 540 }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text size={200} className={s.muted}>{show(path)}</Text>
            <div className={s.browse}>
              {path && <div className={s.dir} onClick={() => setPath(path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')}><ArrowUp20Regular />{t('Übergeordneter Ordner')}</div>}
              {dirs === null ? <Spinner size="tiny" /> : dirs.map(d => (
                <div key={d} className={s.dir} onClick={() => setPath(path ? `${path}/${d}` : d)}><Folder20Filled color="#E8A33D" />{d}</div>
              ))}
            </div>
            {naming === null
              ? <Button appearance="subtle" icon={<FolderAdd20Regular />} style={{ alignSelf: 'flex-start' }} onClick={() => setNaming('')}>{t('Neuer Ordner')}</Button>
              : <div style={{ display: 'flex', gap: 8 }}>
                <Input autoFocus style={{ flex: 1 }} value={naming} placeholder={t('Name des Ordners')} onChange={(_, d) => setNaming(d.value)}
                  onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); create() } else if (e.key === 'Escape') { e.stopPropagation(); setNaming(null) } }} />
                <Button appearance="primary" disabled={!naming.trim()} onClick={create}>{t('Anlegen')}</Button>
                <Button onClick={() => setNaming(null)}>{t('Abbrechen')}</Button>
              </div>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={needPath && !path} onClick={() => onPick(path)}>{t('Diesen Ordner wählen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
