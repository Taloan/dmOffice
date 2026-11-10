import { ProgressBar, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { t } from '../i18n'

const useStyles = makeStyles({
  root: {
    display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', padding: '2px 8px', borderRadius: tokens.borderRadiusMedium,
    marginRight: '4px', minWidth: 0, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  // Bei wenig Platz weichen Balken und Text, die Versionsnummer bleibt
  bar: { width: '45px', flexShrink: 0 },
  text: { fontSize: '12px', color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
})

/** Auf window ausloesen, wenn sich Kontingent oder Belegung geaendert haben. */
export const STORAGE_CHANGED = 'dmoffice:storage'

export interface Storage { database: number; files: number; used?: number; fileQuota: number; diskFree?: number | null; version: string }

/**
 * Belegt und verfügbar - eine Rechnung für Kopfzeile und Dateiablage. Belegt: alles, was der Benutzer belegt (Mails, Kalender,
 * Kontakte, Notizen und Dateien), dieselbe Zahl wie in der Verwaltung. Verfügbar: das Kontingent (je Benutzer oder für alle),
 * aber nie mehr, als der Server tatsächlich noch frei hat; ohne Kontingent alles Belegte plus der freie Platz des Servers.
 * total 0: keine Grenze bekannt. capped: das Kontingent ist größer als der Platz auf dem Server.
 */
export function storageLimit(info: Storage) {
  const used = info.used ?? info.database + info.files
  const quota = info.fileQuota > 0 ? info.fileQuota : 0
  const reachable = info.diskFree != null ? used + info.diskFree : Infinity
  const total = quota > 0 ? Math.min(quota, reachable) : info.diskFree != null ? reachable : 0
  return { used, total, quota, free: Math.max(0, total - used), diskFree: info.diskFree ?? null, capped: quota > 0 && reachable < quota }
}

/** In der Kopfzeile: belegter Platz von dem, was zur Verfuegung steht, mit Balken. Klick: Speicher & Statistik. */
export function StorageInfo({ mobile }: { mobile: boolean }) {
  const s = useStyles()
  const [info, setInfo] = useState<Storage | null>(null)
  useEffect(() => {
    const load = () => { if (!document.hidden) api.get<Storage>('/api/settings/storage').then(setInfo).catch(() => {}) }
    load()
    const timer = setInterval(load, 60_000)
    // Nach einer Aenderung des Kontingents und beim Zurueckkehren ins Fenster sofort neu holen
    window.addEventListener(STORAGE_CHANGED, load)
    document.addEventListener('visibilitychange', load)
    return () => { clearInterval(timer); window.removeEventListener(STORAGE_CHANGED, load); document.removeEventListener('visibilitychange', load) }
  }, [])
  if (!info || mobile) return null
  const { used: shown, total } = storageLimit(info)
  const share = total > 0 ? Math.min(1, shown / total) : 0
  return (
    <Tooltip relationship="description" content={(total > 0 ? t('Belegt: {used} von {total}', { used: fmtSize(shown), total: fmtSize(total) }) + ' · ' : '') + t('Datenbank: {db} · Dateien: {files}', { db: fmtSize(info.database), files: fmtSize(info.files) })
      + (total > shown ? ' · ' + t('Frei: {size}', { size: fmtSize(total - shown) }) : '')}>
      <div className={s.root} onClick={() => { location.hash = '#/settings/stats' }} role="link">
        <Text className={s.text}>{total > 0 ? fmtSize(shown) : t('{size} belegt', { size: fmtSize(shown) })}</Text>
        {total > 0 && <ProgressBar className={s.bar} value={share} thickness="medium" color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />}
        {total > 0 && <Text className={s.text}>{fmtSize(total)}</Text>}
      </div>
    </Tooltip>
  )
}
