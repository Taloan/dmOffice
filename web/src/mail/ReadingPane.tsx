import {
  Avatar, Button, Link, MessageBar, MessageBarActions, MessageBarBody, Spinner, Text, Tooltip, makeStyles, tokens,
  Menu, MenuItem, MenuList, MenuPopover, MenuTrigger,
} from '@fluentui/react-components'
import {
  ArrowReply20Regular, ArrowReplyAll20Regular, ArrowForward20Regular, Attach16Regular, ArrowDownload16Regular, MoreHorizontal20Regular,
  Code20Regular, Print20Regular, ShieldError16Regular, Delete20Regular, WeatherMoon16Regular,
} from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { addrFull, api, fmtSize, MailItem, Message, q } from '../api'
import { peek, remember } from './messageCache'
import { useApp } from '../App'
import { CategoryChips } from './Categories'
import { InviteCard } from './InviteCard'
import { PhishBar } from './PhishBar'
import { SmartCard } from './SmartCard'
import { fmtLong } from '../shell/hooks'
import { useIsDark } from '../theme'
import { usePrefs } from '../prefs'
import { t } from '../i18n'

const useStyles = makeStyles({
  root: { flex: 1, overflowY: 'auto', display: 'flex', flexDirection: 'column', backgroundColor: tokens.colorNeutralBackground2 },
  head: { padding: '16px 20px 12px', display: 'flex', flexDirection: 'column', gap: '10px' },
  meta2: { display: 'flex', flexDirection: 'column', gap: '8px', paddingBottom: '4px' },
  subject: { fontSize: '20px', fontWeight: 600, lineHeight: '26px', wordBreak: 'break-word' },
  sender: { display: 'flex', gap: '12px', alignItems: 'flex-start', flexWrap: 'wrap' },
  meta: { flex: 1, minWidth: '180px', display: 'flex', flexDirection: 'column', gap: '2px', overflowWrap: 'anywhere' },
  small: { fontSize: '12px', color: tokens.colorNeutralForeground3, wordBreak: 'break-word' },
  actions: { display: 'flex', gap: '2px', flexShrink: 0 },
  atts: { display: 'flex', flexWrap: 'wrap', gap: '8px', padding: '0 20px' },
  att: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 10px', borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`, maxWidth: '260px', color: tokens.colorNeutralForeground1, textDecoration: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  attName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: '13px' },
  // Der Inhalt der Mail auf eigener Flaeche - klar getrennt von Kopf, Hinweisen und Anhaengen
  frameWrap: {
    margin: '4px 16px 16px', padding: '16px 20px', flexGrow: 1, flexShrink: 0, borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, boxShadow: tokens.shadow4,
  },
  frame: { width: '100%', border: 'none', minHeight: '200px', display: 'block', backgroundColor: 'transparent' },
  // flexShrink 0: in der Spaltenansicht drueckt das Flex-Layout die Leiste sonst auf ihre Mindesthoehe,
  // und die zweite Zeile (Knopf "Bilder anzeigen") rutscht unter den Mailinhalt
  bar: { margin: '0 20px', flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  imgBar: { display: 'flex', alignItems: 'center', gap: '10px', minWidth: 0 },
})

interface Props {
  item: MailItem
  onLoaded: (m: Message) => void
  onReply: (all: boolean) => void
  onForward: () => void
  onAct: (what: any) => void
}

export function ReadingPane({ item, onLoaded, onReply, onForward, onAct }: Props) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [unsubDone, setUnsubDone] = useState(false)
  // Newsletter abmelden: One-Click oder Mail erledigt der Server, sonst den Link im Browser oeffnen
  const unsubscribe = async () => {
    if (!message) return
    // Nicht still abmelden: ein Klick kann eine Mail vom eigenen Konto ausloesen. Erst nachfragen.
    if (!window.confirm(t('Vom Newsletter abmelden? Dabei wird ggf. eine Abmeldung an den Absender gesendet.'))) return
    try {
      const r = await api.post(`/api/mail/${encodeURIComponent(message.account)}/unsubscribe`, { folder: message.folder, uid: message.uid })
      if (r.done) { setUnsubDone(true); notify(t('Vom Newsletter abgemeldet'), r.how === 'mail' ? t('Die Abmelde-Mail ist verschickt.') : undefined) }
      else if (r.url) window.open(r.url, '_blank', 'noopener,noreferrer')
    } catch (e) { fail(e) }
  }
  const dark = useIsDark()
  const { prefs } = usePrefs()
  const [message, setMessage] = useState<Message | null>(null)
  useEffect(() => setUnsubDone(false), [message?.account, message?.folder, message?.uid])
  const [images, setImages] = useState(false)
  const [invert, setInvert] = useState(true)
  const [showAllTo, setShowAllTo] = useState(false)
  const frame = useRef<HTMLIFrameElement>(null)

  useEffect(() => { setImages(false); setShowAllTo(false) }, [item.id])

  useEffect(() => {
    let alive = true
    const url = `/api/mail/${item.account}/message?` + q({ folder: item.folder, uid: item.uid, images, markSeen: false })
    let loaded: Message | null = null
    // Schon vorgeladen (ohne entfernte Bilder): sofort zeigen
    const early = images ? null : peek(item)
    if (early) { loaded = early; setMessage(early); onLoaded(early) }
    else {
      setMessage(null)
      api.get<Message>(url).then(m => {
        if (!alive) return
        loaded = m
        if (!images) remember(item, m)
        setMessage(m)
        onLoaded(m)
      }).catch(e => alive && fail(e))
    }
    // Gelesen nach einer kurzen Pause, wie in Outlook - wer nur durchblaettert, laesst sie ungelesen
    const t = !item.seen ? setTimeout(() => {
      api.post(`/api/mail/${item.account}/flags`, { folder: item.folder, uids: [item.uid], seen: true })
        .then(() => { if (loaded) remember(item, { ...loaded, seen: true }); if (alive && loaded) onLoaded({ ...loaded, seen: true }) })
        .catch(() => {})
    }, 1500) : undefined
    return () => { alive = false; if (t) clearTimeout(t) }
  }, [item.id, images]) // eslint-disable-line react-hooks/exhaustive-deps

  // Hoehe des iframes an den Inhalt anpassen (gleiche Herkunft, keine Skripte darin)
  const fit = () => {
    const doc = frame.current?.contentDocument
    if (!doc || !frame.current) return
    frame.current.style.height = Math.max(200, doc.documentElement.scrollHeight + 4) + 'px'
  }

  // Bilder dieses Absenders oder dieser Domain immer laden (Einstellungen -> Junk-E-Mail)
  const trust = async (entry: string) => {
    try { await api.post('/api/settings/trusted-images', { entry }); setImages(true) } catch (e) { fail(e) }
  }

  if (!message) return <Spinner style={{ margin: 'auto' }} />

  const darkStyle = dark && invert
    // invertiert: #d6d6d6 wird zum Grau des Lesebereichs, nicht zu Schwarz
    ? 'html{filter:invert(1) hue-rotate(180deg);background:#d6d6d6}body{background:transparent}img,video,[style*="background-image"]{filter:invert(1) hue-rotate(180deg)}'
    : ''
  const doc = `<!doctype html><html><head><meta charset="utf-8"><base target="_blank">
<style>body{margin:0;font-family:'Segoe UI',Calibri,Arial,sans-serif;font-size:14px;line-height:1.45;color:#242424;background:#fff;word-wrap:break-word;overflow-wrap:anywhere}
img{max-width:100%;height:auto}pre{white-space:pre-wrap}blockquote{margin-left:8px;padding-left:10px;border-left:3px solid #ccc}
img[data-remote-src]{display:inline-block;min-width:16px;min-height:16px;background:#f3f3f3}${darkStyle}</style></head><body>${message.html}</body></html>`

  const recipients = [...message.to, ...message.cc]
  // Der Kalenderteil einer Einladung steckt in der Karte - nicht noch einmal als "Teil 1" zeigen; echte .ics-Dateien bleiben
  const shownAttachments = message.attachments.filter(a => !(/calendar/i.test(a.mime) && !/\.ics$/i.test(a.name)))
  const attUrl = (i: number, download = true) => `/api/mail/${item.account}/part?` + q({ folder: item.folder, uid: item.uid, index: i, download })

  return (
    <div className={s.root}>
      <div className={s.head}>
        <div className={s.subject}>{message.subject || t('(ohne Betreff)')}</div>
        {item.keywords?.length > 0 && <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}><CategoryChips keywords={item.keywords} /></div>}
        <div className={s.sender}>
          <Avatar name={message.from?.name || message.from?.address} size={40} color="colorful" />
          <div className={s.meta}>
            <Text weight="semibold">{message.from ? addrFull(message.from) : t('(ohne Absender)')}</Text>
            <span className={s.small}>
              {t('An: {recipients}', { recipients: (showAllTo ? recipients : recipients.slice(0, 4)).map(addrFull).join('; ') })}
              {!showAllTo && recipients.length > 4 && <> <Link inline onClick={() => setShowAllTo(true)}>{t('+{n} weitere', { n: recipients.length - 4 })}</Link></>}
            </span>
            <span className={s.small}>{fmtLong(message.date)}</span>
          </div>
          <div className={s.actions}>
            {/* Standardantwort (Einstellungen -> Verfassen) steht vorne */}
            {(prefs.replyAll ? [true, false] : [false, true]).map(all => (
              <Tooltip key={String(all)} content={all ? t('Allen antworten') : t('Antworten')} relationship="label">
                <Button appearance="subtle" icon={all ? <ArrowReplyAll20Regular /> : <ArrowReply20Regular />} onClick={() => onReply(all)} />
              </Tooltip>
            ))}
            <Tooltip content={t('Weiterleiten')} relationship="label"><Button appearance="subtle" icon={<ArrowForward20Regular />} onClick={onForward} /></Tooltip>
            <Menu>
              <MenuTrigger disableButtonEnhancement><Button appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} /></MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<Delete20Regular />} onClick={() => onAct('delete')}>{t('Löschen')}</MenuItem>
                  <MenuItem icon={<Print20Regular />} onClick={() => frame.current?.contentWindow?.print()}>{t('Drucken')}</MenuItem>
                  <MenuItem icon={<Code20Regular />} onClick={() => window.open(`/api/mail/${item.account}/source?` + q({ folder: item.folder, uid: item.uid }))}>{t('Quelltext (.eml)')}</MenuItem>
                  {dark && <MenuItem icon={<WeatherMoon16Regular />} onClick={() => setInvert(v => !v)}>{invert ? t('Original-Farben') : t('Dunkel darstellen')}</MenuItem>}
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
        </div>
      </div>

      {/* Phishing-Verdacht: Absenderpruefung, Linktext gegen Linkziel, aehnliche Domains, erstmaliger Absender */}
      {message.phish && <PhishBar found={message.phish} className={s.bar} onJunk={message.junk ? undefined : () => onAct('junk')} />}
      {message.junk && (
        // Wie Outlook: in Junk keine Bilder und keine Links - erst "Kein Junk" gibt die Mail frei
        <MessageBar intent="error" className={s.bar} layout="multiline">
          <MessageBarBody><ShieldError16Regular /> {t('Junk-E-Mail: Bilder und Links sind gesperrt. Zum Freigeben die Mail als „Kein Junk“ in den Posteingang verschieben.')}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => onAct('notjunk')}>{t('Kein Junk')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {!message.junk && message.spam != null && message.spam >= 0.2 && message.spam < 0.9 && (
        <MessageBar intent="warning" className={s.bar} layout="multiline">
          <MessageBarBody><ShieldError16Regular /> {t('Der Spamfilter ist unsicher ({p} %).', { p: Math.round(message.spam * 100) })}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => onAct('junk')}>{t('Junk')}</Button>
            <Button size="small" onClick={() => { onAct('notjunk'); setMessage(m => m && { ...m, spam: 0 }) }}>{t('Kein Junk')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {message.unsubscribe && !message.junk && (
        <MessageBar intent={unsubDone ? 'success' : 'info'} className={s.bar} layout="singleline" icon={null}>
          <MessageBarBody className={s.imgBar}>
            {unsubDone ? <Text size={200}>{t('Vom Newsletter abgemeldet')}</Text> : <>
              <Button size="small" onClick={unsubscribe}>{t('Vom Newsletter abmelden')}</Button>
              <Text size={200} className={s.muted}>{t('Diese Mail kommt von einer Verteilerliste.')}</Text>
            </>}
          </MessageBarBody>
        </MessageBar>
      )}
      {message.hasRemoteImages && !message.imagesAllowed && !message.junk && (
        // Einzeilig, Knopf vorne; die Begruendung steht im Tooltip statt als langer Satz
        <MessageBar intent="info" className={s.bar} layout="singleline" icon={null}>
          <MessageBarBody className={s.imgBar}>
            <Menu positioning="below-start">
              <MenuTrigger disableButtonEnhancement><Button size="small" appearance="primary">{t('Bilder anzeigen')} ▾</Button></MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem onClick={() => setImages(true)}>{t('Nur diesmal')}</MenuItem>
                  {message.from && <MenuItem onClick={() => trust(message.from!.address)}>{t('Immer für {who}', { who: message.from.address })}</MenuItem>}
                  {message.from?.address.includes('@') && <MenuItem onClick={() => trust('@' + message.from!.address.split('@')[1])}>{t('Immer für {who}', { who: '@' + message.from.address.split('@')[1] })}</MenuItem>}
                </MenuList>
              </MenuPopover>
            </Menu>
            <Tooltip content={t('Bilder aus dem Internet wurden blockiert – sie verraten dem Absender, dass die Mail gelesen wurde.')} relationship="description">
              <Text size={200} className={s.muted}>{t('Externe Bilder blockiert')}</Text>
            </Tooltip>
          </MessageBarBody>
        </MessageBar>
      )}
      {!message.cached && (
        <MessageBar intent="info" className={s.bar} layout="multiline"><MessageBarBody>{t('Vom Server geladen – zu groß für die Datenbank, oder der Sync ist noch nicht so weit.')}</MessageBarBody></MessageBar>
      )}

      {/* Lieferung oder Buchung (Paket, Flug, Hotel ...) */}
      <SmartCard item={item} />
      {/* Einladung (text/calendar): Zusagen / Mit Vorbehalt / Absagen */}
      {message.attachments.some(a => /calendar|ics/i.test(a.mime) || /\.ics$/i.test(a.name)) && <InviteCard item={item} />}
      {shownAttachments.length > 0 && (
        <div className={s.atts}>
          {shownAttachments.map(a => (
            <a key={a.index} className={s.att} href={attUrl(a.index)} title={t('{name} – herunterladen', { name: a.name })}>
              <Attach16Regular />
              <div style={{ minWidth: 0 }}>
                <div className={s.attName}>{a.name}</div>
                <div className={s.small}>{fmtSize(a.size)}</div>
              </div>
              <ArrowDownload16Regular />
            </a>
          ))}
        </div>
      )}

      <div className={s.frameWrap}>
        <iframe ref={frame} className={s.frame} title={t('Inhalt')} srcDoc={doc} onLoad={() => { fit(); setTimeout(fit, 300); setTimeout(fit, 1500) }}
          sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox allow-modals" />
      </div>
    </div>
  )
}
