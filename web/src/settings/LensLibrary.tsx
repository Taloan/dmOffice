import {
  Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Option, ProgressBar, SearchBox, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowClockwise20Regular, ArrowDownload20Regular, ArrowUpload20Regular, Delete20Regular, DocumentArrowUp20Regular, Filter20Regular,
  ImageSparkle20Regular, Rename20Regular,
} from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { askConfirm, askText } from '../shell/context'
import { FilterSelect, type FilterItem } from '../shell/FilterSelect'
import { RibbonActions, RibbonButton, RibbonDivider, RibbonViewActions } from '../shell/Ribbon'
import { ExplorerTable, pickCol, selectCol, type ExplorerCol } from '../sdk'
import { Hint, Section } from './parts'
import type { ActivityItem } from '../shell/Activity'

/**
 * Objektivprofile einer Person (Kennung lenslib): eigene (eingelesen oder eingemessen) und alle vom Admin (serverweit oder aus einer
 * ZIP-Datei) in einer Liste. Server: Gallery/LensProfiles.cs (GET /list, /item), Gallery/LensOwn.cs, Gallery/LensLibrary.cs.
 */
export interface LensProfileRow {
  id: string; name: string; maker: string | null; camera: string | null; lens: string | null; lensId: string | null
  raw: boolean | null; crop: number | null; focalMin: number | null; focalMax: number | null; apertureMin: number | null; apertureMax: number | null
  /** zip, calibrated, lcp, lensfun, json */
  source: string | null; file: string | null; vignetting: boolean; tca: boolean
  /** own, calibrated, admin */
  origin: string
}
interface ProfileList { count: number; total: number; items: LensProfileRow[]; makers: { name: string; n: number }[]; running: boolean }

const useStyles = makeStyles({
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  sub: { fontSize: '12px', color: tokens.colorNeutralForeground3 },
})

const num = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 1 })
const range = (a: number | null, b: number | null, f: (v: number) => string) => a == null ? '' : b == null || Math.abs(a - b) < 0.05 ? f(a) : `${f(a)} – ${f(b)}`
/** Art des Profils: RAW, JPEG oder beides. */
export const kindOf = (raw: boolean | null) => raw === true ? 'RAW' : raw === false ? 'JPEG' : ''
/** Name mit Kamera und Art (für Auswahllisten). */
export const libLabel = (e: { name: string; camera?: string | null; model?: string | null; raw: boolean | null }) =>
  [e.name, e.camera || e.model, kindOf(e.raw)].filter(Boolean).join(' · ')
/** Herkunft für Personen: eigenes, eingemessenes, vom Admin. */
export const originText = (o: string | null | undefined) => o === 'own' ? t('Eigenes') : o === 'calibrated' ? t('Eingemessen') : o === 'admin' ? t('Vom Admin') : ''
/** Herkunft in der Verwaltung: aus einer ZIP-Datei, eingemessen oder einzeln eingelesen. */
const sourceText = (s: string | null) => s === 'zip' ? t('ZIP-Datei') : s === 'calibrated' ? t('Eingemessen') : t('Einzeldatei')
const mine = (p: LensProfileRow) => p.origin !== 'admin'

const CHUNK = 8 * 1024 * 1024

/** Ein Lauf des Einlesens, aus der Aktivität (dort steht er auch, wenn der Dialog zu ist). */
function useLibJob(on: boolean, onDone: () => void) {
  const [job, setJob] = useState<ActivityItem | null>(null)
  useEffect(() => {
    if (!on) return
    let stop = false
    let timer = 0
    const poll = async () => {
      try {
        const a = await api.get<{ items: ActivityItem[] }>('/api/activity')
        if (stop) return
        const it = a.items.find(x => x.id === 'lens-library') ?? null
        setJob(it)
        if (it && it.state !== 'running' && it.state !== 'waiting') { onDone(); return }
      } catch { /* nächster Versuch */ }
      if (!stop) timer = window.setTimeout(poll, 1000)
    }
    timer = window.setTimeout(poll, 500)
    return () => { stop = true; clearTimeout(timer) }
  }, [on]) // eslint-disable-line react-hooks/exhaustive-deps
  return job
}

/** ZIP hochladen (in Stücken, mit Fortschritt), dann Einlesen im Hintergrund mit „x von y Dateien“. */
function ImportDialog({ file, running, onClose, onChanged }: { file: File | null; running: boolean; onClose: () => void; onChanged: () => void }) {
  const { fail } = useApp()
  const [phase, setPhase] = useState<'upload' | 'read' | 'done' | 'error'>(running ? 'read' : 'upload')
  const [sent, setSent] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [started] = useState(() => Date.now())
  const [ended, setEnded] = useState<number | null>(null)
  const [, tick] = useState(0)
  const stop = useRef(false)
  const job = useLibJob(phase === 'read', () => { setPhase('done'); setEnded(Date.now()); onChanged() })
  useEffect(() => {
    if (ended != null || phase === 'error') return
    const i = window.setInterval(() => tick(x => x + 1), 1000)
    return () => clearInterval(i)
  }, [ended, phase])
  useEffect(() => {
    if (!file || running) return
    stop.current = false
    ;(async () => {
      try {
        const up = await api.post<{ id: string; chunk: number }>('/api/gallery/lens/lib/upload', { size: file.size })
        const size = Math.min(CHUNK, up.chunk)
        for (let at = 0; at < file.size; at += size) {
          if (stop.current) return
          await api.putBlob(`/api/gallery/lens/lib/upload/${up.id}?offset=${at}`, file.slice(at, at + size, 'application/octet-stream'))
          setSent(Math.min(file.size, at + size))
        }
        await api.post(`/api/gallery/lens/lib/upload/${up.id}/done`, { lang })
        setPhase('read')
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e)); setPhase('error'); fail(e)
      }
    })()
    return () => { stop.current = true }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const mb = (v: number) => (v / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 }) + ' MB'
  const secs = Math.round(((ended ?? Date.now()) - started) / 1000)
  const time = secs >= 60 ? `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')} min` : `${secs} s`
  const failedText = job?.detail && phase === 'read' ? job.detail : null
  return <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
    <DialogSurface style={{ maxWidth: 560 }}><DialogBody>
      <DialogTitle>{t('Objektivprofile aus ZIP-Datei aufnehmen')}</DialogTitle>
      <DialogContent>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {phase === 'upload' && file && <>
            <Text>{t('Hochladen: {a} von {b}', { a: mb(sent), b: mb(file.size) })}</Text>
            <ProgressBar value={file.size ? sent / file.size : 0} />
          </>}
          {phase === 'read' && <>
            <Text>{job?.total ? t('Einlesen: {a} von {b} Dateien', { a: (job.done ?? 0).toLocaleString(), b: job.total.toLocaleString() }) : t('Einlesen beginnt …')}</Text>
            <ProgressBar value={job?.total ? Math.min(1, (job.done ?? 0) / job.total) : 0} />
            {failedText && <Text size={200}>{failedText}</Text>}
            <Text size={200}>{t('Läuft im Hintergrund weiter, auch wenn du dieses Fenster schließt. Den Stand zeigt die Aktivität.')}</Text>
          </>}
          {phase === 'done' && <Text>{job?.error ?? job?.detail ?? t('Fertig.')}</Text>}
          {phase === 'done' && job?.report && job.report.length > 0 && <details>
            <summary>{t('Nicht lesbare Dateien ({n})', { n: job.report.length })}</summary>
            <div style={{ maxHeight: 200, overflow: 'auto', fontSize: 12 }}>{job.report.map(r => <div key={r}>{r}</div>)}</div>
          </details>}
          {phase === 'error' && <Text>{error}</Text>}
          {phase !== 'error' && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{ended != null ? t('Dauer: {time}', { time }) : t('Dauer bisher: {time}', { time })}</Text>}
        </div>
      </DialogContent>
      <DialogActions>
        {phase === 'upload' && <Button onClick={() => { stop.current = true; onClose() }}>{t('Abbrechen')}</Button>}
        {phase !== 'upload' && <Button appearance={phase === 'read' ? 'secondary' : 'primary'} onClick={onClose}>{t('Schließen')}</Button>}
      </DialogActions>
    </DialogBody></DialogSurface>
  </Dialog>
}

/**
 * Liste der Objektivprofile mit Suche, Hersteller, Herkunft und Art.
 * scope „person“ (Einstellungen): eigene und alle vom Admin; einlesen aus Einzeldateien, umbenennen und löschen bei eigenen.
 * scope „admin“ (Verwaltung): nur die vom Admin; ZIP-Datei hochladen, Einzeldateien einlesen, mehrere löschen.
 */
export function LensProfilesTab({ scope, engine, onChanged, onApply, onCreate }: {
  scope: 'person' | 'admin'; engine: boolean; onChanged?: () => void
  /** Person: „Auf Fotos anwenden“ */
  onApply?: () => void
  /** Person: neues leeres Profil zum Einmessen */
  onCreate?: () => Promise<string | null>
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const adminScope = scope === 'admin'
  const [data, setData] = useState<ProfileList | null>(null)
  const [q, setQ] = useState('')
  const [maker, setMaker] = useState('')
  const [origin, setOrigin] = useState('')
  const [kind, setKind] = useState<'' | 'raw' | 'jpeg'>('')
  const [active, setActive] = useState<string | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [importing, setImporting] = useState<{ file: File | null } | null>(null)
  const zip = useRef<HTMLInputElement>(null)
  const files = useRef<HTMLInputElement>(null)
  const load = () => api.get<ProfileList>(`/api/gallery/lens/list?q=${encodeURIComponent(q)}&maker=${encodeURIComponent(maker)}&kind=${kind}&origin=${origin}&take=2000`
    + (adminScope ? '&scope=admin' : '')).then(setData).catch(fail)
  useEffect(() => { const h = window.setTimeout(load, 200); return () => clearTimeout(h) }, [q, maker, kind, origin]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (adminScope && data?.running && !importing) setImporting({ file: null }) }, [data?.running]) // eslint-disable-line react-hooks/exhaustive-deps
  const changed = () => { load(); onChanged?.() }

  const rows = data?.items ?? []
  const current = rows.find(p => p.id === active) ?? null
  const chosen = rows.filter(p => picked.includes(p.id))

  const importFiles = async (list: FileList | null) => {
    if (!list) return
    for (const f of Array.from(list)) {
      try {
        const r = await api.putBlob<{ ids: string[]; names: string[] }>(adminScope ? '/api/gallery/lens/admin/import' : '/api/gallery/lens/import', f)
        notify(r.names.length === 1 ? t('Objektivprofil „{name}“ eingelesen', { name: r.names[0] }) : t('{n} Objektivprofile eingelesen', { n: r.names.length }))
        if (r.ids.length && !adminScope) setActive(r.ids[0])
      } catch (e) { fail(e) }
    }
    if (files.current) files.current.value = ''
    changed()
  }
  const exportProfile = (id: string | null) => {
    const a = document.createElement('a')
    a.href = '/api/gallery/lens/export' + (id ? '?id=' + encodeURIComponent(id) : '')
    a.download = ''
    document.body.appendChild(a); a.click(); a.remove()
  }
  const create = async () => { const id = await onCreate?.(); if (id) { setActive(id); load() } }
  const rename = async () => {
    if (!current || !mine(current)) return
    const name = await askText(t('Objektivprofil umbenennen'), current.name, t('Name'), t('Umbenennen'))
    if (!name?.trim() || name === current.name) return
    try { await api.put('/api/gallery/lens/profiles/' + encodeURIComponent(current.id), { name }); changed() } catch (e) { fail(e) }
  }
  const removeOwn = async () => {
    if (!current || !mine(current)) return
    if (!await askConfirm(t('„{name}“ löschen?', { name: current.name }),
      t('Deine Regeln mit diesem Profil werden mit gelöscht. Bearbeitete Fotos mit diesem Profil zeigen danach keine Objektivkorrektur mehr.'), t('Löschen'))) return
    try {
      const r = await api.del<{ rules: number }>('/api/gallery/lens/profiles/' + encodeURIComponent(current.id))
      if (r?.rules) notify(t('{n} Profile und {r} Regeln entfernt', { n: 1, r: r.rules }))
      setActive(null); changed()
    } catch (e) { fail(e) }
  }
  const removeAdmin = async () => {
    if (chosen.length === 0) return
    if (!await askConfirm(chosen.length === 1 ? t('„{name}“ löschen?', { name: chosen[0].name }) : t('{n} Profile löschen?', { n: chosen.length.toLocaleString() }),
      t('Regeln vom Admin mit diesen Profilen werden mit gelöscht. Fotos mit diesen Profilen zeigen danach keine Objektivkorrektur mehr.'), t('Löschen'))) return
    try {
      const r = await api.post<{ rules: number }>('/api/gallery/lens/admin/delete', { ids: chosen.map(p => p.id) })
      notify(r.rules ? t('{n} Profile und {r} Regeln entfernt', { n: chosen.length.toLocaleString(), r: r.rules }) : t('{n} Profile entfernt', { n: chosen.length.toLocaleString() }))
      setPicked([]); changed()
    } catch (e) { fail(e) }
  }

  const makers: FilterItem[] = (data?.makers ?? []).map(m => ({ id: m.name, label: m.name || t('(ohne Hersteller)'), extra: ` · ${m.n.toLocaleString()}` }))
  const origins: FilterItem[] = [{ id: 'own', label: t('Eigenes') }, { id: 'calibrated', label: t('Eingemessen') }, { id: 'admin', label: t('Vom Admin') }]
  const cols: ExplorerCol<LensProfileRow>[] = [
    adminScope ? selectCol<LensProfileRow, string>({ rows, id: p => p.id, selected: picked, onChange: setPicked })
      : pickCol<LensProfileRow, string>({ id: p => p.id, selected: active, onChange: setActive }),
    { key: 'name', label: () => t('Objektiv'), width: 0, sort: e => e.name.toLowerCase(), render: e => e.name },
    adminScope
      ? { key: 'origin', label: () => t('Herkunft'), width: 120, sort: e => sourceText(e.source), render: e => sourceText(e.source) }
      : { key: 'origin', label: () => t('Herkunft'), width: 120, sort: e => originText(e.origin), render: e => originText(e.origin) },
    { key: 'maker', label: () => t('Hersteller'), width: 110, sort: e => (e.maker ?? '').toLowerCase(), render: e => e.maker },
    { key: 'camera', label: () => t('Kamera'), width: 170, sort: e => (e.camera ?? '').toLowerCase(), render: e => e.camera || <span className={s.muted}>{t('alle')}</span> },
    { key: 'kind', label: () => t('Art'), width: 70, sort: e => kindOf(e.raw), render: e => kindOf(e.raw) },
    { key: 'focal', label: () => t('Brennweite'), width: 110, sort: e => e.focalMin ?? 0, render: e => range(e.focalMin, e.focalMax, v => num(v) + ' mm') },
    { key: 'aperture', label: () => t('Blende'), width: 100, sort: e => e.apertureMin ?? 0, render: e => range(e.apertureMin, e.apertureMax, v => 'f/' + num(v)) },
    { key: 'lensId', label: () => t('Objektiv-ID'), width: 90, sort: e => e.lensId ?? '', render: e => e.lensId },
    { key: 'crop', label: () => t('Formatfaktor'), width: 90, sort: e => e.crop ?? 0, render: e => e.crop ? num(e.crop) : '' },
    { key: 'extra', label: () => t('Außerdem'), width: 140, render: e => [e.vignetting && t('Vignettierung'), e.tca && t('Farbsäume')].filter(Boolean).join(', ') },
    { key: 'source', label: () => t('Datei'), width: 260, sort: e => (e.file ?? '').toLowerCase(), render: e => <span className={s.sub}>{e.file}</span> },
  ]
  const filtered = !!(q.trim() || maker || kind || origin)
  const count = data && <Text className={s.muted}>{filtered || data.total > data.items.length
    ? t('{a} von {b} Profilen', { a: data.total.toLocaleString(), b: data.count.toLocaleString() }) + (data.total > data.items.length ? ' · ' + t('die ersten {n} gezeigt – weitertippen', { n: data.items.length.toLocaleString() }) : '')
    : t('{n} Profile', { n: data.count.toLocaleString() })}</Text>
  const table = <>
    <div className={s.bar}>
      <SearchBox size="small" placeholder={t('Objektiv, Kamera oder Datei suchen')} value={q} onChange={(_, d) => setQ(d.value)} style={{ width: 300 }} />
      <FilterSelect size="small" items={makers} value={maker} onChange={setMaker} none={t('Alle Hersteller')} style={{ minWidth: 200 }} aria-label={t('Hersteller')} />
      {!adminScope && <FilterSelect size="small" items={origins} value={origin} onChange={setOrigin} none={t('Jede Herkunft')} style={{ minWidth: 160 }} aria-label={t('Herkunft')} />}
      {count}
    </div>
    <ExplorerTable id={adminScope ? 'lens-admin-profiles' : 'lens-profiles-all'} cols={cols} rows={rows} rowKey={e => e.id} max={2000} initialSort={{ col: 'name', desc: false }}
      onRowClick={e => setActive(e.id)} active={active} />
  </>
  return <>
    <RibbonActions>
      {adminScope ? <>
        <RibbonButton icon={<ArrowUpload20Regular />} label={t('ZIP hochladen')} primary disabled={!engine || data?.running} onClick={() => zip.current?.click()} />
        <RibbonButton icon={<DocumentArrowUp20Regular />} label={t('Dateien einlesen')} disabled={!engine} onClick={() => files.current?.click()} />
        <RibbonDivider />
        <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={chosen.length === 0 || data?.running} onClick={removeAdmin} />
      </> : <>
        <RibbonButton icon={<Add20Regular />} label={t('Neues Profil')} onClick={create} />
        <RibbonButton icon={<DocumentArrowUp20Regular />} label={t('Dateien einlesen')} disabled={!engine} onClick={() => files.current?.click()} />
        <RibbonButton icon={<ArrowDownload20Regular />} label={current ? t('Exportieren') : t('Eigene exportieren')} disabled={!current && !rows.some(mine)} onClick={() => exportProfile(current?.id ?? null)} />
        <RibbonDivider />
        <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!current || !mine(current)} onClick={rename} />
        <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!current || !mine(current)} onClick={removeOwn} />
        {onApply && <><RibbonDivider /><RibbonButton icon={<ImageSparkle20Regular />} label={t('Auf Fotos anwenden')} disabled={!data?.count} onClick={onApply} /></>}
        <RibbonDivider />
        <RibbonButton icon={<ArrowClockwise20Regular />} label={t('Aktualisieren')} onClick={load} />
      </>}
    </RibbonActions>
    <RibbonViewActions>
      {!adminScope && <>
        <RibbonButton icon={<Filter20Regular />} label={t('Nur eigene')} pressed={origin === 'own'} onClick={() => setOrigin(o => o === 'own' ? '' : 'own')} />
        <RibbonButton icon={<Filter20Regular />} label={t('Nur vom Admin')} pressed={origin === 'admin'} onClick={() => setOrigin(o => o === 'admin' ? '' : 'admin')} />
        <RibbonDivider />
      </>}
      <RibbonButton icon={<Filter20Regular />} label={t('Nur RAW')} pressed={kind === 'raw'} onClick={() => setKind(k => k === 'raw' ? '' : 'raw')} />
      <RibbonButton icon={<Filter20Regular />} label={t('Nur JPEG')} pressed={kind === 'jpeg'} onClick={() => setKind(k => k === 'jpeg' ? '' : 'jpeg')} />
      <RibbonButton icon={<Delete20Regular />} label={t('Filter zurücksetzen')} disabled={!filtered} onClick={() => { setQ(''); setMaker(''); setKind(''); setOrigin('') }} />
    </RibbonViewActions>
    <input ref={zip} type="file" accept=".zip,application/zip" hidden onChange={e => { const f = e.target.files?.[0]; if (f) setImporting({ file: f }); e.target.value = '' }} />
    <input ref={files} type="file" accept=".lcp,.xml,.json" multiple hidden onChange={e => importFiles(e.target.files)} />
    {!engine && <Hint>{t('Zum Einlesen wird die Photo-Engine gebraucht; sie ist gerade nicht eingeschaltet.')}</Hint>}
    {adminScope ? <>
      <Text>{t('Diese Profile stehen allen Benutzern zur Verfügung. Eine ZIP-Datei mit Objektivprofilen (.lcp, lensfun-XML, JSON) bis 2 GB nimmt viele Profile auf einmal auf; einzelne Dateien lassen sich ebenso einlesen.')}</Text>
      {table}
      {data && data.count === 0 && <Text className={s.muted}>{t('Noch keine Profile vom Admin. Mit „ZIP hochladen“ oder „Dateien einlesen“ kommen sie dazu.')}</Text>}
    </> : <Section title={t('Profile')} hint={t('Ein Profil beschreibt Verzeichnung, Vignettierung und Farbsäume eines Objektivs. Hier stehen deine eigenen Profile und alle vom Admin. Einlesen lassen sich eigene Exporte, .lcp-Profile und lensfun-Dateien (.xml). Passt keine Regel, bekommt ein Foto automatisch das Profil zum Objektiv aus den Aufnahmedaten – deine eigenen zuerst.')} last>
      {table}
      {data && data.count === 0 && <Text className={s.muted}>{t('Noch keine Objektivprofile. Lies ein Profil ein oder miss dein Objektiv unter „Einmessen“ ein.')}</Text>}
    </Section>}
    {importing && <ImportDialog file={importing.file} running={!importing.file} onClose={() => { setImporting(null); load() }} onChanged={changed} />}
  </>
}

/**
 * Profil wählen mit Eintippen und Filtern: eine Liste aus eigenen Profilen und allen vom Admin (Suche auf dem Server).
 * none: Eintrag „keiner“ (Wert leer).
 */
export function LensProfilePick({ value, onChange, none, size, style, listWidth, valueName, hint, scope, extra }: {
  /** Feste Einträge vor der Liste (etwa „Ohne Profil“) */
  extra?: { id: string; label: string }[]
  value: string | null | undefined; onChange: (id: string, label: string) => void
  none?: string; size?: 'small' | 'medium'; style?: React.CSSProperties; listWidth?: number
  /** Name des gewählten Profils, wenn schon bekannt */
  valueName?: string | null
  /** Suchtext, solange nichts getippt ist (etwa das Objektiv aus der Regel) */
  hint?: string | null
  /** admin: nur Profile vom Admin (Regeln in der Verwaltung) */
  scope?: 'admin'
}) {
  const [query, setQuery] = useState<string | null>(null)
  const [list, setList] = useState<LensProfileRow[]>([])
  const [total, setTotal] = useState(0)
  const [byHint, setByHint] = useState(false)
  const [names, setNames] = useState<Record<string, string>>({})
  const [open, setOpen] = useState(false)
  const q = (query ?? '').trim()
  useEffect(() => {
    if (!open) return
    const text = q || hint?.trim() || ''
    const get = (x: string) => api.get<ProfileList>(`/api/gallery/lens/list?q=${encodeURIComponent(x)}&take=30` + (scope ? '&scope=' + scope : ''))
    const h = window.setTimeout(async () => {
      try {
        let r = await get(text)
        let hinted = !q && !!text
        // Nichts passend zum Objektiv: alle zeigen
        if (hinted && r.total === 0) { r = await get(''); hinted = false }
        setList(r.items); setTotal(r.total); setByHint(hinted)
      } catch { setList([]) }
    }, 200)
    return () => clearTimeout(h)
  }, [q, open, hint]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!value || valueName || names[value] || extra?.some(x => x.id === value)) return
    api.get<LensProfileRow>('/api/gallery/lens/item?id=' + encodeURIComponent(value)).then(e => setNames(n => ({ ...n, [value]: libLabel(e) }))).catch(() => { })
  }, [value]) // eslint-disable-line react-hooks/exhaustive-deps
  const current = !value ? none ?? '' : valueName ?? extra?.find(x => x.id === value)?.label ?? names[value] ?? list.find(e => e.id === value)?.name ?? ''
  const head = { fontSize: 12, color: tokens.colorNeutralForeground3, padding: '4px 8px' }
  return <Combobox size={size} style={style} placeholder={t('Zum Suchen tippen')} value={query ?? current} selectedOptions={[value ?? '']}
    listbox={{ style: { minWidth: listWidth ?? 320, maxHeight: 380 } }}
    onInput={e => setQuery((e.target as HTMLInputElement).value)}
    onFocus={e => (e.target as HTMLInputElement).select?.()}
    onOptionSelect={(_, d) => {
      if (d.optionValue === undefined || d.optionValue.startsWith('\u0000')) return
      const e = list.find(x => x.id === d.optionValue)
      if (e) setNames(n => ({ ...n, [e.id]: libLabel(e) }))
      setQuery(null); onChange(d.optionValue, e ? libLabel(e) : extra?.find(x => x.id === d.optionValue)?.label ?? '')
    }}
    onOpenChange={(_, d) => { setOpen(d.open); if (!d.open) setQuery(null) }}>
    {none !== undefined && !q && <Option value="" text={none}>{none}</Option>}
    {!q && extra?.map(x => <Option key={x.id} value={x.id} text={x.label}>{x.label}</Option>)}
    {byHint && list.length > 0 && <Option disabled value={'\u0000hint'} text=""><span style={head}>{t('Passend zu „{q}“', { q: hint ?? '' })}</span></Option>}
    {list.map(e => <Option key={e.id} value={e.id} text={libLabel(e)}>
      <span style={{ display: 'flex', flexDirection: 'column' }}>
        <span>{e.name}</span>
        <span style={{ fontSize: 12, color: tokens.colorNeutralForeground3 }}>{[!scope && originText(e.origin), e.camera || t('alle Kameras'), kindOf(e.raw), range(e.focalMin, e.focalMax, v => num(v) + ' mm')].filter(Boolean).join(' · ')}</span>
      </span>
    </Option>)}
    {total > list.length && <Option disabled value={'\u0000more'} text="">{t('… und {n} weitere – weitertippen', { n: (total - list.length).toLocaleString() })}</Option>}
    {list.length === 0 && <Option disabled value={'\u0000'} text="">{t('Nichts gefunden')}</Option>}
  </Combobox>
}
