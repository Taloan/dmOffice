import { DmsAttachmentButton, DmsFiledHint } from '../dms/MailToDms'
import {
  Avatar, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Link, MessageBar, MessageBarActions, MessageBarBody, Spinner, Text, Tooltip, makeStyles, tokens,
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
import { AliasHint } from './CatchallFolders'
import { InviteCard } from './InviteCard'
import { PhishBar, phishWarns } from './PhishBar'
import { PgpBar } from './Pgp'
import { SpamWhy } from './SpamWhy'
import { SmartCard } from './SmartCard'
import { fmtLong } from '../shell/hooks'
import { useIsDark } from '../theme'
import { usePrefs } from '../prefs'
import { t } from '../i18n'
import { Viewer } from '../files/Viewer'
import { Item, Source, viewKind } from '../files/common'
import { OfficeEditor, officeMode, useOffice } from '../files/Office'
import { askConfirm } from '../shell/context'
import { cancelExpiry, ExpiryIcon, ExpiryMenuItems, expiryOf, expiryText, useExpiries } from './ExpiryMenu'

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
  /** Klick auf einen Kategorie-Badge nimmt die Kategorie von der Mail (wie in der Liste). */
  onRemoveCategory?: (key: string) => void
}

export function ReadingPane({ item, onLoaded, onReply, onForward, onAct, onRemoveCategory }: Props) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const expiry = expiryOf(useExpiries(), item)
  const [unsubDone, setUnsubDone] = useState(false)
  // Anhänge in dmOffice ansehen: Bilder, PDF, Audio, Video, Text im Betrachter, Office-Dateien im Office-Server
  const [viewing, setViewing] = useState<{ items: Item[]; index: number } | null>(null)
  // PDFs können Schadcode enthalten: erst nach Rückfrage ansehen
  const [askPdf, setAskPdf] = useState<{ index: number; name: string } | null>(null)
  const [spamWhy, setSpamWhy] = useState(false)
  const [officeAtt, setOfficeAtt] = useState<{ index: number; name: string } | null>(null)
  const office = useOffice()
  // Newsletter abmelden: One-Click oder Mail erledigt der Server, sonst den Link im Browser oeffnen
  const unsubscribe = async () => {
    if (!message) return
    // Nicht still abmelden: ein Klick kann eine Mail vom eigenen Konto ausloesen. Erst nachfragen.
    if (!await askConfirm('', t('Vom Newsletter abmelden? Dabei wird ggf. eine Abmeldung an den Absender gesendet.'))) return
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
  // Für den Betrachter der Dateien: Anhänge als Einträge, Inhalt über die Anhang-Adresse
  type Att = (typeof shownAttachments)[number]
  const attItem = (a: Att): Item => ({ path: 'att:' + a.index, name: a.name, isDir: false, size: a.size, mime: a.mime, modified: '' })
  // Zum Blättern im Betrachter: alles Ansehbare außer PDFs (die nur einzeln nach Rückfrage)
  const viewable = shownAttachments.filter(a => { const k = viewKind(attItem(a)); return k != null && k !== 'pdf' })
  const attSource: Source = {
    download: (i, inline) => attUrl(Number(i.path.slice(4)), !inline),
    thumb: i => attUrl(Number(i.path.slice(4)), false),
  }
  const download = (a: { index: number; name: string }) => {
    const link = document.createElement('a')
    link.href = attUrl(a.index); link.download = a.name
    document.body.appendChild(link); link.click(); link.remove()
    // Die installierte App (PWA) zeigt keine Download-Leiste - darum ein Hinweis
    notify(t('„{name}“ ist im Download-Ordner gespeichert.', { name: a.name }))
  }
  const openAtt = (a: Att) => {
    const it = attItem(a)
    if (viewKind(it) === 'pdf') setAskPdf({ index: a.index, name: a.name })
    else if (viewKind(it)) setViewing({ items: viewable.map(attItem), index: viewable.findIndex(v => v.index === a.index) })
    else if (officeMode(it, office)) setOfficeAtt({ index: a.index, name: a.name })
    else download(a)
  }

  return (
    <div className={s.root} style={{ zoom: prefs.textSize === 'small' ? 0.92 : prefs.textSize === 'large' ? 1.12 : 1 }}>
      <div className={s.head}>
        <div className={s.subject}>{message.subject || t('(ohne Betreff)')}</div>
        {item.keywords?.length > 0 && <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}><CategoryChips keywords={item.keywords} onRemove={onRemoveCategory} /></div>}
        <div className={s.sender}>
          <Avatar name={message.from?.name || message.from?.address} size={40} color="colorful" />
          <div className={s.meta}>
            <Text weight="semibold">{message.from ? addrFull(message.from) : t('(ohne Absender)')}</Text>
            <span className={s.small}>
              {t('An: {recipients}', { recipients: (showAllTo ? recipients : recipients.slice(0, 4)).map(addrFull).join('; ') })}
              {!showAllTo && recipients.length > 4 && <> <Link inline onClick={() => setShowAllTo(true)}>{t('+{n} weitere', { n: recipients.length - 4 })}</Link></>}
            </span>
            {message.alias && <AliasHint className={s.small} address={message.alias} state={message.aliasState} sender={message.from?.name} />}
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
                  <ExpiryMenuItems targets={[item]} onError={fail}
                    onDone={w => notify(w === 'set' ? t('Entfernen nach Zeit eingeplant') : t('Entfernen nach Zeit zurückgenommen'))} />
                  <MenuItem icon={<Print20Regular />} onClick={() => frame.current?.contentWindow?.print()}>{t('Drucken')}</MenuItem>
                  <MenuItem icon={<Code20Regular />} onClick={() => window.open(`/api/mail/${item.account}/source?` + q({ folder: item.folder, uid: item.uid }))}>{t('Quelltext (.eml)')}</MenuItem>
                  {dark && <MenuItem icon={<WeatherMoon16Regular />} onClick={() => setInvert(v => !v)}>{invert ? t('Original-Farben') : t('Dunkel darstellen')}</MenuItem>}
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
        </div>
      </div>

      {expiry && (
        <MessageBar intent={expiry.kind === 'destroy' ? 'warning' : 'info'} className={s.bar} layout="singleline" icon={<ExpiryIcon />}>
          <MessageBarBody>{expiryText(expiry)}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => cancelExpiry([item]).then(() => notify(t('Entfernen nach Zeit zurückgenommen'))).catch(fail)}>{t('mail|Zurücknehmen')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {/* Phishing-Verdacht: Absenderpruefung, Linktext gegen Linkziel, aehnliche Domains, erstmaliger Absender */}
      {message.pgp && <PgpBar message={message} className={s.bar} />}
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
          <MessageBarBody><ShieldError16Regular /> {t('Der Spamfilter ist unsicher ({p} %).', { p: Math.round(message.spam * 100) })}{' '}<Link onClick={() => setSpamWhy(true)}>{t('Warum?')}</Link></MessageBarBody>
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
      <DmsFiledHint account={item.account} messageId={message.messageId} />
      {shownAttachments.length > 0 && (
        <div className={s.atts}>
          {shownAttachments.map(a => {
            const it = attItem(a)
            const how = viewKind(it) ? 'view' : officeMode(it, office) ? 'office' : 'download'
            return (
              <div key={a.index} className={s.att} role="button" tabIndex={0} onClick={() => openAtt(a)} onKeyDown={e => { if (e.key === 'Enter') openAtt(a) }}
                title={how === 'download' ? t('{name} – herunterladen', { name: a.name }) : t('{name} – ansehen', { name: a.name })}>
                <Attach16Regular />
                <div style={{ minWidth: 0 }}>
                  <div className={s.attName}>{a.name}</div>
                  <div className={s.small}>{fmtSize(a.size)}</div>
                </div>
                <a href={attUrl(a.index)} download={a.name} onClick={e => { e.stopPropagation(); notify(t('„{name}“ ist im Download-Ordner gespeichert.', { name: a.name })) }}
                  title={t('Herunterladen')} aria-label={t('{name} – herunterladen', { name: a.name })} style={{ display: 'inline-flex', color: 'inherit' }}>
                  <ArrowDownload16Regular />
                </a>
                <DmsAttachmentButton mail={item} index={a.index} name={a.name} />
              </div>
            )
          })}
        </div>
      )}

      {viewing && viewing.items[viewing.index] && <Viewer items={viewing.items} index={viewing.index} source={attSource} onIndex={i => setViewing({ ...viewing, index: i })}
        onClose={() => setViewing(null)} onDownload={i => download({ index: Number(i.path.slice(4)), name: i.name })} />}
      {spamWhy && <SpamWhy item={item} onClose={() => setSpamWhy(false)} />}
      {askPdf && <Dialog open onOpenChange={(_, d) => { if (!d.open) setAskPdf(null) }}>
        <DialogSurface style={{ maxWidth: 460 }}>
          <DialogBody>
            <DialogTitle>{t('PDF aus einer Mail öffnen?')}</DialogTitle>
            <DialogContent>
              {((message.phish && phishWarns(message.phish)) || (message.spam != null && message.spam >= 0.2)) && <MessageBar intent="error" style={{ marginBottom: 8 }}><MessageBarBody>
                {t('Diese Mail ist verdächtig. Öffne den Anhang besser nicht.')}</MessageBarBody></MessageBar>}
              {t('PDFs können Schadcode enthalten. Öffne „{name}“ nur, wenn du den Absender kennst und den Anhang erwartet hast.', { name: askPdf.name })}
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => { const a = askPdf; setAskPdf(null); setViewing({ items: [attItem({ ...shownAttachments.find(x => x.index === a.index)! })], index: 0 }) }}>{t('Ansehen')}</Button>
              <Button onClick={() => { const a = askPdf; setAskPdf(null); download(a) }}>{t('Herunterladen')}</Button>
              <Button appearance="subtle" onClick={() => setAskPdf(null)}>{t('Abbrechen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
      {officeAtt && <OfficeEditor name={officeAtt.name} onClose={() => setOfficeAtt(null)}
        open={() => api.post<{ path: string }>(`/api/mail/${item.account}/part/office`, { folder: item.folder, uid: item.uid, index: officeAtt.index })
          .then(r => api.post('/api/office/open', { path: r.path, view: true }))} />}
      <div className={s.frameWrap}>
        <iframe ref={frame} className={s.frame} title={t('Inhalt')} srcDoc={doc} onLoad={() => { fit(); setTimeout(fit, 300); setTimeout(fit, 1500) }}
          sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox allow-modals" />
      </div>
    </div>
  )
}
