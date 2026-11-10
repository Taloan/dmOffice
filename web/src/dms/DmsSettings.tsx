import {
  Button, Checkbox, Dialog, Divider, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, MessageBar, MessageBarBody, Option, SpinButton, Switch, Text,
  Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ArrowSync20Regular, Delete20Regular, Edit20Regular, Folder20Filled, FolderArrowRight20Regular, FolderLink20Filled, LockClosed20Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { BrowseDialog, PatternField } from '../files/ImportProfiles'
import { lang, t } from '../i18n'
import { askConfirm } from '../shell/context'
import { Hint, PageTitle } from '../settings/parts'
import { Entry, Mount, ordered, Profile, Source, State } from './model'
import { DmsMailSourcesSettings } from './DmsMailSettings'

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
  section: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  head: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, textTransform: 'uppercase', letterSpacing: '0.05em', color: tokens.colorNeutralForeground2 },
  line: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  grow: { flex: 1, minWidth: '160px' },
  list: { display: 'flex', flexDirection: 'column', maxWidth: '760px' },
})

const show = (path: string) => path ? path.split('/').join(' › ') : t('Oberste Ebene')
const FOLDER_TOKENS = ['correspondent', 'correspondent_path', 'doctype', 'doctype_path', 'tag', 'doc_year', 'doc_month', 'doc_month_name', 'import_year', 'import_month', 'source']
const NAME_TOKENS = ['doc_date', 'correspondent', 'doctype', 'title', 'tags', 'tag', 'original_name', 'import_date', 'doc_year', 'doc_month', 'doc_day', 'sender']

// ---------------- Ablageprofile ----------------

/** Einstellungen → DMS → Ablageprofile. */
export function DmsProfilesSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [list, setList] = useState<Profile[] | null>(null)
  const [edit, setEdit] = useState<Profile | 'new' | null>(null)
  const load = () => api.get<Profile[]>('/api/dms/profiles?' + q({ lang })).then(setList).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const [mounts, setMounts] = useState<Mount[]>([])
  useEffect(() => { api.get<Mount[]>('/api/dms/mounts').then(setMounts).catch(() => {}) }, [])
  const target = (p: Profile) => p.target === 'tresor' ? t('Tresor')
    : p.target === 'external' ? mounts.find(m => m.id === p.mount)?.name ?? t('Externer Ordner') : t('Dateiablage')
  const remove = async (p: Profile) => {
    if (!await askConfirm(t('Ablageprofil löschen'), t('Das Ablageprofil „{name}“ wird gelöscht. Abgelegte Dokumente bleiben, wo sie sind.', { name: p.name }), t('Löschen'))) return
    try { await api.del(`/api/dms/profiles/${p.id}`); load() } catch (e) { fail(e) }
  }
  const refile = async (p: Profile) => {
    if (!await askConfirm(t('Nach Vorgabe neu ablegen'), t('Alle Dokumente des Ablageprofils „{name}“ werden nach Ordner- und Namensmuster neu abgelegt, auch von Hand verschobene.', { name: p.name }), t('Neu ablegen'))) return
    try { const r = await api.post<{ count: number }>(`/api/dms/profiles/${p.id}/refile`); notify(r.count === 1 ? t('1 Dokument geprüft') : t('{n} Dokumente geprüft', { n: r.count })) } catch (e) { fail(e) }
  }
  return (
    <>
      <PageTitle>{t('Ablageprofile')}</PageTitle>
      <Hint>{t('Ein Ablageprofil legt fest, in welchem Ordner und unter welchem Namen DMS ein Dokument ablegt: in der Dateiablage, im Tresor oder in einem externen Ordner. Jede Quelle wählt ein Ablageprofil; beim Hochladen ist das als Standard markierte vorbelegt. Zielordner von Ablageprofilen werden aus der Galerie ausgenommen.')}</Hint>
      <div className={s.list}>
        {list?.map(p => (
          <div key={p.id} className={s.row}>
            {/* Ziel auf einen Blick: Dateiablage, Tresor oder externer Ordner */}
            <Tooltip content={target(p)} relationship="label">
              {p.target === 'tresor' ? <LockClosed20Filled color={tokens.colorPaletteMarigoldForeground1} />
                : p.target === 'external' ? <FolderLink20Filled color="#E8A33D" /> : <Folder20Filled color="#E8A33D" />}
            </Tooltip>
            <div className={s.name}>
              <Text weight="semibold">{p.name}{p.default && p.name !== t('Standard') ? ' · ' + t('Standard') : ''}</Text>
              <Text size={200} className={s.muted}>{target(p)} › {show(p.folder)} › {p.folders || '…'} › {p.names}</Text>
            </div>
            <Tooltip content={t('Nach Vorgabe neu ablegen')} relationship="label"><Button appearance="subtle" icon={<FolderArrowRight20Regular />} onClick={() => refile(p)} /></Tooltip>
            <Tooltip content={t('Bearbeiten')} relationship="label"><Button appearance="subtle" icon={<Edit20Regular />} onClick={() => setEdit(p)} /></Tooltip>
            <Tooltip content={t('Löschen')} relationship="label"><Button appearance="subtle" icon={<Delete20Regular />} disabled={list.length <= 1} onClick={() => remove(p)} /></Tooltip>
          </div>
        ))}
      </div>
      <div><Button icon={<Add20Regular />} onClick={() => setEdit('new')}>{t('Neues Ablageprofil')}</Button></div>
      {edit && <EditProfile profile={edit === 'new' ? null : edit} onClose={saved => { setEdit(null); if (saved) load() }} />}
    </>
  )
}

function EditProfile({ profile, onClose }: { profile: Profile | null; onClose: (saved: boolean) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [f, setF] = useState<Profile>(() => profile ? { ...profile } : {
    id: '', name: '', target: 'files', folder: lang === 'en' ? 'Documents' : 'Dokumente', folders: '{correspondent}/{doc_year}', names: '{doc_date} {doctype} {title}',
    unknown: t('Unbekannt'), follow: true, default: false, language: lang,
  })
  const set = (patch: Partial<Profile>) => setF(x => ({ ...x, ...patch }))
  const [preview, setPreview] = useState<string>('')
  const [inboxPreview, setInboxPreview] = useState<string>('')
  const [pick, setPick] = useState(false)
  const [tresor, setTresor] = useState(false)
  const [mounts, setMounts] = useState<Mount[]>([])
  useEffect(() => {
    api.get<State>('/api/dms/state').then(st => setTresor(st.tresor)).catch(() => {})
    api.get<Mount[]>('/api/dms/mounts').then(setMounts).catch(() => {})
  }, [])
  const writable = mounts.filter(m => !m.readOnly)
  const targetName = f.target === 'tresor' ? t('Tresor') : f.target === 'external' ? t('Externer Ordner') : t('Dateiablage')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    const h = setTimeout(() => {
      api.post<{ path?: string; inbox?: string; error?: string }>('/api/dms/profiles/preview?' + q({ lang }), { ...f, language: lang })
        .then(r => { setPreview(r.error ? t(r.error) : r.path ?? ''); setInboxPreview(r.error ? '' : r.inbox ?? '') }).catch(() => setPreview(''))
    }, 300)
    return () => clearTimeout(h)
  }, [f.folder, f.folders, f.names, f.unknown, f.inboxFolder]) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    setBusy(true)
    try {
      const body = { ...f, language: lang }
      if (profile) await api.put(`/api/dms/profiles/${profile.id}`, body)
      else await api.post('/api/dms/profiles', body)
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <>
      <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
        <DialogSurface style={{ maxWidth: 760 }}>
          <DialogBody>
            <DialogTitle>{profile ? t('Ablageprofil bearbeiten') : t('Neues Ablageprofil')}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Field label={t('Name')} required><Input value={f.name} onChange={(_, d) => set({ name: d.value })} /></Field>
              <div className={s.section}>
                <Text className={s.head}>{t('Zielordner')}</Text>
                <div className={s.line}>
                  <Dropdown className={s.grow} value={targetName} selectedOptions={[f.target]}
                    onOptionSelect={(_, d) => {
                      const target = d.optionValue ?? 'files'
                      set({ target, mount: target === 'external' ? f.mount ?? writable[0]?.id ?? null : null, writeMeta: f.writeMeta ?? true })
                    }}>
                    <Option value="files">{t('Dateiablage')}</Option>
                    <Option value="tresor" disabled={!tresor}>{tresor ? t('Tresor') : t('Tresor (erst einrichten)')}</Option>
                    <Option value="external" disabled={!writable.length}>{writable.length ? t('Externer Ordner') : t('Externer Ordner (keiner beschreibbar)')}</Option>
                  </Dropdown>
                  {f.target === 'external' && <Dropdown className={s.grow} value={writable.find(m => m.id === f.mount)?.name ?? ''} selectedOptions={f.mount ? [f.mount] : []}
                    onOptionSelect={(_, d) => set({ mount: d.optionValue ?? null })}>
                    {writable.map(m => <Option key={m.id} value={m.id}>{m.name}</Option>)}
                  </Dropdown>}
                  {(f.target === 'files' || (f.target === 'external' && f.mount)) &&
                    <Button icon={<Folder20Filled color="#E8A33D" />} onClick={() => setPick(true)}>{t('Durchsuchen')}</Button>}
                </div>
                {f.target === 'tresor'
                  ? <Field label={t('Ordner im Tresor')}><Input value={f.folder} onChange={(_, d) => set({ folder: d.value })} /></Field>
                  : <Text>{show(f.folder)}</Text>}
                {f.target === 'tresor' && <Text size={200} className={s.muted}>{t('Im Tresor: Texterkennung, Volltext und Einordnung nur bei offenem Tresor, nie mit KI. Ist er zu, wird Neues verschlüsselt abgelegt und beim nächsten Öffnen übernommen.')}</Text>}
                {f.target === 'external' && <MessageBar intent="warning"><MessageBarBody>{t('Die Dokumente liegen im externen Ordner unverschlüsselt; jeder mit Zugriff auf die Freigabe kann sie lesen. Das Original einer Archivfassung liegt daneben im Unterordner „Original“. Versionen, Papierkorb und Freigabelinks der Dateiablage gibt es dort nicht; Löschen im DMS löscht draußen endgültig.')}</MessageBarBody></MessageBar>}
                <PatternField label={t('Ordnerstruktur-Muster')} value={f.folders} onChange={v => set({ folders: v })} tokens={FOLDER_TOKENS} preview={undefined} />
                <Field label={t('Inbox-Ordner')} hint={t('Bis zum Bestätigen liegen Dokumente flach in diesem Ordner unter dem Zielordner, erst dann nach dem Ordnerstruktur-Muster.')}>
                  <Input value={f.inboxFolder ?? '.INBOX'} onChange={(_, d) => set({ inboxFolder: d.value })} />
                </Field>
              </div>
              <div className={s.section}>
                <Text className={s.head}>{t('Dateiname')}</Text>
                <PatternField label={t('Dateinamen-Muster (ohne Dateiendung)')} value={f.names} onChange={v => set({ names: v })} tokens={NAME_TOKENS} preview={undefined} />
                <Field label={t('Text für leere Platzhalter')}><Input value={f.unknown} onChange={(_, d) => set({ unknown: d.value })} /></Field>
                <Text size={200} className={s.muted}>{t('Gibt es den Namen schon, wird „ (2)“, „ (3)“ … angehängt.')}</Text>
              </div>
              <Text className={s.muted}>{t('In der Inbox')} → {inboxPreview}</Text>
              <Text className={s.muted}>{t('Bestätigt')} → {preview}</Text>
              <Checkbox checked={f.follow} onChange={(_, d) => set({ follow: !!d.checked })}
                label={t('Ablage folgt den Angaben: Ändern sich Korrespondent, Dokumenttyp, Titel, Tags oder Datum, wird die Datei nach dem Muster verschoben bzw. umbenannt. Von Hand verschobene Dateien bleiben, wo sie sind.')} />
              {f.target === 'external' && <Checkbox checked={f.writeMeta ?? true} onChange={(_, d) => set({ writeMeta: !!d.checked })}
                label={t('Angaben in die PDF schreiben: Titel, Korrespondent, Dokumenttyp, Datum und Tags stehen in den Metadaten der Archivfassung und werden nach Änderungen neu geschrieben.')} />}
              <Checkbox checked={f.default} onChange={(_, d) => set({ default: !!d.checked })} label={t('Standard beim Hochladen')} />
            </DialogContent>
            <DialogActions>
              <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
              <Button appearance="primary" disabled={busy || !f.name.trim() || !f.folder || (f.target === 'external' && !f.mount)} onClick={save}>{t('Speichern')}</Button>
            </DialogActions>
          </DialogBody>
          {pick && <BrowseDialog mount={f.target === 'external' ? f.mount ?? '' : ''} start={f.folder} title={t('Zielordner wählen')} needPath onPick={path => { set({ folder: path }); setPick(false) }} onClose={() => setPick(false)} />}
        </DialogSurface>
      </Dialog>
    </>
  )
}

// ---------------- Quellen ----------------

/** Einstellungen → DMS → Quellen (überwachte Ordner). */
export function DmsSourcesSettings() {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<Source[] | null>(null)
  const [profiles, setProfiles] = useState<Profile[]>([])
  const [mounts, setMounts] = useState<{ id: string; name: string }[]>([])
  const [entries, setEntries] = useState<Entry[]>([])
  const [edit, setEdit] = useState<Source | 'new' | null>(null)
  const load = () => api.get<Source[]>('/api/dms/sources').then(setList).catch(fail)
  useEffect(() => {
    load()
    api.get<Profile[]>('/api/dms/profiles?' + q({ lang })).then(setProfiles).catch(fail)
    api.get<{ id: string; name: string }[]>('/api/dms/mounts').then(setMounts).catch(() => {})
    api.get<Entry[]>('/api/dms/lists').then(setEntries).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const act = async (f: () => Promise<unknown>) => { try { await f(); load() } catch (e) { fail(e) } }
  const remove = async (x: Source) => {
    if (await askConfirm(t('Quelle löschen'), t('Die Quelle „{name}“ wird gelöscht. Übernommene Dokumente bleiben.', { name: x.name }), t('Löschen')))
      act(() => api.del(`/api/dms/sources/${x.id}`))
  }
  const where = (x: Source) => (x.mount ? mounts.find(m => m.id === x.mount)?.name ?? x.mount : t('Eigene Ablage')) + ' › ' + show(x.folder)
  const status = (x: Source) => x.error ? t('Fehler: {error}', { error: t(x.error) })
    : x.lastRun ? t('Zuletzt geprüft {when}: {n} übernommen, {d} schon vorhanden, insgesamt {total}', { when: new Date(x.lastRun).toLocaleString(), n: x.lastCount, d: x.lastDuplicates, total: x.total })
    : t('Noch nicht geprüft.')
  return (
    <>
      <PageTitle>{t('Quellen')}</PageTitle>
      <Hint>{t('Quellen legen, was sie finden, in die Warteschlange vom DMS; verarbeitet und abgelegt wird nach dem Ablageprofil der Quelle.')}</Hint>
      <Text size={400} weight="semibold">{t('Dateiordner')}</Text>
      <Hint>{t('Aus einem überwachten Ordner übernimmt das DMS neue Dateien selbst, etwa vom Scanner. Eine Datei wird übernommen, aus der eigenen Ablage sobald sie fertig gespeichert ist, aus externen Ordnern wenn sie zwei Minuten unverändert liegt. Danach wird sie im Ordner entfernt (in der eigenen Ablage: in den Papierkorb). Aus externen Ordnern nur zum Lesen wird sie nicht entfernt; das DMS merkt sich, was übernommen ist.')}</Hint>
      <div className={s.list}>
        {list?.map(x => (
          <div key={x.id} className={s.row}>
            <div className={s.name}>
              <Text weight="semibold">{x.name}</Text>
              <Text size={200} className={s.muted}>{where(x)} → {profiles.find(p => p.id === x.profile)?.name ?? ''}</Text>
              <Text size={200} className={s.muted}>{status(x)}</Text>
            </div>
            <Tooltip content={t('Im Hintergrund regelmäßig prüfen')} relationship="label">
              <Switch checked={x.active} onChange={(_, d) => act(() => api.put(`/api/dms/sources/${x.id}`, { ...x, active: d.checked }))} />
            </Tooltip>
            <Tooltip content={t('Jetzt prüfen')} relationship="label"><Button appearance="subtle" icon={<ArrowSync20Regular />} onClick={() => act(() => api.post(`/api/dms/sources/${x.id}/run`))} /></Tooltip>
            <Tooltip content={t('Bearbeiten')} relationship="label"><Button appearance="subtle" icon={<Edit20Regular />} onClick={() => setEdit(x)} /></Tooltip>
            <Tooltip content={t('Löschen')} relationship="label"><Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(x)} /></Tooltip>
          </div>
        ))}
        {list?.length === 0 && <Text className={s.muted}>{t('Noch keine Dateiordner.')}</Text>}
      </div>
      <div><Button icon={<Add20Regular />} onClick={() => setEdit('new')}>{t('Neuer Dateiordner')}</Button></div>
      {edit && <EditSource source={edit === 'new' ? null : edit} profiles={profiles} mounts={mounts} entries={entries}
        onClose={saved => { setEdit(null); if (saved) load() }} />}
      <Divider />
      <DmsMailSourcesSettings embedded />
    </>
  )
}

function EditSource({ source, profiles, mounts, entries, onClose }: {
  source: Source | null; profiles: Profile[]; mounts: { id: string; name: string }[]; entries: Entry[]; onClose: (saved: boolean) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [f, setF] = useState<Source>(() => source ? { ...source } : {
    id: '', name: '', mount: '', folder: '', profile: profiles.find(p => p.default)?.id ?? profiles[0]?.id ?? null, correspondent: null, doctype: null, tags: [],
    every: 5, active: true, lastRun: null, error: null, lastCount: 0, lastDuplicates: 0, total: 0,
  })
  const set = (patch: Partial<Source>) => setF(x => ({ ...x, ...patch }))
  const [pick, setPick] = useState(false)
  const [busy, setBusy] = useState(false)
  const save = async () => {
    setBusy(true)
    try {
      if (source) await api.put(`/api/dms/sources/${source.id}`, f)
      else await api.post('/api/dms/sources', f)
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const pickList = (kind: 'correspondent' | 'doctype', value: string | null, onSel: (v: string | null) => void) => {
    const opts = ordered(entries, kind)
    return (
      <Dropdown className={s.grow} value={value ? opts.find(o => o.entry.id === value)?.entry.name ?? '' : t('Keine Vorgabe')} selectedOptions={[value ?? '']}
        onOptionSelect={(_, d) => onSel(d.optionValue || null)}>
        <Option value="">{t('Keine Vorgabe')}</Option>
        {opts.map(o => <Option key={o.entry.id} value={o.entry.id} text={o.entry.name}><span style={{ paddingLeft: o.depth * 12 }}>{o.entry.name}</span></Option>)}
      </Dropdown>
    )
  }
  const tagOpts = ordered(entries, 'tag')
  return (
    <>
      <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
        <DialogSurface style={{ maxWidth: 680 }}>
          <DialogBody>
            <DialogTitle>{source ? t('Quelle bearbeiten') : t('Neue Quelle')}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Field label={t('Name')} required><Input value={f.name} onChange={(_, d) => set({ name: d.value })} /></Field>
              <div className={s.section}>
                <Text className={s.head}>{t('Überwachter Ordner')}</Text>
                <div className={s.line}>
                  <Dropdown className={s.grow} value={f.mount ? mounts.find(m => m.id === f.mount)?.name ?? f.mount : t('Eigene Ablage')} selectedOptions={[f.mount]}
                    onOptionSelect={(_, d) => set({ mount: d.optionValue ?? '', folder: '' })}>
                    <Option value="">{t('Eigene Ablage')}</Option>
                    {mounts.map(m => <Option key={m.id} value={m.id}>{m.name}</Option>)}
                  </Dropdown>
                  <Button icon={<Folder20Filled color="#E8A33D" />} onClick={() => setPick(true)}>{t('Durchsuchen')}</Button>
                </div>
                <Text>{f.folder ? show(f.folder) : t('Noch kein Ordner gewählt.')}</Text>
                {/* Eigene Ablage: sofort bei jeder fertig gespeicherten Datei, kein Abstand; nur externe Ordner werden abgefragt */}
                {f.mount ? <>
                  <div className={s.line}>
                    <Text>{t('Überwachung: alle')}</Text>
                    <SpinButton style={{ width: 90 }} min={1} max={1440} value={f.every} onChange={(_, d) => set({ every: d.value ?? (Number(d.displayValue) || f.every) })} />
                    <Text>{t('Minuten auf neue Dateien prüfen')}</Text>
                  </div>
                  <Text size={200} className={s.muted}>{t('Externe Ordner meldet niemand; sie werden im eingestellten Abstand geprüft.')}</Text>
                </> : <Text size={200} className={s.muted}>{t('Neue Dateien übernimmt das DMS sofort, sobald sie fertig gespeichert sind.')}</Text>}
                <Checkbox checked={f.active} label={t('Überwachung aktiv')} onChange={(_, d) => set({ active: !!d.checked })} />
              </div>
              <div className={s.section}>
                <Text className={s.head}>{t('Ablage und Vorgaben')}</Text>
                <Field label={t('Ablageprofil')}>
                  <Dropdown value={profiles.find(p => p.id === f.profile)?.name ?? ''} selectedOptions={[f.profile ?? '']} onOptionSelect={(_, d) => set({ profile: d.optionValue ?? null })}>
                    {profiles.map(p => <Option key={p.id} value={p.id}>{p.name}</Option>)}
                  </Dropdown>
                </Field>
                <Field label={t('Korrespondent')}>{pickList('correspondent', f.correspondent, v => set({ correspondent: v }))}</Field>
                <Field label={t('Dokumenttyp')}>{pickList('doctype', f.doctype, v => set({ doctype: v }))}</Field>
                <Field label={t('Tags')}>
                  <Dropdown multiselect value={f.tags.map(id => entries.find(e => e.id === id)?.name ?? '').join(', ')} selectedOptions={f.tags}
                    onOptionSelect={(_, d) => set({ tags: d.selectedOptions })}>
                    {tagOpts.map(o => <Option key={o.entry.id} value={o.entry.id} text={o.entry.name}><span style={{ paddingLeft: o.depth * 12 }}>{o.entry.name}</span></Option>)}
                  </Dropdown>
                </Field>
                <Text size={200} className={s.muted}>{t('Vorgaben gelten vor Regeln, KI und Gelerntem.')}</Text>
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
              <Button appearance="primary" disabled={busy || !f.name.trim() || !f.folder} onClick={save}>{t('Speichern')}</Button>
            </DialogActions>
          </DialogBody>
          {pick && <BrowseDialog mount={f.mount} start={f.folder} title={t('Ordner wählen')} needPath={!f.mount}
            onPick={path => { set({ folder: path }); setPick(false) }} onClose={() => setPick(false)} />}
        </DialogSurface>
      </Dialog>
    </>
  )
}

// ---------------- KI und Stand ----------------

/** Einstellungen → DMS → Einordnung: KI beim Import, Stand von Texterkennung und PDF/A. */
export function DmsAssignSettings() {
  const { fail } = useApp()
  const [state, setState] = useState<State | null>(null)
  useEffect(() => { api.get<State>('/api/dms/state').then(setState).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!state) return null
  const toggle = async (ai: boolean) => {
    try { await api.put('/api/dms/settings', { ai }); setState({ ...state, ai }) } catch (e) { fail(e) }
  }
  return (
    <>
      <PageTitle>{t('Einordnung')}</PageTitle>
      <Hint>{t('Neue Dokumente bekommen Korrespondent, Dokumenttyp und Tags in dieser Reihenfolge: Vorgaben der Quelle, Regeln der Einträge, KI (wenn eingeschaltet), Gelerntes („Automatisch“). Gelernt wird aus geprüften Dokumenten: Bestätigen und Ändern in der Inbox.')}</Hint>
      <PageTitle>{t('KI beim Import')}</PageTitle>
      {state.aiAllowed ? <>
        <Switch checked={state.ai} label={t('Beim Import mit KI einordnen')} onChange={(_, d) => toggle(d.checked)} />
        <Hint>{t('Dabei gehen der erkannte Text (höchstens 8000 Zeichen), Dateiname, bei Mails Absender und Betreff sowie die Namen der Korrespondenten, Dokumenttypen und Tags an: {service}. Ausgenommen sind Dokumente, die in den Tresor abgelegt werden: Von ihnen geht nichts an {service}.', { service: state.aiService ?? '' })}</Hint>
      </> : <Hint>{t('Keine KI verfügbar: Sie muss in der Verwaltung eingerichtet und für dieses Konto freigegeben sein.')}</Hint>}
      <PageTitle>{t('Texterkennung')}</PageTitle>
      <Hint>{state.ocr
        ? (state.pdfa ? t('Scans und Bilder werden als durchsuchbare PDF/A abgelegt; das Original bleibt im DMS abrufbar.') : t('Scans und Bilder werden als durchsuchbare PDF abgelegt; PDF/A ist auf diesem Server nicht verfügbar (Ghostscript fehlt).'))
        : t('Text in Scans und Bildern wird auf diesem Server nicht erkannt. Das lässt sich in der Verwaltung einrichten.')}</Hint>
    </>
  )
}
