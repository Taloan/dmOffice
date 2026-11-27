// GateKeeper: Zähler „Heute / 30 Tage“ für Einstellungen → GateKeeper und die Verwaltung (gleicher Aufbau).
import { Text, makeStyles, tokens } from '@fluentui/react-components'
import { fmtSize } from '../api'
import { t } from '../i18n'
import { type GkCounts, fmtNum } from './model'

const useStyles = makeStyles({
  grid: {
    display: 'grid', gridTemplateColumns: 'minmax(140px, max-content) repeat(2, minmax(90px, max-content))', gap: '6px 24px', alignItems: 'baseline',
    fontVariantNumeric: 'tabular-nums',
  },
  head: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, textAlign: 'right' },
  num: { textAlign: 'right' },
  hint: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, maxWidth: '640px' },
})

/** Zähler zweier Zeiträume; people (nur Verwaltung): Benutzer mit Verkehr heute bzw. im Zeitraum. */
export function GkCountsTable({ today, month, people }: { today: GkCounts; month: GkCounts; people?: [number, number] }) {
  const s = useStyles()
  const row = (label: string, f: (c: GkCounts) => string) => <>
    <Text>{label}</Text>
    <Text className={s.num}>{f(today)}</Text>
    <Text className={s.num}>{f(month)}</Text>
  </>
  return (
    <>
      <div className={s.grid}>
        <span />
        <Text className={s.head}>{t('Heute')}</Text>
        <Text className={s.head}>{t('30 Tage')}</Text>
        {people && <>
          <Text>{t('gk|Benutzer')}</Text>
          <Text className={s.num}>{fmtNum(people[0])}</Text>
          <Text className={s.num}>{fmtNum(people[1])}</Text>
        </>}
        {row(t('Verbindungen'), c => fmtNum(c.connections))}
        {row(t('DNS-Anfragen'), c => fmtNum(c.dns))}
        {row(t('gk|Gesperrt'), c => fmtNum(c.blocked))}
        {row(t('Datenverkehr'), c => fmtSize(c.traffic ?? 0))}
        {row(t('Eingespart (geschätzt)'), c => fmtSize(c.saved ?? 0))}
      </div>
      <Text as="p" className={s.hint}>{t('Eingespart: gesperrte Anfragen mal der mittleren Größe einer Verbindung (Median) im selben Zeitraum.')}</Text>
    </>
  )
}
