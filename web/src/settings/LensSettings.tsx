import {
  Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions, MessageBarBody,
  Option, ProgressBar, SearchBox, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowClockwise20Regular, ArrowDown20Regular, ArrowDownload20Regular, ArrowUp20Regular, ArrowUpload20Regular, Delete20Regular, Edit20Regular,
  Grid20Regular, Image20Regular, ImageSparkle20Regular, Rename20Regular, Save20Regular, Sparkle20Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
// [feature:lenslib]
import { api, featureOn } from '../api'
// [feature:lenslib:else]
// import { api } from '../api'
// [/feature:lenslib]
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { PickPhotoDialog } from '../files/PickPhotoDialog'
import { askConfirm, askText } from '../shell/context'
import { FilterSelect } from '../shell/FilterSelect'
import { RibbonActions, RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { ExplorerTable, pickCol, type ExplorerCol } from '../sdk'
import { Hint, PageTabs, Section, formWidth } from './parts'
import type { ActivityItem } from '../shell/Activity'
import { LensProfilePick, LensProfilesTab } from './LensLibrary' // [feature:lenslib]

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0, ...formWidth },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  steps: { margin: 0, paddingLeft: '20px', display: 'flex', flexDirection: 'column', gap: '4px', maxWidth: '760px' },
  form: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gap: '8px 12px' },
  wide: { gridColumn: '1 / -1' },
  pair: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  shot: { flex: '1 1 320px', minWidth: 0, maxWidth: '560px', display: 'flex', flexDirection: 'column', gap: '4px' },
  img: { width: '100%', display: 'block', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3 },
  board: { position: 'fixed', inset: 0, zIndex: 100000, backgroundColor: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', cursor: 'pointer' },
  boardImg: { width: '100%', height: '100%', objectFit: 'contain' },
})

/** Objektivprofil in der Liste (Server: Gallery/LensProfiles.cs). */
interface Profile { id: string; name: string; lens: string | null; camera: string | null; source: string | null; focals: number[]; vignetting: boolean; tca: boolean }
export interface Rule { id: string; camera: string | null; lens: string | null; focalMin: number | null; focalMax: number | null; profile: string
  /** Regel vom Admin (gilt nach den eigenen) */
  fromAdmin?: boolean // [feature:lenslib]
}
interface State { profiles: Profile[]; rules: Rule[]; admin: boolean; engine: boolean; autoNew: boolean
  /** Eigene Profile: Regeln vom Admin, Namen der Profile in Regeln, Zahl aller Profile */
  adminRules?: Rule[]; names?: Record<string, string | null>; count?: number | null // [feature:lenslib]
}
type Tab = 'profiles' | 'rules' | 'calibrate'
/** Aufnahmedaten der Bibliothek, zusammengefasst (GET /api/gallery/lens/library). */
export interface LibRow { camera: string | null; lens: string | null; focal: number | null; n: number }
/** Betroffene Fotos je Regel (GET /api/gallery/lens/apply). */
interface ApplyInfo { rules: { id: string; todo: number; done: number; off: number }[]; todo: number; running: boolean; engine: boolean }

/** Wie der Server (LensRule.Matches): Groß-/Kleinschreibung und mehrfache Leerzeichen zählen nicht. */
const squeeze = (v: string | null | undefined) => (v ?? '').trim().replace(/\s+/g, ' ').toLowerCase()
const contains = (value: string | null, part: string | null | undefined) => !squeeze(part) || squeeze(value).includes(squeeze(part))
const ruleHits = (r: { camera: string | null; lens: string | null; focalMin: number | null; focalMax: number | null }, x: LibRow) =>
  contains(x.camera, r.camera) && contains(x.lens, r.lens)
  && (r.focalMin == null || (x.focal != null && x.focal >= r.focalMin - 0.01)) && (r.focalMax == null || (x.focal != null && x.focal <= r.focalMax + 0.01))
/** Ein Foto zum Einmessen: erkannte Ecken (von der Engine) und Brennweite. */
interface Shot { key: string; name: string; focal: number | null; found: boolean; token: string | null; corners: unknown; error?: string; busy?: boolean
  /** Aufnahmedaten: tragen Objektiv und Kamera ins eigene Profil ein */
  camera?: string | null; lens?: string | null // [feature:lenslib]
}
interface Fit { k: number[]; residual_px: number; before_px: number }

const SOURCES: Record<string, () => string> = {
  lcp: () => t('Profil (.lcp)'), lensfun: () => t('lensfun'), json: () => t('dmOffice-Datei'), calibrated: () => t('Eingemessen'),
}
export const mm = (f: number) => (Math.round(f * 10) / 10).toLocaleString() + ' mm'
const px = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 2 }) + ' px'

/**
 * Einstellungen → Galerie → Objektivprofile (Kennung lenscorr): Profile (Verzeichnung, Vignettierung, Farbsäume je Brennweite),
 * Regeln (Kamera/Objektiv/Brennweite → Profil) und Einmessen mit einem Schachbrett am Bildschirm. Server: Gallery/LensProfiles.cs,
 * Rechnen: photo-engine/engine/lens.py.
 */
export function LensSettings() {
  const s = useStyles()
  const { notify, fail } = useApp()
  const [st, setSt] = useState<State | null>(null)
  const [tab, setTab] = useState<Tab>('profiles')
  const load = () => api.get<State>('/api/gallery/lens/').then(setSt).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  // Profile
  const [query, setQuery] = useState('')
  const [active, setActive] = useState<string | null>(null)
  const file = useRef<HTMLInputElement>(null)
  const profiles = st?.profiles ?? []
  const shownProfiles = profiles.filter(p => !query.trim() || [p.name, p.lens, p.camera].some(x => x?.toLowerCase().includes(query.trim().toLowerCase())))
  const current = profiles.find(p => p.id === active) ?? null
  const items = useMemo(() => profiles.map(p => ({ id: p.id, label: p.name })).sort((a, b) => a.label.localeCompare(b.label)), [profiles])
  // [feature:lenslib]
  // Eigene Profile: jede Person hat eigene Profile und Regeln, dazu die vom Admin
  const own = featureOn('lenslib')
  const nameOf = (id: string) => profiles.find(p => p.id === id)?.name ?? st?.names?.[id] ?? t('(gelöscht)')
  const libCount = own ? st?.count ?? 0 : 0
  // [feature:lenslib:else]
  // const nameOf = (id: string) => profiles.find(p => p.id === id)?.name ?? t('(gelöscht)')
  // const libCount = 0
  // const own = false
  // [/feature:lenslib]

  const newProfile = async () => {
    const name = await askText(t('Neues Objektivprofil'), '', t('Name'), t('Anlegen'))
    if (!name?.trim()) return null
    try { const r = await api.post<{ id: string }>('/api/gallery/lens/profiles', { name }); await load(); setActive(r.id); return r.id } catch (e) { fail(e); return null }
  }
  const importFiles = async (list: FileList | null) => {
    if (!list) return
    for (const f of Array.from(list)) {
      try {
        const r = await api.putBlob<{ ids: string[]; names: string[] }>('/api/gallery/lens/import', f)
        notify(r.names.length === 1 ? t('Objektivprofil „{name}“ eingelesen', { name: r.names[0] }) : t('{n} Objektivprofile eingelesen', { n: r.names.length }))
        if (r.ids.length) setActive(r.ids[0])
      } catch (e) { fail(e) }
    }
    if (file.current) file.current.value = ''
    load()
  }
  const exportProfile = (id: string | null) => {
    const a = document.createElement('a')
    a.href = '/api/gallery/lens/export' + (id ? '?id=' + encodeURIComponent(id) : '')
    a.download = ''
    document.body.appendChild(a); a.click(); a.remove()
  }
  const rename = async () => {
    if (!current) return
    const name = await askText(t('Objektivprofil umbenennen'), current.name, t('Name'), t('Umbenennen'))
    if (!name?.trim() || name === current.name) return
    try { await api.put('/api/gallery/lens/profiles/' + current.id, { name }); load() } catch (e) { fail(e) }
  }
  const remove = async () => {
    if (!current) return
    const used = (st?.rules ?? []).filter(r => r.profile === current.id).length
    if (!await askConfirm(t('„{name}“ löschen?', { name: current.name }),
      used ? t('Die {n} Regeln mit diesem Profil werden mit gelöscht. Bearbeitete Fotos mit diesem Profil zeigen danach keine Objektivkorrektur mehr.', { n: used })
        : t('Bearbeitete Fotos mit diesem Profil zeigen danach keine Objektivkorrektur mehr.'), t('Löschen'))) return
    try { await api.del('/api/gallery/lens/profiles/' + current.id); setActive(null); load() } catch (e) { fail(e) }
  }
  const profileCols: ExplorerCol<Profile>[] = [
    pickCol<Profile, string>({ id: p => p.id, selected: active, onChange: setActive }),
    { key: 'name', label: () => t('Name'), width: 0, sort: p => p.name.toLowerCase(), render: p => p.name },
    { key: 'lens', label: () => t('Objektiv'), width: 180, sort: p => (p.lens ?? '').toLowerCase(), render: p => p.lens },
    { key: 'focals', label: () => t('Brennweiten'), width: 120, sort: p => p.focals[0] ?? 0,
      render: p => p.focals.length === 0 ? <span className={s.muted}>{t('noch keine')}</span> : p.focals.length > 4 ? `${mm(p.focals[0])} … ${mm(p.focals[p.focals.length - 1])} (${p.focals.length})` : p.focals.map(mm).join(', ') },
    { key: 'extra', label: () => t('Außerdem'), width: 130, render: p => [p.vignetting && t('Vignettierung'), p.tca && t('Farbsäume')].filter(Boolean).join(', ') },
    { key: 'source', label: () => t('Herkunft'), width: 110, sort: p => p.source ?? '', render: p => p.source ? (SOURCES[p.source]?.() ?? p.source) : '' },
  ]

  // Regeln: die erste passende gilt
  const [rule, setRule] = useState<Rule | null>(null)
  const [activeRule, setActiveRule] = useState<string | null>(null)
  const rules = st?.rules ?? []
  // [feature:lenslib]
  // Eigene Regeln zuerst, dann die vom Admin (ändern nur Admins); Verschieben innerhalb der eigenen oder der vom Admin
  const adminRules = own ? (st?.adminRules ?? []).map(r => ({ ...r, fromAdmin: true })) : []
  const shownRules: Rule[] = [...rules, ...adminRules]
  const pickedRule = shownRules.find(r => r.id === activeRule) ?? null
  const pickedAdmin = !!pickedRule?.fromAdmin
  const groupRules = pickedAdmin ? adminRules : rules
  // [feature:lenslib:else]
  // const shownRules: Rule[] = rules
  // const pickedRule = rules.find(r => r.id === activeRule) ?? null
  // const pickedAdmin = false
  // const groupRules = rules
  // [/feature:lenslib]
  const saveRules = async (next: Rule[], fromAdmin = false) => {
    // [feature:lenslib]
    try {
      const plain = next.map(({ fromAdmin: _, ...r }) => r)
      const r = await api.put<{ rules: Rule[]; names?: Record<string, string | null> }>(fromAdmin ? '/api/gallery/lens/admin/rules' : '/api/gallery/lens/rules', { rules: plain })
      setSt(x => x && { ...x, ...(fromAdmin ? { adminRules: r.rules } : { rules: r.rules }), names: { ...x.names, ...r.names } })
      return true
    }
    // [feature:lenslib:else]
    // try { const r = await api.put<{ rules: Rule[] }>('/api/gallery/lens/rules', { rules: next }); setSt(x => x && { ...x, rules: r.rules }); return true }
    // [/feature:lenslib]
    catch (e) { fail(e); return false }
  }
  // Aufnahmedaten der Bibliothek für Vorschläge und Trefferzahl im Regeldialog (einmal je Besuch der Seite)
  const [lib, setLib] = useState<LibRow[] | null>(null)
  const editRule = (r: Rule | null) => {
    if (!lib) api.get<{ items: LibRow[] }>('/api/gallery/lens/library').then(x => setLib(x.items)).catch(() => setLib([]))
    setRule(r ?? { id: '', camera: '', lens: '', focalMin: null, focalMax: null, profile: current?.id ?? profiles[0]?.id ?? '' })
  }
  // Auf Fotos anwenden: alle Regeln oder nur die eben gespeicherte
  const [applying, setApplying] = useState<{ rule: string | null } | null>(null)
  const [ask, setAsk] = useState<{ rule: string; todo: number } | null>(null)
  const askAfterSave = async (id: string) => {
    try { const r = await api.get<ApplyInfo>('/api/gallery/lens/apply?rule=' + encodeURIComponent(id)); setAsk(r.todo > 0 ? { rule: id, todo: r.todo } : null) }
    catch { setAsk(null) }
  }
  const setAuto = async (on: boolean) => {
    try { await api.put('/api/gallery/lens/auto', { on }); setSt(x => x && { ...x, autoNew: on }) } catch (e) { fail(e) }
  }
  const moveRule = (d: number) => {
    const i = groupRules.findIndex(r => r.id === activeRule), j = i + d
    if (i < 0 || j < 0 || j >= groupRules.length) return
    const next = [...groupRules];[next[i], next[j]] = [next[j], next[i]]
    saveRules(next, pickedAdmin)
  }
  const deleteRule = async () => {
    if (!activeRule || !await askConfirm(t('Regel löschen?'), t('Fotos, die diese Regel bisher bekommen haben, behalten ihre gespeicherte Bearbeitung.'), t('Löschen'))) return
    if (await saveRules(groupRules.filter(r => r.id !== activeRule), pickedAdmin)) setActiveRule(null)
  }
  const focalText = (r: Rule) => r.focalMin == null && r.focalMax == null ? t('alle') : r.focalMin === r.focalMax ? mm(r.focalMin!)
    : `${r.focalMin != null ? mm(r.focalMin) : '…'} – ${r.focalMax != null ? mm(r.focalMax) : '…'}`
  const ruleCols: ExplorerCol<Rule>[] = [
    pickCol<Rule, string>({ id: r => r.id, selected: activeRule, onChange: setActiveRule }),
    { key: 'order', label: () => t('Reihenfolge'), width: 100, sort: r => shownRules.indexOf(r), render: r => shownRules.indexOf(r) + 1 },
    { key: 'camera', label: () => t('Kamera enthält'), width: 150, sort: r => r.camera ?? '', render: r => r.camera || <span className={s.muted}>{t('jede')}</span> },
    { key: 'lens', label: () => t('Objektiv enthält'), width: 0, sort: r => r.lens ?? '', render: r => r.lens || <span className={s.muted}>{t('jedes')}</span> },
    { key: 'focal', label: () => t('Brennweite'), width: 120, sort: r => r.focalMin ?? 0, render: focalText },
    { key: 'profile', label: () => t('Profil'), width: 180, sort: r => nameOf(r.profile).toLowerCase(), render: r => nameOf(r.profile) },
    ...(own ? [{ key: 'origin', label: () => t('Herkunft'), width: 120, sort: (r: Rule) => r.fromAdmin ? 1 : 0, render: (r: Rule) => r.fromAdmin ? t('Vom Admin') : t('Eigene') }] : []), // [feature:lenslib]
  ]

  // Einmessen
  const [board, setBoard] = useState(false)
  const boardRef = useRef<HTMLDivElement>(null)
  const [shots, setShots] = useState<Shot[]>([])
  const [picking, setPicking] = useState(false)
  const [target, setTarget] = useState<string>('')
  const [fits, setFits] = useState<Record<string, Fit | { error: string } | 'busy'>>({})
  const [shotActive, setShotActive] = useState<string | null>(null)
  const upload = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (!board) return
    boardRef.current?.requestFullscreen?.().catch(() => { })
    const off = () => { if (!document.fullscreenElement) setBoard(false) }
    document.addEventListener('fullscreenchange', off)
    return () => document.removeEventListener('fullscreenchange', off)
  }, [board])
  const closeBoard = () => { if (document.fullscreenElement) document.exitFullscreen().catch(() => { }); setBoard(false) }

  type Detect = { found: boolean; token: string | null; focal: number | null; corners: unknown; camera?: string | null; lens?: string | null }
  const detect = async (key: string, name: string, run: () => Promise<Detect>) => {
    setShots(l => [...l.filter(x => x.key !== key), { key, name, focal: null, found: false, token: null, corners: null, busy: true }])
    try {
      const r = await run()
      setShots(l => l.map(x => x.key === key ? { ...x, busy: false, found: r.found, token: r.token, focal: r.focal, corners: r.corners,
        camera: r.camera, lens: r.lens, // [feature:lenslib]
        error: r.found ? undefined : t('Kein Schachbrett erkannt') } : x))
    } catch (e) { setShots(l => l.map(x => x.key === key ? { ...x, busy: false, error: e instanceof Error ? e.message : String(e) } : x)) }
  }
  const fromGallery = (list: { path: string; name: string }[]) => {
    for (const i of list) detect('g:' + i.path, i.name, () => api.post<Detect>('/api/gallery/lens/calibrate/detect?path=' + encodeURIComponent(i.path), {}))
  }
  const fromDevice = (list: FileList | null) => {
    if (!list) return
    for (const f of Array.from(list)) detect('u:' + f.name + ':' + f.size, f.name, () => api.putBlob<Detect>('/api/gallery/lens/calibrate/detect?name=' + encodeURIComponent(f.name), f))
    if (upload.current) upload.current.value = ''
  }
  // Je Brennweite (auf 0,1 mm) eine Gruppe; jede Änderung rechnet die Gruppe neu
  const groups = useMemo(() => {
    const g = new Map<string, Shot[]>()
    for (const x of shots) if (x.found && x.focal) { const k = String(Math.round(x.focal * 10) / 10); g.set(k, [...(g.get(k) ?? []), x]) }
    return [...g.entries()].sort((a, b) => Number(a[0]) - Number(b[0]))
  }, [shots])
  const groupKey = groups.map(([f, l]) => f + ':' + l.map(x => x.key).join(',')).join('|')
  useEffect(() => {
    let stop = false
    for (const [f, l] of groups) {
      setFits(x => ({ ...x, [f]: 'busy' }))
      api.post<Fit>('/api/gallery/lens/calibrate/fit', { images: l.map(x => x.corners), terms: 2 })
        .then(r => { if (!stop) setFits(x => ({ ...x, [f]: r })) })
        .catch(e => { if (!stop) setFits(x => ({ ...x, [f]: { error: e instanceof Error ? e.message : String(e) } })) })
    }
    setFits(x => Object.fromEntries(Object.entries(x).filter(([k]) => groups.some(([f]) => f === k))))
    return () => { stop = true }
  }, [groupKey]) // eslint-disable-line react-hooks/exhaustive-deps
  const setFocal = (key: string, v: string) => {
    const n = Number(v.replace(',', '.'))
    setShots(l => l.map(x => x.key === key ? { ...x, focal: Number.isFinite(n) && n > 0 ? n : null } : x))
  }
  const ready = groups.filter(([f]) => { const r = fits[f]; return r && r !== 'busy' && !('error' in r) })
  const saveFits = async () => {
    let id = target
    if (!id) { const made = await newProfile(); if (!made) return; id = made; setTarget(made) }
    try {
      // [feature:lenslib]
      // Objektiv und Kamera aus den Aufnahmedaten: das eigene Profil passt dann auch automatisch
      for (const [f, l] of ready) await api.post('/api/gallery/lens/calibrate/save', { profile: id, focal: Number(f), k: (fits[f] as Fit).k,
        lens: l.find(x => x.lens)?.lens ?? null, camera: l.find(x => x.camera)?.camera ?? null })
      // [feature:lenslib:else]
      // for (const [f] of ready) await api.post('/api/gallery/lens/calibrate/save', { profile: id, focal: Number(f), k: (fits[f] as Fit).k })
      // [/feature:lenslib]
      notify(t('Verzeichnung für {n} Brennweiten in „{name}“ übernommen', { n: ready.length, name: nameOf(id) }))
      load()
    } catch (e) { fail(e) }
  }
  // Vorher/Nachher des gewählten Fotos
  const [pair, setPair] = useState<{ before: string; after: string | null } | null>(null)
  const chosen = shots.find(x => x.key === shotActive && x.found && x.token)
  const chosenFit = chosen?.focal ? fits[String(Math.round(chosen.focal * 10) / 10)] : undefined
  const chosenK = chosenFit && chosenFit !== 'busy' && !('error' in chosenFit) ? chosenFit.k : null
  useEffect(() => {
    setPair(null)
    if (!chosen?.token) return
    const urls: string[] = []
    const get = async (k: number[]) => {
      const r = await fetch('/api/gallery/lens/calibrate/preview', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: chosen.token, k, focal: chosen.focal }) })
      if (!r.ok) return null
      const u = URL.createObjectURL(await r.blob()); urls.push(u); return u
    }
    let stop = false
    ;(async () => { const before = await get([]); const after = chosenK ? await get(chosenK) : null; if (!stop && before) setPair({ before, after }) })()
    return () => { stop = true; urls.forEach(URL.revokeObjectURL) }
  }, [chosen?.token, chosen?.focal, chosenK?.join(',')]) // eslint-disable-line react-hooks/exhaustive-deps

  const shotCols: ExplorerCol<Shot>[] = [
    pickCol<Shot, string>({ id: x => x.key, selected: shotActive, onChange: setShotActive }),
    { key: 'name', label: () => t('Foto'), width: 220, sort: x => x.name.toLowerCase(), render: x => x.name },
    { key: 'focal', label: () => t('Brennweite'), width: 140, sort: x => x.focal ?? 0,
      render: x => x.found ? <Input size="small" style={{ width: 110, minWidth: 110 }} input={{ style: { minWidth: 0 } }} defaultValue={x.focal ? String(x.focal) : ''} placeholder={t('mm')} contentAfter={<span style={{ whiteSpace: 'nowrap' }}>mm</span>}
        onClick={e => e.stopPropagation()} onBlur={e => setFocal(x.key, e.target.value)} onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }} /> : null },
    { key: 'state', label: () => t('Ergebnis'), width: 0, render: x => x.busy ? <Spinner size="tiny" /> : x.error ? <span className={s.muted}>{x.error}</span>
      : x.focal ? t('Schachbrett erkannt') : t('Schachbrett erkannt - bitte Brennweite eintragen') },
  ]

  if (!st) return null
  const admin = st.admin
  // [feature:lenslib]
  // Eigene Profile: die Liste bringt ihr Menüband selbst mit; Regeln vom Admin ändern nur Admins
  const showRibbon = !own || tab !== 'profiles'
  const mayOwn = own || admin
  const mayRule = !!pickedRule && (own ? !pickedRule.fromAdmin || admin : admin)
  // [feature:lenslib:else]
  // const showRibbon = true
  // const mayOwn = admin
  // const mayRule = admin && !!pickedRule
  // [/feature:lenslib]

  const ribbon = tab === 'profiles' ? <>
    <RibbonButton icon={<Add20Regular />} label={t('Neues Profil')} disabled={!admin} onClick={() => { newProfile() }} />
    <RibbonButton icon={<ArrowUpload20Regular />} label={t('Importieren')} disabled={!admin} onClick={() => file.current?.click()} />
    <RibbonButton icon={<ArrowDownload20Regular />} label={current ? t('Exportieren') : t('Alle exportieren')} disabled={profiles.length === 0} onClick={() => exportProfile(current?.id ?? null)} />
    <RibbonDivider />
    <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!admin || !current} onClick={rename} />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!admin || !current} onClick={remove} />
    <RibbonDivider />
    <RibbonButton icon={<ImageSparkle20Regular />} label={t('Auf Fotos anwenden')} disabled={shownRules.length + libCount === 0} onClick={() => setApplying({ rule: null })} />
    <RibbonDivider />
    <RibbonButton icon={<ArrowClockwise20Regular />} label={t('Aktualisieren')} onClick={load} />
  </> : tab === 'rules' ? <>
    <RibbonButton icon={<Add20Regular />} label={t('Neue Regel')} disabled={!mayOwn || profiles.length + libCount === 0} onClick={() => editRule(null)} />
    <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!mayRule} onClick={() => editRule(pickedRule)} />
    <RibbonButton icon={<ArrowUp20Regular />} label={t('Nach oben')} disabled={!mayRule || groupRules[0]?.id === activeRule} onClick={() => moveRule(-1)} />
    <RibbonButton icon={<ArrowDown20Regular />} label={t('Nach unten')} disabled={!mayRule || groupRules[groupRules.length - 1]?.id === activeRule} onClick={() => moveRule(1)} />
    <RibbonDivider />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!mayRule} onClick={deleteRule} />
    <RibbonDivider />
    <RibbonButton icon={<ImageSparkle20Regular />} label={t('Auf Fotos anwenden')} disabled={shownRules.length + libCount === 0} onClick={() => setApplying({ rule: null })} />
    <RibbonButton icon={<Sparkle20Regular />} label={t('Neue Fotos gleich korrigieren')} pressed={st.autoNew} onClick={() => setAuto(!st.autoNew)} />
  </> : <>
    <RibbonButton icon={<Grid20Regular />} label={t('Schachbrett zeigen')} onClick={() => setBoard(true)} />
    <RibbonDivider />
    <RibbonButton icon={<Image20Regular />} label={t('Aus der Galerie')} disabled={!mayOwn || !st.engine} onClick={() => setPicking(true)} />
    <RibbonButton icon={<ArrowUpload20Regular />} label={t('Hochladen')} disabled={!mayOwn || !st.engine} onClick={() => upload.current?.click()} />
    <RibbonDivider />
    <RibbonButton icon={<Save20Regular />} label={t('In Profil übernehmen')} primary disabled={!mayOwn || ready.length === 0} onClick={saveFits} />
    <RibbonButton icon={<Delete20Regular />} label={t('Fotos entfernen')} disabled={shots.length === 0} onClick={() => { setShots([]); setShotActive(null) }} />
  </>

  return (
    <div className={s.page}>
      <PageTabs tabs={[['profiles', t('Profile')], ['rules', t('Regeln')], ['calibrate', t('Einmessen')]]} value={tab} onChange={v => setTab(v as typeof tab)} />
      {showRibbon && <RibbonActions>{ribbon}</RibbonActions>}
      <input ref={file} type="file" accept=".lcp,.xml,.json" multiple hidden onChange={e => importFiles(e.target.files)} />
      <input ref={upload} type="file" accept="image/*,.nef,.nrw,.arw,.cr2,.cr3,.raf,.dng,.orf,.rw2,.pef" multiple hidden onChange={e => fromDevice(e.target.files)} />
      {!mayOwn && <Hint>{t('Objektivprofile und Regeln legt ein Admin an. Im Foto-Editor lässt sich die Objektivkorrektur je Foto ein- und ausschalten.')}</Hint>}
      {!st.engine && <Hint>{t('Die Objektivkorrektur rechnet die Photo-Engine; sie ist gerade nicht eingeschaltet.')}</Hint>}

      {/* [feature:lenslib] */}
      {own && tab === 'profiles' && <LensProfilesTab scope="person" engine={st.engine} onChanged={load} onCreate={newProfile} onApply={() => setApplying({ rule: null })} />}
      {/* [/feature:lenslib] */}
      {!own && tab === 'profiles' && <Section title={t('Profile')} hint={t('Ein Profil beschreibt Verzeichnung, Vignettierung und Farbsäume eines Objektivs je Brennweite. Einlesen lassen sich eigene Exporte, .lcp-Profile und lensfun-Dateien (.xml).')} last>
        <div className={s.bar}><SearchBox size="small" placeholder={t('Profile durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ width: 280 }} /></div>
        <ExplorerTable id="lens-profiles" cols={profileCols} rows={shownProfiles} rowKey={p => p.id} initialSort={{ col: 'name', desc: false }}
          onRowClick={p => setActive(p.id)} active={active} />
        {profiles.length === 0 && <Text className={s.muted}>{t('Noch keine Objektivprofile. Lies ein Profil ein oder miss dein Objektiv unter „Einmessen“ ein.')}</Text>}
      </Section>}

      {tab === 'rules' && <Section title={t('Regeln')} hint={t('Passt eine Regel zu Kamera, Objektiv und Brennweite eines Fotos, bekommt das Foto die Objektivkorrektur mit diesem Profil. Die erste passende Regel gilt. „Auf Fotos anwenden“ trägt sie bei vorhandenen Fotos ein; neue Fotos bekommen sie beim Einlesen, solange „Neue Fotos gleich korrigieren“ an ist. Diese Einstellung gilt für deine eigenen Fotos.')} last>
        {own && <Text>{t('Deine eigenen Regeln gelten vor den Regeln vom Admin. Passt keine Regel, wählt dmOffice das Profil automatisch nach dem Objektiv – deine eigenen Profile zuerst.')}</Text>} {/* [feature:lenslib] */}
        <ExplorerTable id="lens-rules" cols={ruleCols} rows={shownRules} rowKey={r => r.id} initialSort={{ col: 'order', desc: false }}
          onRowOpen={r => { setActiveRule(r.id); if (own ? !r.fromAdmin || admin : admin) editRule(r) }} // [feature:rowdblopen]
          onRowClick={r => setActiveRule(r.id)} active={activeRule} />
        {shownRules.length === 0 &&<Text className={s.muted}>{profiles.length + libCount ? t('Noch keine Regeln.') : t('Für Regeln braucht es zuerst ein Objektivprofil.')}</Text>}
        {ask && <MessageBar intent="info">
          <MessageBarBody>{ask.todo === 1 ? t('Regel gespeichert. Sie passt zu 1 Foto ohne Objektivkorrektur.') : t('Regel gespeichert. Sie passt zu {n} Fotos ohne Objektivkorrektur.', { n: ask.todo.toLocaleString() })}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" appearance="primary" onClick={() => { setApplying({ rule: ask.rule }); setAsk(null) }}>{t('Auf diese Fotos anwenden')}</Button>
            <Button size="small" onClick={() => setAsk(null)}>{t('Später')}</Button>
          </MessageBarActions>
        </MessageBar>}
      </Section>}

      {tab === 'calibrate' && <>
        <Section title={t('So geht es')}>
          <ol className={s.steps}>
            <li>{t('„Schachbrett zeigen“ füllt diesen Bildschirm mit einem Schachbrett. Ein Klick oder Esc beendet die Anzeige.')}</li>
            <li>{t('Je Brennweite ein bis drei Fotos aufnehmen: das Schachbrett möglichst bildfüllend, gerade von vorn, scharf, ohne Spiegelungen. Leicht versetzte Aufnahmen verbessern das Ergebnis.')}</li>
            <li>{t('Die Fotos aus der Galerie wählen oder hochladen. Die Verzeichnung je Brennweite wird sofort berechnet; „In Profil übernehmen“ speichert sie im gewählten Profil.')}</li>
          </ol>
        </Section>
        <Section title={t('Fotos')}>
          <ExplorerTable id="lens-shots" cols={shotCols} rows={shots} rowKey={x => x.key} onRowClick={x => setShotActive(x.key)} active={shotActive} />
          {shots.length === 0 && <Text className={s.muted}>{t('Noch keine Fotos.')}</Text>}
        </Section>
        <Section title={t('Ergebnis je Brennweite')} hint={t('Abweichung: wie weit die Ecken des Schachbretts im Mittel von geraden Linien abweichen - vorher und nach der Korrektur.')}>
          {groups.length === 0 ? <Text className={s.muted}>{t('Noch kein Ergebnis.')}</Text> : groups.map(([f, l]) => {
            const r = fits[f]
            return <Text key={f} block>
              <b>{mm(Number(f))}</b>{' · '}{t('{n} Fotos', { n: l.length })}{' · '}
              {r === 'busy' || !r ? <Spinner size="tiny" style={{ display: 'inline-flex' }} /> : 'error' in r ? r.error
                : t('Abweichung vorher {a}, nachher {b}', { a: px(r.before_px), b: px(r.residual_px) })}
            </Text>
          })}
          <div className={s.bar}>
            <Text>{t('Profil')}</Text>
            <FilterSelect size="small" items={items} value={target} onChange={setTarget} none={t('Neues Profil anlegen')} style={{ minWidth: 260 }} />
          </div>
        </Section>
        {chosen && <Section title={t('Vorher und nachher')} hint={t('Ein Foto in der Liste wählen.')} last>
          <div className={s.pair}>
            <div className={s.shot}><Text weight="semibold">{t('Vorher')}</Text>{pair ? <img className={s.img} src={pair.before} alt={t('Vorher')} /> : <Spinner size="tiny" />}</div>
            <div className={s.shot}><Text weight="semibold">{t('Nachher')}</Text>{pair?.after ? <img className={s.img} src={pair.after} alt={t('Nachher')} />
              : chosenK ? <Spinner size="tiny" /> : <Text className={s.muted}>{t('Noch kein Ergebnis.')}</Text>}</div>
          </div>
        </Section>}
      </>}

      {board && <div ref={boardRef} className={s.board} onClick={closeBoard} role="button" aria-label={t('Schachbrett schließen')}>
        <img className={s.boardImg} src="/api/gallery/lens/checkerboard.svg" alt={t('Schachbrett')} />
      </div>}
      {picking && <PickPhotoDialog title={t('Fotos mit Schachbrett wählen')} onClose={() => setPicking(false)} onPickMany={list => { setPicking(false); fromGallery(list) }} />}
      {rule && <RuleDialog rule={rule} items={items} lib={lib} nameOf={nameOf} onClose={() => setRule(null)}
        onSave={async r => {
          const group = rule.id ? groupRules : rules
          const saved = r.id ? r : { ...r, id: Date.now().toString(36) + Math.random().toString(36).slice(2, 8) }
          const next = r.id ? group.map(x => x.id === r.id ? saved : x) : [...group, saved]
          if (await saveRules(next, !!rule.id && pickedAdmin)) { setRule(null); setActiveRule(saved.id); askAfterSave(saved.id) }
        }} />}
      {applying && <ApplyDialog rule={applying.rule} rules={shownRules} nameOf={nameOf} focalText={focalText} onClose={() => setApplying(null)} />}
    </div>
  )
}

/** Vorschläge aus der Bibliothek: Name mit Anzahl der Fotos und Brennweiten, nach Anzahl sortiert, gefiltert nach dem Eingetippten. */
interface Suggestion { name: string; n: number; focals: Set<number> }
function suggest(rows: LibRow[], pick: (x: LibRow) => string | null, typed: string | null): Suggestion[] {
  const m = new Map<string, Suggestion>()
  for (const x of rows) {
    const name = pick(x)?.trim()
    if (!name) continue
    const k = squeeze(name)
    const e = m.get(k) ?? { name, n: 0, focals: new Set<number>() }
    e.n += x.n
    if (x.focal != null) e.focals.add(x.focal)
    m.set(k, e)
  }
  const q = squeeze(typed)
  return [...m.values()].filter(e => !q || squeeze(e.name).includes(q)).sort((a, b) => b.n - a.n || a.name.localeCompare(b.name))
}
const focalSpan = (f: Set<number>) => {
  const l = [...f].sort((a, b) => a - b)
  return l.length === 0 ? '' : l.length === 1 ? mm(l[0]) : `${mm(l[0])} – ${mm(l[l.length - 1])}`
}

/** Feld mit Vorschlägen beim Tippen (Eintippen und Filtern; freier Text bleibt möglich). */
function SuggestField({ label, hint, value, onChange, options, loading }: {
  label: string; hint: string; value: string; onChange: (v: string) => void; options: Suggestion[]; loading: boolean
}) {
  const shown = options.slice(0, 12)
  return <Field label={label} hint={hint}>
    <Combobox freeform value={value} selectedOptions={[]} onChange={e => onChange((e.target as HTMLInputElement).value)}
      onOptionSelect={(_, d) => { if (d.optionValue) onChange(d.optionValue) }} listbox={{ style: { maxHeight: 320 } }}>
      {loading && <Option disabled value="" text="">{t('Aufnahmedaten werden gelesen …')}</Option>}
      {shown.map(o => <Option key={o.name} value={o.name} text={o.name}>
        <span style={{ display: 'flex', flexDirection: 'column' }}>
          <span>{o.name}</span>
          <span style={{ fontSize: 12, color: tokens.colorNeutralForeground3 }}>
            {[t('{n} Fotos', { n: o.n.toLocaleString() }), focalSpan(o.focals)].filter(Boolean).join(' · ')}
          </span>
        </span>
      </Option>)}
      {!loading && shown.length === 0 && <Option disabled value="" text="">{t('Keine passenden Aufnahmedaten in deinen Fotos')}</Option>}
      {options.length > shown.length && <Option disabled value="" text="">{t('… und {n} weitere – weitertippen', { n: options.length - shown.length })}</Option>}
    </Combobox>
  </Field>
}

/** Regel anlegen oder ändern: Kamera enthält, Objektiv enthält (Groß-/Kleinschreibung und Leerzeichen egal), Brennweite von/bis, Profil. Vorschläge aus der Bibliothek. */
// [feature:lenslib]
export function RuleDialog({ rule, items, lib, nameOf, onClose, onSave, scope }: {
// [feature:lenslib:else]
// export function RuleDialog({ rule, items, lib, nameOf, onClose, onSave }: {
// [/feature:lenslib]
  rule: Rule; items: { id: string; label: string }[]; lib: LibRow[] | null; nameOf: (id: string) => string; onClose: () => void; onSave: (r: Rule) => void
  /** Regeln vom Admin (Verwaltung): nur Profile vom Admin */
  scope?: 'admin' // [feature:lenslib]
}) {
  const s = useStyles()
  const [r, setR] = useState(rule)
  const num = (v: string) => { const n = Number(v.replace(',', '.')); return v.trim() && Number.isFinite(n) && n > 0 ? n : null }
  const [from, setFrom] = useState(rule.focalMin != null ? String(rule.focalMin) : '')
  const [to, setTo] = useState(rule.focalMax != null ? String(rule.focalMax) : '')
  const ok = !!r.profile && !!(r.camera?.trim() || r.lens?.trim())
  const cameras = useMemo(() => suggest(lib ?? [], x => x.camera, r.camera), [lib, r.camera])
  const lenses = useMemo(() => suggest((lib ?? []).filter(x => contains(x.camera, r.camera)), x => x.lens, r.lens), [lib, r.camera, r.lens])
  // Brennweiten der Fotos mit dieser Kamera und diesem Objektiv: Hilfe für den Bereich
  const focals = useMemo(() => {
    const m = new Map<number, number>()
    for (const x of lib ?? []) if (x.focal != null && contains(x.camera, r.camera) && contains(x.lens, r.lens)) m.set(x.focal, (m.get(x.focal) ?? 0) + x.n)
    return [...m.entries()].sort((a, b) => a[0] - b[0])
  }, [lib, r.camera, r.lens])
  const named = !!(r.camera?.trim() || r.lens?.trim())
  const hits = named && lib ? lib.filter(x => ruleHits({ ...r, focalMin: num(from), focalMax: num(to) }, x)).reduce((a, x) => a + x.n, 0) : null
  const focalHint = focals.length === 0 ? '' : focals.length <= 8
    ? t('In deinen Fotos: {list}', { list: focals.map(([f, n]) => `${mm(f)} (${n.toLocaleString()})`).join(', ') })
    : t('In deinen Fotos: {from} bis {to} ({n} Brennweiten)', { from: mm(focals[0][0]), to: mm(focals[focals.length - 1][0]), n: focals.length })
  return <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
    <DialogSurface style={{ maxWidth: 620 }}><DialogBody>
      <DialogTitle>{rule.id ? t('Regel bearbeiten') : t('Neue Regel')}</DialogTitle>
      <DialogContent>
        <div className={s.form}>
          <SuggestField label={t('Kamera enthält')} hint={t('zum Beispiel „Z f“')} value={r.camera ?? ''} onChange={v => setR(x => ({ ...x, camera: v }))} options={cameras} loading={lib == null} />
          <SuggestField label={t('Objektiv enthält')} hint={t('zum Beispiel „NOKTON 28mm“')} value={r.lens ?? ''} onChange={v => setR(x => ({ ...x, lens: v }))} options={lenses} loading={lib == null} />
          <Field label={t('Brennweite von')}><Input value={from} onChange={(_, d) => setFrom(d.value)} contentAfter="mm" /></Field>
          <Field label={t('Brennweite bis')}><Input value={to} onChange={(_, d) => setTo(d.value)} contentAfter="mm" /></Field>
          {focalHint && <Text className={s.wide} size={200}>{focalHint}</Text>}
          {/* [feature:lenslib] */}
          <Field className={s.wide} label={t('Profil')} hint={scope ? t('Profile vom Admin; tippen zum Suchen.') : t('Deine eigenen Profile und die vom Admin; tippen zum Suchen.')}>
            <LensProfilePick value={r.profile} hint={r.lens} valueName={r.profile === rule.profile ? nameOf(r.profile) : null} scope={scope}
              onChange={id => setR(x => ({ ...x, profile: id }))} />
          </Field>
          {/* [feature:lenslib:else] */}
          {/* <Field className={s.wide} label={t('Profil')}><FilterSelect items={items} value={r.profile} onChange={id => setR(x => ({ ...x, profile: id }))} /></Field> */}
          {/* [/feature:lenslib] */}
          <Text className={s.wide} size={200}>{t('Ohne Brennweite gilt die Regel für alle Brennweiten. Kamera oder Objektiv muss angegeben sein. Groß- und Kleinschreibung und doppelte Leerzeichen spielen keine Rolle.')}</Text>
          <Text className={s.wide} weight="semibold" aria-live="polite">
            {!named ? '' : hits == null ? t('Fotos werden gezählt …') : hits === 1 ? t('Passt zu einem deiner Fotos') : hits > 0 ? t('Passt zu {n} deiner Fotos', { n: hits.toLocaleString() }) : t('Passt derzeit zu keinem deiner Fotos')}
          </Text>
        </div>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('Abbrechen')}</Button>
        <Button appearance="primary" disabled={!ok} onClick={() => onSave({ ...r, camera: r.camera?.trim() || null, lens: r.lens?.trim() || null, focalMin: num(from), focalMax: num(to) })}>{t('Speichern')}</Button>
      </DialogActions>
    </DialogBody></DialogSurface>
  </Dialog>
}

/** Fotos, auf die Regeln passen: Zahlen je Regel, Start, dann Fortschritt (der Lauf geht im Hintergrund weiter). */
function ApplyDialog({ rule, rules, nameOf, focalText, onClose }: {
  rule: string | null; rules: Rule[]; nameOf: (id: string) => string; focalText: (r: Rule) => string; onClose: () => void
}) {
  const { fail } = useApp()
  const [info, setInfo] = useState<ApplyInfo | null>(null)
  const [phase, setPhase] = useState<'ask' | 'run' | 'done'>('ask')
  const [job, setJob] = useState<ActivityItem | null>(null)
  useEffect(() => {
    api.get<ApplyInfo>('/api/gallery/lens/apply' + (rule ? '?rule=' + encodeURIComponent(rule) : ''))
      .then(r => { setInfo(r); if (r.running) setPhase('run') }).catch(e => { fail(e); onClose() })
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Fortschritt aus der Aktivität (dort steht er auch, wenn das Fenster zu ist)
  useEffect(() => {
    if (phase !== 'run') return
    let stop = false
    let timer = 0
    const poll = async () => {
      try {
        const a = await api.get<{ items: ActivityItem[] }>('/api/activity')
        if (stop) return
        const it = a.items.find(x => x.id === 'lens-apply') ?? null
        setJob(it)
        if (it && it.state !== 'running' && it.state !== 'waiting') { setPhase('done'); return }
      } catch { /* nächster Versuch */ }
      if (!stop) timer = window.setTimeout(poll, 1500)
    }
    timer = window.setTimeout(poll, 700)
    return () => { stop = true; clearTimeout(timer) }
  }, [phase])
  const start = async () => {
    try {
      const r = await api.post<{ started: boolean; count: number }>('/api/gallery/lens/apply', { rule, lang })
      setPhase(r.started ? 'run' : 'done')
    } catch (e) { fail(e) }
  }
  const label = (r: Rule) => [r.camera, r.lens].filter(Boolean).join(' · ') + ' · ' + focalText(r) + ' → ' + nameOf(r.profile)
  const n = (v: number) => v.toLocaleString()
  const cell = { padding: '4px 8px', textAlign: 'right' as const, whiteSpace: 'nowrap' as const }
  return <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
    <DialogSurface style={{ maxWidth: 640 }}><DialogBody>
      <DialogTitle>{t('Objektivkorrektur auf Fotos anwenden')}</DialogTitle>
      <DialogContent>
        {!info ? <Spinner size="small" label={t('Fotos werden gezählt …')} /> : phase === 'ask' ? <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {info.rules.every(x => x.todo + x.done + x.off === 0) ? <Text>{t('Die Regeln passen derzeit zu keinem deiner Fotos.')}</Text> : <>
            <Text>{info.todo > 0 ? (info.todo === 1 ? t('1 Foto bekommt die Objektivkorrektur.') : t('{n} Fotos bekommen die Objektivkorrektur.', { n: n(info.todo) })) : t('Alle passenden Fotos haben die Objektivkorrektur schon.')}</Text>
            <table style={{ borderCollapse: 'collapse', fontSize: 13 }}>
              <thead><tr style={{ color: tokens.colorNeutralForeground3 }}>
                <th style={{ padding: '2px 8px 2px 0', fontWeight: 400, textAlign: 'left' }}>{t('Regel')}</th>
                <th style={{ ...cell, fontWeight: 400 }}>{t('Bekommen sie')}</th>
                <th style={{ ...cell, fontWeight: 400 }}>{t('Haben sie schon')}</th>
                <th style={{ ...cell, fontWeight: 400 }}>{t('Bewusst aus')}</th>
              </tr></thead>
              <tbody>{info.rules.map(x => {
                const r = rules.find(y => y.id === x.id)
                return <tr key={x.id} style={{ borderTop: `1px solid ${tokens.colorNeutralStroke2}` }}>
                  <td style={{ padding: '4px 8px 4px 0' }}>{r ? label(r)
                    : x.id === 'library' ? t('Ohne passende Regel: automatisch nach dem Objektiv') // [feature:lenslib]
                    : x.id}</td>
                  <td style={{ ...cell, fontWeight: 600 }}>{n(x.todo)}</td>
                  <td style={cell}>{n(x.done)}</td>
                  <td style={cell}>{n(x.off)}</td>
                </tr>
              })}</tbody>
            </table>
            <Text size={200}>{t('Zuschnitt, Belichtung und andere Bearbeitungen bleiben erhalten. Fotos mit eigenen Werten für die Objektivkorrektur oder bewusst ausgeschalteter Korrektur bleiben, wie sie sind. Die Vorschaubilder entstehen danach neu, das Original bleibt unverändert.')}</Text>
            {!info.engine && <Text size={200}>{t('Sichtbar wird die Korrektur, sobald die Photo-Engine eingeschaltet ist.')}</Text>}
          </>}
        </div> : <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {phase === 'run' && <>
            <ProgressBar value={job?.total ? Math.min(1, (job.done ?? 0) / job.total) : undefined} />
            <Text>{job?.total ? t('{a} von {b} Fotos', { a: n(job.done ?? 0), b: n(job.total) }) : t('Beginnt …')}</Text>
            <Text size={200}>{t('Läuft im Hintergrund weiter, auch wenn du dieses Fenster schließt oder die Seite verlässt. Den Stand zeigt die Aktivität.')}</Text>
          </>}
          {phase === 'done' && <Text>{job?.error ?? job?.detail ?? t('Alle passenden Fotos haben die Objektivkorrektur schon.')}</Text>}
        </div>}
      </DialogContent>
      <DialogActions>
        {phase === 'ask' && <>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" disabled={!info || info.todo === 0} onClick={start}>{t('Anwenden')}</Button>
        </>}
        {phase !== 'ask' && <Button appearance={phase === 'done' ? 'primary' : 'secondary'} onClick={onClose}>{t('Schließen')}</Button>}
      </DialogActions>
    </DialogBody></DialogSurface>
  </Dialog>
}
