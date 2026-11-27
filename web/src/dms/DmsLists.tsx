import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Menu, MenuDivider, MenuItem, MenuList,
  MenuPopover, Option, Text, Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowMove20Regular, Delete20Regular, Dismiss20Regular, DocumentSearch20Regular, Edit20Regular, Lightbulb20Regular, SelectAllOn20Regular, Merge20Regular, MoreHorizontal20Regular, Rename20Regular, Search20Regular, Sparkle20Regular,
} from '@fluentui/react-icons'
import { useEffect, useState, useRef } from 'react'
import { api, featureOn, ApiError } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { askConfirm, askText, isTyping, useMultiSelect } from '../shell/context'
import { EntrySelect } from './EntrySelect'
import { ColDef, tableHeadLook, useGridColumns } from '../shell/GridColumns'
import { useIsMobile } from '../shell/mobile'
import { FilterSelect } from '../shell/FilterSelect'
import { RibbonActions, RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { dblOpen } from '../shell/rowOpen' // [feature:rowdblopen]
import { Entry, Kind, kindLabel, Match, matchLabel, ordered } from './model'

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px 20px', minHeight: 0, flex: 1, '@media (max-width: 767px)': { padding: '8px 12px' } },
  bar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  table: { flex: 1, minHeight: 0, overflowY: 'auto' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(160px, 2fr) minmax(110px, 1fr) 70px minmax(90px, 1fr) auto', alignItems: 'center', gap: '12px',
    padding: '4px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr) auto' } },
  head: { ...tableHeadLook, minHeight: '32px' },
  name: { display: 'flex', alignItems: 'center', gap: '8px', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: 'pointer',
    color: tokens.colorBrandForeground1, ':hover': { textDecoration: 'underline' } },
  chip: { display: 'inline-block', width: '12px', height: '12px', borderRadius: '3px', flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '2px', justifySelf: 'end' },
  cell: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  branch: { color: tokens.colorNeutralForeground4, flexShrink: 0 },
  dropOn: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-2px', borderRadius: tokens.borderRadiusMedium },
  // Markiert (dmskeys): wie in den anderen Listen
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected },
})

/** Antwort beim Zusammenführen (dmsmerge): Dokumente im offenen Bereich und im Tresor (null: Tresor zu), Tresor später, Kennung für Rückgängig */
interface MergeResult { docs: number; tresorDocs: number | null; tresorLater: boolean; change: string }

/** Datentyp beim Ziehen eines Eintrags (nicht zu verwechseln mit Dateien bzw. Dokumenten) */
const DRAG = 'application/x-dms-entry'

/** Ids des Eintrags und aller darunter. */
const below = (all: Entry[], id: string): Set<string> => {
  const out = new Set([id])
  for (let grew = true; grew;) {
    grew = false
    for (const e of all) if (e.parent && out.has(e.parent) && !out.has(e.id)) { out.add(e.id); grew = true }
  }
  return out
}

type MenuAt = { x: number; y: number; kind: Kind; entry: Entry | null; extra?: { label: string; onClick: () => void }[] }

/**
 * Aktionen auf Einträgen für Bäume und die Verwalten-Seiten: Kontextmenü (Rechtsklick bzw. „…“) mit „Neuer Untereintrag …“,
 * „Umbenennen …“, „Verschieben unter …“, „Löschen …“ (ohne Eintrag: „Neuer Eintrag …“), dazu Ziehen und Ablegen eines Eintrags
 * auf einen anderen (= darunter einordnen). ui gehört einmal in die Seite.
 */
export function useEntryActions(entries: Entry[], onChanged: () => void, ai?: (kind: Kind, entry: Entry | null) => void) {
  const { fail, notify, undoable } = useApp()
  const [menu, setMenu] = useState<MenuAt | null>(null)
  const [edit, setEdit] = useState<{ kind: Kind; entry: Entry | null; parent: string | null } | null>(null)
  const [moving, setMoving] = useState<Entry | null>(null)
  const [over, setOver] = useState<string | null>(null)

  /**
   * Zusammenführen (dmsmerge): Rückfrage mit der Anzahl betroffener Dokumente, danach Rückgängig über die Versionen der Dokumente.
   * intro: Satz vor der Rückfrage (Umbenennen auf einen vorhandenen Namen). true: zusammengeführt.
   */
  const merge = async (sources: Entry[], target: Entry, intro?: string): Promise<boolean> => {
    const src = sources.filter(e => e.id !== target.id)
    if (!src.length) return false
    try {
      const body = { target: target.id, sources: src.map(e => e.id) }
      const dry = await api.post<MergeResult>('/api/dms/lists/merge', { ...body, dryRun: true })
      const n = dry.docs + (dry.tresorDocs ?? 0)
      const text = [
        intro,
        src.length === 1 ? t('„{source}“ wird mit „{target}“ zusammengeführt.', { source: src[0].name, target: target.name })
          : t('{n} Einträge werden mit „{target}“ zusammengeführt.', { n: src.length, target: target.name }),
        n === 0 ? t('Kein Dokument ist betroffen.') : n === 1 ? t('1 Dokument bekommt „{target}“.', { target: target.name })
          : t('{n} Dokumente bekommen „{target}“.', { n, target: target.name }),
        src.length === 1 ? t('Das Gelernte geht auf „{target}“ über, „{source}“ wird gelöscht.', { source: src[0].name, target: target.name })
          : t('Das Gelernte geht auf „{target}“ über, die übrigen Einträge werden gelöscht.', { target: target.name }),
        dry.tresorLater ? t('Im Tresor werden die Dokumente beim nächsten Öffnen umgestellt.') : '',
      ].filter(Boolean).join(' ')
      if (!await askConfirm(t('{kind} zusammenführen', { kind: kindLabel(target.kind) }), text, t('Zusammenführen'))) return false
      const r = await api.post<MergeResult>('/api/dms/lists/merge', body)
      onChanged()
      const done = t('Mit „{target}“ zusammengeführt', { target: target.name })
      // Rückgängig: die Dokumente bekommen ihre Einträge zurück, gelöschte Einträge entstehen dabei wieder
      if (featureOn('dmshistory') && (r.docs || r.tresorDocs)) undoable(done, async () => {
        await api.post('/api/dms/history/undo', { change: r.change })
        if (r.tresorDocs) await api.post('/api/dms/history/undo?area=tresor', { change: r.change })
        onChanged()
      })
      else notify(done)
      return true
    } catch (x) { fail(x); return false }
  }
  /** Name schon vergeben (Antwort 409 mit dem vorhandenen Eintrag): Zusammenführen anbieten statt abzulehnen. true: behandelt. */
  const offerMerge = async (x: unknown, e: Entry) => {
    const existing = featureOn('dmsmerge') && x instanceof ApiError && x.status === 409 ? entries.find(y => y.id === x.data?.existing) : undefined
    if (!existing) return false
    await merge([e], existing, t('„{name}“ gibt es hier schon.', { name: existing.name }))
    return true
  }
  const put = (e: Entry, body: Record<string, unknown>) => api.put(`/api/dms/lists/${e.id}`, body).then(onChanged)
    .catch(async x => { if (!await offerMerge(x, e)) fail(x) })
  const rename = async (e: Entry) => {
    const n = (await askText(t('Umbenennen'), e.name))?.trim()
    if (n && n !== e.name) put(e, { name: n })
  }
  const moveTo = (e: Entry, parent: string | null) => {
    if (parent === e.parent) return
    if (parent && below(entries, e.id).has(parent)) { notify(t('Ein Eintrag kann nicht unter sich selbst liegen.')); return }
    put(e, { parent: parent ?? '' })
  }
  /** Dokumente, die den Eintrag selbst tragen (e.count zählt die Unterbegriffe mit, die beim Löschen bleiben). */
  const usage = (e: Entry) => api.get<{ docs: number }>(`/api/dms/lists/${e.id}/usage`).then(r => r.docs).catch(() => e.count)
  const remove = async (e: Entry) => {
    const n = await usage(e)
    const text = n === 0 ? t('„{name}“ wird gelöscht. Untereinträge rücken eine Ebene hoch.', { name: e.name })
      : n === 1 ? t('„{name}“ wird gelöscht und an 1 Dokument entfernt. Das Dokument bleibt; Untereinträge rücken eine Ebene hoch.', { name: e.name })
      : t('„{name}“ wird gelöscht und an {n} Dokumenten entfernt. Die Dokumente bleiben; Untereinträge rücken eine Ebene hoch.', { name: e.name, n })
    if (!await askConfirm(t('{kind} löschen', { kind: kindLabel(e.kind) }), text, t('Löschen'))) return
    try { await api.del(`/api/dms/lists/${e.id}`); onChanged() } catch (x) { fail(x) }
  }

  /** Mehrere auf einmal; true: gelöscht. */
  const removeMany = async (list: Entry[]) => {
    const docs = (await Promise.all(list.map(usage))).reduce((n, d) => n + d, 0)
    const text = docs === 0 ? t('{n} Einträge werden gelöscht. Untereinträge rücken eine Ebene hoch.', { n: list.length })
      : t('{n} Einträge werden gelöscht und an den Dokumenten entfernt ({d} Zuordnungen). Die Dokumente bleiben; Untereinträge rücken eine Ebene hoch.', { n: list.length, d: docs })
    if (!await askConfirm(t('{n} Einträge löschen', { n: list.length }), text, t('Löschen'))) return false
    try { for (const e of list) await api.del(`/api/dms/lists/${e.id}`) } catch (x) { fail(x) }
    onChanged()
    return true
  }

  /** Kontextmenü an der Maus (Rechtsklick) bzw. am „…“-Knopf öffnen. */
  const openMenu = (ev: React.MouseEvent, kind: Kind, entry: Entry | null, extra?: MenuAt['extra']) => {
    ev.preventDefault(); ev.stopPropagation()
    setMenu({ x: ev.clientX, y: ev.clientY, kind, entry, extra })
  }
  const newEntry = (kind: Kind, parent: string | null = null) => setEdit({ kind, entry: null, parent })
  const editEntry = (e: Entry) => setEdit({ kind: e.kind, entry: e, parent: e.parent })

  /** Ziehen: auf der Zeile eines Eintrags verteilen. */
  const dnd = (e: Entry) => ({
    draggable: true,
    onDragStart: (ev: React.DragEvent) => { ev.stopPropagation(); ev.dataTransfer.setData(DRAG, e.id); ev.dataTransfer.effectAllowed = 'move' },
    onDragOver: (ev: React.DragEvent) => { if (ev.dataTransfer.types.includes(DRAG)) { ev.preventDefault(); ev.stopPropagation(); setOver(e.id) } },
    onDragLeave: () => setOver(o => o === e.id ? null : o),
    onDrop: (ev: React.DragEvent) => {
      const id = ev.dataTransfer.getData(DRAG)
      setOver(null)
      if (!id) return
      ev.preventDefault(); ev.stopPropagation()
      const src = entries.find(x => x.id === id)
      if (src && src.id !== e.id && src.kind === e.kind) moveTo(src, e.id)
    },
  })

  const m = menu
  const ui = <>
    {m && <Menu open onOpenChange={(_, d) => { if (!d.open) setMenu(null) }}
      positioning={{ autoSize: 'height', target: { getBoundingClientRect: () => ({ x: m.x, y: m.y, top: m.y, left: m.x, bottom: m.y, right: m.x, width: 0, height: 0 }) } as any }}>
      <MenuPopover>
        <MenuList>
          {m.entry ? <>
            <MenuItem icon={<Add20Regular />} onClick={() => newEntry(m.kind, m.entry!.id)}>{t('Neuer Untereintrag …')}</MenuItem>
            <MenuItem icon={<Edit20Regular />} onClick={() => editEntry(m.entry!)}>{t('Bearbeiten …')}</MenuItem>
            <MenuItem icon={<Rename20Regular />} onClick={() => rename(m.entry!)}>{t('Umbenennen …')}</MenuItem>
            {ai && m.kind !== 'role' && <MenuItem icon={<Sparkle20Regular />} onClick={() => ai(m.kind, m.entry)}>{t('Zuordnung mit KI prüfen …')}</MenuItem>}
            {m.extra?.length ? <><MenuDivider />{m.extra.map(x => <MenuItem key={x.label} onClick={x.onClick}>{x.label}</MenuItem>)}<MenuDivider /></> : null}
            <MenuItem icon={<ArrowMove20Regular />} onClick={() => setMoving(m.entry)}>{t('Verschieben unter …')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<Delete20Regular />} onClick={() => remove(m.entry!)}>{t('Löschen …')}</MenuItem>
          </> : <>
            <MenuItem icon={<Add20Regular />} onClick={() => newEntry(m.kind)}>{t('Neuer Eintrag …')}</MenuItem>
            {ai && m.kind !== 'role' && <MenuItem icon={<Sparkle20Regular />} onClick={() => ai(m.kind, null)}>{t('Ohne Zuordnung mit KI einordnen …')}</MenuItem>}
          </>}
          {!m.entry && m.extra?.length ? <><MenuDivider />{m.extra.map(x => <MenuItem key={x.label} onClick={x.onClick}>{x.label}</MenuItem>)}</> : null}
        </MenuList>
      </MenuPopover>
    </Menu>}
    {edit && <EditEntry kind={edit.kind} entries={entries} entry={edit.entry} initialParent={edit.parent}
      onClose={saved => { setEdit(null); if (saved) onChanged() }} />}
    {moving && <MoveEntry entry={moving} entries={entries} onClose={parent => { const e = moving; setMoving(null); if (parent !== undefined) moveTo(e, parent) }} />}
  </>
  return { removeMany, openMenu, newEntry, editEntry, remove, rename, merge, dnd, over, ui }
}

/** „Verschieben unter …“: übergeordneten Eintrag wählen (nicht sich selbst oder darunter). undefined: abgebrochen. */
function MoveEntry({ entry, entries, onClose }: { entry: Entry; entries: Entry[]; onClose: (parent: string | null | undefined) => void }) {
  const [parent, setParent] = useState<string | null>(entry.parent)
  const own = below(entries, entry.id)
  const options = ordered(entries, entry.kind).filter(o => !own.has(o.entry.id))
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(undefined) }}>
      <DialogSurface style={{ maxWidth: 440 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ verschieben', { name: entry.name })}</DialogTitle>
          <DialogContent>
            <Field label={t('Übergeordneter Eintrag')}>
              {featureOn('dmstreepick') ? <EntrySelect entries={entries} kind={entry.kind} value={parent} exclude={own} none={t('Keiner (oberste Ebene)')}
                placeholder={t('Keiner (oberste Ebene)')} onPick={id => setParent(id || null)} /> :
              featureOn('filterpick') ? <FilterSelect value={parent ?? ''} none={t('Keiner (oberste Ebene)')} onChange={id => setParent(id || null)}
                items={options.map(p => ({ id: p.entry.id, label: p.entry.name, depth: p.depth }))} /> :
              <Dropdown value={parent ? options.find(p => p.entry.id === parent)?.entry.name ?? '' : t('Keiner (oberste Ebene)')} selectedOptions={[parent ?? '']}
                onOptionSelect={(_, d) => setParent(d.optionValue || null)}>
                <Option value="">{t('Keiner (oberste Ebene)')}</Option>
                {options.map(p => <Option key={p.entry.id} value={p.entry.id} text={p.entry.name}><span style={{ paddingLeft: p.depth * 12 }}>{p.entry.name}</span></Option>)}
              </Dropdown>}
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(undefined)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={parent === entry.parent} onClick={() => onClose(parent)}>{t('Verschieben')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

const MATCHES: Match[] = ['auto', 'none', 'any', 'all', 'exact', 'regex', 'sender']
const PALETTE = ['#75dca7', '#d49554', '#31c797', '#5147d1', '#65d8b2', '#e598cb', '#3ccece', '#a77429', '#6cd9da', '#99d351', '#7b9cde', '#9ae5b4', '#83d454', '#e59f9a']

/** Spalten der Verwalten-Listen (Rechtsklick auf die Kopfzeile wählt sie) */
const LIST_COLS: Record<string, ColDef> = {
  name: { label: () => t('Name'), width: 240, sort: 'name' },
  match: { label: () => t('Zuweisung'), width: 200, sort: 'match' },
  count: { label: () => t('Dokumente'), width: 90, sort: 'count' },
  lastUsed: { label: () => t('Zuletzt benutzt'), width: 120, sort: 'lastUsed' },
  parent: { label: () => t('Übergeordneter Eintrag'), width: 180 },
}
const LIST_DEFAULT = ['name', 'match', 'count', 'lastUsed']
/** Rollen: werden nur von Hand gesetzt und gehören genau einem Dokument - statt der Zuweisung steht das Dokument da. */
const ROLE_COLS: Record<string, ColDef> = {
  name: LIST_COLS.name,
  holder: { label: () => t('Dokument'), width: 280, sort: 'holder' },
  lastUsed: LIST_COLS.lastUsed,
}
const ROLE_DEFAULT = ['name', 'holder', 'lastUsed']
interface Holder { role: string; id: number; tresor: boolean; title: string | null }

/** Geschwister im Baum nach der Spalte ordnen; der Baum selbst bleibt. */
const listCompare = (key: string | undefined, desc: boolean | undefined, holderText?: (e: Entry) => string) => {
  const cmp = (a: Entry, b: Entry): number => {
    switch (key) {
      case 'holder': return holderText ? holderText(a).localeCompare(holderText(b)) : 0
      case 'match': return matchLabel(a.match).localeCompare(matchLabel(b.match)) || (a.matchText ?? '').localeCompare(b.matchText ?? '')
      case 'count': return a.count - b.count
      case 'lastUsed': return (a.lastUsed ?? '').localeCompare(b.lastUsed ?? '')
      case 'name': return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
      default: return 0
    }
  }
  return (a: Entry, b: Entry) => desc ? cmp(b, a) : cmp(a, b)
}

/** Verwaltung einer Liste (Korrespondenten, Dokumenttypen oder Tags): Name, Zuweisung, Anzahl, zuletzt benutzt, Farbe. */
export function ListsView({ kind, entries, onChanged, onShow }: {
  kind: Kind; entries: Entry[]; onChanged: () => void; onShow: (e: Entry) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const mobile = useIsMobile()
  const [filter, setFilter] = useState('')
  const [pickedOld, setPickedOld] = useState<Set<string>>(new Set())
  const act = useEntryActions(entries, onChanged)
  // Tastatur und Mehrfachauswahl wie in den anderen Listen (dmskeys): Klick, Strg+Klick, Umschalt+Klick, Pfeile; Zusammenführen (dmsmerge)
  const keys = featureOn('dmskeys')
  const mergeOn = featureOn('dmsmerge')
  const [merging, setMerging] = useState<Entry[] | null>(null)
  const filterInput = useRef<HTMLInputElement>(null)
  // Spalten wie im Explorer; Sortieren ordnet die Einträge jeder Ebene, der Baum bleibt
  const roles = kind === 'role'
  const grid = useGridColumns(roles ? 'dms-roles' : 'dms-lists', roles ? ROLE_COLS : LIST_COLS, roles ? ROLE_DEFAULT : LIST_DEFAULT, '', 'minmax(max-content, 1fr)')
  const [holders, setHolders] = useState<Map<string, Holder>>(new Map())
  useEffect(() => {
    if (!roles) return
    api.get<Holder[]>('/api/dms/roles/holders').then(l => setHolders(new Map(l.map(h => [h.role, h])))).catch(fail)
  }, [roles, entries]) // eslint-disable-line react-hooks/exhaustive-deps
  const holderText = (e: Entry) => { const h = holders.get(e.id); return !h ? '' : h.title ?? t('Im Tresor') }
  // Als Baum (eingerückt); beim Filtern bleiben die übergeordneten Einträge der Treffer stehen
  const all = ordered(entries, kind, listCompare(grid.sort?.key, grid.sort?.desc, holderText))
  const hits = new Set(all.filter(r => !filter || r.entry.name.toLowerCase().includes(filter.toLowerCase())).map(r => r.entry.id))
  const shown = new Set<string>()
  for (const id of hits) for (let e = entries.find(x => x.id === id); e && !shown.has(e.id); e = entries.find(x => x.id === e!.parent)) shown.add(e.id)
  const rows = all.filter(r => shown.has(r.entry.id))
  const ms = useMultiSelect<string>(rows.map(r => r.entry.id))
  const picked = keys ? new Set(ms.selected) : pickedOld
  const setPicked = (p: Set<string>) => keys ? ms.setSelected(rows.map(r => r.entry.id).filter(id => p.has(id))) : setPickedOld(p)
  const suggestion = async () => {
    try { await api.post('/api/dms/lists/suggestion?lang=' + lang); onChanged() } catch (x) { fail(x) }
  }
  const pickedList = rows.map(r => r.entry).filter(e => picked.has(e.id))
  const empty = rows.map(r => r.entry).filter(e => e.count === 0)
  const pick = (id: string, on: boolean) => keys ? ms.setSelected(sel => on ? (sel.includes(id) ? sel : [...sel, id]) : sel.filter(x => x !== id))
    : setPickedOld(p => { const x = new Set(p); if (on) x.add(id); else x.delete(id); return x })
  // Tasten: Pfeile (mit Umschalt erweitern), Strg+A, Esc, Entf (mit Rückfrage), Enter zeigt die Dokumente, F2 benennt um, Strg+F filtert
  const onKey = useRef<(e: KeyboardEvent) => void>(() => {})
  onKey.current = e => {
    if (!location.hash.startsWith('#/dms') || isTyping(e) || document.querySelector('[role=dialog],[role=alertdialog]')) return
    const sel = pickedList
    const k = e.key
    if (k === 'ArrowDown' || k === 'ArrowUp') { e.preventDefault(); ms.step(k === 'ArrowDown' ? 1 : -1, e.shiftKey) }
    else if ((e.ctrlKey || e.metaKey) && k.toLowerCase() === 'a') { e.preventDefault(); ms.all() }
    else if ((e.ctrlKey || e.metaKey) && k.toLowerCase() === 'f') { e.preventDefault(); filterInput.current?.focus(); filterInput.current?.select() }
    else if (k === 'Escape' && sel.length) { e.preventDefault(); ms.clear() }
    else if (k === 'Delete' && sel.length) { e.preventDefault(); act.removeMany(sel).then(ok => { if (ok) ms.clear() }) }
    else if (k === 'Enter' && sel.length === 1) { e.preventDefault(); onShow(sel[0]) }
    else if (k === 'F2' && sel.length === 1) { e.preventDefault(); act.rename(sel[0]) }
  }
  useEffect(() => {
    if (!keys) return
    const on = (e: KeyboardEvent) => onKey.current(e)
    document.addEventListener('keydown', on)
    return () => document.removeEventListener('keydown', on)
  }, [keys])
  // Mit den Pfeilen erreichte Zeile sichtbar halten
  const lastPicked = ms.selected[ms.selected.length - 1]
  useEffect(() => {
    if (keys && lastPicked) document.querySelector(`[data-dms-entry="${CSS.escape(lastPicked)}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [keys, lastPicked])
  return (
    <div className={s.root}>
      <div className={s.bar}>
        <Input input={{ ref: filterInput }} contentBefore={<Search20Regular />} placeholder={t('Filtern nach Name')} value={filter} onChange={(_, d) => setFilter(d.value)} style={{ minWidth: 240 }} />
      </div>
      <RibbonActions>
        <RibbonButton primary icon={<Add20Regular />} label={t('Neuer Eintrag …')} onClick={() => act.newEntry(kind)} />
        {kind === 'doctype' && !entries.some(e => e.kind === 'doctype') && <RibbonButton icon={<Lightbulb20Regular />} label={t('Vorschlag übernehmen')} onClick={suggestion} />}
        <RibbonDivider />
        <RibbonButton icon={<SelectAllOn20Regular />} label={t('Leere auswählen ({n})', { n: empty.length })} disabled={!empty.length} onClick={() => setPicked(new Set(empty.map(e => e.id)))} />
        {mergeOn && <RibbonButton icon={<Merge20Regular />} label={t('Zusammenführen …')} disabled={pickedList.length < 2} onClick={() => setMerging(pickedList)} />}
        <RibbonButton icon={<Delete20Regular />} label={pickedList.length ? t('{n} Einträge löschen', { n: pickedList.length }) : t('Löschen')} disabled={!pickedList.length}
          onClick={async () => { if (await act.removeMany(pickedList)) setPicked(new Set()) }} />
        <RibbonButton icon={<Dismiss20Regular />} label={t('Auswahl aufheben')} disabled={!pickedList.length} onClick={() => setPicked(new Set())} />
      </RibbonActions>
      <Text size={200} className={s.muted}>{t('Untereinträge über Rechtsklick bzw. „…“ an einem Eintrag oder durch Ziehen eines Eintrags auf einen anderen.')}</Text>
      <div className={s.table} onContextMenu={e => act.openMenu(e, kind, null)}>
        <div className={`${s.row} ${s.head}`} style={mobile ? undefined : { gridTemplateColumns: grid.template, minWidth: 'max-content' }}
          onContextMenu={x => { x.stopPropagation(); grid.openMenu(x) }}>
          {mobile ? <><span>{t('Name')}</span><span /></> : <>{grid.cells}<span /></>}
        </div>
        {grid.columnMenu}
        {rows.map(({ entry: e, depth }) => (
          <div key={e.id} data-dms-entry={e.id} className={mergeClasses(s.row, act.over === e.id && s.dropOn, keys && picked.has(e.id) && s.sel)}
            style={mobile ? undefined : { gridTemplateColumns: grid.template, minWidth: 'max-content' }}
            onClick={keys ? x => ms.click(x, e.id) : undefined}
            {...dblOpen(() => onShow(e))} // [feature:rowdblopen]
            onContextMenu={x => { if (keys) ms.pick(e.id); act.openMenu(x, kind, e) }} {...act.dnd(e)}>
            {(mobile ? ['name'] : grid.cols.map(c => c.key)).map(k => {
              switch (k) {
                case 'name': return (
                  <span key={k} className={s.name} style={{ paddingLeft: depth * 20 }} title={t('Dokumente zeigen')}
                    onClick={x => { if (keys && (x.ctrlKey || x.metaKey || x.shiftKey)) return; x.stopPropagation(); onShow(e) }}>
                    <span onClick={x => x.stopPropagation()} style={{ display: 'inline-flex' }}>
                      <Checkbox checked={picked.has(e.id)} onChange={(_, d) => pick(e.id, !!d.checked)} aria-label={t('{name} auswählen', { name: e.name })} />
                    </span>
                    {depth > 0 && <span className={s.branch}>└</span>}
                    {kind === 'tag' && <span className={s.chip} style={{ backgroundColor: e.color ?? tokens.colorNeutralBackground5 }} />}
                    {hits.has(e.id) ? e.name : <span className={s.muted}>{e.name}</span>}
                  </span>)
                case 'match': return <span key={k} className={s.cell}>{matchLabel(e.match)}{e.matchText ? <Text size={200} className={s.muted}> · {e.matchText}</Text> : null}</span>
                case 'holder': { const h = holders.get(e.id); return <span key={k} className={s.cell}>{!h ? <span className={s.muted}>{t('Keinem Dokument zugeordnet')}</span>
                  : <>{h.title ?? t('Im Tresor')}{h.tresor && h.title && <Text size={200} className={s.muted}> · {t('Tresor')}</Text>}</>}</span> }
                case 'count': return <span key={k} className={s.cell}>{e.count}</span>
                case 'lastUsed': return <span key={k} className={s.cell}>{e.lastUsed ? new Date(e.lastUsed).toLocaleDateString() : ''}</span>
                default: return <span key={k} className={s.cell}>{e.parent ? entries.find(x => x.id === e.parent)?.name ?? '' : ''}</span>
              }
            })}
            <span className={s.actions} onClick={x => x.stopPropagation()}>
              <Tooltip content={t('Dokumente zeigen')} relationship="label"><Button appearance="subtle" size="small" icon={<DocumentSearch20Regular />} onClick={() => onShow(e)} /></Tooltip>
              <Tooltip content={t('Bearbeiten')} relationship="label"><Button appearance="subtle" size="small" icon={<Edit20Regular />} onClick={() => act.editEntry(e)} /></Tooltip>
              <Tooltip content={t('Weitere Aktionen')} relationship="label"><Button appearance="subtle" size="small" icon={<MoreHorizontal20Regular />} onClick={x => act.openMenu(x, kind, e)} /></Tooltip>
            </span>
          </div>
        ))}
        {rows.length === 0 && <Text className={s.muted}>{filter ? t('Nichts gefunden.') : t('Noch keine Einträge.')}</Text>}
        <Text size={200} className={s.muted}>{t('Insgesamt {n}', { n: entries.filter(e => e.kind === kind).length })}</Text>
      </div>
      {act.ui}
      {merging && <MergeDialog kind={kind} entries={entries} picked={merging} onClose={async target => {
        const list = merging
        setMerging(null)
        if (target && await act.merge(list, target)) setPicked(new Set())
      }} />}
    </div>
  )
}

/** Zusammenführen (dmsmerge): Ziel wählen - vorgeschlagen ist der markierte Eintrag mit den meisten Dokumenten. null: abgebrochen. */
function MergeDialog({ kind, entries, picked, onClose }: { kind: Kind; entries: Entry[]; picked: Entry[]; onClose: (target: Entry | null) => void }) {
  const [target, setTarget] = useState<string | null>(() => [...picked].sort((a, b) => b.count - a.count)[0]?.id ?? null)
  const chosen = entries.find(e => e.id === target) ?? null
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(null) }}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{t('{kind} zusammenführen', { kind: kindLabel(kind) })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{t('Die markierten Einträge gehen im Ziel auf. Ihre Dokumente bekommen das Ziel.')}</Text>
            <Field label={t('Ziel')}>
              <EntrySelect entries={entries} kind={kind} value={target} onPick={id => setTarget(id || null)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!chosen || picked.every(e => e.id === chosen.id)} onClick={() => onClose(chosen)}>{t('Weiter …')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Eintrag anlegen oder bearbeiten: Name, Oberbegriff, Farbe (Tags), Zuweisung. */
export function EditEntry({ kind, entries, entry, initialName, initialParent, onClose }: {
  kind: Kind; entries: Entry[]; entry: Entry | null; initialName?: string; initialParent?: string | null; onClose: (saved: Entry | null) => void
}) {
  const { fail } = useApp()
  const [f, setF] = useState(() => entry ? { ...entry } : {
    name: initialName ?? '', parent: (initialParent ?? null) as string | null, color: kind === 'tag' ? PALETTE[entries.filter(e => e.kind === 'tag').length % PALETTE.length] : null,
    match: (kind === 'role' ? 'none' : 'auto') as Match, matchText: '' as string | null, caseSensitive: false,
  })
  const set = (patch: Partial<typeof f>) => setF(x => ({ ...x, ...patch }))
  const [busy, setBusy] = useState(false)
  const own = entry ? below(entries, entry.id) : new Set<string>()
  const parents = ordered(entries, kind).filter(o => !own.has(o.entry.id))
  const needsText = f.match !== 'auto' && f.match !== 'none'
  const save = async () => {
    setBusy(true)
    try {
      const body = { kind, name: f.name, parent: f.parent ?? '', color: f.color ?? '', match: f.match, matchText: f.matchText ?? '', caseSensitive: f.caseSensitive }
      const saved = entry ? await api.put<Entry>(`/api/dms/lists/${entry.id}`, body) : await api.post<Entry>('/api/dms/lists', body)
      onClose(saved)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const hint: Record<Match, string> = {
    auto: t('Lernt aus geprüften Dokumenten und setzt den Eintrag, wenn das Gelernte eindeutig ist (ab drei Dokumenten).'),
    none: t('Nur von Hand.'),
    any: t('Mindestens eines der Wörter steht im Text (durch Leerzeichen oder Komma getrennt).'),
    all: t('Alle Wörter stehen im Text.'),
    exact: t('Die Wortfolge steht genau so im Text.'),
    regex: t('Der reguläre Ausdruck passt auf den Text.'),
    sender: t('Mailadresse oder Domain des Absenders (Mails und Anhänge).'),
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(null) }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{entry ? t('{kind} bearbeiten', { kind: kindLabel(kind) }) : t('Neuer Eintrag: {kind}', { kind: kindLabel(kind) })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Name')} required><Input autoFocus value={f.name} onChange={(_, d) => set({ name: d.value })} /></Field>
            {kind === 'role' && <Text size={200}>{t('Zu jeder Rolle gibt es im gesamten DMS genau ein geltendes Dokument, etwa das aktuelle Testament. Bekommt ein anderes Dokument die Rolle, bleibt das bisherige als frühere Version erhalten.')}</Text>}
            {kind !== 'role' && <Field label={t('Übergeordneter Eintrag')}>
              {featureOn('dmstreepick') ? <EntrySelect entries={entries} kind={kind} value={f.parent} exclude={own} none={t('Keiner (oberste Ebene)')}
                placeholder={t('Keiner (oberste Ebene)')} onPick={id => set({ parent: id || null })} /> :
              featureOn('filterpick') ? <FilterSelect value={f.parent ?? ''} none={t('Keiner (oberste Ebene)')} onChange={id => set({ parent: id || null })}
                items={parents.map(p => ({ id: p.entry.id, label: p.entry.name, depth: p.depth }))} /> :
              <Dropdown value={f.parent ? parents.find(p => p.entry.id === f.parent)?.entry.name ?? '' : t('Keiner (oberste Ebene)')} selectedOptions={[f.parent ?? '']}
                onOptionSelect={(_, d) => set({ parent: d.optionValue || null })}>
                <Option value="">{t('Keiner (oberste Ebene)')}</Option>
                {parents.map(p => <Option key={p.entry.id} value={p.entry.id} text={p.entry.name}><span style={{ paddingLeft: p.depth * 12 }}>{p.entry.name}</span></Option>)}
              </Dropdown>}
            </Field>}
            {kind === 'tag' && <Field label={t('Farbe')}>
              <input type="color" value={f.color ?? '#7b9cde'} onChange={e => set({ color: e.target.value })} style={{ width: 60, height: 32, border: 'none', background: 'none' }} />
            </Field>}
            {kind !== 'role' && <Field label={t('Zuweisung')} hint={hint[f.match]}>
              <Dropdown value={matchLabel(f.match)} selectedOptions={[f.match]} onOptionSelect={(_, d) => set({ match: d.optionValue as Match })}>
                {MATCHES.filter(m => m !== 'sender' || kind === 'correspondent').map(m => <Option key={m} value={m}>{matchLabel(m)}</Option>)}
              </Dropdown>
            </Field>}
            {needsText && <>
              <Field label={f.match === 'regex' ? t('Ausdruck') : f.match === 'sender' ? t('Adressen oder Domains') : t('Wörter')} required>
                <Input value={f.matchText ?? ''} onChange={(_, d) => set({ matchText: d.value })} />
              </Field>
              {f.match !== 'sender' && <Checkbox checked={f.caseSensitive} label={t('Groß-/Kleinschreibung beachten')} onChange={(_, d) => set({ caseSensitive: !!d.checked })} />}
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !f.name.trim() || (needsText && !f.matchText?.trim())} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
