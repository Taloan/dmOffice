import { Input, Portal, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Add12Regular, Add16Regular, Checkmark16Regular, ChevronDown12Regular, ChevronDown16Regular, ChevronRight12Regular } from '@fluentui/react-icons'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { featureOn } from '../api'
import { t } from '../i18n'
import { Entry, Kind, fullName, ordered } from './model'

/** Ähnlichkeit zweier Namen (gemeinsame Buchstabenpaare, 0–1) - findet Einträge trotz Lesefehlern der Texterkennung. */
function similar(a: string, b: string) {
  const pairs = (s: string) => { const x = s.replace(/\s+/g, ''); const out: string[] = []; for (let i = 0; i < x.length - 1; i++) out.push(x.slice(i, i + 2)); return out }
  const pa = pairs(a), pb = pairs(b)
  if (!pa.length || !pb.length) return 0
  const rest = [...pb]
  let same = 0
  for (const p of pa) { const i = rest.indexOf(p); if (i >= 0) { same++; rest.splice(i, 1) } }
  return 2 * same / (pa.length + pb.length)
}

const useStyles = makeStyles({
  list: {
    position: 'fixed', zIndex: 1000000, maxHeight: 'min(360px, 50vh)', overflowY: 'auto', backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow16, borderRadius: tokens.borderRadiusMedium, padding: '4px', boxSizing: 'border-box',
    border: `1px solid ${tokens.colorNeutralStroke1}`, color: tokens.colorNeutralForeground1, fontFamily: tokens.fontFamilyBase, fontSize: tokens.fontSizeBase300,
  },
  option: { display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', minHeight: '20px' },
  active: { backgroundColor: tokens.colorNeutralBackground1Hover },
  check: { width: '16px', flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  hint: { padding: '4px 8px 2px', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
})

type Item = { id: string; label: string; depth: number; near?: boolean }

/** Eigenschaften der Auswahl (gleich für Liste und Baum) */
export interface EntrySelectProps {
  entries: Entry[]; kind: Kind
  /** Ein Eintrag (Korrespondent, Dokumenttyp) - oder values für mehrere (Tags) */
  value?: string | null; values?: string[]
  /** Eintrag „keiner“ (nur bei einem Eintrag); ohne: nicht angeboten */
  none?: string; placeholder?: string; className?: string; size?: 'small' | 'medium'
  onPick?: (id: string) => void; onPickMany?: (ids: string[]) => void
  /** Neu anlegen: getippter Name (leer: „Neu …“), parent = übergeordneter Eintrag (nur Baumfeld, dmstreepick) */
  onNew?: (name: string, parent?: string | null) => void
  /** Text, der beim Erscheinen ins Suchfeld kommt und die Liste öffnet (aus einem Rahmen im Dokument) - neu mit anderem key */
  prefill?: string | null
  /** Beim Erscheinen gleich offen (Bearbeiten in der Liste); onClose beim Verlassen des Felds */
  autoFocus?: boolean; onClose?: () => void
  /** Nicht anbieten (etwa der Eintrag selbst und alles darunter beim Wählen des übergeordneten Eintrags); nur Baumfeld */
  exclude?: Set<string>
}

/**
 * Auswahl eines Eintrags: mit dmstreepick als Baumfeld ({@link EntryTreeSelect}), sonst als Liste mit Suche wie bisher.
 */
export function EntrySelect(props: EntrySelectProps) {
  return featureOn('dmstreepick') ? <EntryTreeSelect {...props} /> : <EntryListSelect {...props} />
}

/**
 * Auswahl aus einer Liste (Korrespondent, Dokumenttyp, Tags) mit Suche: Tippen filtert (auch nach Oberbegriffen) und zeigt
 * ähnlich geschriebene Einträge; Pfeiltasten und Enter wählen, Escape schließt. Ohne Treffer legt Enter den getippten Namen an
 * (onNew). Die Liste ist begrenzt hoch und rollt.
 */
function EntryListSelect({ entries, kind, value, values, none, placeholder, className, size, onPick, onPickMany, onNew, prefill, autoFocus, onClose }: EntrySelectProps) {
  const s = useStyles()
  const multi = values !== undefined
  const input = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const [query, setQuery] = useState(prefill ?? '')
  const [open, setOpen] = useState(!!prefill)
  const [active, setActive] = useState(0)
  // Beim Öffnen gewählte Tags (stehen oben in der Liste)
  const [pinned, setPinned] = useState<string[]>([])
  useEffect(() => { if (open) setPinned(multi ? values : []) }, [open]) // eslint-disable-line react-hooks/exhaustive-deps
  const [box, setBox] = useState<{ left: number; top: number; width: number; up: boolean } | null>(null)

  useEffect(() => { if (prefill || autoFocus) input.current?.focus() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Unter dem Feld, bei wenig Platz darüber; beim Rollen mitwandern
  useLayoutEffect(() => {
    if (!open) return
    const place = () => {
      const r = input.current?.parentElement?.getBoundingClientRect()
      if (!r) return
      const up = window.innerHeight - r.bottom < 240 && r.top > window.innerHeight - r.bottom
      setBox({ left: r.left, top: up ? r.top - 4 : r.bottom + 4, width: Math.max(r.width, 220), up })
    }
    place()
    window.addEventListener('scroll', place, true)
    window.addEventListener('resize', place)
    return () => { window.removeEventListener('scroll', place, true); window.removeEventListener('resize', place) }
  }, [open])

  const shown = multi ? values.map(id => entries.find(e => e.id === id)?.name ?? '').filter(Boolean).join(', ') : value ? fullName(entries, value) : ''
  const q = query.trim().toLocaleLowerCase()
  const all = ordered(entries, kind)
  const matching = q ? all.filter(o => fullName(entries, o.entry.id).toLocaleLowerCase().includes(q)) : all
  // Mehrfachauswahl (Tags): die beim Öffnen gewählten zuerst, mit vollem Namen - bleibt beim Anklicken stehen, damit die Liste nicht springt
  const pinnedItems = multi ? matching.filter(o => pinned.includes(o.entry.id)) : []
  const found = pinnedItems.length ? [...pinnedItems, ...matching.filter(o => !pinned.includes(o.entry.id))] : matching
  // Dazu ähnlich geschriebene (Lesefehler), die ähnlichsten zuerst
  const near = q.length >= 3 ? all.filter(o => !found.includes(o)).map(o => ({ o, v: similar(q, o.entry.name.toLocaleLowerCase()) }))
    .filter(x => x.v >= 0.5).sort((a, b) => b.v - a.v).slice(0, 5).map(x => x.o) : []
  const exact = !!q && all.some(o => o.entry.name.toLocaleLowerCase() === q)
  const items: Item[] = [
    ...(none !== undefined && !q ? [{ id: '', label: none, depth: 0 }] : []),
    ...found.map(o => pinnedItems.includes(o) ? { id: o.entry.id, label: fullName(entries, o.entry.id), depth: 0 }
      : { id: o.entry.id, label: q ? fullName(entries, o.entry.id) : o.entry.name, depth: q ? 0 : o.depth }),
    ...near.map(o => ({ id: o.entry.id, label: fullName(entries, o.entry.id), depth: 0, near: true })),
    ...(onNew && (q || !autoFocus) ? [{ id: '+', label: q && !exact ? t('„{name}“ neu anlegen', { name: query.trim() }) : t('Neu …'), depth: 0 }] : []),
  ]
  const at = Math.min(active, Math.max(0, items.length - 1))
  useEffect(() => { listRef.current?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }) }, [at, open])

  const close = () => { setOpen(false); setQuery('') }
  const choose = (it: Item) => {
    if (it.id === '+') { onNew?.(query.trim()); close(); input.current?.blur(); return }
    if (multi) {
      onPickMany?.(values.includes(it.id) ? values.filter(x => x !== it.id) : [...values, it.id])
      setQuery('')
      return
    }
    onPick?.(it.id)
    close()
    input.current?.blur()
  }
  const selected = (id: string) => multi ? values.includes(id) : (value ?? '') === id

  return <>
    <Input ref={input} className={className} size={size} placeholder={open ? shown || placeholder : placeholder} value={open ? query : shown}
      contentAfter={<ChevronDown16Regular style={{ cursor: 'pointer' }} onMouseDown={e => { e.preventDefault(); if (open) close(); else { setOpen(true); input.current?.focus() } }} />}
      onFocus={() => { setOpen(true); setActive(0) }}
      onBlur={() => { close(); onClose?.() }}
      onChange={(_, d) => { setQuery(d.value); setOpen(true); setActive(0) }}
      onKeyDown={e => {
        if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive(a => Math.min(a + 1, items.length - 1)) }
        else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)) }
        else if (e.key === 'Enter') {
          e.preventDefault()
          // Mit Suchtext: genau passender Eintrag, sonst der markierte; ohne Treffer neu anlegen
          const hit = q ? items.find(it => it.id !== '+' && it.id !== '' && entries.find(x => x.id === it.id)?.name.toLocaleLowerCase() === q) : undefined
          const it = hit ?? items[at]
          if (it) choose(it)
        }
        else if (e.key === 'Escape') { if (open) { e.preventDefault(); e.stopPropagation(); close() } }
      }} />
    {/* Fluents Portal: die Liste bekommt Farben und Schrift des Themas (unter document.body fehlen sie sonst) */}
    {open && box && <Portal>
      <div ref={listRef} role="listbox" className={s.list}
        style={{ left: box.left, width: box.width, ...(box.up ? { bottom: window.innerHeight - box.top } : { top: box.top }) }}
        // Klicks in die Liste nehmen dem Feld nicht den Fokus
        onMouseDown={e => e.preventDefault()}>
        {items.map((it, i) => <div key={it.id || '-'} role="option" aria-selected={selected(it.id)} data-active={i === at}
          className={mergeClasses(s.option, i === at && s.active)} onMouseEnter={() => setActive(i)} onClick={() => choose(it)}>
          <span className={s.check}>{it.id !== '+' && selected(it.id) && <Checkmark16Regular />}</span>
          <span className={it.near ? s.muted : undefined} style={{ paddingLeft: it.depth * 12 }}>{it.label}</span>
        </div>)}
        {near.length > 0 && <div className={s.hint}>{t('Grau: ähnlich geschrieben')}</div>}
      </div></Portal>}
  </>
}

// ---------------- Baumfeld (dmstreepick) ----------------

const useTreeStyles = makeStyles({
  pop: {
    position: 'fixed', zIndex: 1000000, display: 'flex', flexDirection: 'column', backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow16, borderRadius: tokens.borderRadiusMedium, boxSizing: 'border-box',
    border: `1px solid ${tokens.colorNeutralStroke1}`, color: tokens.colorNeutralForeground1, fontFamily: tokens.fontFamilyBase, fontSize: tokens.fontSizeBase300,
  },
  list: { overflowY: 'auto', padding: '4px', minHeight: 0 },
  row: { display: 'flex', alignItems: 'center', gap: '2px', padding: '0 4px 0 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', minHeight: '32px' },
  active: { backgroundColor: tokens.colorNeutralBackground1Hover },
  check: { width: '16px', flexShrink: 0, display: 'inline-flex' },
  twisty: { width: '20px', height: '20px', flexShrink: 0, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', borderRadius: tokens.borderRadiusSmall,
    color: tokens.colorNeutralForeground3, ':hover': { backgroundColor: tokens.colorNeutralBackground1Pressed } },
  label: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', paddingLeft: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  create: { color: tokens.colorBrandForeground1 },
  // „+“ je Zeile: sichtbar an der markierten Zeile, auf Touch-Geräten immer
  plus: { width: '24px', height: '24px', flexShrink: 0, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', borderRadius: tokens.borderRadiusSmall,
    opacity: 0, color: tokens.colorNeutralForeground2, ':hover': { backgroundColor: tokens.colorNeutralBackground1Pressed, color: tokens.colorBrandForeground1 },
    '@media (hover: none)': { opacity: 1 } },
  plusOn: { opacity: 1 },
  hint: { padding: '6px 8px', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  divider: { height: '1px', margin: '4px 0', backgroundColor: tokens.colorNeutralStroke2, flexShrink: 0 },
})

type TreeRow = {
  /** new: „Neu …“, none: „keiner“, entry: Eintrag, create/createUnder: getippten Namen anlegen, hint: nur Text */
  key: string; type: 'new' | 'none' | 'entry' | 'create' | 'createUnder' | 'hint'
  id?: string; label: string; depth: number; kids?: boolean; prefix?: string; near?: boolean
}

/**
 * Baumfeld für Einträge mit Oberbegriffen: zeigt den Wert als Pfad („50_Wohnen › Vodafone“). Aufgeklappt ohne Suchtext ein
 * Baum (oberste Ebene, der Zweig des Werts offen), mit Suchtext die Treffer flach mit Pfad. Tasten wie im Explorer:
 * ↑/↓ bewegen, → aufklappen bzw. zum ersten Untereintrag, ← zuklappen bzw. zum übergeordneten Eintrag, Enter wählt, Escape schließt.
 * „Neu …“ steht oben; „+“ an einer Zeile legt darunter an, ein getippter Name ohne Treffer lässt sich gleich anlegen.
 * Der Fokus bleibt im Feld (Dialoge halten ihn sonst fest), die Liste hängt per Portal am Feld und passt in den sichtbaren Bereich.
 */
function EntryTreeSelect({ entries, kind, value, values, none, placeholder, className, size, onPick, onPickMany, onNew, prefill, autoFocus, onClose, exclude }: EntrySelectProps) {
  const s = useTreeStyles()
  const multi = values !== undefined
  // Rollen haben keine Oberbegriffe
  const tree = kind !== 'role'
  const input = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const [query, setQuery] = useState(prefill ?? '')
  const [open, setOpen] = useState(!!prefill)
  const [activeKey, setActiveKey] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  // „+“ an einer Zeile: der getippte Name kommt darunter
  const [under, setUnder] = useState<string | null>(null)
  // Beim ersten getippten Zeichen markierter Eintrag: „‚XY‘ unter … anlegen“
  const [anchor, setAnchor] = useState<string | null>(null)
  const [box, setBox] = useState<{ left: number; width: number; top?: number; bottom?: number; maxHeight: number } | null>(null)

  const byId = new Map(entries.map(e => [e.id, e]))
  const all = ordered(entries, kind).filter(o => !exclude?.has(o.entry.id))
  const kids = new Map<string | null, Entry[]>()
  for (const o of all) { const p = o.entry.parent; if (!kids.has(p)) kids.set(p, []); kids.get(p)!.push(o.entry) }
  const ancestors = (id: string | null | undefined) => {
    const out: string[] = []
    for (let e = id ? byId.get(id) : undefined; e?.parent && out.length < 20; e = byId.get(e.parent)) out.push(e.parent)
    return out
  }
  const chosen = multi ? values : value ? [value] : []

  useEffect(() => { if (prefill || autoFocus) input.current?.focus() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Beim Öffnen: Zweig des Werts aufklappen, Wert markieren
  useEffect(() => {
    if (!open) return
    setExpanded(new Set(chosen.flatMap(ancestors)))
    setActiveKey(chosen.length ? 'e:' + chosen[0] : null)
    setUnder(null)
  }, [open]) // eslint-disable-line react-hooks/exhaustive-deps
  // Unter dem Feld, bei wenig Platz darüber; Höhe auf den sichtbaren Bereich begrenzt, beim Rollen mitwandern
  useLayoutEffect(() => {
    if (!open) return
    const place = () => {
      const r = input.current?.parentElement?.getBoundingClientRect()
      if (!r) return
      const vv = window.visualViewport
      const vh = vv ? vv.height + vv.offsetTop : window.innerHeight
      const vw = vv ? vv.width + vv.offsetLeft : window.innerWidth
      const below = vh - r.bottom - 8, above = r.top - 8
      const up = below < 260 && above > below
      const width = Math.min(Math.max(r.width, 300), vw - 16)
      const left = Math.max(8, Math.min(r.left, vw - width - 8))
      const maxHeight = Math.max(120, Math.min(up ? above : below, 520))
      const next = up ? { left, width, bottom: window.innerHeight - r.top + 4, maxHeight } : { left, width, top: r.bottom + 4, maxHeight }
      setBox(b => b && b.left === next.left && b.width === next.width && b.top === next.top && b.bottom === next.bottom && b.maxHeight === next.maxHeight ? b : next)
    }
    // Jedes Bild nachmessen: Dialoge blenden mit Bewegung ein, Rollen und Größenänderungen verschieben das Feld
    let frame = 0
    const loop = () => { place(); frame = requestAnimationFrame(loop) }
    loop()
    return () => cancelAnimationFrame(frame)
  }, [open])

  const shown = multi ? values.map(id => byId.get(id)?.name ?? '').filter(Boolean).join(', ') : value ? fullName(entries, value) : ''
  const typed = query.trim()
  const q = typed.toLocaleLowerCase()
  const same = (e: Entry) => e.name.toLocaleLowerCase() === q
  const rows: TreeRow[] = []
  if (under) {
    // Untereintrag anlegen: nur den Namen
    const parentName = byId.get(under)?.name ?? ''
    const there = (kids.get(under) ?? []).find(same)
    if (!q) rows.push({ key: 'hint', type: 'hint', label: t('Neuer Untereintrag unter „{name}“', { name: parentName }), depth: 0 })
    else if (there) rows.push({ key: 'e:' + there.id, type: 'entry', id: there.id, label: there.name, prefix: fullName(entries, under), depth: 0 })
    else rows.push({ key: 'cu', type: 'createUnder', label: t('„{name}“ unter „{parent}“ anlegen', { name: typed, parent: parentName }), depth: 0 })
  } else if (q) {
    // Anlegen oben, darunter die Treffer flach mit Pfad und ähnlich geschriebene (Lesefehler der Texterkennung)
    const hits = all.filter(o => fullName(entries, o.entry.id).toLocaleLowerCase().includes(q))
    if (onNew) {
      if (!(kids.get(null) ?? []).some(same)) rows.push({ key: 'c', type: 'create', label: t('„{name}“ anlegen', { name: typed }), depth: 0 })
      const a = tree && anchor && byId.has(anchor) && !exclude?.has(anchor) ? anchor : null
      if (a && !(kids.get(a) ?? []).some(same)) rows.push({ key: 'cu', type: 'createUnder', label: t('„{name}“ unter „{parent}“ anlegen', { name: typed, parent: byId.get(a)!.name }), depth: 0 })
    }
    const path = (e: Entry) => e.parent ? fullName(entries, e.parent) : undefined
    for (const o of hits) rows.push({ key: 'e:' + o.entry.id, type: 'entry', id: o.entry.id, label: o.entry.name, prefix: path(o.entry), depth: 0 })
    if (q.length >= 3) all.filter(o => !hits.includes(o)).map(o => ({ o, v: similar(q, o.entry.name.toLocaleLowerCase()) })).filter(x => x.v >= 0.5)
      .sort((a, b) => b.v - a.v).slice(0, 5)
      .forEach(({ o }) => rows.push({ key: 'e:' + o.entry.id, type: 'entry', id: o.entry.id, label: o.entry.name, prefix: path(o.entry), depth: 0, near: true }))
    if (!rows.some(r => r.type === 'entry') && !rows.length) rows.push({ key: 'hint', type: 'hint', label: t('Kein Treffer'), depth: 0 })
  } else {
    // „Neu …“ oben (nicht beim Bearbeiten in der Liste: dort fehlt der Dialog), „keiner“, dann der Baum
    if (onNew && !autoFocus) rows.push({ key: 'new', type: 'new', label: t('Neu …'), depth: 0 })
    if (none !== undefined && !multi) rows.push({ key: 'none', type: 'none', label: none, depth: 0 })
    const walk = (parent: string | null, depth: number) => {
      for (const e of kids.get(parent) ?? []) {
        const has = (kids.get(e.id)?.length ?? 0) > 0
        rows.push({ key: 'e:' + e.id, type: 'entry', id: e.id, label: e.name, depth, kids: has })
        if (has && expanded.has(e.id) && depth < 20) walk(e.id, depth + 1)
      }
    }
    walk(null, 0)
    if (!kids.get(null)?.length) rows.push({ key: 'hint', type: 'hint', label: t('Noch keine Einträge.'), depth: 0 })
  }
  const pickable = (r: TreeRow) => r.type !== 'hint'
  // Markiert: die gemerkte Zeile, sonst bei Suchtext der erste Treffer bzw. „anlegen“, sonst die erste Zeile nach „Neu …“
  let at = rows.findIndex(r => r.key === activeKey && pickable(r))
  if (at < 0) at = q ? Math.max(rows.findIndex(r => r.type === 'entry' && !r.near), rows.findIndex(pickable))
    : rows.findIndex(r => pickable(r) && r.type !== 'new')
  if (at < 0) at = rows.findIndex(pickable)
  const cur = at >= 0 ? rows[at] : undefined
  // Neue Suche beginnt oben, damit die Zeilen zum Anlegen sichtbar bleiben
  useLayoutEffect(() => { if (listRef.current) listRef.current.scrollTop = 0 }, [query, under])
  useEffect(() => { listRef.current?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }) }, [cur?.key, open])

  const close = () => { setOpen(false); setQuery(''); setUnder(null); setAnchor(null) }
  const toggle = (id: string, on?: boolean) => setExpanded(x => {
    const n = new Set(x)
    if (on ?? !n.has(id)) n.add(id); else n.delete(id)
    return n
  })
  const startUnder = (id: string) => { setUnder(id); setQuery(''); setActiveKey(null); input.current?.focus() }
  const choose = (r: TreeRow) => {
    if (r.type === 'hint') return
    if (r.type === 'new') { onNew?.(''); close(); input.current?.blur(); return }
    if (r.type === 'create' || r.type === 'createUnder') {
      if (!typed) return
      onNew?.(typed, r.type === 'createUnder' ? under ?? anchor : null)
      close(); input.current?.blur(); return
    }
    const id = r.type === 'none' ? '' : r.id!
    if (multi) {
      onPickMany?.(values.includes(id) ? values.filter(x => x !== id) : [...values, id])
      // Zurück zum Baum, die gewählte Zeile bleibt sichtbar und markiert
      setQuery(''); setUnder(null); setExpanded(x => new Set([...x, ...ancestors(id)])); setActiveKey('e:' + id)
      return
    }
    onPick?.(id)
    close()
    input.current?.blur()
  }
  const move = (step: number) => {
    for (let i = at + step; i >= 0 && i < rows.length; i += step) if (pickable(rows[i])) { setActiveKey(rows[i].key); return }
  }
  const isChosen = (r: TreeRow) => r.type === 'none' ? !multi && !value : r.type === 'entry' && chosen.includes(r.id!)

  return <>
    <Input ref={input} className={className} size={size} value={open ? query : shown} aria-expanded={open}
      placeholder={open ? (under ? t('Name des Untereintrags') : shown || placeholder) : placeholder}
      title={!open && shown ? shown : undefined}
      // Nach Escape bleibt der Fokus im Feld: ein Klick öffnet wieder
      onClick={() => { if (!open) setOpen(true) }}
      contentAfter={<ChevronDown16Regular style={{ cursor: 'pointer' }} onMouseDown={e => { e.preventDefault(); if (open) close(); else { setOpen(true); input.current?.focus() } }} />}
      onFocus={() => setOpen(true)}
      onBlur={() => { close(); onClose?.() }}
      onChange={(_, d) => {
        // Beim ersten Zeichen den markierten Eintrag merken: dort lässt sich der Name auch als Untereintrag anlegen
        // Geschlossen steht der Wert im Feld: Getipptes beginnt eine neue Suche
        const text = !open && shown && d.value.startsWith(shown) ? d.value.slice(shown.length) : d.value
        if (!query.trim() && text.trim() && !under) setAnchor(!open ? (multi ? null : value ?? null)
          : cur?.type === 'entry' && !cur.near && cur.key === activeKey ? cur.id! : null)
        setQuery(text); setOpen(true); setActiveKey(null)
      }}
      onKeyDown={e => {
        if (e.key === 'ArrowDown') { e.preventDefault(); if (!open) setOpen(true); else move(1) }
        else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1) }
        // → und ← nur im Baum (mit Suchtext bewegen sie die Schreibmarke)
        else if (open && !q && !under && cur?.type === 'entry' && (e.key === 'ArrowRight' || e.key === 'ArrowLeft')) {
          e.preventDefault()
          const id = cur.id!
          if (e.key === 'ArrowRight') {
            if (cur.kids && !expanded.has(id)) toggle(id, true)
            else if (cur.kids) { const first = kids.get(id)?.[0]; if (first) setActiveKey('e:' + first.id) }
          } else if (cur.kids && expanded.has(id)) toggle(id, false)
          else { const p = byId.get(id)?.parent; if (p && !exclude?.has(p)) setActiveKey('e:' + p) }
        }
        // Geschlossen gehört Enter dem Formular (etwa „Übernehmen und schließen“)
        else if (e.key === 'Enter' && open) {
          e.preventDefault()
          // Mit Suchtext: genau passender Eintrag vor dem markierten
          const hit = q && !under ? rows.find(r => r.type === 'entry' && !r.near && byId.get(r.id!) && same(byId.get(r.id!)!)) : undefined
          const r = hit ?? cur
          if (r) choose(r)
        }
        else if (e.key === 'Escape') {
          if (!open) return
          e.preventDefault(); e.stopPropagation()
          if (under) { setUnder(null); setQuery('') } else close()
        }
      }} />
    {open && box && <Portal>
      <div className={s.pop} style={{ left: box.left, width: box.width, top: box.top, bottom: box.bottom, maxHeight: box.maxHeight }}
        // Klicks in die Liste nehmen dem Feld nicht den Fokus
        onMouseDown={e => e.preventDefault()}>
        <div ref={listRef} role={tree ? 'tree' : 'listbox'} aria-multiselectable={multi || undefined} className={s.list}>
          {rows.map((r, i) => {
            if (r.type === 'hint') return <div key={r.key} className={s.hint}>{r.label}</div>
            const entry = r.type === 'entry'
            const full = entry ? (r.prefix ? r.prefix + ' › ' : '') + r.label : r.label
            return <div key={r.key}>
              <div role={tree ? 'treeitem' : 'option'} aria-selected={isChosen(r)} aria-expanded={r.kids ? expanded.has(r.id!) : undefined} aria-level={tree && entry ? r.depth + 1 : undefined}
                data-active={i === at} title={full} className={mergeClasses(s.row, i === at && s.active)}
                onMouseEnter={() => setActiveKey(r.key)} onClick={() => choose(r)}>
                <span className={s.check}>{isChosen(r) && <Checkmark16Regular />}</span>
                {tree && !q && !under && <span style={{ width: r.depth * 16, flexShrink: 0 }} />}
                {tree && !q && !under && (r.kids
                  ? <span className={s.twisty} role="button" aria-label={expanded.has(r.id!) ? t('Zuklappen') : t('Aufklappen')}
                    onClick={e => { e.stopPropagation(); toggle(r.id!); setActiveKey(r.key) }}>
                    {expanded.has(r.id!) ? <ChevronDown12Regular /> : <ChevronRight12Regular />}</span>
                  : <span className={s.twisty} style={{ visibility: 'hidden' }} />)}
                <span className={mergeClasses(s.label, r.near && s.muted, (r.type === 'new' || r.type === 'create' || r.type === 'createUnder') && s.create)}>
                  {(r.type === 'create' || r.type === 'createUnder') && <Add12Regular style={{ marginRight: 6, verticalAlign: 'middle' }} />}
                  {r.prefix && <span className={s.muted}>{r.prefix} › </span>}{r.label}
                </span>
                {entry && tree && onNew && !r.near && <span className={mergeClasses(s.plus, i === at && s.plusOn)} role="button" aria-label={t('Neuer Untereintrag …')}
                  title={t('Neuer Untereintrag …')} onClick={e => { e.stopPropagation(); startUnder(r.id!) }}><Add16Regular /></span>}
              </div>
              {/* „Neu …“ und „keiner“ vom Baum absetzen */}
              {!q && !under && (r.type === 'new' || r.type === 'none') && rows[i + 1]?.type === 'entry' && <div className={s.divider} />}
            </div>
          })}
          {q && rows.some(r => r.near) && <div className={s.hint}>{t('Grau: ähnlich geschrieben')}</div>}
        </div>
      </div></Portal>}
  </>
}
