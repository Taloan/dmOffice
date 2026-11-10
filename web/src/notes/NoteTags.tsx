import { Combobox, Option, Tag, TagGroup, tokens } from '@fluentui/react-components'
import { Tag16Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { t } from '../i18n'

/** Anzeige eines Schlagworts: das Rezept-Kennzeichen aus notizi heisst in der Oberflaeche „Rezept“. */
export const tagLabel = (tag: string) => (tag.toLowerCase() === 'recipe' ? t('Rezept') : tag)

/**
 * Schlagwoerter einer Notiz (Version 2): als Chips mit x zum Entfernen, darunter ein Feld zum Hinzufuegen mit Vorschlaegen
 * aus den Schlagwoertern der anderen Notizen. Enter oder Komma fuegt hinzu. Die Tags gehen beim Abgleich auch an notizi.
 */
export function NoteTags({ tags, known, readOnly, onChange }: {
  tags: string[]; known: string[]; readOnly?: boolean; onChange: (tags: string[]) => void
}) {
  const [text, setText] = useState('')
  const has = (x: string) => tags.some(y => y.toLowerCase() === x.toLowerCase())
  const add = (raw: string) => {
    const v = raw.trim().replace(/^#/, '')
    setText('')
    if (v && !has(v)) onChange([...tags, v])
  }
  const suggestions = known.filter(k => !has(k) && k.toLowerCase().includes(text.trim().toLowerCase())).slice(0, 12)
  if (readOnly && tags.length === 0) return null
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', padding: '2px 8px 6px', color: tokens.colorNeutralForeground3 }}>
      <Tag16Regular aria-label={t('Schlagwörter')} />
      {tags.length > 0 && (
        <TagGroup size="small" onDismiss={readOnly ? undefined : (_, d) => onChange(tags.filter(x => x !== d.value))} aria-label={t('Schlagwörter')}>
          {tags.map(x => <Tag key={x} value={x} dismissible={!readOnly} shape="circular" appearance="brand"
            dismissIcon={{ 'aria-label': t('Entfernen') }}>{tagLabel(x)}</Tag>)}
        </TagGroup>
      )}
      {!readOnly && (
        <Combobox size="small" appearance="underline" freeform style={{ minWidth: 140, width: 160 }} placeholder={t('Schlagwort hinzufügen')}
          value={text} selectedOptions={[]}
          onChange={e => { const v = e.target.value; if (v.endsWith(',')) add(v.slice(0, -1)); else setText(v) }}
          onOptionSelect={(_, d) => { if (d.optionValue) add(d.optionValue) }}
          onKeyDown={e => { if (e.key === 'Enter' && text.trim()) { e.preventDefault(); add(text) } }}
          onBlur={() => { if (text.trim()) add(text) }}>
          {suggestions.map(k => <Option key={k} value={k}>{tagLabel(k)}</Option>)}
        </Combobox>
      )}
    </div>
  )
}
