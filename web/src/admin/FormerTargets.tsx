import { Button, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, ProgressBar, Spinner, Text } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { askConfirm } from '../shell/context'

interface Run { busy: boolean; done: number; total: number; error: string | null; count: number | null; bytes: number | null }
interface Former { id: string; purpose: 'storage' | 'backup' | 'files'; target: string; where: string; replaced: string; inUse: boolean; emptyPrefix: boolean; run: Run | null }

const purposeLabel = (p: Former['purpose']) => p === 'backup' ? t('Core-Backup') : p === 'files' ? t('Files-Backup') : t('Storage Extender')

/**
 * Alte Daten an früheren Zielen: Nach einem Wechsel des Ziels liegen am bisherigen noch Daten. Hier lassen sie sich löschen (nur, was
 * dmOffice unter seinem Präfix bzw. Ordner abgelegt hat) oder behalten - dann vergisst dmOffice nur die Zugangsdaten.
 */
export function FormerTargets({ purposes }: { purposes: Former['purpose'][] }) {
  const { fail, notify } = useApp()
  const [list, setList] = useState<Former[]>([])
  const [counting, setCounting] = useState<string | null>(null)
  const load = () => api.get<Former[]>('/api/admin/storage/former').then(l => setList(l.filter(f => purposes.includes(f.purpose)))).catch(() => {})
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Während gelöscht wird, regelmäßig den Fortschritt holen
  const busy = list.some(f => f.run?.busy)
  useEffect(() => {
    if (!busy) return
    const h = setInterval(load, 2000)
    return () => clearInterval(h)
  }, [busy]) // eslint-disable-line react-hooks/exhaustive-deps
  if (list.length === 0) return null

  const count = async (f: Former) => {
    setCounting(f.id)
    try { return await api.post<{ count: number; bytes: number }>(`/api/admin/storage/former/${f.id}/count`) }
    catch (e) { fail(e); return null }
    finally { setCounting(null); load() }
  }
  const remove = async (f: Former) => {
    const size = await count(f)
    if (!size) return
    if (size.count === 0) {
      if (await askConfirm(t('Nichts mehr da'), t('Am bisherigen Ziel liegen keine Daten von dmOffice mehr. Die Zugangsdaten vergessen?'), t('Vergessen'))) forget(f, true)
      return
    }
    const text = t('{count} Dateien ({size}) an „{where}“ werden endgültig gelöscht. Das lässt sich nicht rückgängig machen.', { count: size.count.toLocaleString(locale), size: fmtSize(size.bytes), where: f.where })
      + (f.emptyPrefix ? ' ' + t('Für dieses Ziel war kein Präfix eingestellt: gelöscht wird alles, was dort liegt.') : '')
    if (!await askConfirm(t('Alte Daten löschen'), text, t('Endgültig löschen'))) return
    try { await api.post(`/api/admin/storage/former/${f.id}/delete`); notify(t('Löschen läuft im Hintergrund'), t('Den Fortschritt siehst du hier und unter Aktivität.')); load() } catch (e) { fail(e) }
  }
  const forget = async (f: Former, asked = false) => {
    if (!asked && !await askConfirm(t('Alte Daten behalten'), t('dmOffice vergisst das bisherige Ziel samt Zugangsdaten. Die Daten dort bleiben liegen; löschen lassen sie sich dann nur noch dort selbst.'), t('Behalten'))) return
    try { await api.del(`/api/admin/storage/former/${f.id}`); load() } catch (e) { fail(e) }
  }

  return <>{list.map(f => {
    const r = f.run
    return <MessageBar key={f.id} intent={r?.error ? 'error' : 'warning'} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{t('Alte Daten am bisherigen Ziel ({purpose})', { purpose: purposeLabel(f.purpose) })}</MessageBarTitle>
        <div>{t('{where} – ersetzt am {date}.', { where: f.where, date: new Date(f.replaced).toLocaleDateString(locale) })}</div>
        {f.inUse && <div>{t('Dieser Ort wird gerade wieder genutzt. Löschen ist deshalb nicht möglich.')}</div>}
        {r?.count != null && !r.busy && !r.error && r.bytes != null && <div>{t('Dort liegen {count} Dateien ({size}).', { count: r.count.toLocaleString(locale), size: fmtSize(r.bytes) })}</div>}
        {r?.busy && <div style={{ marginTop: 6 }}>
          <Text size={200}>{r.total ? t('Gelöscht: {done} von {total}', { done: r.done.toLocaleString(locale), total: r.total.toLocaleString(locale) }) : t('Dateien werden aufgelistet …')}</Text>
          <ProgressBar value={r.total ? r.done / r.total : undefined} style={{ marginTop: 4 }} />
        </div>}
        {r?.error && <div>{t('Löschen abgebrochen: {error}', { error: r.error })}</div>}
      </MessageBarBody>
      {!r?.busy && <MessageBarActions>
        {!f.inUse && <Button size="small" disabled={counting === f.id} icon={counting === f.id ? <Spinner size="tiny" /> : undefined} onClick={() => remove(f)}>{t('Alte Daten löschen')}</Button>}
        <Button size="small" onClick={() => forget(f)}>{t('Behalten')}</Button>
      </MessageBarActions>}
    </MessageBar>
  })}</>
}
