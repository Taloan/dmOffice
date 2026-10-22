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

interface Storage { database: number; files: number; used?: number; fileQuota: number; diskFree?: number | null; version: string }

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
  // Alles, was der Benutzer belegt (Mails, Kalender, Kontakte, Notizen und Dateien) - dieselbe Zahl wie in der Verwaltung
  const used = info.used ?? info.database + info.files
  // Hat die Verwaltung ein Kontingent gesetzt (je Benutzer oder fuer alle), zaehlt dieses - nie mehr, als der Server
  // tatsaechlich noch frei hat. Ohne Kontingent: alles, was belegt ist, plus der freie Platz des Servers.
  const limited = info.fileQuota > 0
  const reachable = info.diskFree != null ? used + info.diskFree : Infinity
  const total = limited ? Math.min(info.fileQuota, reachable) : info.diskFree != null ? used + info.diskFree : 0
  const shown = used
  const share = total > 0 ? Math.min(1, shown / total) : 0
  return (
    <Tooltip relationship="description" content={t('Datenbank (Mails, Kalender, Kontakte, Notizen): {db} · Dateien: {files}', { db: fmtSize(info.database), files: fmtSize(info.files) })}>
      <div className={s.root} onClick={() => { location.hash = '#/settings/stats' }} role="link">
        <Text className={s.text}>{total > 0 ? fmtSize(shown) : t('{size} belegt', { size: fmtSize(shown) })}</Text>
        {total > 0 && <ProgressBar className={s.bar} value={share} thickness="medium" color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />}
        {total > 0 && <Text className={s.text}>{fmtSize(total)}</Text>}
      </div>
    </Tooltip>
  )
}
