import { Button, Dropdown, Field, Input, Link, MessageBar, MessageBarBody, Option, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { fmt } from './api'
import { askConfirm } from '../shell/context'

interface GarminState { allowed: boolean; connected: boolean; expires: string | null; lastSync: string | null; lastError: string | null; admin: boolean; forAll: boolean }
interface DayValues {
  day: string; steps: number | null; activeKcal: number | null; distanceM: number | null; restingHr: number | null; sleepMin: number | null
  weightKg: number | null; bodyFat: number | null; stress: number | null; bodyBatteryHigh: number | null; bodyBatteryLow: number | null; hrv: number | null
}
interface SyncResult { days: number; sources: Record<string, string>; values: DayValues[]; state: GarminState }

const useStyles = makeStyles({
  box: { display: 'flex', flexDirection: 'column', gap: '10px', maxWidth: '640px', marginTop: '20px', paddingTop: '14px', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  section: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-end' },
  table: { borderCollapse: 'collapse', fontSize: tokens.fontSizeBase200, fontVariantNumeric: 'tabular-nums', '& td, & th': { padding: '3px 10px 3px 0', textAlign: 'left' } },
})

const VALID: [number, string][] = [[30, '30 Tage'], [90, '90 Tage'], [180, '180 Tage'], [365, '1 Jahr']]

/**
 * Garmin Personal API im Coach-Profil: eigenen Schlüssel aus Garmin Connect eintragen, Verbindung testen (was wurde erkannt?),
 * Rohdaten ansehen, trennen. Zuerst nur für Admins; der Admin kann es für alle freigeben.
 */
export function GarminSection() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [st, setSt] = useState<GarminState | null>(null)
  const [key, setKey] = useState('')
  const [valid, setValid] = useState(365)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<SyncResult | null>(null)
  useEffect(() => { api.get<GarminState>('/api/coach/garmin').then(setSt).catch(() => {}) }, [])
  if (!st) return null
  if (!st.allowed) return (
    <div className={s.box}>
      <Text className={s.section}>{t('Garmin')}</Text>
      <Text className={s.muted}>{t('Die direkte Verbindung zu Garmin schaltet der Admin frei. Schritte, Schlaf und Gewicht von der Uhr kommen auch über die Handy-App mit Health Connect.')}</Text>
    </div>
  )
  const connect = async () => {
    setBusy(true)
    try { setSt(await api.put<GarminState>('/api/coach/garmin', { key: key.trim(), validDays: valid })); setKey(''); notify(t('Verbunden. Die letzten 30 Tage werden gerade abgeholt.')) }
    catch (e) { fail(e) } finally { setBusy(false) }
  }
  const sync = async () => {
    setBusy(true); setResult(null)
    try { const r = await api.post<SyncResult>('/api/coach/garmin/sync'); setResult(r); setSt(r.state) }
    catch (e) { fail(e); api.get<GarminState>('/api/coach/garmin').then(setSt).catch(() => {}) } finally { setBusy(false) }
  }
  const disconnect = async () => {
    if (!await askConfirm('', t('Die Verbindung zu Garmin trennen? Was schon abgeholt ist, bleibt im Coach.'))) return
    try { setSt(await api.del<GarminState>('/api/coach/garmin')); setResult(null) } catch (e) { fail(e) }
  }
  const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })
  return (
    <div className={s.box}>
      <Text className={s.section}>{t('Garmin')}</Text>
      {!st.connected ? <>
        <Text className={s.muted}>
          {t('Mit deinem persönlichen Garmin-Schlüssel holt dmOffice alle drei Stunden deine Tageswerte direkt bei Garmin ab: Schritte, aktive Kalorien, Schlaf, Ruhepuls, Gewicht, dazu Body Battery, Stress und HRV. Nur Lesen, nur deine eigenen Daten.')}
        </Text>
        <Text className={s.muted}>
          {t('So geht es: In Garmin Connect im Browser unter Einstellungen → Persönlicher API-Schlüssel einen Schlüssel anlegen, die Gültigkeit wählen und die Bedingungen annehmen. Garmin zeigt den Schlüssel nur einmal – kopiere ihn gleich hier hinein.')}
          {' '}<Link href="https://connect.garmin.com/" target="_blank" rel="noreferrer">connect.garmin.com</Link>
        </Text>
        <div className={s.row}>
          <Field label={t('Persönlicher API-Schlüssel')} style={{ flex: '1 1 260px' }}>
            <Input type="password" value={key} onChange={(_, d) => setKey(d.value)} autoComplete="off" />
          </Field>
          <Field label={t('Gültig')}>
            <Dropdown value={t(VALID.find(v => v[0] === valid)![1])} selectedOptions={[String(valid)]} onOptionSelect={(_, d) => setValid(Number(d.optionValue))} style={{ minWidth: 120 }}>
              {VALID.map(([d, l]) => <Option key={d} value={String(d)}>{t(l)}</Option>)}
            </Dropdown>
          </Field>
          <Button appearance="primary" disabled={busy || key.trim().length < 10} onClick={connect}>{busy ? <Spinner size="tiny" /> : t('Verbinden')}</Button>
        </div>
        <Text className={s.muted}>{t('Wähle dieselbe Gültigkeit wie bei Garmin – eine Woche vor Ablauf erinnert dich Pukki an einen neuen Schlüssel.')}</Text>
      </> : <>
        <Text>
          {t('Verbunden')}{st.expires ? ' · ' + t('Schlüssel gültig bis {date}', { date: new Date(st.expires).toLocaleDateString() }) : ''}
          {st.lastSync ? ' · ' + t('zuletzt abgeholt {when}', { when: when(st.lastSync) }) : ' · ' + t('wird gerade zum ersten Mal abgeholt …')}
        </Text>
        {st.lastError && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{st.lastError}</MessageBarBody></MessageBar>}
        <div className={s.row}>
          <Button disabled={busy} onClick={sync}>{busy ? <Spinner size="tiny" /> : t('Jetzt abholen und testen')}</Button>
          <Button appearance="subtle" as="a" href="/api/coach/garmin/raw" target="_blank">{t('Rohdaten von gestern ansehen')}</Button>
          <Button appearance="subtle" onClick={disconnect}>{t('Trennen')}</Button>
        </div>
        {result && <>
          <Text className={s.muted}>{Object.entries(result.sources).map(([k, v]) => `${t(k)}: ${v === 'ok' ? t('ok') : v === 'leer' ? t('leer') : v}`).join(' · ')}</Text>
          {result.values.length === 0 ? <Text className={s.muted}>{t('Garmin hat für die letzten drei Tage keine Werte geliefert.')}</Text> : (
            <table className={s.table}>
              <thead><tr><th>{t('Tag')}</th><th>{t('Schritte')}</th><th>{t('aktiv')}</th><th>{t('Schlaf')}</th><th>{t('Ruhepuls')}</th><th>{t('Gewicht')}</th><th>Body Battery</th><th>{t('Stress')}</th><th>HRV</th></tr></thead>
              <tbody>{result.values.map(v => (
                <tr key={v.day}>
                  <td>{new Date(v.day).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'numeric' })}</td>
                  <td>{fmt(v.steps)}</td><td>{v.activeKcal != null ? fmt(v.activeKcal) + ' kcal' : '–'}</td>
                  <td>{v.sleepMin != null ? `${Math.floor(v.sleepMin / 60)} h ${v.sleepMin % 60} min` : '–'}</td>
                  <td>{fmt(v.restingHr)}</td><td>{v.weightKg != null ? fmt(v.weightKg, 1) + ' kg' : '–'}</td>
                  <td>{v.bodyBatteryHigh != null ? `${fmt(v.bodyBatteryLow)}–${fmt(v.bodyBatteryHigh)}` : '–'}</td><td>{fmt(v.stress)}</td><td>{fmt(v.hrv)}</td>
                </tr>
              ))}</tbody>
            </table>
          )}
          <Text className={s.muted}>{t('Fehlt hier etwas, das deine Uhr misst? Dann nennt Garmin das Feld anders – „Rohdaten von gestern ansehen“ zeigt, was Garmin liefert.')}</Text>
        </>}
      </>}
    </div>
  )
}
