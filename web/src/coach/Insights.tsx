import { Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Checks } from './Checks'

/** Coach: Hinweise („Metawissen“) aus Tagebuch, Profil und belegtem Wissen - /api/coach/insights. */

interface Insight { key: string; kind: 'diary' | 'profile' | 'know' | 'hype'; title: string; text: string; source: string | null; why: string | null }

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '18px', maxWidth: '860px' },
  group: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: '12px' },
  card: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '6px' },
  head: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  why: { color: tokens.colorBrandForeground1, fontSize: tokens.fontSizeBase200 },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
})

// Satzteile mit Zahlen aus dem Server („an 2 von 11 Tagen“) - Muster übersetzen, Zahlen behalten
const why = (w: string) => w
  .replace(/^an (\d+) von (\d+) Tagen mit Einträgen$/, (_, a, b) => t('an {a} von {b} Tagen mit Einträgen', { a, b }))
  .replace(/^an (\d+) von (\d+) Tagen$/, (_, a, b) => t('an {a} von {b} Tagen', { a, b }))
  .replace(/^(\d+)× in (\d+) Tagen$/, (_, a, b) => t('{a}× in {b} Tagen', { a, b }))
  .replace(/^im Schnitt ([\d,.]+) g pro Tag$/, (_, a) => t('im Schnitt {a} g pro Tag', { a }))
  .replace(/^im Schnitt ([\d,.]+) g pro kg$/, (_, a) => t('im Schnitt {a} g pro kg', { a }))

export function Insights() {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<Insight[] | null>(null)
  useEffect(() => { api.get<Insight[]>('/api/coach/insights').then(setList).catch(fail) }, [fail])
  if (!list) return <Spinner />
  const card = (i: Insight) => (
    <div key={i.key} className={s.card}>
      <Text className={s.head}>{t(i.title)}</Text>
      {i.why && <Text className={s.why}>{why(i.why)}</Text>}
      <Text>{t(i.text)}</Text>
      {i.source && <Text className={s.muted}>{t('Quelle:')} {t(i.source)}</Text>}
    </div>
  )
  const section = (kind: Insight['kind'], title: string, empty?: string) => {
    const items = list.filter(i => i.kind === kind)
    if (!items.length && !empty) return null
    return <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <Text weight="semibold" size={500}>{title}</Text>
      {items.length ? <div className={s.group}>{items.map(card)}</div> : <Text className={s.muted}>{empty}</Text>}
    </div>
  }
  return (
    <div className={s.root}>
      <Text className={s.muted}>{t('Über die bekannten Regeln hinaus – nicht rauchen, wenig Alkohol, Bewegung, Schlaf: was für ein langes, gesundes Leben gut belegt ist. Keine Diagnose und kein Ersatz für ärztlichen Rat.')}</Text>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <Text weight="semibold" size={500}>{t('Vorsorge und Impfungen')}</Text>
        <Checks />
      </div>
      {section('diary', t('Aus deinem Tagebuch (letzte 14 Tage)'), t('Mit Einträgen an mindestens fünf der letzten 14 Tage zeigt der Coach hier, was noch fehlt.'))}
      {section('profile', t('Passend zu deinem Profil'))}
      {section('know', t('Gut zu wissen'))}
      {section('hype', t('Gehypt, aber nicht belegt'))}
    </div>
  )
}
