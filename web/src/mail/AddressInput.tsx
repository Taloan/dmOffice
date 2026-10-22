import { Avatar, makeStyles, tokens, Tag, TagGroup } from '@fluentui/react-components'
import { useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { useDebounced } from '../shell/hooks'
import { t } from '../i18n'

const useStyles = makeStyles({
  root: { position: 'relative', flex: 1, display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '4px', minHeight: '32px', padding: '2px 0' },
  input: {
    flex: 1, minWidth: '140px', border: 'none', outline: 'none', background: 'transparent', color: tokens.colorNeutralForeground1,
    fontSize: '14px', fontFamily: 'inherit', padding: '4px 2px',
  },
  list: {
    position: 'absolute', top: '100%', left: 0, zIndex: 20, minWidth: 'min(320px, 80vw)', maxHeight: '280px', overflowY: 'auto',
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow16, borderRadius: tokens.borderRadiusMedium, padding: '4px',
  },
  item: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer' },
  active: { backgroundColor: tokens.colorNeutralBackground1Hover },
  sub: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  forget: {
    border: 'none', background: 'transparent', color: tokens.colorNeutralForeground3, cursor: 'pointer', fontSize: '18px', lineHeight: '18px',
    width: '24px', height: '24px', borderRadius: '12px', ':hover': { backgroundColor: tokens.colorNeutralBackground3Hover, color: tokens.colorNeutralForeground1 },
  },
})

interface Suggestion { name?: string | null; address: string; source: string }

/** Empfaenger als Kaertchen, mit Vorschlaegen aus Kontakten und zuletzt Benutzten. */
export function AddressInput({ value, onChange, autoFocus }: { value: string[]; onChange: (v: string[]) => void; autoFocus?: boolean }) {
  const s = useStyles()
  const [text, setText] = useState('')
  const [items, setItems] = useState<Suggestion[]>([])
  const [active, setActive] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  const term = useDebounced(text, 150)

  useEffect(() => {
    if (term.trim().length < 2) { setItems([]); return }
    api.get<Suggestion[]>('/api/pim/suggest?' + q({ q: term })).then(r => { setItems(r); setActive(0) }).catch(() => setItems([]))
  }, [term])

  const add = (entry: string) => {
    const e = entry.trim().replace(/[,;]$/, '')
    if (e && !value.includes(e)) onChange([...value, e])
    setText('')
    setItems([])
  }
  const fmt = (x: Suggestion) => (x.name ? `"${x.name.replace(/"/g, '')}" <${x.address}>` : x.address)

  const onKey = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' && items.length) { e.preventDefault(); setActive(a => Math.min(a + 1, items.length - 1)) }
    else if (e.key === 'ArrowUp' && items.length) { e.preventDefault(); setActive(a => Math.max(a - 1, 0)) }
    else if ((e.key === 'Enter' || e.key === 'Tab') && items.length && text) { e.preventDefault(); add(fmt(items[active])) }
    else if ((e.key === 'Enter' || e.key === ',' || e.key === ';') && text.trim()) { e.preventDefault(); add(text) }
    else if (e.key === 'Backspace' && !text && value.length) onChange(value.slice(0, -1))
    else if (e.key === 'Escape') setItems([])
  }

  const label = (v: string) => {
    const m = v.match(/^"?([^"<]*)"?\s*<(.+)>$/)
    return m && m[1].trim() ? m[1].trim() : v
  }

  return (
    <div className={s.root} onClick={() => input.current?.focus()}>
      {value.length > 0 && (
        <TagGroup onDismiss={(_, d) => onChange(value.filter(v => v !== d.value))} size="small">
          {value.map(v => (
            <Tag key={v} value={v} dismissible title={v} media={<Avatar name={label(v)} size={20} color="colorful" />}
              appearance={/@/.test(v) ? 'filled' : 'outline'}>{label(v)}</Tag>
          ))}
        </TagGroup>
      )}
      <input ref={input} className={s.input} value={text} autoFocus={autoFocus}
        onChange={e => setText(e.target.value)} onKeyDown={onKey}
        onBlur={() => setTimeout(() => { if (text.includes('@')) add(text); setItems([]) }, 150)} />
      {items.length > 0 && (
        <div className={s.list} onMouseDown={e => e.preventDefault()}>
          {items.map((x, i) => (
            <div key={x.address} className={`${s.item} ${i === active ? s.active : ''}`} onMouseEnter={() => setActive(i)} onClick={() => add(fmt(x))}>
              <Avatar name={x.name || x.address} size={28} color="colorful" />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div>{x.name || x.address}</div>
                {x.name && <div className={s.sub}>{x.address}</div>}
              </div>
              {x.source === 'recent' && (
                // Wie in Outlook: einen Vorschlag aus der Liste der zuletzt benutzten nehmen
                <button className={s.forget} title={t('Aus Vorschlägen entfernen')} aria-label={t('Aus Vorschlägen entfernen')}
                  onClick={e => {
                    e.stopPropagation()
                    api.post('/api/pim/recent/delete', { addresses: [x.address] }).catch(() => {})
                    setItems(list => list.filter(y => y.address !== x.address))
                  }}>×</button>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
