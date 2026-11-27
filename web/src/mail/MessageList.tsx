import { Avatar, Button, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Attach16Regular, Flag16Filled, Flag16Regular, ArrowReply16Regular, ArrowForward16Regular, ShieldError16Regular } from '@fluentui/react-icons'
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons' // [feature:mailthreads]
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { addrText, featureOn, Folder, MailItem } from '../api'
import { Snippet } from '../shell/Snippet'
import { fmtDate } from '../shell/hooks'
import { folderLabel } from './FolderPane'
import { usePrefs } from '../prefs'
import { CategoryChips } from './Categories'
import { Expiry, ExpiryTag } from './ExpiryMenu'
import { mailAccountType } from './dragTypes'
import { locale, t } from '../i18n'
import { offsetsOf, rangeOf } from './virtualList'

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
  preview2: { lineHeight: '16px', fontSize: '12px', color: tokens.colorNeutralForeground3, overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflowWrap: 'anywhere' },
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
  // [feature:mailtocolumn]
  to: { fontSize: '12px', color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '45%', flexShrink: 1,
  },
  // [/feature:mailtocolumn]
  // [feature:mailavatarfilter]
  avatarButton: { cursor: 'pointer', borderRadius: '50%', flexShrink: 0, ':hover': { outline: `2px solid ${tokens.colorBrandStroke1}` },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` } },
  // [/feature:mailavatarfilter]
  // [feature:mailthreads]
  chevron: { minWidth: '20px', width: '20px', height: '20px', padding: 0, flexShrink: 0, alignSelf: 'center' },
  count: { fontSize: '11px', lineHeight: '16px', padding: '0 5px', borderRadius: '8px', flexShrink: 0,
    backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2 },
  member: { backgroundColor: tokens.colorNeutralBackground2 },
  // [/feature:mailthreads]
})

// [feature:mailtocolumn]
/** Spalte „An“ in der Kartenliste: Empfänger hinter dem Absender (nur Anzeige). */
function ToPill({ address, className }: { address: string; className: string }) {
  return <span className={className} title={t('An: {who}', { who: address })}>→ {address}</span>
}
// [/feature:mailtocolumn]

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
  /** Läuft gerade eine Anfrage zum Nachladen (nur dann der Kreis unter der Liste) */
  loadingMore?: boolean
  onMore: () => void
  onToggleFlag: (i: MailItem) => void
  /** Wann eine zurueckgelegte Mail wiederkommt (Wiedervorlage). */
  snoozeOf?: (i: MailItem) => string | undefined
  /** Entfernen nach Zeit: Sanduhr mit Zeitpunkt */
  expiryOf?: (i: MailItem) => Expiry | undefined
  /** Klick auf einen Kategorie-Badge: Kategorie von dieser Mail entfernen. */
  onRemoveCategory?: (i: MailItem, key: string) => void
  /** Rechtsklick: Mail (ggf. erst auswaehlen) und Mausposition. */
  onContextMenu?: (item: MailItem, x: number, y: number) => void
  /** Was eine leere Liste bedeutet: leerer Posteingang, keine Treffer, nichts fuer den Filter, leerer Ordner. */
  empty?: 'inbox' | 'search' | 'filter' | 'folder'
  /** Doppelklick: Mail im eigenen Fenster oeffnen */
  onOpen?: (item: MailItem) => void
  /** Ueberschriften nach Datum (Heute, Letzte Woche ...) - nicht bei Sortierung nach Absender, Betreff, Relevanz */
  grouped?: boolean
  /** Eigene Überschrift je Mail statt nur nach Datum („Ähnliche Mails“: Stufe und Datum); date ist die Datumsgruppe */
  groupOf?: (item: MailItem, date: string) => string
  // [feature:mailtocolumn]
  /** Spalte „An“: Empfänger, wenn er von der Kontoadresse abweicht (Catchall-Alias zuerst); undefined: nichts anzeigen */
  toOf?: (item: MailItem) => string | undefined
  // [/feature:mailtocolumn]
  /** Klick auf das Absenderbild: alle Mails dieses Absenders zeigen (Kennung mailavatarfilter) */
  onSenderFilter?: (address: string) => void // [feature:mailavatarfilter]
  // [feature:mailthreads]
  /** Als Unterhaltungen: aufgeklappte Unterhaltungen (Schlüssel) und Auf-/Zuklappen */
  expanded?: Set<string>
  onToggleThread?: (item: MailItem) => void
  // [/feature:mailthreads]
}

// Leerer Posteingang: ein freundlicher Spruch statt "Hier ist nichts."
/** Eine Zeile der Liste: Datumsüberschrift oder Mail */
type Entry = { head: string; key: string } | { item: MailItem; member: boolean; key: string }
/** Rand über und unter dem sichtbaren Ausschnitt, der schon im DOM steht (Pfeiltasten, schnelles Scrollen) */
const OVERSCAN = 800

const inboxZero = () => [
  t('Posteingang leer – Zeit für einen Kaffee. ☕'),
  t('Alles erledigt. Gute Arbeit!'),
  t('Keine neuen Mails. Genieß die Ruhe.'),
  t('Inbox Zero erreicht. 🎉'),
  t('Leer gefegt. Wie wär’s mit einer kleinen Pause?'),
  t('Nichts mehr zu tun – zumindest hier.'),
  t('Alles abgearbeitet. Der Rest des Tages gehört dir.'),
]

export function MessageList({ items, selected, onSelect, loading, showFolder, folders, more, loadingMore = false, onMore, onToggleFlag, snoozeOf, expiryOf, onContextMenu, onRemoveCategory, empty, onOpen, grouped = true, groupOf
  , toOf // [feature:mailtocolumn]
  , onSenderFilter // [feature:mailavatarfilter]
  , expanded, onToggleThread // [feature:mailthreads]
}: Props) {
  // Einmal je Anzeige auswuerfeln, nicht bei jedem Neuzeichnen
  const saying = useMemo(() => { const l = inboxZero(); return l[Math.floor(Math.random() * l.length)] }, [empty])
  const s = useStyles()
  const { prefs } = usePrefs()
  const anchor = useRef<number | null>(null)
  // Textgroesse und Abstand (Einstellungen -> E-Mail -> Nachrichtenliste)
  // Die Liste folgt nur „Abstände“; Zoom gibt es für den Mailtext im Lesebereich
  const zoom = 1
  const density = prefs.listDensity ?? prefs.density ?? 'normal'
  const padY = density === 'compact' ? 2 : density === 'roomy' ? 14 : 7
  const avatarSize = density === 'compact' ? 24 : density === 'roomy' ? 40 : 32
  const rowGap = density === 'compact' ? 0 : density === 'roomy' ? 4 : 1
  const avatars = prefs.showAvatars ?? true
  // Vorschautext: aus, eine oder zwei Zeilen (Kennung mailpreview), sonst ein/aus
  const lines = featureOn('mailpreview') ? (prefs.previewLines ?? ((prefs.showPreview ?? true) ? 1 : 0)) : ((prefs.showPreview ?? true) ? 1 : 0)
  const preview = lines === 1

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

  // Nur der sichtbare Ausschnitt (mit Rand) steht im DOM, damit auch Listen mit Tausenden Mails flüssig bleiben. Die Zeilen sind
  // unterschiedlich hoch (Überschriften, Vorschau, Kategorien): gemessene Höhen werden gemerkt, die übrigen mit dem Mittel geschätzt.
  const rootRef = useRef<HTMLDivElement | null>(null)
  const [root, setRoot] = useState<HTMLDivElement | null>(null)
  const attach = useCallback((el: HTMLDivElement | null) => { rootRef.current = el; setRoot(el) }, [])
  const [box, setBox] = useState({ top: 0, height: 800 })
  // Ausgeblendet (Handy: Lesebereich offen) misst alles 0 – dann den letzten Stand behalten, sonst springt die Liste nach oben
  const readBox = (el: HTMLElement) => { if (el.clientHeight) setBox(b => b.top === el.scrollTop && b.height === el.clientHeight ? b : { top: el.scrollTop, height: el.clientHeight }) }
  useEffect(() => {
    if (!root) return
    readBox(root)
    const ro = new ResizeObserver(() => readBox(root))
    ro.observe(root)
    return () => ro.disconnect()
  }, [root])
  const entries = useMemo(() => {
    const out: Entry[] = []
    let last = ''
    for (const item of items) {
      let member = false
      member = !!item.member // [feature:mailthreads]
      // Aufgeklappte Mails einer Unterhaltung gehören zu ihrer Zeile, nicht zu einer eigenen Datumsgruppe
      const g = member ? last : groupOf ? groupOf(item, group(new Date(item.date))) : group(new Date(item.date))
      if (grouped && g !== last) out.push({ head: g, key: 'g' + item.id })
      last = g
      out.push({ item, member, key: String(item.id) })
    }
    return out
  }, [items, grouped, groupOf])
  const heights = useRef(new Map<string, number>())
  const avg = useRef({ head: [0, 0], mail: [0, 0] })
  // Andere Abstände oder Vorschau: alle Höhen neu messen
  const layoutKey = `${density}|${lines}|${avatars}`
  const lastLayout = useRef(layoutKey)
  if (lastLayout.current !== layoutKey) { lastLayout.current = layoutKey; heights.current.clear(); avg.current = { head: [0, 0], mail: [0, 0] } }
  const [measured, setMeasured] = useState(0)
  const offsets = useMemo(() => offsetsOf(entries.length, i => {
    const e = entries[i], h = heights.current.get(e.key)
    if (h !== undefined) return h
    const [sum, n] = 'head' in e ? avg.current.head : avg.current.mail
    return n ? sum / n : 'head' in e ? 30 : 64
  }), [entries, measured, layoutKey]) // eslint-disable-line react-hooks/exhaustive-deps
  const [first, last] = rangeOf(offsets, box.top, box.height, OVERSCAN)
  useLayoutEffect(() => {
    if (!root || !root.clientHeight) return
    let changed = false
    for (const el of root.querySelectorAll<HTMLElement>(':scope > [data-vkey]')) {
      const k = el.dataset.vkey!, h = el.offsetHeight, old = heights.current.get(k)
      if (old === h) continue
      if (old === undefined) { const a = el.dataset.vhead ? avg.current.head : avg.current.mail; a[0] += h; a[1]++ }
      heights.current.set(k, h)
      changed = true
    }
    if (changed) setMeasured(m => m + 1)
  })
  // Ausgewaehlte Mail sichtbar machen - auch wenn sie beim Oeffnen des Ordners wiederhergestellt wird oder (noch) nicht im DOM steht
  const shownSel = useRef<number | null>(null)
  useEffect(() => {
    const id = selected.length === 1 ? selected[0] : null
    if (id == null) { shownSel.current = null; return }
    if (shownSel.current === id || !root) return
    const el = root.querySelector(`[data-mail="${id}"]`)
    if (el) { el.scrollIntoView({ block: 'nearest' }); shownSel.current = id; return }
    // Außerhalb des Ausschnitts: grob hinscrollen, nach dem Zeichnen genau (dann steht sie im DOM)
    const i = entries.findIndex(e => 'item' in e && e.item.id === id)
    if (i >= 0) root.scrollTop = Math.max(0, offsets[i] - root.clientHeight / 2)
  }, [selected, items, root, first, last]) // eslint-disable-line react-hooks/exhaustive-deps

  const onScroll = (e: React.UIEvent<HTMLDivElement>) => {
    const el = e.currentTarget
    readBox(el)
    if (more && !loadingMore && el.scrollTop + el.clientHeight > el.scrollHeight - 300) onMore()
  }

  if (!items.length && loading) return <Spinner style={{ marginTop: 40 }} />
  if (!items.length) return (
    <div className={s.empty}>
      <Text>{empty === 'inbox' ? saying : empty === 'search' ? t('Keine Treffer.') : empty === 'filter' ? t('Keine Mails für diesen Filter.')
        : empty === 'folder' ? t('Dieser Ordner ist leer.') : t('Hier ist nichts.')}</Text>
    </div>
  )

  const renderItem = (item: MailItem, member: boolean, key: string) => {
    // [feature:mailthreads]
    const count = item.threadCount ?? 0
    const open = count > 1 && !!item.threadKey && !!expanded?.has(item.threadKey)
    // [/feature:mailthreads]
    const isSel = selected.includes(item.id)
    const unsure = item.spam != null && item.spam >= 0.2 && item.spam < 0.9
    const folder = showFolder || member ? folders[item.account]?.find(f => f.path === item.folder) : undefined
    let to: string | undefined
    // [feature:mailtocolumn]
    to = toOf?.(item)
    // [/feature:mailtocolumn]
    return (
      <div key={key} data-vkey={key} data-mail={item.id} role="option" aria-selected={isSel}
        className={mergeClasses(s.item, !item.seen && s.unread,
          member && s.member, // [feature:mailthreads]
          isSel && s.selected)}
        style={{ paddingTop: padY, paddingBottom: padY, fontSize: density === 'compact' ? 13 : undefined, paddingLeft: member ? avatarSize + 16 : undefined }}
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
          // Postfach als eigener Typ: beim Ziehen sind nur die Typen lesbar, nicht die Daten (Ordner anderer Postfächer sind kein Ziel)
          e.dataTransfer.setData(mailAccountType(item.account), '')
          // Ziehen verschiebt, mit Strg kopiert (auch in andere Postfächer)
          e.dataTransfer.effectAllowed = featureOn('mailmove') ? 'copyMove' : 'move'
        }}>
        {/* [feature:mailthreads] */}
        {onToggleThread && !member && (count > 1
          ? <Button appearance="transparent" size="small" className={s.chevron} icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
              aria-expanded={open} aria-label={open ? t('Unterhaltung zuklappen') : t('Unterhaltung aufklappen')}
              onClick={e => { e.stopPropagation(); onToggleThread(item) }} onDoubleClick={e => e.stopPropagation()} />
          : <span className={s.chevron} style={{ display: 'inline-block' }} />)}
        {/* [/feature:mailthreads] */}
        {/* [feature:mailavatarfilter] */}
        {avatars && !member && onSenderFilter && item.from?.address
          ? <Avatar name={item.from?.name || item.from?.address || '?'} size={avatarSize as 24 | 32 | 40} color="colorful" className={s.avatarButton}
              role="button" tabIndex={0} title={t('Alle Mails von {who} anzeigen', { who: addrText(item.from) || item.from.address })}
              aria-label={t('Alle Mails von {who} anzeigen', { who: addrText(item.from) || item.from.address })}
              onClick={e => { e.stopPropagation(); onSenderFilter(item.from!.address) }} onDoubleClick={e => e.stopPropagation()}
              onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); e.stopPropagation(); onSenderFilter(item.from!.address) } }} />
          : avatars && !member && <Avatar name={item.from?.name || item.from?.address || '?'} size={avatarSize as 24 | 32 | 40} color="colorful" />}
        {/* [feature:mailavatarfilter:else] */}
        {/* {avatars && <Avatar name={item.from?.name || item.from?.address || '?'} size={avatarSize as 24 | 32 | 40} color="colorful" />} */}
        {/* [/feature:mailavatarfilter] */}
        <div className={s.body} style={{ gap: rowGap }}>
          <div className={s.row}>
            <span className={mergeClasses(s.from, !item.seen && s.bold)}>{
              addrText(item.from) || t('(ohne Absender)')}</span>
            {/* [feature:mailtocolumn] */}
            {to && <ToPill className={s.to} address={to} />}
            {/* [/feature:mailtocolumn] */}
            {item.answered && <ArrowReply16Regular />}
            {item.forwarded && <ArrowForward16Regular />}
            {item.hasAttachments && <Attach16Regular />}
            <Button appearance="transparent" size="small" className={mergeClasses(s.flag, 'dm-flag', item.flagged && s.flagOn)}
              icon={item.flagged ? <Flag16Filled /> : <Flag16Regular />}
              onClick={e => { e.stopPropagation(); onToggleFlag(item) }} aria-label={t('Kennzeichnen')} />
          </div>
          <div className={s.row}>
            <span className={mergeClasses(s.subject, !item.seen && s.bold, !item.seen && s.brand)}>{item.subject || t('(ohne Betreff)')}</span>
            {count > 1 && <span className={s.count} title={t('{n} Mails in dieser Unterhaltung', { n: count })}>{count}</span>} {/* [feature:mailthreads] */}
            <span className={s.date}>{fmtDate(item.date)}</span>
          </div>
          {(preview || unsure || folder
            || item.keywords?.length > 0 || expiryOf?.(item)) && <div className={s.row}>
            <CategoryChips keywords={item.keywords} onRemove={onRemoveCategory ? k => onRemoveCategory(item, k) : undefined} />
            {snoozeOf?.(item) && <span className={s.tag} title={t('Wiedervorlage')}>⏰ {fmtDate(snoozeOf(item)!)}</span>}
            {expiryOf?.(item) && <ExpiryTag e={expiryOf(item)!} className={s.tag} />}
            {unsure && <span className={mergeClasses(s.tag, s.unsure)}><ShieldError16Regular style={{ verticalAlign: -3 }} /> {t('unsicher')}</span>}
            {folder && <span className={s.tag} title={folder.path}>{folderLabel(folder)}</span>}
            {preview && !item.match && <span className={s.preview}>{item.preview || (item.cached ? '' : '…')}</span>}
          </div>}
          {lines === 2 && !item.match && <div className={s.preview2}>{item.preview || (item.cached ? '' : '…')}</div>}
          {item.match && <div className={s.row}>
            <span className={s.match} title={item.match.name}><Attach16Regular style={{ verticalAlign: -3 }} /> {t('im Anhang {name}', { name: item.match.name })}: <Snippet text={item.match.snippet} /></span>
          </div>}
        </div>
      </div>
    )
  }

  return (
    <div ref={attach} className={s.root} onScroll={onScroll} role="listbox" aria-multiselectable style={{ zoom }}>
      <div style={{ height: offsets[first] }} />
      {entries.slice(first, last).map(e => 'head' in e
        ? <div className={s.group} key={e.key} data-vkey={e.key} data-vhead="1">{e.head}</div>
        : renderItem(e.item, e.member, e.key))}
      <div style={{ height: offsets[entries.length] - offsets[last] }} />
      {more && <div style={{ padding: 12, textAlign: 'center' }}>{loadingMore ? <Spinner size="tiny" /> : <Button appearance="subtle" onClick={onMore}>{t('Mehr laden')}</Button>}</div>}
    </div>
  )
}
