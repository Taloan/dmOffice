import { Avatar, Button, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Attach16Regular, Flag16Filled, Flag16Regular, ArrowReply16Regular, ArrowForward16Regular, ShieldError16Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useRef } from 'react'
import { addrText, Folder, MailItem } from '../api'
import { Snippet } from '../shell/Snippet'
import { fmtDate } from '../shell/hooks'
import { folderLabel } from './FolderPane'
import { usePrefs } from '../prefs'
import { CategoryChips } from './Categories'
import { locale, t } from '../i18n'

const useStyles = makeStyles({
  root: { flex: 1, overflowY: 'auto' },
  group: { padding: '10px 12px 4px', fontSize: '12px', fontWeight: 600, color: tokens.colorNeutralForeground2 },
  item: {
    display: 'flex', gap: '10px', padding: '8px 10px 8px 8px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, cursor: 'pointer',
    borderLeft: '3px solid transparent', position: 'relative',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':hover .dm-flag': { opacity: 1 },
  },
  unread: { borderLeftColor: tokens.colorBrandBackground },
  selected: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  body: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '1px' },
  row: { display: 'flex', alignItems: 'center', gap: '6px', minWidth: 0 },
  from: { flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: '14px' },
  subject: { flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: '13px' },
  bold: { fontWeight: 600 },
  brand: { color: tokens.colorBrandForeground1 },
  // Treffer im Anhang: zwei Zeilen, damit das gefundene Wort sichtbar bleibt
  match: { lineHeight: '16px', minWidth: 0, fontSize: '12px', color: tokens.colorNeutralForeground3, overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflowWrap: 'anywhere' },
  preview: { lineHeight: '16px', minWidth: 0, fontSize: '12px', color: tokens.colorNeutralForeground3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  date: { fontSize: '12px', color: tokens.colorNeutralForeground3, flexShrink: 0 },
  // Nicht gekennzeichnet: grauer Umriss beim Darueberfahren (nicht in der Markenfarbe - das sah aus wie gesetzt); gekennzeichnet: rote Flagge
  flag: { opacity: 0, minWidth: '20px', height: '20px', padding: 0, color: tokens.colorNeutralForeground3, ':hover': { color: tokens.colorNeutralForeground1 } },
  flagOn: { opacity: 1, color: tokens.colorPaletteRedForeground1, ':hover': { color: tokens.colorPaletteRedForeground1 } },
  // Eine Zeile, gekuerzt - sonst wird die Zeile hoeher als die anderen
  tag: { fontSize: '11px', lineHeight: '16px', padding: '0 4px', borderRadius: '3px', backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2,
    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '40%', flexShrink: 0 },
  unsure: { backgroundColor: tokens.colorPaletteYellowBackground2 },
  empty: { padding: '40px 20px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
})

function group(d: Date): string {
  const now = new Date()
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const day = new Date(d.getFullYear(), d.getMonth(), d.getDate())
  // Gerundet: ueber die Zeitumstellung hat ein Tag 23 oder 25 Stunden
  const diff = Math.round((today.getTime() - day.getTime()) / 86400000)
  if (diff <= 0) return t('Heute')
  if (diff === 1) return t('Gestern')
  if (diff < today.getDay() || (today.getDay() === 0 && diff < 7)) return t('Diese Woche')
  if (diff < 14) return t('Letzte Woche')
  if (d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth()) return t('Diesen Monat')
  return d.toLocaleDateString(locale, { month: 'long', year: 'numeric' })
}

interface Props {
  items: MailItem[]
  selected: number[]
  onSelect: (ids: number[]) => void
  loading: boolean
  showFolder: boolean
  folders: Record<string, Folder[]>
  spamCutoffs: null
  more: boolean
  onMore: () => void
  onToggleFlag: (i: MailItem) => void
  /** Wann eine zurueckgelegte Mail wiederkommt (Wiedervorlage). */
  snoozeOf?: (i: MailItem) => string | undefined
  /** Rechtsklick: Mail (ggf. erst auswaehlen) und Mausposition. */
  onContextMenu?: (item: MailItem, x: number, y: number) => void
  /** Was eine leere Liste bedeutet: leerer Posteingang, keine Treffer, nichts fuer den Filter, leerer Ordner. */
  empty?: 'inbox' | 'search' | 'filter' | 'folder'
  /** Doppelklick: Mail im eigenen Fenster oeffnen */
  onOpen?: (item: MailItem) => void
  /** Ueberschriften nach Datum (Heute, Letzte Woche ...) - nicht bei Sortierung nach Absender, Betreff, Relevanz */
  grouped?: boolean
}

// Leerer Posteingang: ein freundlicher Spruch statt "Hier ist nichts."
const inboxZero = () => [
  t('Posteingang leer – Zeit für einen Kaffee. ☕'),
  t('Alles erledigt. Gute Arbeit!'),
  t('Keine neuen Mails. Genieß die Ruhe.'),
  t('Inbox Zero erreicht. 🎉'),
  t('Leer gefegt. Wie wär’s mit einer kleinen Pause?'),
  t('Nichts mehr zu tun – zumindest hier.'),
  t('Alles abgearbeitet. Der Rest des Tages gehört dir.'),
]

export function MessageList({ items, selected, onSelect, loading, showFolder, folders, more, onMore, onToggleFlag, snoozeOf, onContextMenu, empty, onOpen, grouped = true }: Props) {
  // Einmal je Anzeige auswuerfeln, nicht bei jedem Neuzeichnen
  const saying = useMemo(() => { const l = inboxZero(); return l[Math.floor(Math.random() * l.length)] }, [empty])
  const s = useStyles()
  const { prefs } = usePrefs()
  const anchor = useRef<number | null>(null)
  // Ausgewaehlte Mail sichtbar machen - auch wenn sie beim Oeffnen des Ordners wiederhergestellt wird
  const rootRef = useRef<HTMLDivElement>(null)
  const shownSel = useRef<number | null>(null)
  useEffect(() => {
    const id = selected.length === 1 ? selected[0] : null
    if (id == null) { shownSel.current = null; return }
    if (shownSel.current === id) return
    const el = rootRef.current?.querySelector(`[data-mail="${id}"]`)
    if (!el) return
    el.scrollIntoView({ block: 'nearest' })
    shownSel.current = id
  }, [selected, items])
  // Textgroesse und Abstand (Einstellungen -> E-Mail -> Nachrichtenliste)
  const zoom = prefs.textSize === 'small' ? 0.92 : prefs.textSize === 'large' ? 1.12 : 1
  const padY = prefs.density === 'compact' ? 3 : prefs.density === 'roomy' ? 12 : 8
  const avatars = prefs.showAvatars ?? true
  const preview = prefs.showPreview ?? true

  const click = (e: React.MouseEvent, item: MailItem) => {
    if (e.ctrlKey || e.metaKey) {
      onSelect(selected.includes(item.id) ? selected.filter(x => x !== item.id) : [...selected, item.id])
      anchor.current = item.id
    } else if (e.shiftKey && anchor.current != null) {
      const a = items.findIndex(i => i.id === anchor.current)
      const b = items.findIndex(i => i.id === item.id)
      const [from, to] = a < b ? [a, b] : [b, a]
      onSelect(items.slice(from, to + 1).map(i => i.id))
    } else {
      onSelect([item.id])
      anchor.current = item.id
    }
  }

  const onScroll = (e: React.UIEvent<HTMLDivElement>) => {
    const el = e.currentTarget
    if (more && !loading && el.scrollTop + el.clientHeight > el.scrollHeight - 300) onMore()
  }

  if (!items.length && loading) return <Spinner style={{ marginTop: 40 }} />
  if (!items.length) return (
    <div className={s.empty}>
      <Text>{empty === 'inbox' ? saying : empty === 'search' ? t('Keine Treffer.') : empty === 'filter' ? t('Keine Mails für diesen Filter.')
        : empty === 'folder' ? t('Dieser Ordner ist leer.') : t('Hier ist nichts.')}</Text>
    </div>
  )

  let last = ''
  return (
    <div ref={rootRef} className={s.root} onScroll={onScroll} role="listbox" aria-multiselectable style={{ zoom }}>
      {items.map(item => {
        const g = group(new Date(item.date))
        const header = grouped && g !== last ? <div className={s.group} key={'g' + item.id}>{g}</div> : null
        last = g
        const isSel = selected.includes(item.id)
        const unsure = item.spam != null && item.spam >= 0.2 && item.spam < 0.9
        const folder = showFolder ? folders[item.account]?.find(f => f.path === item.folder) : undefined
        return [header,
          <div key={item.id} data-mail={item.id} role="option" aria-selected={isSel}
            className={mergeClasses(s.item, !item.seen && s.unread, isSel && s.selected)} style={{ paddingTop: padY, paddingBottom: padY }}
            onClick={e => click(e, item)} onDoubleClick={() => onOpen?.(item)} draggable
            onContextMenu={e => {
              if (!onContextMenu) return
              e.preventDefault()
              // Wie in Outlook: Rechtsklick ausserhalb der Auswahl waehlt diese eine Mail
              if (!selected.includes(item.id)) { onSelect([item.id]); anchor.current = item.id }
              onContextMenu(item, e.clientX, e.clientY)
            }}
            onDragStart={e => {
              const ids = isSel ? selected : [item.id]
              e.dataTransfer.setData('application/x-dmoffice-mail', JSON.stringify({ account: item.account, ids }))
              e.dataTransfer.effectAllowed = 'move'
            }}>
            {avatars && <Avatar name={item.from?.name || item.from?.address || '?'} size={32} color="colorful" />}
            <div className={s.body}>
              <div className={s.row}>
                <span className={mergeClasses(s.from, !item.seen && s.bold)}>{addrText(item.from) || t('(ohne Absender)')}</span>
                {item.answered && <ArrowReply16Regular />}
                {item.forwarded && <ArrowForward16Regular />}
                {item.hasAttachments && <Attach16Regular />}
                <Button appearance="transparent" size="small" className={mergeClasses(s.flag, 'dm-flag', item.flagged && s.flagOn)}
                  icon={item.flagged ? <Flag16Filled /> : <Flag16Regular />}
                  onClick={e => { e.stopPropagation(); onToggleFlag(item) }} aria-label={t('Kennzeichnen')} />
              </div>
              <div className={s.row}>
                <span className={mergeClasses(s.subject, !item.seen && s.bold, !item.seen && s.brand)}>{item.subject || t('(ohne Betreff)')}</span>
                <span className={s.date}>{fmtDate(item.date)}</span>
              </div>
              {(preview || unsure || folder || item.keywords?.length > 0) && <div className={s.row}>
                <CategoryChips keywords={item.keywords} />
                {snoozeOf?.(item) && <span className={s.tag} title={t('Wiedervorlage')}>⏰ {fmtDate(snoozeOf(item)!)}</span>}
                {unsure && <span className={mergeClasses(s.tag, s.unsure)}><ShieldError16Regular style={{ verticalAlign: -3 }} /> {t('unsicher')}</span>}
                {folder && <span className={s.tag} title={folder.path}>{folderLabel(folder)}</span>}
                {preview && !item.match && <span className={s.preview}>{item.preview || (item.cached ? '' : '…')}</span>}
              </div>}
              {item.match && <div className={s.row}>
                <span className={s.match} title={item.match.name}><Attach16Regular style={{ verticalAlign: -3 }} /> {t('im Anhang {name}', { name: item.match.name })}: <Snippet text={item.match.snippet} /></span>
              </div>}
            </div>
          </div>]
      })}
      {more && <div style={{ padding: 12, textAlign: 'center' }}>{loading ? <Spinner size="tiny" /> : <Button appearance="subtle" onClick={onMore}>{t('Mehr laden')}</Button>}</div>}
    </div>
  )
}
