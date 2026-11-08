import { Input, Portal, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Checkmark16Regular, ChevronDown16Regular } from '@fluentui/react-icons'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
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

/**
 * Auswahl aus einer Liste (Korrespondent, Dokumenttyp, Tags) mit Suche: Tippen filtert (auch nach Oberbegriffen) und zeigt
 * ähnlich geschriebene Einträge; Pfeiltasten und Enter wählen, Escape schließt. Ohne Treffer legt Enter den getippten Namen an
 * (onNew). Die Liste ist begrenzt hoch und rollt.
 */
export function EntrySelect({ entries, kind, value, values, none, placeholder, className, onPick, onPickMany, onNew, prefill }: {
  entries: Entry[]; kind: Kind
  /** Ein Eintrag (Korrespondent, Dokumenttyp) - oder values für mehrere (Tags) */
  value?: string | null; values?: string[]
  /** Eintrag „keiner“ (nur bei einem Eintrag); ohne: nicht angeboten */
  none?: string; placeholder?: string; className?: string
  onPick?: (id: string) => void; onPickMany?: (ids: string[]) => void; onNew?: (name: string) => void
  /** Text, der beim Erscheinen ins Suchfeld kommt und die Liste öffnet (aus einem Rahmen im Dokument) - neu mit anderem key */
  prefill?: string | null
}) {
  const s = useStyles()
  const multi = values !== undefined
  const input = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const [query, setQuery] = useState(prefill ?? '')
  const [open, setOpen] = useState(!!prefill)
  const [active, setActive] = useState(0)
  const [box, setBox] = useState<{ left: number; top: number; width: number; up: boolean } | null>(null)

  useEffect(() => { if (prefill) input.current?.focus() }, []) // eslint-disable-line react-hooks/exhaustive-deps
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
  const found = q ? all.filter(o => fullName(entries, o.entry.id).toLocaleLowerCase().includes(q)) : all
  // Dazu ähnlich geschriebene (Lesefehler), die ähnlichsten zuerst
  const near = q.length >= 3 ? all.filter(o => !found.includes(o)).map(o => ({ o, v: similar(q, o.entry.name.toLocaleLowerCase()) }))
    .filter(x => x.v >= 0.5).sort((a, b) => b.v - a.v).slice(0, 5).map(x => x.o) : []
  const exact = !!q && all.some(o => o.entry.name.toLocaleLowerCase() === q)
  const items: Item[] = [
    ...(none !== undefined && !q ? [{ id: '', label: none, depth: 0 }] : []),
    ...found.map(o => ({ id: o.entry.id, label: q ? fullName(entries, o.entry.id) : o.entry.name, depth: q ? 0 : o.depth })),
    ...near.map(o => ({ id: o.entry.id, label: fullName(entries, o.entry.id), depth: 0, near: true })),
    ...(onNew ? [{ id: '+', label: q && !exact ? t('„{name}“ neu anlegen', { name: query.trim() }) : t('Neu …'), depth: 0 }] : []),
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
    <Input ref={input} className={className} placeholder={open ? shown || placeholder : placeholder} value={open ? query : shown}
      contentAfter={<ChevronDown16Regular style={{ cursor: 'pointer' }} onMouseDown={e => { e.preventDefault(); if (open) close(); else { setOpen(true); input.current?.focus() } }} />}
      onFocus={() => { setOpen(true); setActive(0) }}
      onBlur={close}
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
