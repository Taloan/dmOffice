import { Text } from '@fluentui/react-components'
import { ActivityList, useActivity } from '../shell/Activity'
import { PageTitle } from './parts'
import { t } from '../i18n'

/** Alle Hintergrundaufgaben des Servers auf einer Seite (am Handy statt des Fensters in der Kopfzeile). */
export function ActivitySettings() {
  const data = useActivity(true)
  return (
    <>
      <PageTitle>{t('Aktivität')}</PageTitle>
      <Text>{t('Was der Server für dich im Hintergrund erledigt: läuft, wartet, zuletzt fertig oder mit Fehler. Die Anzeige aktualisiert sich von selbst.')}</Text>
      <ActivityList data={data} />
    </>
  )
}
