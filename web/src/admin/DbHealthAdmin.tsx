import { Badge, Button, Spinner, Text, tokens } from '@fluentui/react-components'
import { ArrowClockwise20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t, locale } from '../i18n'

type Result = { name: string; label: string; ok: boolean; problem?: string | null; repaired: boolean; ms: number }
type State = { running: boolean; report: { at: string; items: Result[] } | null }

/**
 * Datenbanken: Ergebnis der nächtlichen Prüfung (integrity_check) je Datenbank - Benutzer, Chat, Alben, Push.
 * Kaputte Indizes baut der Server selbst neu auf; was dann noch beschädigt ist, gehört aus der Sicherung zurück.
 */
export function DbHealthAdmin() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<State | null>(null)
  const load = () => api.get<State>('/api/admin/health').then(setSt).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Während der Prüfung nachsehen, bis sie fertig ist
  useEffect(() => {
    if (!st?.running) return
    const timer = setInterval(load, 3000)
    return () => clearInterval(timer)
  }, [st?.running]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!st) return null
  const r = st.report
  const bad = r?.items.filter(i => !i.ok) ?? []
  // Der Server meldet den Lauf sofort - neu laden, dann fragt die Seite nach, bis er fertig ist
  const run = () => api.post('/api/admin/health/run').then(() => { notify(t('Prüfung gestartet')); load() }).catch(fail)
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxWidth: 640 }}>
      <Text>{t('Nächtliche Prüfung aller Datenbanken (SQLite integrity_check). Beschädigte Indizes baut dmOffice neu auf.')}</Text>
      {r == null ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Noch nicht geprüft.')}</Text> : (
        <>
          <Text weight="semibold" style={{ color: bad.length ? tokens.colorPaletteRedForeground1 : tokens.colorPaletteGreenForeground1 }}>
            {bad.length ? t('{n} beschädigt', { n: bad.length }) : t('Alles in Ordnung')}
            <span style={{ fontWeight: 'normal', color: tokens.colorNeutralForeground3 }}> · {t('geprüft {when}', { when: new Date(r.at).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' }) })}</span>
          </Text>
          <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr auto', gap: '4px 12px', alignItems: 'baseline' }}>
            {r.items.map(i => (
              <div key={i.name} style={{ display: 'contents' }}>
                <Text>{i.name.startsWith('user:') ? i.label : t(i.label)}</Text>
                <Text size={200} style={{ color: i.ok ? tokens.colorNeutralForeground3 : tokens.colorPaletteRedForeground1, wordBreak: 'break-word' }}>
                  {!i.ok ? i.problem : i.repaired ? t('Index neu aufgebaut') : ''}
                </Text>
                <Badge appearance="tint" color={!i.ok ? 'danger' : i.repaired ? 'warning' : 'success'}>{!i.ok ? t('beschädigt') : t('ok')}</Badge>
              </div>
            ))}
          </div>
          {bad.length > 0 && <Text size={200}>{t('Beschädigte Datenbank aus dem Core-Backup wiederherstellen. Solange eine Datenbank beschädigt ist, löscht dmOffice keine alten Sicherungsstände.')}</Text>}
        </>
      )}
      <div>
        <Button icon={st.running ? <Spinner size="tiny" /> : <ArrowClockwise20Regular />} disabled={st.running} onClick={run}>{st.running ? t('Prüft …') : t('Jetzt prüfen')}</Button>
      </div>
    </div>
  )
}
