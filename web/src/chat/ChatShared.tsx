import { Divider, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Chat20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { fmtDate } from '../shell/hooks'

const useStyles = makeStyles({
  section: { fontWeight: 600, display: 'flex', alignItems: 'center', gap: '6px' },
  row: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, cursor: 'pointer', ':hover': { color: tokens.colorNeutralForeground1 } },
})

interface SharedIn { room: number; kind: string; title: string; people: string[]; last: string; count: number }

/** Datei-Details: in welchen Unterhaltungen diese eigene Datei verschickt wurde (antippen oeffnet die Unterhaltung). */
export function ChatShared({ fileId }: { fileId: number }) {
  const s = useStyles()
  const [list, setList] = useState<SharedIn[]>([])
  useEffect(() => { api.get<SharedIn[]>(`/api/chat/shared?fileId=${fileId}`).then(setList).catch(() => setList([])) }, [fileId])
  if (!list.length) return null
  return (
    <>
      <Divider style={{ flexGrow: 0 }} />
      <span className={s.section}><Chat20Regular /> {t('Im Chat geteilt mit')}</span>
      {list.map(x => (
        <Text key={x.room} className={s.row} onClick={() => { location.hash = `#/chat/${x.room}` }}>
          {x.kind === 'group' ? `${x.title} (${x.people.join(', ')})` : x.title} · {fmtDate(x.last)}{x.count > 1 ? ` · ${x.count}×` : ''}
        </Text>
      ))}
    </>
  )
}
