import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Menu, MenuDivider, MenuItem, MenuList,
  MenuPopover, Option, Text, Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowMove20Regular, Delete20Regular, DocumentSearch20Regular, Edit20Regular, MoreHorizontal20Regular, Rename20Regular, Search20Regular,
} from '@fluentui/react-icons'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { askConfirm, askText } from '../shell/context'
import { ColDef, useGridColumns } from '../shell/GridColumns'
import { useIsMobile } from '../shell/mobile'
import { Entry, Kind, kindLabel, Match, matchLabel, ordered } from './model'

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px 20px', minHeight: 0, flex: 1, '@media (max-width: 767px)': { padding: '8px 12px' } },
  bar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  table: { flex: 1, minHeight: 0, overflowY: 'auto' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(160px, 2fr) minmax(110px, 1fr) 70px minmax(90px, 1fr) auto', alignItems: 'center', gap: '12px',
    padding: '4px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr) auto' } },
  head: { fontSize: '12px', color: tokens.colorNeutralForeground3, minHeight: '30px' },
  name: { display: 'flex', alignItems: 'center', gap: '8px', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: 'pointer',
    color: tokens.colorBrandForeground1, ':hover': { textDecoration: 'underline' } },
  chip: { display: 'inline-block', width: '12px', height: '12px', borderRadius: '3px', flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '2px', justifySelf: 'end' },
  cell: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  branch: { color: tokens.colorNeutralForeground4, flexShrink: 0 },
  dropOn: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-2px', borderRadius: tokens.borderRadiusMedium },
})

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
export function useEntryActions(entries: Entry[], onChanged: () => void) {
  const { fail, notify } = useApp()
  const [menu, setMenu] = useState<MenuAt | null>(null)
  const [edit, setEdit] = useState<{ kind: Kind; entry: Entry | null; parent: string | null } | null>(null)
  const [moving, setMoving] = useState<Entry | null>(null)
  const [over, setOver] = useState<string | null>(null)

  const put = (e: Entry, body: Record<string, unknown>) => api.put(`/api/dms/lists/${e.id}`, body).then(onChanged).catch(fail)
  const rename = async (e: Entry) => {
    const n = (await askText(t('Umbenennen'), e.name))?.trim()
    if (n && n !== e.name) put(e, { name: n })
  }
  const moveTo = (e: Entry, parent: string | null) => {
    if (parent === e.parent) return
    if (parent && below(entries, e.id).has(parent)) { notify(t('Ein Eintrag kann nicht unter sich selbst liegen.')); return }
    put(e, { parent: parent ?? '' })
  }
  const remove = async (e: Entry) => {
    if (!await askConfirm(t('{kind} löschen', { kind: kindLabel(e.kind) }), e.count === 1 ? t('„{name}“ wird gelöscht und an 1 Dokument entfernt. Das Dokument bleibt; Untereinträge rücken eine Ebene hoch.', { name: e.name }) : t('„{name}“ wird gelöscht und an {n} Dokumenten entfernt. Die Dokumente bleiben; Untereinträge rücken eine Ebene hoch.', { name: e.name, n: e.count }), t('Löschen'))) return
    try { await api.del(`/api/dms/lists/${e.id}`); onChanged() } catch (x) { fail(x) }
  }

  /** Mehrere auf einmal; true: gelöscht. */
  const removeMany = async (list: Entry[]) => {
    const docs = list.reduce((n, e) => n + e.count, 0)
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
      positioning={{ target: { getBoundingClientRect: () => ({ x: m.x, y: m.y, top: m.y, left: m.x, bottom: m.y, right: m.x, width: 0, height: 0 }) } as any }}>
      <MenuPopover>
        <MenuList>
          {m.entry ? <>
            <MenuItem icon={<Add20Regular />} onClick={() => newEntry(m.kind, m.entry!.id)}>{t('Neuer Untereintrag …')}</MenuItem>
            <MenuItem icon={<Rename20Regular />} onClick={() => rename(m.entry!)}>{t('Umbenennen …')}</MenuItem>
            <MenuItem icon={<ArrowMove20Regular />} onClick={() => setMoving(m.entry)}>{t('Verschieben unter …')}</MenuItem>
            <MenuItem icon={<Edit20Regular />} onClick={() => editEntry(m.entry!)}>{t('Bearbeiten …')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<Delete20Regular />} onClick={() => remove(m.entry!)}>{t('Löschen …')}</MenuItem>
          </> : <MenuItem icon={<Add20Regular />} onClick={() => newEntry(m.kind)}>{t('Neuer Eintrag …')}</MenuItem>}
          {m.extra?.length ? <><MenuDivider />{m.extra.map(x => <MenuItem key={x.label} onClick={x.onClick}>{x.label}</MenuItem>)}</> : null}
        </MenuList>
      </MenuPopover>
    </Menu>}
    {edit && <EditEntry kind={edit.kind} entries={entries} entry={edit.entry} initialParent={edit.parent}
      onClose={saved => { setEdit(null); if (saved) onChanged() }} />}
    {moving && <MoveEntry entry={moving} entries={entries} onClose={parent => { const e = moving; setMoving(null); if (parent !== undefined) moveTo(e, parent) }} />}
  </>
  return { removeMany, openMenu, newEntry, editEntry, remove, dnd, over, ui }
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
              <Dropdown value={parent ? options.find(p => p.entry.id === parent)?.entry.name ?? '' : t('Keiner (oberste Ebene)')} selectedOptions={[parent ?? '']}
                onOptionSelect={(_, d) => setParent(d.optionValue || null)}>
                <Option value="">{t('Keiner (oberste Ebene)')}</Option>
                {options.map(p => <Option key={p.entry.id} value={p.entry.id} text={p.entry.name}><span style={{ paddingLeft: p.depth * 12 }}>{p.entry.name}</span></Option>)}
              </Dropdown>
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

/** Geschwister im Baum nach der Spalte ordnen; der Baum selbst bleibt. */
const listCompare = (key: string | undefined, desc: boolean | undefined) => {
  const cmp = (a: Entry, b: Entry): number => {
    switch (key) {
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
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const act = useEntryActions(entries, onChanged)
  // Spalten wie im Explorer; Sortieren ordnet die Einträge jeder Ebene, der Baum bleibt
  const grid = useGridColumns('dms-lists', LIST_COLS, LIST_DEFAULT, '', 'minmax(max-content, 1fr)')
  // Als Baum (eingerückt); beim Filtern bleiben die übergeordneten Einträge der Treffer stehen
  const all = ordered(entries, kind, listCompare(grid.sort?.key, grid.sort?.desc))
  const hits = new Set(all.filter(r => !filter || r.entry.name.toLowerCase().includes(filter.toLowerCase())).map(r => r.entry.id))
  const shown = new Set<string>()
  for (const id of hits) for (let e = entries.find(x => x.id === id); e && !shown.has(e.id); e = entries.find(x => x.id === e!.parent)) shown.add(e.id)
  const rows = all.filter(r => shown.has(r.entry.id))
  const suggestion = async () => {
    try { await api.post('/api/dms/lists/suggestion?lang=' + lang); onChanged() } catch (x) { fail(x) }
  }
  const pickedList = rows.map(r => r.entry).filter(e => picked.has(e.id))
  const empty = rows.map(r => r.entry).filter(e => e.count === 0)
  const pick = (id: string, on: boolean) => setPicked(p => { const x = new Set(p); if (on) x.add(id); else x.delete(id); return x })
  return (
    <div className={s.root}>
      <div className={s.bar}>
        <Input contentBefore={<Search20Regular />} placeholder={t('Filtern nach Name')} value={filter} onChange={(_, d) => setFilter(d.value)} style={{ minWidth: 240 }} />
        <Button icon={<Add20Regular />} onClick={() => act.newEntry(kind)}>{t('Neuer Eintrag …')}</Button>
        {kind === 'doctype' && !entries.some(e => e.kind === 'doctype') && <Button onClick={suggestion}>{t('Vorschlag übernehmen')}</Button>}
        {empty.length > 0 && <Button onClick={() => setPicked(new Set(empty.map(e => e.id)))}>{t('Leere auswählen ({n})', { n: empty.length })}</Button>}
        {pickedList.length > 0 && <>
          <Button icon={<Delete20Regular />} onClick={async () => { if (await act.removeMany(pickedList)) setPicked(new Set()) }}>{t('{n} Einträge löschen', { n: pickedList.length })}</Button>
          <Button appearance="subtle" onClick={() => setPicked(new Set())}>{t('Auswahl aufheben')}</Button>
        </>}
      </div>
      <Text size={200} className={s.muted}>{t('Untereinträge über Rechtsklick bzw. „…“ an einem Eintrag oder durch Ziehen eines Eintrags auf einen anderen.')}</Text>
      <div className={s.table} onContextMenu={e => act.openMenu(e, kind, null)}>
        <div className={`${s.row} ${s.head}`} style={mobile ? undefined : { gridTemplateColumns: grid.template, minWidth: 'max-content' }}
          onContextMenu={x => { x.stopPropagation(); grid.openMenu(x) }}>
          {mobile ? <><span>{t('Name')}</span><span /></> : <>{grid.cells}<span /></>}
        </div>
        {grid.columnMenu}
        {rows.map(({ entry: e, depth }) => (
          <div key={e.id} className={mergeClasses(s.row, act.over === e.id && s.dropOn)} style={mobile ? undefined : { gridTemplateColumns: grid.template, minWidth: 'max-content' }}
            onContextMenu={x => act.openMenu(x, kind, e)} {...act.dnd(e)}>
            {(mobile ? ['name'] : grid.cols.map(c => c.key)).map(k => {
              switch (k) {
                case 'name': return (
                  <span key={k} className={s.name} style={{ paddingLeft: depth * 20 }} onClick={() => onShow(e)} title={t('Dokumente zeigen')}>
                    <span onClick={x => x.stopPropagation()} style={{ display: 'inline-flex' }}>
                      <Checkbox checked={picked.has(e.id)} onChange={(_, d) => pick(e.id, !!d.checked)} aria-label={t('{name} auswählen', { name: e.name })} />
                    </span>
                    {depth > 0 && <span className={s.branch}>└</span>}
                    {kind === 'tag' && <span className={s.chip} style={{ backgroundColor: e.color ?? tokens.colorNeutralBackground5 }} />}
                    {hits.has(e.id) ? e.name : <span className={s.muted}>{e.name}</span>}
                  </span>)
                case 'match': return <span key={k} className={s.cell}>{matchLabel(e.match)}{e.matchText ? <Text size={200} className={s.muted}> · {e.matchText}</Text> : null}</span>
                case 'count': return <span key={k} className={s.cell}>{e.count}</span>
                case 'lastUsed': return <span key={k} className={s.cell}>{e.lastUsed ? new Date(e.lastUsed).toLocaleDateString() : ''}</span>
                default: return <span key={k} className={s.cell}>{e.parent ? entries.find(x => x.id === e.parent)?.name ?? '' : ''}</span>
              }
            })}
            <span className={s.actions}>
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
    </div>
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
              <Dropdown value={f.parent ? parents.find(p => p.entry.id === f.parent)?.entry.name ?? '' : t('Keiner (oberste Ebene)')} selectedOptions={[f.parent ?? '']}
                onOptionSelect={(_, d) => set({ parent: d.optionValue || null })}>
                <Option value="">{t('Keiner (oberste Ebene)')}</Option>
                {parents.map(p => <Option key={p.entry.id} value={p.entry.id} text={p.entry.name}><span style={{ paddingLeft: p.depth * 12 }}>{p.entry.name}</span></Option>)}
              </Dropdown>
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
