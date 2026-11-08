import { Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { AdminHeading } from './AdminHeading'

interface GarminState { forAll: boolean }

/** Coach-Einstellungen für den ganzen Server (bisher: wer Garmin verbinden darf). */
export function CoachAdmin() {
  const { fail } = useApp()
  const [st, setSt] = useState<GarminState | null>(null)
  useEffect(() => { api.get<GarminState>('/api/coach/garmin').then(setSt).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!st) return null
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 680 }}>
      <AdminHeading first>{t('Garmin')}</AdminHeading>
      <Switch checked={st.forAll} label={t('Auch andere Benutzer dürfen ihren eigenen Garmin-Schlüssel eintragen')}
        onChange={async (_, d) => { try { setSt(await api.put<GarminState>('/api/coach/garmin/forall', { on: d.checked })) } catch (e) { fail(e) } }} />
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Garmin hat den persönlichen Schlüssel für einzelne Personen und ihre eigenen Daten gedacht. Jeder trägt seinen eigenen Schlüssel ein, und dmOffice holt nur dessen Daten.')}</Text>
    </div>
  )
}
