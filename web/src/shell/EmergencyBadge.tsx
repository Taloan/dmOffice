import { Button, Tooltip, tokens } from '@fluentui/react-components'
import { Warning20Filled } from '@fluentui/react-icons'
import { useEffect } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { usePoll } from './hooks'

interface Badge { count: number; session: { requester: string; owner: string } | null }

/**
 * Notfallzugang oben in der Kopfleiste: rot, solange eine Anfrage offen ist, bei der ich widersprechen kann -
 * und in einer Notfallsitzung der Hinweis, in wessen Konto man gerade ist.
 */
export function EmergencyBadge({ className, onLogout }: { className?: string; onLogout: () => void }) {
  const badge = usePoll(() => api.get<Badge>('/api/emergency/badge'), 60000, 'dmoffice:emergency')
  // Link aus der Einladung (#/notfall/<Kennung>) fuehrt zur Seite in den Einstellungen
  useEffect(() => {
    const go = () => {
      const m = location.hash.match(/^#\/?notfall\/([0-9a-f]+)/i)
      if (m) location.replace('#/settings/emergency/' + m[1])
    }
    go()
    window.addEventListener('hashchange', go)
    return () => window.removeEventListener('hashchange', go)
  }, [])
  if (badge?.session) return (
    <Tooltip content={t('Du bist über den Notfallzugang von {who} im Konto von {owner}. Abmelden beendet das.', { who: badge.session.requester, owner: badge.session.owner })} relationship="description">
      <Button appearance="primary" size="small" icon={<Warning20Filled />} onClick={onLogout}
        style={{ backgroundColor: tokens.colorPaletteRedBackground3, marginRight: 8 }}>{t('Notfallzugang · abmelden')}</Button>
    </Tooltip>
  )
  if (!badge?.count) return null
  return (
    <Tooltip content={t('Jemand hat einen Notfallzugang angefordert – bestätige, wenn kein Notfall vorliegt.')} relationship="description">
      <Button appearance="primary" size="small" className={className} icon={<Warning20Filled />} onClick={() => { location.hash = '#/settings/emergency' }}
        style={{ backgroundColor: tokens.colorPaletteRedBackground3, marginRight: 8 }}>
        {badge.count === 1 ? t('Notfall-Anfrage') : t('{n} Notfall-Anfragen', { n: badge.count })}
      </Button>
    </Tooltip>
  )
}
