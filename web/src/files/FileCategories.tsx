import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Input, MessageBar, MessageBarBody, Option, Spinner, Switch, Text,
  Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add16Regular, Checkmark16Regular, ChevronDown16Regular, ChevronRight16Regular, Delete16Regular, Dismiss16Regular, Edit16Regular, Settings16Regular,
  Sparkle16Regular, Tag20Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'

/** Kategorie der Dateiablage (docs/datei-kategorien-konzept.md): verschachtelt, lernt, ordnet neue Dateien automatisch ein. */
export interface FileCat {
  id: string; parent: string | null; name: string; color: string | null; learn: boolean; auto: boolean
  /** Direkt zugeordnete Dateien, davon noch nicht bestätigte (automatisch eingeordnet) */
  count: number; unconfirmed: number
}
export interface FileTag { tag: string; count: number }
export interface Labels { cats: { id: string; auto: boolean }[]; tags: string[] }

export const PALETTE = ['#D13438', '#CA5010', '#C19C00', '#13A10E', '#00B7C3', '#0F6CBD', '#8764B8', '#E3008C', '#8A8886', '#4F6BED']

export const catsApi = {
  labelsOf: (paths: string[]) => api.post<{ items: Record<string, Labels> }>('/api/files/labels/of', { paths }).then(r => r.items),
  setLabels: (body: { paths: string[]; add?: string[]; remove?: string[]; addTags?: string[]; removeTags?: string[] }) => api.post('/api/files/labels', body),
  create: (body: { name: string; parent: string | null; color?: string | null; learn?: boolean; auto?: boolean }) => api.post<FileCat>('/api/files/categories', body),
  update: (id: string, body: Partial<{ name: string; parent: string; color: string; learn: boolean; auto: boolean }>) => api.put<FileCat>('/api/files/categories/' + encodeURIComponent(id), body),
  remove: (id: string) => api.del('/api/files/categories/' + encodeURIComponent(id)),
}

// Einmal laden für alle Stellen (Leiste, Dialoge, Details); nach Änderungen neu
let cache: { cats: FileCat[]; tags: FileTag[] } | null = null
/** Der Baum ist geladen (leer heißt dann wirklich: keine Kategorien). */
export const catsLoaded = () => cache != null
let loading: Promise<void> | null = null
const listeners = new Set<(c: { cats: FileCat[]; tags: FileTag[] }) => void>()
export function reloadCats(): Promise<void> {
  loading = api.get<{ cats: FileCat[]; tags: FileTag[] }>('/api/files/categories')
    .then(r => { cache = r; listeners.forEach(l => l(r)) })
    .catch(() => { /* Ablage abgeschaltet oder offline: bleibt leer */ })
    .finally(() => { loading = null })
  return loading
}
export function useFileCats() {
  const [state, setState] = useState(cache ?? { cats: [], tags: [] })
  useEffect(() => {
    listeners.add(setState)
    if (!cache && !loading) reloadCats()
    return () => { listeners.delete(setState) }
  }, [])
  return state
}

/** Die Kategorie und alle darunter. */
export function below(cats: FileCat[], id: string): Set<string> {
  const set = new Set([id])
  for (let more = true; more;) {
    more = false
    for (const c of cats) if (c.parent && set.has(c.parent) && !set.has(c.id)) { set.add(c.id); more = true }
  }
  return set
}
/** Oberkategorien, die nächste zuerst. */
export function above(cats: FileCat[], id: string): string[] {
  const by = new Map(cats.map(c => [c.id, c]))
  const list: string[] = []
  for (let c = by.get(id); c?.parent && by.has(c.parent) && !list.includes(c.parent) && c.parent !== id; c = by.get(c.parent)) list.push(c.parent)
  return list
}
export const fullName = (cats: FileCat[], id: string) => {
  const by = new Map(cats.map(c => [c.id, c]))
  return [...above(cats, id).reverse(), id].map(x => by.get(x)?.name ?? '?').join(' › ')
}
const kidsOf = (cats: FileCat[], parent: string | null) => cats.filter(c => c.parent === parent)
/** Baum in Lesereihenfolge mit Tiefe. */
function flat(cats: FileCat[], open?: Record<string, boolean>): { c: FileCat; depth: number }[] {
  const out: { c: FileCat; depth: number }[] = []
  const walk = (parent: string | null, depth: number) => {
    for (const c of kidsOf(cats, parent)) { out.push({ c, depth }); if (!open || open[c.id]) walk(c.id, depth + 1) }
  }
  walk(null, 0)
  return out
}
/** Anzahl samt Unterkategorien (eine Datei in zwei Unterkategorien zählt doppelt - nur als Anhaltspunkt). */
const total = (cats: FileCat[], id: string, key: 'count' | 'unconfirmed') => [...below(cats, id)].reduce((n, x) => n + (cats.find(c => c.id === x)?.[key] ?? 0), 0)

export function Dot({ color, size = 10 }: { color: string | null; size?: number }) {
  return <span style={{ display: 'inline-block', width: size, height: size, borderRadius: size / 2, flexShrink: 0, background: color ?? tokens.colorNeutralForeground3 }} />
}

const useStyles = makeStyles({
  head: { display: 'flex', alignItems: 'center', padding: '2px 8px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  count: { marginLeft: 'auto', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100, paddingLeft: '6px' },
  pending: { marginLeft: 'auto', color: tokens.colorBrandForeground1, fontSize: tokens.fontSizeBase100, paddingLeft: '6px', fontWeight: tokens.fontWeightSemibold },
  tree: { display: 'flex', flexDirection: 'column', gap: '2px', maxHeight: '50vh', overflowY: 'auto', marginTop: '8px' },
  row: { display: 'flex', alignItems: 'center', gap: '6px', minHeight: '30px' },
  chips: { display: 'flex', flexWrap: 'wrap', gap: '4px', alignItems: 'center' },
  chip: {
    display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '1px 8px', borderRadius: '10px', fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground3, maxWidth: '100%',
  },
  chipAuto: { border: `1px dashed ${tokens.colorBrandStroke1}` },
  muted: { color: tokens.colorNeutralForeground3 },
})

/** Klassen der Navigationsleiste der Dateiablage, damit die Kategorien dort aussehen wie die Ordner. */
export interface NavClasses { nav: string; active: string; target: string; twisty: string; navName: string }

/** Linke Leiste: Kategorien als Baum (virtuelle Ordner) und Tags. Dateien lassen sich auf eine Kategorie ziehen. */
export function CategoryNav({ cls, current, currentTag, onOpen, onOpenTag, onDrop, accepts, onManage }: {
  cls: NavClasses; current: string | null; currentTag: string | null; onOpen: (id: string) => void; onOpenTag: (tag: string) => void
  onDrop: (e: React.DragEvent, id: string) => void; accepts: (e: React.DragEvent) => boolean; onManage: () => void
}) {
  const s = useStyles()
  const { cats, tags } = useFileCats()
  const [open, setOpen] = useState<Record<string, boolean>>(() => { try { return JSON.parse(localStorage.getItem('files.cats.open') ?? '{}') } catch { return {} } })
  // Ganzer Abschnitt eingeklappt (Kopf anklicken); bleibt im Browser gemerkt
  const [folded, setFolded] = useState(() => { try { return localStorage.getItem('files.cats.folded') === '1' } catch { return false } })
  const fold = () => setFolded(v => { try { localStorage.setItem('files.cats.folded', v ? '0' : '1') } catch { /* egal */ } return !v })
  const [showTags, setShowTags] = useState(() => { try { return localStorage.getItem('files.tags.open') === '1' } catch { return false } })
  const [over, setOver] = useState<string | null>(null)
  const toggle = (id: string) => setOpen(o => { const n = { ...o, [id]: !o[id] }; try { localStorage.setItem('files.cats.open', JSON.stringify(n)) } catch { /* egal */ } return n })
  // Den Weg zur geöffneten Kategorie aufklappen
  useEffect(() => { if (current) setOpen(o => { const n = { ...o }; above(cats, current).forEach(p => { n[p] = true }); return n }) }, [current, cats])
  return <>
    <div className={s.head} style={{ cursor: 'pointer' }} onClick={fold} title={folded ? t('Aufklappen') : t('Zuklappen')}>
      {folded ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
      <span style={{ flex: 1, marginLeft: 4 }}>{t('Kategorien')}</span>
      <Button size="small" appearance="subtle" icon={<Settings16Regular />} aria-label={t('Kategorien verwalten')} title={t('Kategorien verwalten')} onClick={e => { e.stopPropagation(); onManage() }} />
    </div>
    {!folded && <>
    {cats.length === 0 && <div className={cls.nav} onClick={onManage}><span className={cls.twisty} /><Add16Regular /><span className={cls.navName}>{t('Kategorien anlegen')}</span></div>}
    {flat(cats, open).map(({ c, depth }) => {
      const kids = kidsOf(cats, c.id).length > 0
      const pending = total(cats, c.id, 'unconfirmed')
      return (
        <div key={c.id} className={mergeClasses(cls.nav, current === c.id && cls.active, over === c.id && cls.target)} style={{ paddingLeft: 8 + depth * 14 }}
          title={fullName(cats, c.id) + (pending ? ' · ' + t('{n} automatisch eingeordnet, noch nicht bestätigt', { n: pending }) : '')}
          onClick={() => onOpen(c.id)}
          onDragOver={e => { if (accepts(e)) { e.preventDefault(); setOver(c.id) } }}
          onDragLeave={() => setOver(o => o === c.id ? null : o)}
          onDrop={e => { e.preventDefault(); e.stopPropagation(); setOver(null); onDrop(e, c.id) }}>
          <span className={cls.twisty} onClick={e => { e.stopPropagation(); if (kids) toggle(c.id) }}>{kids ? open[c.id] ? <ChevronDown16Regular /> : <ChevronRight16Regular /> : null}</span>
          <span style={{ width: 20, display: 'inline-flex', justifyContent: 'center' }}><Dot color={c.color} /></span>
          <span className={cls.navName}>{c.name}</span>
          {pending > 0 ? <span className={s.pending}>{pending}</span> : <span className={s.count}>{total(cats, c.id, 'count') || ''}</span>}
        </div>
      )
    })}
    {tags.length > 0 && <>
      <div className={cls.nav} onClick={() => setShowTags(v => { try { localStorage.setItem('files.tags.open', v ? '0' : '1') } catch { /* egal */ } return !v })}>
        <span className={cls.twisty}>{showTags ? <ChevronDown16Regular /> : <ChevronRight16Regular />}</span><Tag20Regular /><span className={cls.navName}>{t('Tags')}</span>
        <span className={s.count}>{tags.length}</span>
      </div>
      {showTags && tags.map(g => (
        <div key={g.tag} className={mergeClasses(cls.nav, currentTag?.toLowerCase() === g.tag.toLowerCase() && cls.active)} style={{ paddingLeft: 30 }} onClick={() => onOpenTag(g.tag)}>
          <span className={cls.navName}>#{g.tag}</span><span className={s.count}>{g.count}</span>
        </div>
      ))}
    </>}
    </>}
  </>
}

/** Kategorie für Auswahllisten: eingerückt mit Farbpunkt. */
function CatLabel({ c, depth }: { c: FileCat; depth: number }) {
  return <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, paddingLeft: depth * 16 }}><Dot color={c.color} />{c.name}</span>
}

/** Kategorien und Tags für ausgewählte Dateien setzen (Kontextmenü, Menüband). Bei mehreren: halb angehakt = bei einigen. */
export function LabelsDialog({ paths, onClose, onDone }: { paths: string[]; onClose: () => void; onDone?: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const { cats, tags: allTags } = useFileCats()
  const [of, setOf] = useState<Record<string, Labels> | null>(null)
  const [cat, setCat] = useState<Record<string, boolean>>({})
  const [tags, setTags] = useState<Record<string, boolean>>({})
  const [newTag, setNewTag] = useState('')
  const [newCat, setNewCat] = useState<{ parent: string | null; name: string } | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { catsApi.labelsOf(paths).then(setOf).catch(e => { fail(e); onClose() }) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const files = of ? Object.values(of) : []
  const n = files.length
  // Wie viele der Dateien die Kategorie (bzw. das Tag) schon haben, davon automatisch
  const has = (id: string) => files.filter(f => f.cats.some(c => c.id === id)).length
  const autoOf = (id: string) => files.filter(f => f.cats.some(c => c.id === id && c.auto)).length
  const hasTag = (tag: string) => files.filter(f => f.tags.some(x => x.toLowerCase() === tag.toLowerCase())).length
  const state = (now: boolean | undefined, count: number): boolean | 'mixed' => now ?? (count === 0 ? false : count === n ? true : 'mixed')
  const shownTags = useMemo(() => {
    const set = new Map<string, string>()
    for (const f of files) for (const x of f.tags) set.set(x.toLowerCase(), x)
    for (const x of Object.keys(tags)) set.set(x.toLowerCase(), x)
    return [...set.values()].sort((a, b) => a.localeCompare(b))
  }, [files, tags])
  const save = async () => {
    setBusy(true)
    try {
      // Automatisch eingeordnete, die angehakt bleiben, gelten als bestätigt
      const add = Object.entries(cat).filter(([, v]) => v).map(([k]) => k)
      for (const c of cats) if (cat[c.id] === undefined && autoOf(c.id) > 0 && has(c.id) === n) add.push(c.id)
      await catsApi.setLabels({
        paths, add, remove: Object.entries(cat).filter(([, v]) => !v).map(([k]) => k),
        addTags: Object.entries(tags).filter(([, v]) => v).map(([k]) => k), removeTags: Object.entries(tags).filter(([, v]) => !v).map(([k]) => k),
      })
      await reloadCats()
      onDone?.(); onClose()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const addCat = async () => {
    if (!newCat?.name.trim()) return
    try {
      const c = await catsApi.create({ name: newCat.name.trim(), parent: newCat.parent, color: PALETTE[cats.length % PALETTE.length] })
      await reloadCats()
      setCat(x => ({ ...x, [c.id]: true })); setNewCat(null)
    } catch (e) { fail(e) }
  }
  const addTag = (v: string) => {
    const x = v.trim().replace(/^#/, '')
    if (x) setTags(o => ({ ...o, [x]: true }))
    setNewTag('')
  }
  const suggestions = allTags.map(g => g.tag).filter(x => newTag && x.toLowerCase().startsWith(newTag.toLowerCase()) && !shownTags.some(y => y.toLowerCase() === x.toLowerCase())).slice(0, 6)
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{n > 1 ? t('Kategorien und Tags für {n} Dateien', { n }) : t('Kategorien und Tags')}</DialogTitle>
          <DialogContent>
            {!of ? <Spinner size="tiny" /> : n === 0 ? <Text>{t('Kategorien und Tags gibt es nur für eigene Dateien, nicht für Ordner.')}</Text> : <>
              <Text weight="semibold">{t('Kategorien')}</Text>
              <div className={s.tree}>
                {cats.length === 0 && <Text className={s.muted}>{t('Noch keine Kategorien.')}</Text>}
                {flat(cats).map(({ c, depth }) => {
                  const a = autoOf(c.id)
                  return <div key={c.id} className={s.row} style={{ paddingLeft: depth * 18 }}>
                    <Checkbox checked={state(cat[c.id], has(c.id))} onChange={(_, d) => setCat(x => ({ ...x, [c.id]: d.checked === true }))}
                      label={<span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}><Dot color={c.color} />{c.name}
                        {a > 0 && cat[c.id] === undefined && <Tooltip content={t('Automatisch eingeordnet - Speichern bestätigt es, Abhaken entfernt es')} relationship="label">
                          <span className={s.muted} style={{ display: 'inline-flex' }}><Sparkle16Regular /></span></Tooltip>}</span>} />
                    <Button size="small" appearance="subtle" icon={<Add16Regular />} title={t('Unterkategorie anlegen')} aria-label={t('Unterkategorie anlegen')}
                      onClick={() => setNewCat({ parent: c.id, name: '' })} />
                  </div>
                })}
              </div>
              {newCat ? <div className={s.row} style={{ marginTop: 6 }}>
                <Input autoFocus size="small" placeholder={newCat.parent ? t('Unter „{name}“', { name: fullName(cats, newCat.parent) }) : t('Name der Kategorie')} value={newCat.name}
                  onChange={(_, d) => setNewCat({ ...newCat, name: d.value })} onKeyDown={e => { if (e.key === 'Enter') addCat(); if (e.key === 'Escape') setNewCat(null) }} style={{ flex: 1 }} />
                <Button size="small" onClick={addCat}>{t('Anlegen')}</Button>
                <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={t('Abbrechen')} onClick={() => setNewCat(null)} />
              </div> : <Button size="small" appearance="subtle" icon={<Add16Regular />} style={{ marginTop: 6 }} onClick={() => setNewCat({ parent: null, name: '' })}>{t('Neue Kategorie')}</Button>}

              <Text weight="semibold" block style={{ marginTop: 16 }}>{t('Tags')}</Text>
              <div className={s.chips} style={{ marginTop: 6 }}>
                {shownTags.map(x => <Checkbox key={x} checked={state(tags[x], hasTag(x))} label={'#' + x} onChange={(_, d) => setTags(o => ({ ...o, [x]: d.checked === true }))} />)}
              </div>
              <div className={s.row} style={{ marginTop: 6 }}>
                <Input size="small" placeholder={t('Tag hinzufügen')} value={newTag} onChange={(_, d) => setNewTag(d.value)}
                  onKeyDown={e => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addTag(newTag) } }} style={{ flex: 1 }} />
                <Button size="small" onClick={() => addTag(newTag)} disabled={!newTag.trim()}>{t('Hinzufügen')}</Button>
              </div>
              {suggestions.length > 0 && <div className={s.chips} style={{ marginTop: 4 }}>
                {suggestions.map(x => <Button key={x} size="small" appearance="subtle" onClick={() => addTag(x)}>#{x}</Button>)}
              </div>}
            </>}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={!of || n === 0 || busy} onClick={save}>{t('Speichern')}</Button>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface Preview { files: number; ready: string[]; learning: string[]; folders: { path: string; count: number }[] }

/**
 * Zuordnungsassistent: vorhandene Dateien ohne Kategorie einordnen - Schritt für Schritt. 1. Ordner wählen (oder bei null
 * anfangen), 2. Probelauf mit Beispielen, wie einige zugeordnet würden, 3. einordnen. Alles bleibt als „automatisch“ markiert.
 */
function Assistant({ preview, onClose }: { preview: Preview; onClose: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [step, setStep] = useState(1)
  const tops = preview.folders.filter(f => !f.path.includes('/'))
  const [picked, setPicked] = useState<string[]>([])
  const [open, setOpen] = useState<string[]>([])
  const [sample, setSample] = useState<{ path: string; cats: string[] }[] | null>(null)
  // Ein Ordner der ersten Ebene schließt seine Unterordner ein - nicht doppelt zählen
  const effective = picked.filter(p => !p.includes('/') || !picked.includes(p.split('/')[0]))
  const total = preview.folders.filter(f => effective.includes(f.path)).reduce((n, f) => n + f.count, 0)
  const toggle = (p: string) => setPicked(x => x.includes(p) ? x.filter(y => y !== p) : [...x.filter(y => !y.startsWith(p + '/')), p])
  const loadSample = () => { setSample(null); api.post<{ path: string; cats: string[] }[]>('/api/files/categories/classify-existing/sample', { folders: effective }).then(setSample).catch(fail) }
  const label = (p: string) => p === '' ? t('Direkt in „Meine Dateien“') : p.split('/').pop()!
  const files = (n: number) => n === 1 ? t('1 Datei') : t('{n} Dateien', { n })
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>{t('Zuordnungsassistent')} <Text size={200} className={s.muted}>· {t('Schritt {n} von 3', { n: step })}</Text></DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {step === 1 && <>
              <Text>{t('Der Assistent ordnet Dateien ein, die noch keine Kategorie haben. Du musst das nicht tun: Kategorien lernen auch nur an neuen Dateien und an dem, was du von Hand zuordnest – dann fängst du einfach bei null an.')}</Text>
              <Text weight="semibold">{t('Aus welchen Ordnern?')}</Text>
              <div className={s.tree} style={{ maxHeight: '40vh', padding: 6 }}>
                {tops.length === 0 && <Text className={s.muted}>{t('Alle Dateien haben schon eine Kategorie.')}</Text>}
                {tops.map(f => {
                  const subs = preview.folders.filter(x => x.path.startsWith(f.path + '/') && f.path !== '')
                  return <div key={f.path}>
                    <div className={s.row}>
                      {subs.length > 0
                        ? <Button size="small" appearance="transparent" style={{ minWidth: 24, padding: 0 }} aria-label={open.includes(f.path) ? t('Zuklappen') : t('Aufklappen')}
                            onClick={() => setOpen(o => o.includes(f.path) ? o.filter(x => x !== f.path) : [...o, f.path])}>{open.includes(f.path) ? '▾' : '▸'}</Button>
                        : <span style={{ width: 24 }} />}
                      <Checkbox checked={picked.includes(f.path)} onChange={() => toggle(f.path)} label={`${label(f.path)} · ${files(f.count)}`} />
                    </div>
                    {open.includes(f.path) && subs.map(x => <div key={x.path} className={s.row} style={{ paddingLeft: 48 }}>
                      <Checkbox checked={picked.includes(f.path) || picked.includes(x.path)} disabled={picked.includes(f.path)} onChange={() => toggle(x.path)}
                        label={`${label(x.path)} · ${files(x.count)}`} />
                    </div>)}
                  </div>
                })}
              </div>
              <Text size={200} className={s.muted}>{t('Nur deine Ablage. Nicht dabei: Tresor, externe Ordner, „Mit mir geteilt“ und der notizi-Ordner. Hast du einer Datei eine Kategorie entfernt, bekommt sie diese nicht wieder.')}</Text>
            </>}
            {step === 2 && <>
              {preview.ready.length > 0
                ? <Text>{t('Zuordnen können nur Kategorien, die schon genug gelernt haben: {names}. Sie ordnen eine Datei nur zu, wenn sie sich sicher sind – viele Dateien bekommen keine.', { names: preview.ready.join(', ') })}</Text>
                : <MessageBar intent="info"><MessageBarBody>{t('Noch keine Kategorie hat genug gelernt – es würde nichts zugeordnet. Ordne zuerst je Kategorie einige Dateien von Hand zu und entferne sie bei einigen, die nicht dazugehören (je mindestens drei).')}</MessageBarBody></MessageBar>}
              {preview.learning.length > 0 && <Text size={200} className={s.muted}>{t('Lernen noch: {names}', { names: preview.learning.join(', ') })}</Text>}
              <Text weight="semibold">{t('Probelauf – so würden einige Dateien eingeordnet:')}</Text>
              <div className={s.tree} style={{ maxHeight: '36vh', padding: 6 }}>
                {sample == null && <Text className={s.muted}>{t('Wird geprüft …')}</Text>}
                {sample?.length === 0 && <Text className={s.muted}>{t('Keine Dateien in den gewählten Ordnern.')}</Text>}
                {sample?.map(x => <div key={x.path} className={s.row} style={{ justifyContent: 'space-between' }}>
                  <Text size={200} truncate wrap={false} title={x.path} style={{ minWidth: 0, flex: 1 }}>{x.path}</Text>
                  <Text size={200} className={x.cats.length ? undefined : s.muted}>{x.cats.length ? x.cats.join(', ') : t('keine Kategorie')}</Text>
                </div>)}
              </div>
              <div><Button size="small" onClick={loadSample}>{t('Andere Beispiele')}</Button></div>
            </>}
            {step === 3 && <>
              <Text>{t('{n} Dateien werden im Hintergrund geprüft. Was eine Kategorie bekommt, ist als „automatisch“ markiert, bis du es bestätigst.', { n: total })}</Text>
              <Text>{t('Passt etwas nicht: In „Kategorien verwalten“ nimmt „Unbestätigte entfernen“ alles wieder zurück – je Kategorie oder alle. Von Hand Zugeordnetes bleibt.')}</Text>
            </>}
          </DialogContent>
          <DialogActions>
            {step === 1 && <Button onClick={onClose}>{t('Bei null anfangen')}</Button>}
            {step > 1 && <Button onClick={() => setStep(step - 1)}>{t('Zurück')}</Button>}
            {step === 1 && <Button appearance="primary" disabled={effective.length === 0} onClick={() => { setStep(2); loadSample() }}>{t('Weiter')}</Button>}
            {step === 2 && <Button appearance="primary" disabled={preview.ready.length === 0} onClick={() => setStep(3)}>{t('Weiter')}</Button>}
            {step === 3 && <Button appearance="primary" onClick={() => {
              api.post<{ queued: number }>('/api/files/categories/classify-existing', { folders: effective })
                .then(r => { notify(t('{n} Dateien werden im Hintergrund geprüft', { n: r.queued })); onClose() }).catch(fail)
            }}>{t('Einordnen')}</Button>}
            {step > 1 && <Button appearance="subtle" onClick={onClose}>{t('Abbrechen')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Kategorien verwalten: anlegen, Unterkategorie, umbenennen, verschieben, Farbe, die beiden Schalter, löschen. */
export function ManageCategories({ onClose }: { onClose: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const { cats } = useFileCats()
  const [edit, setEdit] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [adding, setAdding] = useState<{ parent: string | null; name: string } | null>(null)
  const [confirm, setConfirm] = useState<FileCat | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [remove, setRemove] = useState<{ cat: FileCat | null; n: number } | null>(null)
  const unconfirmed = cats.reduce((n, c) => n + c.unconfirmed, 0)
  const run = async (f: () => Promise<unknown>) => { try { await f(); await reloadCats() } catch (e) { fail(e) } }
  const add = () => adding?.name.trim() && run(async () => {
    await catsApi.create({ name: adding.name.trim(), parent: adding.parent, color: PALETTE[cats.length % PALETTE.length] }); setAdding(null)
  })
  const rename = (c: FileCat) => run(async () => { if (name.trim() && name.trim() !== c.name) await catsApi.update(c.id, { name: name.trim() }); setEdit(null) })
  const parentOptions = (c: FileCat) => flat(cats).filter(x => !below(cats, c.id).has(x.c.id))
  return (<>
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{t('Kategorien verwalten')}</DialogTitle>
          <DialogContent>
            <Text className={s.muted} block>
              {t('Kategorien ordnen Dateien unabhängig vom Ordner – etwa alle Rechnungen, egal wo sie liegen. Eine Oberkategorie zeigt auch alles aus ihren Unterkategorien.')}
            </Text>
            {/* Noch keine: der Vorschlag steht sichtbar da, der Knopf sagt, was er tut */}
            {cats.length === 0 && <div className={s.tree} style={{ marginTop: 12, padding: 12, display: 'flex', flexDirection: 'column', gap: 8 }}>
              <Text weight="semibold">{t('Noch keine Kategorien. Vorschlag zum Anfangen:')}</Text>
              <Text size={200}>{t('Finanzen (Bank, Rechnungen, Steuer, Versicherungen), Verträge, Wohnen, Fahrzeug, Gesundheit, Arbeit, Behörden & Ausweise, Schule & Ausbildung')}</Text>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <Button appearance="primary" onClick={() => run(() => api.post('/api/files/categories/suggestion?lang=' + lang, {}))}>{t('Diese Kategorien anlegen')}</Button>
                <Text size={200} className={s.muted}>{t('Danach lassen sie sich umbenennen, verschieben und löschen. Oder unten mit „Neue Kategorie“ selbst anfangen.')}</Text>
              </div>
            </div>}
            {cats.length > 0 && <Text size={200} className={s.muted} block style={{ marginTop: 8 }}>
              {t('„Lernt“: Zuordnen und Entfernen von Hand bringt der Kategorie bei, was dazugehört. „Automatisch“: neue Dateien bekommen sie selbst, wenn das Gelernte eindeutig ist – markiert, bis du bestätigst oder entfernst.')}
            </Text>}
            {cats.length > 0 && <div style={{ marginTop: 12, display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <Button size="small" onClick={() => api.get<Preview>('/api/files/categories/classify-existing').then(setPreview).catch(fail)}>{t('Zuordnungsassistent starten')}</Button>
              {unconfirmed > 0 && <Button size="small" onClick={() => setRemove({ cat: null, n: unconfirmed })}>{t('Alle unbestätigten entfernen ({n})', { n: unconfirmed })}</Button>}
            </div>}
            <div className={s.tree} style={{ maxHeight: '55vh' }}>
              {flat(cats).map(({ c, depth }) => (
                <div key={c.id} className={s.row} style={{ paddingLeft: depth * 20, flexWrap: 'wrap', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, padding: `4px 0 4px ${depth * 20}px` }}>
                  {/* Farbe: Klick wechselt zur nächsten */}
                  <button type="button" title={t('Farbe ändern')} aria-label={t('Farbe ändern')} style={{ border: 'none', background: 'none', padding: 2, cursor: 'pointer' }}
                    onClick={() => run(() => catsApi.update(c.id, { color: PALETTE[(PALETTE.indexOf(c.color ?? '') + 1) % PALETTE.length] }))}><Dot color={c.color} size={14} /></button>
                  {edit === c.id
                    ? <Input autoFocus size="small" value={name} onChange={(_, d) => setName(d.value)} onBlur={() => rename(c)}
                        onKeyDown={e => { if (e.key === 'Enter') rename(c); if (e.key === 'Escape') setEdit(null) }} style={{ flex: 1, minWidth: 140 }} />
                    : <Text style={{ flex: 1, minWidth: 140, cursor: 'text' }} onClick={() => { setEdit(c.id); setName(c.name) }}>{c.name}
                        {c.count > 0 && <span className={s.muted}> · {c.count}</span>}
                        {c.unconfirmed > 0 && <Button size="small" appearance="transparent" style={{ minWidth: 0, padding: '0 4px' }} title={t('Unbestätigte Zuordnungen dieser Kategorie entfernen')}
                          onClick={e => { e.stopPropagation(); setRemove({ cat: c, n: c.unconfirmed }) }}>{t('{n} unbestätigt', { n: c.unconfirmed })}</Button>}</Text>}
                  <Dropdown size="small" style={{ minWidth: 150 }} aria-label={t('Liegt unter')}
                    value={c.parent ? fullName(cats, c.parent) : t('(oberste Ebene)')} selectedOptions={[c.parent ?? '']}
                    onOptionSelect={(_, d) => run(() => catsApi.update(c.id, { parent: d.optionValue ?? '' }))}>
                    <Option value="" text={t('(oberste Ebene)')}>{t('(oberste Ebene)')}</Option>
                    {parentOptions(c).map(({ c: p, depth: pd }) => <Option key={p.id} value={p.id} text={fullName(cats, p.id)}><CatLabel c={p} depth={pd} /></Option>)}
                  </Dropdown>
                  <Switch label={t('Lernt')} checked={c.learn} onChange={(_, d) => run(() => catsApi.update(c.id, { learn: d.checked }))} />
                  <Switch label={t('Automatisch')} checked={c.auto} disabled={!c.learn} onChange={(_, d) => run(() => catsApi.update(c.id, { auto: d.checked }))} />
                  <Button size="small" appearance="subtle" icon={<Add16Regular />} title={t('Unterkategorie anlegen')} aria-label={t('Unterkategorie anlegen')} onClick={() => setAdding({ parent: c.id, name: '' })} />
                  <Button size="small" appearance="subtle" icon={<Edit16Regular />} title={t('Umbenennen')} aria-label={t('Umbenennen')} onClick={() => { setEdit(c.id); setName(c.name) }} />
                  <Button size="small" appearance="subtle" icon={<Delete16Regular />} title={t('Löschen')} aria-label={t('Löschen')} onClick={() => setConfirm(c)} />
                </div>
              ))}
            </div>
            {adding ? <div className={s.row} style={{ marginTop: 8 }}>
              <Input autoFocus size="small" placeholder={adding.parent ? t('Unter „{name}“', { name: fullName(cats, adding.parent) }) : t('Name der Kategorie')} value={adding.name}
                onChange={(_, d) => setAdding({ ...adding, name: d.value })} onKeyDown={e => { if (e.key === 'Enter') add(); if (e.key === 'Escape') setAdding(null) }} style={{ flex: 1 }} />
              <Button size="small" appearance="primary" onClick={add}>{t('Anlegen')}</Button>
              <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={t('Abbrechen')} onClick={() => setAdding(null)} />
            </div> : <Button icon={<Add16Regular />} style={{ marginTop: 8 }} onClick={() => setAdding({ parent: null, name: '' })}>{t('Neue Kategorie')}</Button>}
          </DialogContent>
          <DialogActions><Button appearance="primary" onClick={onClose}>{t('Fertig')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
    {preview && <Assistant preview={preview} onClose={() => setPreview(null)} />}
    {remove && <Dialog open onOpenChange={(_, d) => { if (!d.open) setRemove(null) }}>
        <DialogSurface style={{ maxWidth: 480 }}>
          <DialogBody>
            <DialogTitle>{remove.cat ? t('Unbestätigte aus „{name}“ entfernen?', { name: remove.cat.name }) : t('Alle unbestätigten entfernen?')}</DialogTitle>
            <DialogContent>{t('{n} automatische Zuordnungen, die du noch nicht bestätigt hast, werden entfernt. Die Dateien bleiben, von Hand Zugeordnetes bleibt, und die Kategorie lernt daraus nichts.', { n: remove.n })}</DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => {
                const r = remove; setRemove(null)
                run(async () => { const x = await api.post<{ removed: number }>('/api/files/categories/unconfirmed/remove' + (r.cat ? '?' + q({ cat: r.cat.id }) : ''), {}); notify(t('{n} entfernt', { n: x.removed })) })
              }}>{t('Entfernen')}</Button>
              <Button onClick={() => setRemove(null)}>{t('Abbrechen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
    {confirm && <Dialog open onOpenChange={(_, d) => { if (!d.open) setConfirm(null) }}>
        <DialogSurface style={{ maxWidth: 440 }}>
          <DialogBody>
            <DialogTitle>{t('Kategorie „{name}“ löschen?', { name: confirm.name })}</DialogTitle>
            <DialogContent>{t('Die Dateien bleiben, sie sind nur nicht mehr in dieser Kategorie. Unterkategorien rücken eine Ebene hoch. Was die Kategorie gelernt hat, geht verloren.')}</DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => { const c = confirm; setConfirm(null); run(() => catsApi.remove(c.id)) }}>{t('Löschen')}</Button>
              <Button onClick={() => setConfirm(null)}>{t('Abbrechen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
  </>)
}

/** Details einer Datei: ihre Kategorien (automatisch eingeordnete mit Bestätigen/Entfernen) und Tags. */
export function LabelsDetail({ path, version, onEdit, onOpenCat, onOpenTag }: {
  path: string; version?: number; onEdit: () => void; onOpenCat?: (id: string) => void; onOpenTag?: (tag: string) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const { cats } = useFileCats()
  const [labels, setLabels] = useState<Labels | null>(null)
  const load = () => catsApi.labelsOf([path]).then(r => setLabels(r[path] ?? null)).catch(() => setLabels(null))
  useEffect(() => { setLabels(null); load() }, [path, version]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!labels) return null
  const set = async (body: { add?: string[]; remove?: string[] }) => {
    try { await catsApi.setLabels({ paths: [path], ...body }); await Promise.all([load(), reloadCats()]) } catch (e) { fail(e) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div className={s.chips}>
        {labels.cats.length === 0 && labels.tags.length === 0 && <Text size={200} className={s.muted}>{t('Keine Kategorien und Tags.')}</Text>}
        {labels.cats.map(a => (
          <span key={a.id} className={mergeClasses(s.chip, a.auto && s.chipAuto)} title={a.auto ? t('Automatisch eingeordnet') : undefined}>
            <Dot color={cats.find(c => c.id === a.id)?.color ?? null} />
            <span style={{ cursor: onOpenCat ? 'pointer' : undefined, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} onClick={() => onOpenCat?.(a.id)}>{fullName(cats, a.id)}</span>
            {a.auto && <>
              <Button size="small" appearance="transparent" icon={<Checkmark16Regular />} title={t('Bestätigen')} aria-label={t('Bestätigen')} onClick={() => set({ add: [a.id] })} style={{ minWidth: 0, padding: 0 }} />
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} title={t('Gehört nicht dazu')} aria-label={t('Gehört nicht dazu')} onClick={() => set({ remove: [a.id] })} style={{ minWidth: 0, padding: 0 }} />
            </>}
          </span>
        ))}
        {labels.tags.map(x => <span key={x} className={s.chip} style={{ cursor: onOpenTag ? 'pointer' : undefined }} onClick={() => onOpenTag?.(x)}>#{x}</span>)}
      </div>
      <div><Button size="small" icon={<Edit16Regular />} onClick={onEdit}>{t('Kategorien und Tags …')}</Button></div>
    </div>
  )
}

/** In der Liste eines virtuellen Ordners: automatisch eingeordnet - mit Bestätigen und Entfernen. */
export function AutoBadge({ path, ids, onChanged }: { path: string; ids: string[]; onChanged: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const { cats } = useFileCats()
  const set = async (e: React.MouseEvent, body: { add?: string[]; remove?: string[] }) => {
    e.stopPropagation()
    try { await catsApi.setLabels({ paths: [path], ...body }); await reloadCats(); onChanged() } catch (err) { fail(err) }
  }
  return (
    <span className={mergeClasses(s.chip, s.chipAuto)} style={{ marginLeft: 6, verticalAlign: 'middle' }} title={t('Automatisch eingeordnet')} onClick={e => e.stopPropagation()}>
      <Sparkle16Regular />{ids.map(id => cats.find(c => c.id === id)?.name ?? '').join(', ')}
      <Button size="small" appearance="transparent" icon={<Checkmark16Regular />} title={t('Bestätigen')} aria-label={t('Bestätigen')} onClick={e => set(e, { add: ids })} style={{ minWidth: 0, padding: 0 }} />
      <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} title={t('Gehört nicht dazu')} aria-label={t('Gehört nicht dazu')} onClick={e => set(e, { remove: ids })} style={{ minWidth: 0, padding: 0 }} />
    </span>
  )
}
