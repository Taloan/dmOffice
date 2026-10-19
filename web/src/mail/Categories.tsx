import { Button, Input, MenuDivider, MenuItem, MenuItemCheckbox, MenuList, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import { Add16Regular, Delete16Regular, Settings20Regular, TagDismiss20Regular, ArrowUp16Regular, ArrowDown16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

/** Kategorie: auf dem Mailserver ein IMAP-Schlagwort (key), hier Name und Farbe. */
export interface MailCategory { key: string; name: string; color: string }

// Einmal laden, fuer alle Stellen (Liste, Menue, Lesebereich, Einstellungen)
let cache: MailCategory[] | null = null
let loading: Promise<MailCategory[]> | null = null
const listeners = new Set<(c: MailCategory[]) => void>()

function load() {
  loading ??= api.get<MailCategory[]>('/api/settings/categories').then(c => { cache = c; listeners.forEach(l => l(c)); return c })
    .catch(() => { loading = null; return [] })
  return loading
}

export async function saveCategories(list: MailCategory[]) {
  const saved = await api.put<MailCategory[]>('/api/settings/categories', list)
  cache = saved
  listeners.forEach(l => l(saved))
  return saved
}

export function useCategories(): MailCategory[] {
  const [cats, setCats] = useState<MailCategory[]>(cache ?? [])
  useEffect(() => {
    listeners.add(setCats)
    if (!cache) load()
    return () => { listeners.delete(setCats) }
  }, [])
  return cats
}

/** Farbe fuer Schrift auf dem Farbfeld - hell oder dunkel, je nach Helligkeit. */
function textOn(hex: string) {
  const n = parseInt(hex.slice(1), 16)
  const l = 0.299 * (n >> 16) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)
  return l > 150 ? '#1b1b1b' : '#ffffff'
}

const useStyles = makeStyles({
  chip: {
    fontSize: '11px', lineHeight: '16px', padding: '0 6px', borderRadius: '8px', whiteSpace: 'nowrap', flexShrink: 0,
    overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '120px',
  },
  dot: { width: '12px', height: '12px', borderRadius: '3px', display: 'inline-block', flexShrink: 0 },
  row: { display: 'flex', alignItems: 'center', gap: '8px' },
  swatch: { width: '32px', height: '32px', padding: 0, border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: '4px', cursor: 'pointer', backgroundColor: 'transparent' },
  key: { color: tokens.colorNeutralForeground3, fontFamily: 'Consolas, monospace', fontSize: '12px', minWidth: '140px' },
})

export function CategoryDot({ color }: { color: string }) {
  const s = useStyles()
  return <span className={s.dot} style={{ backgroundColor: color }} />
}

/** Farbige Kaertchen einer Mail. Unbekannte Schlagworte (z. B. von anderem Rechner) erscheinen grau mit ihrem Namen. */
export function CategoryChips({ keywords, compact }: { keywords?: string[]; compact?: boolean }) {
  const s = useStyles()
  const cats = useCategories()
  if (!keywords?.length) return null
  return <>
    {keywords.map(k => {
      const c = cats.find(x => x.key === k)
      if (!c && !k.startsWith('dm_')) return null
      const color = c?.color ?? '#8a8886'
      const name = c?.name ?? k.slice(3)
      return compact
        ? <Tooltip key={k} content={name} relationship="label"><span className={s.dot} style={{ backgroundColor: color, width: 10, height: 10 }} /></Tooltip>
        : <span key={k} className={s.chip} style={{ backgroundColor: color, color: textOn(color) }} title={name}>{name}</span>
    })}
  </>
}

/** Eintraege fuer ein Kategorisieren-Menue: Haken, wenn alle gewaehlten Mails die Kategorie tragen. */
export function CategoryMenuList({ selectedKeywords, onToggle, onClear }: {
  selectedKeywords: string[][]
  onToggle: (key: string, on: boolean) => void
  onClear: () => void
}) {
  const cats = useCategories()
  const all = (k: string) => selectedKeywords.length > 0 && selectedKeywords.every(list => list.includes(k))
  const checked = cats.filter(c => all(c.key)).map(c => c.key)
  const anything = selectedKeywords.some(l => l.some(k => k.startsWith('dm_')))
  return (
    <MenuList checkedValues={{ cat: checked }}>
      {cats.length === 0 && <MenuItem disabled>{t('Keine Kategorien')}</MenuItem>}
      {cats.map(c => (
        <MenuItemCheckbox key={c.key} name="cat" value={c.key} icon={<CategoryDot color={c.color} />}
          onClick={() => onToggle(c.key, !all(c.key))}>{c.name}</MenuItemCheckbox>
      ))}
      <MenuDivider />
      <MenuItem icon={<TagDismiss20Regular />} disabled={!anything} onClick={onClear}>{t('Alle Kategorien löschen')}</MenuItem>
      <MenuItem icon={<Settings20Regular />} onClick={() => { location.hash = '#/settings/categories' }}>{t('Kategorien verwalten …')}</MenuItem>
    </MenuList>
  )
}

const palette = ['#D13438', '#CA5010', '#C19C00', '#13A10E', '#00B7C3', '#0F6CBD', '#8764B8', '#E3008C', '#8A8886', '#4F6BED']

/** Einstellungen -> E-Mail -> Kategorien */
export function CategorySettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const cats = useCategories()
  const [list, setList] = useState<MailCategory[]>(cats)
  const [dirty, setDirty] = useState(false)
  useEffect(() => { if (!dirty) setList(cats) }, [cats, dirty])

  const change = (next: MailCategory[]) => { setList(next); setDirty(true) }
  const update = (i: number, p: Partial<MailCategory>) => change(list.map((c, j) => j === i ? { ...c, ...p } : c))
  const swap = (i: number, j: number) => { const n = [...list]; [n[i], n[j]] = [n[j], n[i]]; change(n) }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {list.map((c, i) => (
        <div key={i} className={s.row}>
          <label className={s.swatch} style={{ backgroundColor: c.color }}>
            <input type="color" value={c.color} onChange={e => update(i, { color: e.target.value })} style={{ opacity: 0, width: 0, height: 0 }} />
          </label>
          <Input value={c.name} onChange={(_, d) => update(i, { name: d.value })} style={{ width: 220 }} />
          <span className={s.key} title={t('IMAP-Schlagwort auf dem Mailserver')}>{c.key || t('(neu)')}</span>
          <Button size="small" appearance="subtle" icon={<ArrowUp16Regular />} disabled={i === 0} onClick={() => swap(i, i - 1)} aria-label={t('Nach oben')} />
          <Button size="small" appearance="subtle" icon={<ArrowDown16Regular />} disabled={i === list.length - 1} onClick={() => swap(i, i + 1)} aria-label={t('Nach unten')} />
          <Button size="small" appearance="subtle" icon={<Delete16Regular />} onClick={() => change(list.filter((_, j) => j !== i))} aria-label={t('Entfernen')} />
        </div>
      ))}
      <div className={s.row}>
        <Button icon={<Add16Regular />} onClick={() => change([...list, { key: '', name: t('Neue Kategorie'), color: palette[list.length % palette.length] }])}>{t('Kategorie')}</Button>
        <Button appearance="primary" disabled={!dirty} onClick={async () => {
          try { await saveCategories(list); setDirty(false); notify(t('Kategorien gespeichert')) } catch (e) { fail(e) }
        }}>{t('Speichern')}</Button>
        {dirty && <Button appearance="subtle" onClick={() => { setList(cats); setDirty(false) }}>{t('Verwerfen')}</Button>}
      </div>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
        {t('Umbenennen und Umfärben ändert nur die Anzeige – das Schlagwort auf dem Server bleibt, die Mails behalten ihre Kategorie.')}{' '}
        {t('Eine entfernte Kategorie bleibt an den Mails stehen und erscheint dann grau.')}
      </Text>
    </div>
  )
}
