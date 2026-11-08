import { Checkbox, Spinner, Text, tokens } from '@fluentui/react-components'
import { Folder20Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { t } from '../i18n'

/**
 * Welche Ordner der obersten Ebene eines externen Ordners in dmOffice erscheinen: „Alle“ oder einzelne.
 * include leer heißt alle - auch Ordner, die später dazukommen. „Alles“ abwählen: Nichts ist gewählt, man kreuzt selbst an. Solange
 * keiner gewählt ist, meldet onNone das - gespeichert hieße es sonst wieder alle, der Dialog darf also nicht speichern.
 */
export function TopFolderChoice({ dirs, include, onChange, onNone }: { dirs: string[] | null; include: string[]; onChange: (include: string[]) => void; onNone?: (none: boolean) => void }) {
  const [picking, setPicking] = useState(false)
  const none = picking && include.length === 0
  useEffect(() => { onNone?.(none); return () => onNone?.(false) }, [none]) // eslint-disable-line react-hooks/exhaustive-deps
  if (dirs === null) return <Spinner size="tiny" />
  if (dirs.length === 0) return <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Darin liegen keine Ordner – es erscheint alles.')}</Text>
  const all = include.length === 0 && !picking
  // Untereinander und alphabetisch, wie im Explorer
  const sorted = [...dirs].sort((a, b) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' }))
  const chosen = all ? dirs.length : include.filter(n => dirs.includes(n)).length
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <Checkbox label={t('Alles (auch Ordner, die später dazukommen)')} checked={all} onChange={(_, d) => { setPicking(!d.checked); onChange([]) }} />
      <div style={{ display: 'flex', flexDirection: 'column', maxHeight: 320, overflowY: 'auto', marginLeft: 24,
        border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '2px 0' }}>
        {sorted.map(n => (
          <Checkbox key={n} checked={all || include.includes(n)} disabled={all}
            label={<span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}><Folder20Filled color="#E8A33D" />{n}</span>}
            onChange={(_, d) => { setPicking(true); onChange(d.checked ? [...include, n] : include.filter(x => x !== n)) }} />
        ))}
      </div>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3, marginLeft: 24 }}>{t('{n} von {m} Ordnern gewählt', { n: chosen, m: dirs.length })}</Text>
      {none && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground2, marginLeft: 24 }}>{t('Mindestens einen Ordner wählen oder „Alles“ ankreuzen.')}</Text>}
    </div>
  )
}
