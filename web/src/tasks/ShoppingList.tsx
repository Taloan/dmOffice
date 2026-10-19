import { Button, Checkbox, Dropdown, Input, Option, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Add20Regular, Dismiss12Regular, Edit16Regular, History16Regular } from '@fluentui/react-icons'
import { useMemo, useState } from 'react'
import { Task } from '../api'
import { t } from '../i18n'
import { composeItem, guessSection, itemOf, parseItem } from './shopParse'

export interface ListMeta {
  type: 'tasks' | 'shop' | 'check'
  sections: string[]
  done?: 'place' | 'bottom' | 'hide' | null
  history: { name: string; unit?: string | null; section?: string | null; count: number }[]
}

/** Einkaufslisten haben Abteilungen, Checklisten Kategorien - gleiche Sache, anderes Wort. */
export const sectionWords = (check: boolean) => check
  ? { one: t('Kategorie'), many: t('Kategorien'), none: t('Ohne Kategorie'), auto: t('Kategorie: automatisch'), add: t('Neue Kategorie'),
      intro: t('In der Reihenfolge, in der du die Liste abarbeitest – mit der Maus ziehen oder mit den Pfeilen verschieben.') }
  : { one: t('Abteilung'), many: t('Abteilungen'), none: t('Ohne Abteilung'), auto: t('Abteilung: automatisch'), add: t('Neue Abteilung'),
      intro: t('In der Reihenfolge, in der du durch den Markt gehst – mit der Maus ziehen oder mit den Pfeilen verschieben. So steht die Liste in der richtigen Reihenfolge.') }

/** Wie abgehakte Eintraege erscheinen - Vorgabe je Typ. */
export const doneMode = (m: ListMeta | null) => m?.done ?? (m?.type === 'check' ? 'place' : 'bottom')

const NONE = '\u0000'

const useStyles = makeStyles({
  add: { display: 'flex', gap: '8px', position: 'relative' },
  suggest: {
    position: 'absolute', top: '100%', left: 0, right: 0, zIndex: 10, marginTop: '2px', maxHeight: '280px', overflowY: 'auto',
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow16, borderRadius: tokens.borderRadiusMedium, padding: '4px',
  },
  sug: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  sugOn: { backgroundColor: tokens.colorNeutralBackground1Selected },
  sugHint: { marginLeft: 'auto', fontSize: '12px', color: tokens.colorNeutralForeground3 },
  // Abteilung/Kategorie als Balken, getoent in der Listenfarbe (wie in der App)
  group: {
    fontSize: '12px', fontWeight: 600, textTransform: 'uppercase', letterSpacing: '.04em', color: tokens.colorNeutralForeground1, margin: '10px 0 2px',
    padding: '5px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorBrandBackground2,
  },
  item: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground2, ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  sel: { outline: `2px solid ${tokens.colorBrandStroke1}` },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  qty: { color: tokens.colorNeutralForeground2, fontVariantNumeric: 'tabular-nums', flexShrink: 0 },
  done: { textDecoration: 'line-through', color: tokens.colorNeutralForeground3 },
  note: { fontSize: '12px', color: tokens.colorNeutralForeground3 },
  hint: { color: tokens.colorNeutralForeground3 },
})

/**
 * Einkaufs- oder Checkliste: Eingabe mit Vorschlaegen, nach Abteilungen gruppiert.
 * Ein Klick auf einen Eintrag hakt ab - und stellt einen abgehakten wieder her.
 */
export function ShoppingList({ meta, tasks, showHidden, selectedId, inputRef, onAdd, onToggle, onEdit, onForget, color }: {
  meta: ListMeta; tasks: Task[]; showHidden: boolean; selectedId?: string
  /** Farbe der Liste fuer die Abteilungsbalken; ohne: Akzentfarbe. */
  color?: string | null
  inputRef: React.RefObject<HTMLInputElement>
  onAdd: (x: { title: string; item: string; qty: string; unit: string; section: string }) => void
  onToggle: (task: Task) => void
  onEdit: (task: Task) => void
  onForget: (item: string) => void
}) {
  const s = useStyles()
  const words = sectionWords(meta.type === 'check')
  const [text, setText] = useState('')
  const [section, setSection] = useState<string>('')   // leer: automatisch
  const [focus, setFocus] = useState(false)
  const [active, setActive] = useState(-1)
  const mode = doneMode(meta)
  const shop = meta.type === 'shop'

  const parsed = parseItem(text)
  const onList = useMemo(() => new Set(tasks.filter(x => !x.completed).map(x => itemOf(x).item.toLowerCase())), [tasks])
  const suggestions = useMemo(() => {
    const q = parsed.item.toLowerCase()
    if (!q) return []
    const starts = meta.history.filter(h => h.name.toLowerCase().startsWith(q))
    const contains = meta.history.filter(h => !h.name.toLowerCase().startsWith(q) && h.name.toLowerCase().includes(q))
    return [...starts, ...contains].slice(0, 8)
  }, [parsed.item, meta.history])

  const add = (item = parsed.item) => {
    if (!item.trim()) return
    const known = meta.history.find(h => h.name.toLowerCase() === item.toLowerCase())
    const unit = parsed.unit || (parsed.qty ? known?.unit ?? '' : '')
    const sec = section || known?.section || guessSection(item, meta.sections) || ''
    const x = { qty: parsed.qty, unit, item: known?.name ?? item.trim() }
    onAdd({ title: composeItem(x), ...x, section: sec })
    setText('')
    setSection('')
    setActive(-1)
  }

  // Gruppen in der Reihenfolge des Laufwegs; was keine (bekannte) Abteilung hat, kommt zuletzt
  const open = tasks.filter(x => !x.completed)
  const done = tasks.filter(x => x.completed)
  const inGroups = mode === 'place' ? tasks : open
  const groups = [...meta.sections, NONE]
    .map(sec => ({
      sec,
      items: inGroups.filter(x => sec === NONE ? !x.section || !meta.sections.includes(x.section) : x.section === sec)
        .sort((a, b) => itemOf(a).item.localeCompare(itemOf(b).item)),
    }))
    .filter(g => g.items.length > 0)
  const tail = mode === 'bottom' || (mode === 'hide' && showHidden) ? done : []

  const row = (x: Task) => {
    const it = itemOf(x)
    return (
      <div key={x.id} className={mergeClasses(s.item, selectedId === x.id && s.sel)} onClick={() => onToggle(x)}
        title={x.completed ? t('Klicken stellt den Eintrag wieder her') : undefined}>
        <Checkbox checked={x.completed} shape="circular" onClick={e => e.stopPropagation()} onChange={() => onToggle(x)}
          aria-label={x.completed ? t('Als nicht erledigt markieren') : t('Als erledigt markieren')} />
        <span className={mergeClasses(s.name, x.completed && s.done)}>
          {it.item}
          {x.description && <span className={s.note}> · {x.description}</span>}
        </span>
        {(it.qty || it.unit) && <span className={mergeClasses(s.qty, x.completed && s.done)}>{[it.qty, it.unit].filter(Boolean).join(' ')}</span>}
        <Button appearance="transparent" size="small" icon={<Edit16Regular />} aria-label={t('Bearbeiten')} title={t('Bearbeiten')}
          onClick={e => { e.stopPropagation(); onEdit(x) }} />
      </div>
    )
  }

  return (
    <>
      <form className={s.add} onSubmit={e => { e.preventDefault(); const sug = suggestions[active]; add(focus && sug && active >= 0 ? sug.name : undefined) }}>
        <Input ref={inputRef} style={{ flex: 1 }} value={text} contentBefore={<Add20Regular />}
          placeholder={shop ? t('Artikel hinzufügen – z. B. „2 kg Kartoffeln“') : t('Eintrag hinzufügen')}
          onChange={(_, d) => { setText(d.value); setActive(-1) }}
          onFocus={() => setFocus(true)} onBlur={() => setTimeout(() => setFocus(false), 150)}
          onKeyDown={e => {
            if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, suggestions.length - 1)) }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, -1)) }
            else if (e.key === 'Escape') setText('')
          }} />
        {meta.sections.length > 0 && (
          <Dropdown style={{ minWidth: 150 }} aria-label={words.one} value={section || words.auto} selectedOptions={[section]}
            onOptionSelect={(_, d) => setSection(d.optionValue ?? '')}>
            <Option value="" text={words.auto}>{t('Automatisch')}</Option>
            {meta.sections.map(x => <Option key={x} value={x}>{x}</Option>)}
          </Dropdown>
        )}
        <Button appearance="primary" type="submit" disabled={!text.trim()}>{t('Hinzufügen')}</Button>
        {focus && suggestions.length > 0 && (
          <div className={s.suggest} role="listbox">
            {suggestions.map((h, i) => (
              <div key={h.name} role="option" aria-selected={i === active} className={mergeClasses(s.sug, i === active && s.sugOn)}
                onMouseDown={e => { e.preventDefault(); add(h.name) }}>
                <History16Regular />
                <span>{h.name}</span>
                {onList.has(h.name.toLowerCase()) && <span className={s.sugHint}>{t('schon auf der Liste')}</span>}
                <span className={s.sugHint}>{h.section ?? ''}</span>
                <Button appearance="transparent" size="small" icon={<Dismiss12Regular />} title={t('Vorschlag vergessen')} aria-label={t('Vorschlag vergessen')}
                  onMouseDown={e => { e.preventDefault(); e.stopPropagation(); onForget(h.name) }} />
              </div>
            ))}
          </div>
        )}
      </form>
      {tasks.length === 0 && <Text className={s.hint}>{shop ? t('Die Liste ist leer.') : t('Noch keine Einträge.')}</Text>}
      {groups.map(g => (
        <div key={g.sec} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          {(meta.sections.length > 0) && <div className={s.group} style={color ? { backgroundColor: `color-mix(in srgb, ${color} 22%, transparent)` } : undefined}>{g.sec === NONE ? words.none : g.sec}</div>}
          {g.items.map(row)}
        </div>
      ))}
      {tail.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <div className={s.group}>{shop ? t('Im Wagen ({n})', { n: tail.length }) : t('Erledigt ({n})', { n: tail.length })}</div>
          {tail.map(row)}
        </div>
      )}
      {mode === 'hide' && !showHidden && done.length > 0 && (
        <Text className={s.hint}>{done.length === 1 ? t('1 abgehakter Eintrag ausgeblendet') : t('{n} abgehakte Einträge ausgeblendet', { n: done.length })}</Text>
      )}
    </>
  )
}
