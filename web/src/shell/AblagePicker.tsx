// Dateien aus der eigenen Ablage wählen (für Plugins über @dmoffice/sdk und intern, etwa Bons im Haushalt): Ordner durchgehen,
// in Kacheln mit großen Vorschaubildern oder als Liste, neueste zuerst. Mit accept nur bestimmte Arten (etwa Bilder).
// Zurück kommen die gewählten Einträge; laden kann man sie mit ablageBlob.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Spinner, ToggleButton, Tooltip,
  makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { ArrowUp20Regular, ArrowUpload20Regular, ChevronRight12Regular, Grid20Regular, List20Regular, Search20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { locale, t } from '../i18n'
import { fmtWhen, hasThumb, Item, KindIcon, ownSource, parentOf, Thumb } from '../files/common'

const useStyles = makeStyles({
  surface: { maxWidth: '880px', width: '100%' },
  bar: { display: 'flex', alignItems: 'center', gap: '4px', minHeight: '32px', flexWrap: 'wrap' },
  search: { width: '200px', '@media (max-width: 600px)': { order: 1, width: '100%' } },
  crumbs: { display: 'flex', alignItems: 'center', flexGrow: 1, flexBasis: '120px', minWidth: 0, overflow: 'hidden', whiteSpace: 'nowrap' },
  crumb: { minWidth: 0, maxWidth: '220px', overflow: 'hidden', textOverflow: 'ellipsis', fontWeight: tokens.fontWeightRegular },
  crumbLast: { fontWeight: tokens.fontWeightSemibold },
  sep: { color: tokens.colorNeutralForeground3, flexShrink: 0 },
  // Feste Höhe: der Dialog selbst scrollt nicht, nur die Liste
  wrap: { position: 'relative', marginTop: '6px' },
  list: {
    height: 'min(520px, calc(100vh - 280px))', minHeight: '220px', overflowY: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1,
  },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', '@media (max-width: 600px)': { gridTemplateColumns: 'repeat(auto-fill, minmax(110px, 1fr))' }, gap: '8px', padding: '8px', alignContent: 'start' },
  tile: {
    position: 'relative', display: 'flex', flexDirection: 'column', gap: '4px', padding: '6px', borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer', outline: '2px solid transparent', outlineOffset: '-2px', minWidth: 0,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  // Vorschau quadratisch, ganzes Bild sichtbar (ein Bon ist lang und schmal); position relative, damit das noch ladende
  // Vorschaubild (Thumb legt es unsichtbar absolut ab) mit der Liste scrollt und nachlädt
  tileImg: {
    position: 'relative', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', display: 'flex', alignItems: 'center',
    justifyContent: 'center', backgroundColor: tokens.colorNeutralBackground3,
  },
  img: { width: '100%', height: '100%', display: 'block' },
  tileName: { fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  tileCheck: { position: 'absolute', top: '8px', left: '8px', borderRadius: tokens.borderRadiusSmall, backgroundColor: tokens.colorNeutralBackground1 },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '4px 8px', minHeight: '56px', cursor: 'pointer',
    outline: '2px solid transparent', outlineOffset: '-2px',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  rowThumb: {
    position: 'relative', width: '48px', height: '48px', flexShrink: 0, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden',
    display: 'flex', alignItems: 'center', justifyContent: 'center',
  },
  rowThumbBg: { backgroundColor: tokens.colorNeutralBackground3 },
  chosen: {
    backgroundColor: tokens.colorBrandBackground2, outline: `2px solid ${tokens.colorBrandStroke1}`,
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover },
  },
  name: { flexGrow: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  ellipsis: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  empty: { padding: '16px', color: tokens.colorNeutralForeground3 },
  // Beim Ziehen über die Liste: Hinweis über der Liste, verschiebt nichts
  drop: {
    position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: tokens.borderRadiusMedium,
    border: `2px dashed ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorNeutralBackgroundAlpha2, color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold, pointerEvents: 'none',
  },
  // Über die ganze Breite: links Hochladen, rechts die gewählte Datei und die Knöpfe
  actions: { gridColumnStart: 1, gridColumnEnd: -1, justifySelf: 'stretch', display: 'flex', alignItems: 'center', gap: '8px', justifyContent: 'flex-end', '@media (max-width: 600px)': { flexWrap: 'wrap' } },
  upload: { flexShrink: 0, whiteSpace: 'nowrap' },
  picked: { flexGrow: 1, flexBasis: '120px', minWidth: 0, '@media (max-width: 600px)': { display: 'none' }, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
})

export type AblageItem = Item

/** Inhalt einer gewählten Datei der Ablage (etwa um sie in ein Plugin zu übernehmen). */
export async function ablageBlob(item: AblageItem): Promise<File> {
  const r = await fetch('/api/files/download?' + q({ path: item.path }), { credentials: 'same-origin' })
  if (!r.ok) throw new Error(t('Die Datei „{name}“ ließ sich nicht laden.', { name: item.name }))
  const b = await r.blob()
  return new File([b], item.name, { type: b.type || 'application/octet-stream' })
}

const VIEW_KEY = 'ablagepicker.view'
const folderKey = (k: string) => 'ablagepicker.folder.' + k
function readStore(k: string): string | null { try { return localStorage.getItem(k) } catch { return null } }
function writeStore(k: string, v: string) { try { localStorage.setItem(k, v) } catch { /* egal */ } }

/** Ordner zuerst (nach Name), dann Dateien, neueste zuerst. */
const order = (a: Item, b: Item) => Number(b.isDir) - Number(a.isDir)
  || (a.isDir ? a.name.localeCompare(b.name, locale) : Date.parse(b.modified) - Date.parse(a.modified))

/**
 * Dialog „Aus der Ablage wählen“.
 * @param accept      Prüft, ob eine Datei wählbar ist (z. B. nur Bilder); Ordner sind immer begehbar.
 * @param multiple    Mehrere Dateien ankreuzen; ohne: ein Klick markiert, Doppelklick übernimmt.
 * @param rememberKey Merkt sich je Zweck den zuletzt benutzten Ordner und öffnet dort wieder.
 * @param start       Ordner, in denen der Dialog sonst öffnet: der erste, den es gibt; sonst „Dateien“.
 * @param onUpload    Bietet „Vom Gerät hochladen“ und Ziehen und Ablegen an; die Dateien gehen an den Aufrufer.
 * @param uploadAccept accept für die Dateiauswahl des Geräts (etwa "image/*,application/pdf").
 */
export function AblagePicker({ title, accept, multiple = true, rememberKey, start, onUpload, uploadAccept, onPick, onClose }: {
  title?: string; accept?: (item: AblageItem) => boolean; multiple?: boolean
  rememberKey?: string; start?: string | string[]
  onUpload?: (files: File[]) => void; uploadAccept?: string
  onPick: (items: AblageItem[]) => void; onClose: () => void
}) {
  const s = useStyles()
  // null: Startordner wird noch gesucht
  const [path, setPath] = useState<string | null>(null)
  const [items, setItems] = useState<Item[] | null>(null)
  const [filter, setFilter] = useState('')
  const [found, setFound] = useState<Item[] | null>(null)
  const [chosen, setChosen] = useState<Item[]>([])
  const [view, setView] = useState<'tiles' | 'list'>(() => readStore(VIEW_KEY) === 'list' ? 'list' : 'tiles')
  const [dragging, setDragging] = useState(false)
  const file = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  // Ordner, dessen Inhalt schon da ist (Startordner): nicht gleich noch einmal laden
  const loaded = useRef<string | null>(null)
  const loadDir = (p: string) => api.get<{ items: Item[] }>('/api/files/?' + q({ path: p })).then(d => d.items)

  // Startordner: zuletzt für diesen Zweck benutzt, sonst der erste vorhandene aus start, sonst die Wurzel
  useEffect(() => {
    let off = false
    const remembered = rememberKey ? readStore(folderKey(rememberKey)) : null
    const candidates = [...(remembered != null ? [remembered] : []), ...(start == null ? [] : Array.isArray(start) ? start : [start])]
    ;(async () => {
      for (const c of candidates) {
        try { const list = await loadDir(c); if (off) return; loaded.current = c; setItems(list); setPath(c); return } catch { /* gibt es nicht */ }
      }
      if (!off) setPath('')
    })()
    return () => { off = true }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (path == null) return
    let off = false
    setFilter(''); setFound(null)
    listRef.current?.scrollTo(0, 0)
    if (loaded.current === path) { loaded.current = null; return }
    setItems(null)
    loadDir(path).then(l => { if (!off) setItems(l) }).catch(() => { if (!off) setItems([]) })
    return () => { off = true }
  }, [path]) // eslint-disable-line react-hooks/exhaustive-deps

  // Suchbegriff: in der ganzen Ablage suchen (nicht nur im offenen Ordner)
  const term = filter.trim()
  useEffect(() => {
    if (!term) { setFound(null); return }
    setFound(null)
    const timer = setTimeout(() => {
      api.get<{ items: Item[] }>('/api/files/search?' + q({ q: term })).then(d => setFound(d.items.filter(i => !i.isDir))).catch(() => setFound([]))
    }, 250)
    return () => clearTimeout(timer)
  }, [term])

  const source = term ? found : path == null ? null : items
  const shown = (source ?? []).filter(i => i.isDir || !accept || accept(i)).sort(order)
  const isChosen = (i: Item) => chosen.some(x => x.path === i.path)
  const toggle = (i: Item) => setChosen(c => c.some(x => x.path === i.path) ? c.filter(x => x.path !== i.path) : multiple ? [...c, i] : [i])
  const pick = (list: Item[]) => {
    if (!list.length) return
    if (rememberKey) writeStore(folderKey(rememberKey), parentOf(list[0].path))
    onPick(list)
  }
  const open = (i: Item) => setPath(i.path)
  const choose = (i: Item) => (i.isDir ? open(i) : multiple ? toggle(i) : setChosen([i]))
  const keys = (e: React.KeyboardEvent, i: Item) => {
    if (e.key === 'Enter') { e.preventDefault(); if (i.isDir) open(i); else if (multiple) toggle(i); else pick([i]) }
    else if (e.key === ' ' && !i.isDir) { e.preventDefault(); choose(i) }
  }
  const switchView = (v: 'tiles' | 'list') => { setView(v); writeStore(VIEW_KEY, v) }
  const dropped = (files: File[]) => { if (onUpload && files.length) onUpload(multiple ? files : files.slice(0, 1)) }

  const parts = path ? path.split('/') : []
  const when = (i: Item) => fmtWhen(i.modified, locale)
  const sub = (i: Item) => term ? (parentOf(i.path) || t('Dateien')) + ' · ' + when(i) : when(i) + ' · ' + fmtSize(i.size)
  const thumb = (i: Item) => hasThumb(i) ? <Thumb item={i} source={ownSource} size={256} fit="contain" className={s.img} fallbackLarge={view === 'tiles'} />
    : <KindIcon item={i} large={view === 'tiles'} />
  const check = (i: Item, cls?: string) => multiple && !i.isDir
    ? <Checkbox className={cls} checked={isChosen(i)} onClick={e => e.stopPropagation()} onChange={() => toggle(i)} aria-label={i.name} />
    : null

  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{title ?? t('Aus der Ablage wählen')}</DialogTitle>
          <DialogContent>
            <div className={s.bar}>
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path || !!term} onClick={() => path && setPath(parentOf(path))} aria-label={t('folder|Nach oben')} />
              <div className={s.crumbs}>
                <Button size="small" appearance="subtle" className={mergeClasses(s.crumb, !parts.length && s.crumbLast)} onClick={() => setPath('')}>{t('Dateien')}</Button>
                {parts.map((p, n) => <span key={n} style={{ display: 'contents' }}>
                  <ChevronRight12Regular className={s.sep} />
                  <Button size="small" appearance="subtle" className={mergeClasses(s.crumb, n === parts.length - 1 && s.crumbLast)} title={p}
                    onClick={() => setPath(parts.slice(0, n + 1).join('/'))}>{p}</Button>
                </span>)}
              </div>
              <Input size="small" contentBefore={<Search20Regular />} placeholder={t('In der Ablage suchen')} value={filter} onChange={(_, d) => setFilter(d.value)} className={s.search} />
              <Tooltip content={t('Kacheln')} relationship="label">
                <ToggleButton size="small" appearance="subtle" checked={view === 'tiles'} icon={<Grid20Regular />} onClick={() => switchView('tiles')} />
              </Tooltip>
              <Tooltip content={t('Liste')} relationship="label">
                <ToggleButton size="small" appearance="subtle" checked={view === 'list'} icon={<List20Regular />} onClick={() => switchView('list')} />
              </Tooltip>
            </div>
            <div className={s.wrap}
              onDragOver={onUpload ? e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } } : undefined}
              onDragLeave={onUpload ? e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragging(false) } : undefined}
              onDrop={onUpload ? e => { e.preventDefault(); setDragging(false); dropped(Array.from(e.dataTransfer.files ?? [])) } : undefined}>
              <div ref={listRef} className={mergeClasses(s.list, view === 'tiles' && s.grid)} role="listbox" aria-multiselectable={multiple}>
                {source == null && <Spinner size="small" style={{ padding: 12, gridColumn: '1 / -1' }} />}
                {source != null && shown.length === 0 && <div className={s.empty} style={{ gridColumn: '1 / -1' }}>
                  {term ? t('Keine passende Datei gefunden.') : t('Hier ist nichts Passendes.')}</div>}
                {shown.map(i => view === 'tiles'
                  ? <div key={i.path} className={mergeClasses(s.tile, isChosen(i) && s.chosen)} role="option" aria-selected={isChosen(i)} tabIndex={0}
                      title={term ? i.path : i.name} onClick={() => choose(i)} onDoubleClick={() => !i.isDir && !multiple && pick([i])} onKeyDown={e => keys(e, i)}>
                      <div className={s.tileImg}>{i.isDir ? <KindIcon item={i} large /> : thumb(i)}</div>
                      {check(i, s.tileCheck)}
                      <span className={s.tileName}>{i.name}</span>
                      {!i.isDir && <span className={s.sub}>{term ? parentOf(i.path) || t('Dateien') : when(i)}</span>}
                    </div>
                  : <div key={i.path} className={mergeClasses(s.row, isChosen(i) && s.chosen)} role="option" aria-selected={isChosen(i)} tabIndex={0}
                      title={term ? i.path : i.name} onClick={() => choose(i)} onDoubleClick={() => !i.isDir && !multiple && pick([i])} onKeyDown={e => keys(e, i)}>
                      {check(i)}
                      <span className={mergeClasses(s.rowThumb, !i.isDir && hasThumb(i) && s.rowThumbBg)}>{i.isDir ? <KindIcon item={i} /> : thumb(i)}</span>
                      <span className={s.name}><span className={s.ellipsis}>{i.name}</span>{!i.isDir && <span className={s.sub}>{sub(i)}</span>}</span>
                    </div>)}
              </div>
              {dragging && <div className={s.drop}>{t('Zum Hochladen loslassen')}</div>}
            </div>
          </DialogContent>
          <DialogActions className={s.actions}>
              {onUpload && <>
                <Button className={s.upload} icon={<ArrowUpload20Regular />} onClick={() => file.current?.click()}>{t('Vom Gerät hochladen …')}</Button>
                <input ref={file} type="file" hidden accept={uploadAccept} multiple={multiple}
                  onChange={e => { const f = Array.from(e.target.files ?? []); e.target.value = ''; dropped(f) }} />
              </>}
              <span className={s.picked} title={chosen.map(c => c.name).join(', ')}>
                {chosen.length === 1 ? chosen[0].name : chosen.length > 1 ? t('{n} Dateien gewählt', { n: chosen.length }) : ''}
              </span>
              <Button onClick={onClose}>{t('Abbrechen')}</Button>
              <Button appearance="primary" disabled={chosen.length === 0} onClick={() => pick(chosen)}>
                {chosen.length > 1 ? t('{n} Dateien übernehmen', { n: chosen.length }) : t('pick|Übernehmen')}
              </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
