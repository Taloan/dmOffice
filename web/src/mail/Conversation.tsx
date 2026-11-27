// Ansicht „Als Unterhaltungen“ (Kennung mailthreads): die Mails einer Unterhaltung untereinander im Lesebereich
import { Avatar, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons'
import { ReactNode, useEffect, useState } from 'react'
import { addrText, Folder, MailItem } from '../api'
import { fmtDate } from '../shell/hooks'
import { t } from '../i18n'
import { folderLabel } from './FolderPane'

const useStyles = makeStyles({
  root: { flex: 1, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px 0 16px', backgroundColor: tokens.colorNeutralBackground2 },
  title: { padding: '4px 20px 4px', display: 'flex', alignItems: 'baseline', gap: '8px', flexWrap: 'wrap' },
  subject: { fontSize: '20px', fontWeight: 600, lineHeight: '26px', wordBreak: 'break-word' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  card: { margin: '0 12px', borderRadius: tokens.borderRadiusXLarge, border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1, overflow: 'hidden', flexShrink: 0 },
  current: { border: `1px solid ${tokens.colorBrandStroke1}` },
  bar: { display: 'flex', alignItems: 'center', gap: '10px', padding: '8px 12px', cursor: 'pointer', minWidth: 0, background: 'none', border: 'none',
    width: '100%', textAlign: 'left', color: 'inherit', font: 'inherit', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  who: { fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', flexShrink: 1, minWidth: 0 },
  preview: { flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', color: tokens.colorNeutralForeground3, fontSize: '13px' },
  chip: { fontSize: '11px', padding: '0 6px', borderRadius: '8px', backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2, whiteSpace: 'nowrap', flexShrink: 0 },
  date: { fontSize: '12px', color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap', flexShrink: 0 },
  unread: { fontWeight: 700 },
})

interface Props {
  /** Die gewählte Zeile der Liste */
  head: MailItem
  /** Alle Mails der Unterhaltung, neueste zuerst (mit der gewählten) */
  items: MailItem[]
  folders: Record<string, Folder[]>
  /** Inhalt einer aufgeklappten Mail (Lesebereich ohne eigene Bildlaufleiste) */
  render: (m: MailItem) => ReactNode
}

export function ConversationPane({ head, items, folders, render }: Props) {
  const s = useStyles()
  // Aufgeklappt: die gewählte, die neueste und die ungelesenen
  const [open, setOpen] = useState<Set<number>>(new Set())
  useEffect(() => {
    setOpen(new Set([head.id, ...(items[0] ? [items[0].id] : []), ...items.filter(i => !i.seen).map(i => i.id)]))
  }, [head.threadKey]) // eslint-disable-line react-hooks/exhaustive-deps
  const toggle = (id: number) => setOpen(o => { const n = new Set(o); if (n.has(id)) n.delete(id); else n.add(id); return n })
  return (
    <div className={s.root}>
      <div className={s.title}>
        <span className={s.subject}>{head.subject || t('(ohne Betreff)')}</span>
        <Text className={s.muted}>{t('{n} Mails in dieser Unterhaltung', { n: items.length })}</Text>
      </div>
      {items.map(m => {
        const isOpen = open.has(m.id)
        const folder = folders[m.account]?.find(f => f.path === m.folder)
        const who = addrText(m.from) || m.from?.address || '?'
        return (
          <div key={m.id} className={mergeClasses(s.card, m.id === head.id && s.current)}>
            <button type="button" className={s.bar} aria-expanded={isOpen} onClick={() => toggle(m.id)}>
              {isOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
              <Avatar name={who} size={24} color="colorful" />
              <span className={mergeClasses(s.who, !m.seen && s.unread)}>{who}</span>
              <span className={s.preview}>{isOpen ? '' : m.preview}</span>
              {m.folder !== head.folder && folder && <span className={s.chip}>{folderLabel(folder)}</span>}
              <span className={s.date}>{fmtDate(m.date)}</span>
            </button>
            {isOpen && render(m)}
          </div>
        )
      })}
    </div>
  )
}
