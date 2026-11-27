// Mailkonto deaktivieren statt löschen (Kennung mailaccountoff): Schalter je Konto in Einstellungen › E-Mail › E-Mail-Konten.
// Deaktiviert ruft dmOffice das Konto nicht ab, sendet nichts darüber und zeigt es im Mailbereich nicht an; die Mails bleiben.
import { Switch, Text, tokens } from '@fluentui/react-components'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

/** Hinweis des Servers: was am Konto hängt und ruht (MailAccountOff.Notes). */
export interface OffNote { kind: string; count?: number; name?: string | null }

function noteText(n: OffNote): string {
  switch (n.kind) {
    case 'default': return t('Standard-Postfach – solange es deaktiviert ist, gilt „{name}“ als Standard.', { name: n.name ?? '' })
    case 'defaultNone': return t('Standard-Postfach – solange es deaktiviert ist, gibt es kein aktives Mailkonto.')
    case 'outbox': return t('Postausgang: {count} E-Mail(s) warten und gehen erst nach dem Aktivieren raus.', { count: String(n.count ?? 0) })
    case 'catchall': return t('Catch-all läuft über dieses Konto und ruht.')
    case 'rules': return t('Regeln: {count} betreffen dieses Konto und ruhen.', { count: String(n.count ?? 0) })
    case 'graph': return t('Kalender, Kontakte und Aufgaben von Microsoft werden nicht abgeglichen.')
    case 'waiting': return t('Wiedervorlage und Entfernen nach Zeit: {count} Einträge ruhen.', { count: String(n.count ?? 0) })
    case 'dms': return t('DMS: Mailordner dieses Kontos als Quelle ruhen.')
    default: return ''
  }
}

/** Schalter „Aktiv“ und, bei deaktiviertem Konto, die Hinweise darunter. */
export function MailAccountActive({ id, disabled, notes, locked, onChanged }: {
  id: string; disabled: boolean; notes?: OffNote[] | null; locked?: boolean; onChanged: () => void
}) {
  const { fail } = useApp()
  const [busy, setBusy] = useState(false)
  const toggle = async (active: boolean) => {
    setBusy(true)
    try { await api.put(`/api/settings/accounts/${id}/active`, { active }); onChanged() } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const lines = (notes ?? []).map(noteText).filter(Boolean)
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '2px' }}>
      <Switch checked={!disabled} disabled={busy || locked} label={disabled ? t('Deaktiviert') : t('Aktiv')} onChange={(_, d) => toggle(d.checked)} />
      {disabled && lines.map(l => <Text key={l} size={200} style={{ color: tokens.colorNeutralForeground3 }}>{l}</Text>)}
    </div>
  )
}

/** Hinweiszeile unter der Kontenliste: was Deaktivieren bewirkt. */
export function MailAccountActiveHint() {
  return <Text size={200}>{t('Ein deaktiviertes Konto ruft dmOffice nicht ab, sendet nichts darüber und zeigt es im Mailbereich nicht an. Mails und Einstellungen bleiben; nach dem Aktivieren holt der Abgleich nur Neues.')}</Text>
}
