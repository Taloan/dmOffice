import { ProgressBar, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { t } from '../i18n'

const useStyles = makeStyles({
  root: {
    flexShrink: 0, borderTop: `1px solid ${tokens.colorNeutralStroke2}`, padding: '6px 12px 8px', cursor: 'pointer',
    display: 'flex', flexDirection: 'column', gap: '4px', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  row: { display: 'flex', alignItems: 'center', gap: '8px', fontSize: '11px', color: tokens.colorNeutralForeground3 },
  version: { marginLeft: 'auto', whiteSpace: 'nowrap', opacity: 0.8 },
})

interface Storage { database: number; files: number; fileQuota: number; version: string }

/** Unten im Ordnerbereich: belegter Platz (wie Proton) und die Versionsnummer. Klick: Speicher & Statistik. */
export function StorageFooter() {
  const s = useStyles()
  const [info, setInfo] = useState<Storage | null>(null)
  useEffect(() => {
    const load = () => api.get<Storage>('/api/settings/storage').then(setInfo).catch(() => {})
    load()
    const timer = setInterval(load, 10 * 60_000)
    return () => clearInterval(timer)
  }, [])
  if (!info) return null
  const share = info.fileQuota > 0 ? Math.min(1, info.files / info.fileQuota) : 0
  return (
    <Tooltip relationship="description" content={t('Datenbank (Mails, Kalender, Kontakte, Notizen): {db} · Dateien: {files}', { db: fmtSize(info.database), files: fmtSize(info.files) })}>
      <div className={s.root} onClick={() => { location.hash = '#/settings/stats' }}>
        {info.fileQuota > 0 && <ProgressBar value={share} thickness="medium" color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />}
        <div className={s.row}>
          <Text size={100}>
            {info.fileQuota > 0
              ? t('{used} / {quota} Dateien', { used: fmtSize(info.files), quota: fmtSize(info.fileQuota) })
              : t('{size} belegt', { size: fmtSize(info.database + info.files) })}
          </Text>
          <span className={s.version}>{info.version}</span>
        </div>
      </div>
    </Tooltip>
  )
}
