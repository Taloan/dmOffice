import { Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, MessageBar, MessageBarBody, Option, Spinner, Text, tokens } from '@fluentui/react-components'
import { ArrowClockwise20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, featureOn, fmtSize } from '../api'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { useApp } from '../App'
import { t, locale } from '../i18n'
import { askConfirm } from '../shell/context'

type Result = { name: string; label: string; ok: boolean; problem?: string | null; repaired: boolean; ms: number }
type State = { running: boolean; report: { at: string; items: Result[] } | null }
type Stand = { name: string; created: string; bytes: number }

const when = (iso: string) => new Date(iso).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' })
const nameOf = (i: Result) => i.name.startsWith('user:') ? i.label : t(i.label)

/**
 * Datenbanken: Ergebnis der nächtlichen Prüfung (integrity_check) je Datenbank - Benutzer, Chat, Alben, Push.
 * Kaputte Indizes baut der Server selbst neu auf. Bei einem anderen Befund hilft die Seite für genau diese eine Datenbank:
 * reparieren (alles Lesbare übernehmen) oder aus einem Core-Stand ersetzen. Alle anderen Datenbanken bleiben, wie sie sind.
 */
export function DbHealthAdmin() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<State | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [replace, setReplace] = useState<Result | null>(null)
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
  const repair = async (i: Result) => {
    if (!await askConfirm(t('Reparieren versuchen'), t('dmOffice übernimmt alles Lesbare aus „{name}“ in eine neue Datenbank und prüft sie. Die beschädigte Datei bleibt als Kopie erhalten. Alle anderen Datenbanken bleiben unverändert.', { name: nameOf(i) }), t('Reparieren'))) return
    setBusy(i.name)
    try { const x = await api.post<{ message: string }>('/api/admin/health/repair', { name: i.name }); notify(t('Repariert'), x.message); load() }
    catch (e) { fail(e) } finally { setBusy(null) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxWidth: 640 }}>
      <Text>{t('Nächtliche Prüfung aller Datenbanken (SQLite integrity_check). Beschädigte Indizes baut dmOffice neu auf.')}</Text>
      {r == null ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Noch nicht geprüft.')}</Text> : (
        <>
          <MessageBar intent={bad.length ? 'error' : 'success'} layout="multiline"><MessageBarBody>
            {bad.length ? t('{n} beschädigt', { n: bad.length }) : t('Alles in Ordnung')} · {t('geprüft {when}', { when: when(r.at) })}
          </MessageBarBody></MessageBar>
          <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr auto', gap: '6px 12px', alignItems: 'baseline' }}>
            {r.items.map(i => (
              <div key={i.name} style={{ display: 'contents' }}>
                <Text>{nameOf(i)}</Text>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
                  <Text size={200} style={{ color: i.ok ? tokens.colorNeutralForeground3 : tokens.colorPaletteRedForeground1, wordBreak: 'break-word' }}>
                    {!i.ok ? i.problem : i.repaired ? t('Index neu aufgebaut') : ''}
                  </Text>
                  {!i.ok && <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                    <Button size="small" disabled={busy != null} icon={busy === i.name ? <Spinner size="tiny" /> : undefined} onClick={() => repair(i)}>{t('Reparieren versuchen')}</Button>
                    <Button size="small" disabled={busy != null} onClick={() => setReplace(i)}>{t('Aus Backup ersetzen')}</Button>
                  </div>}
                </div>
                <Badge appearance="tint" color={!i.ok ? 'danger' : i.repaired ? 'warning' : 'success'}>{!i.ok ? t('beschädigt') : t('ok')}</Badge>
              </div>
            ))}
          </div>
          {bad.length > 0 && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Beide Wege betreffen nur die beschädigte Datenbank, alle anderen bleiben unverändert. Solange eine Datenbank beschädigt ist, löscht dmOffice keine alten Backup-Stände.')}</Text>}
        </>
      )}
      {featureOn('adminribbon') ? <RibbonActions>
        <RibbonButton primary icon={st.running ? <Spinner size="tiny" /> : <ArrowClockwise20Regular />} label={st.running ? t('Prüft …') : t('Jetzt prüfen')} disabled={st.running} onClick={run} />
      </RibbonActions> : <div>
        <Button icon={st.running ? <Spinner size="tiny" /> : <ArrowClockwise20Regular />} disabled={st.running} onClick={run}>{st.running ? t('Prüft …') : t('Jetzt prüfen')}</Button>
      </div>}
      {replace && <ReplaceDialog item={replace} onClose={() => setReplace(null)} onDone={msg => { setReplace(null); notify(t('Ersetzt'), msg); load() }} />}
    </div>
  )
}

/** Einen Core-Stand wählen und nur diese Datenbank daraus ersetzen - vorher steht da, was dabei verloren geht. */
function ReplaceDialog({ item, onClose, onDone }: { item: Result; onClose: () => void; onDone: (message: string) => void }) {
  const { fail } = useApp()
  const [stands, setStands] = useState<Stand[] | null>(null)
  const [pick, setPick] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { api.get<Stand[]>('/api/admin/health/stands').then(l => { setStands(l); setPick(l[0]?.name ?? null) }).catch(e => { fail(e); setStands([]) }) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const chosen = stands?.find(s => s.name === pick)
  const user = item.name.startsWith('user:')
  const go = async () => {
    if (!pick) return
    setBusy(true)
    try { const x = await api.post<{ message: string }>('/api/admin/health/replace', { name: item.name, stand: pick }); onDone(x.message) }
    catch (e) { fail(e); setBusy(false) }
  }
  return <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
    <DialogSurface style={{ maxWidth: 520 }}>
      <DialogBody>
        <DialogTitle>{t('„{name}“ aus Backup ersetzen', { name: nameOf(item) })}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {stands === null ? <Spinner size="small" /> : stands.length === 0
            ? <Text>{t('Es ist kein Core-Backup eingerichtet, oder am Ziel liegt kein Stand.')}</Text>
            : <>
              <Field label={t('backup|Stand')}>
                <Dropdown value={chosen ? when(chosen.created) : ''} selectedOptions={pick ? [pick] : []} onOptionSelect={(_, d) => setPick(d.optionValue ?? null)}>
                  {stands.map(s => <Option key={s.name} value={s.name} text={when(s.created)}>{when(s.created)} · {fmtSize(s.bytes)}</Option>)}
                </Dropdown>
              </Field>
              {chosen && <MessageBar intent="warning" layout="multiline"><MessageBarBody>
                {user
                  ? t('Nur die Datenbank von {name} wird durch den Stand vom {when} ersetzt. Änderungen von {name} seit diesem Zeitpunkt gehen verloren. Dateien, die danach hochgeladen wurden, bleiben erhalten und liegen ohne Namen im Ordner „Nach Wiederherstellung gefunden“.', { name: nameOf(item), when: when(chosen.created) })
                  : t('Nur die Datenbank „{name}“ wird durch den Stand vom {when} ersetzt. Änderungen darin seit diesem Zeitpunkt gehen für alle verloren.', { name: nameOf(item), when: when(chosen.created) })}
                {' '}{t('Die bisherige Datei bleibt als Kopie erhalten. Alle anderen Datenbanken bleiben unverändert.')}
              </MessageBarBody></MessageBar>}
            </>}
        </DialogContent>
        <DialogActions>
          <Button disabled={busy} onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" disabled={busy || !pick} icon={busy ? <Spinner size="tiny" /> : undefined} onClick={go}>{t('Ersetzen')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}
