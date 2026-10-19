import {
  Button, Dropdown, Option, Text, Tooltip, makeStyles, tokens, Checkbox, Spinner, Link,
} from '@fluentui/react-components'
import { Send20Regular, Attach20Regular, Dismiss20Regular, Save20Regular, Dismiss16Regular, Important20Regular, WindowNew20Regular, ChevronDown20Regular } from '@fluentui/react-icons'
import { SendLaterMenu } from './Outbox'
import { fmtDate } from '../shell/hooks'
import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Account, addrFull, api, Attachment, fmtSize, Message } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { RichEditor, RichEditorHandle } from '../shell/RichEditor'
import { AddressInput } from './AddressInput'
import { t } from '../i18n'

export interface ComposeInit {
  account: string
  to: string[]
  cc: string[]
  bcc: string[]
  subject: string
  html: string
  inReplyTo?: string
  references?: string[]
  replyFolder?: string
  replyUid?: number
  forwardFolder?: string
  forwardUid?: number
  forwardAttachments?: Attachment[]
  /** Die ganze Mail als .eml anhaengen; Name fuer die Anzeige */
  forwardAsAttachment?: string
  draftUid?: number
  // Beim Auskoppeln in ein eigenes Fenster: alles, was schon da ist
  files?: File[]
  important?: boolean
  receipt?: boolean
  draftAccount?: string
  dirty?: boolean
}

/** Von aussen: beim Wegklicken als Entwurf sichern, beim Auskoppeln den Stand mitgeben. */
/** Gerade in den Postausgang gelegt: noch zurueckrufbar bis sendAt. */
export interface QueuedSend { id: number; sendAt: string; snapshot: ComposeInit }

export interface ComposeHandle {
  /** Ungesicherte Aenderungen still als Entwurf speichern. true: es wurde gespeichert. */
  flush(): Promise<boolean>
  /** Der ganze Stand, um im eigenen Fenster weiterzuschreiben. */
  snapshot(): ComposeInit
}

const esc = (t: string) => t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

// Zitatkopf und Praefixe in der Sprache des Kontos (Einstellungen -> E-Mail-Konten)
const words = {
  de: { from: 'Von', sent: 'Gesendet', to: 'An', subject: 'Betreff', re: 'AW: ', fwd: 'WG: ', locale: 'de-DE' },
  en: { from: 'From', sent: 'Sent', to: 'To', subject: 'Subject', re: 'Re: ', fwd: 'Fwd: ', locale: 'en-US' },
}
const lang = (a: Account) => words[a.language === 'en' ? 'en' : 'de']
const prefixed = (subject: string, forward: boolean, a: Account) =>
  (forward ? /^\s*(wg|fw|fwd):/i : /^\s*(aw|re|antw):/i).test(subject) ? subject : (forward ? lang(a).fwd : lang(a).re) + subject

function quote(m: Message, a: Account): string {
  const w = lang(a)
  const date = new Date(m.date).toLocaleString(w.locale, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })
  return `<br><div style="border-top:1px solid #b5c4df;padding-top:6px;margin-top:12px"><b>${w.from}:</b> ${esc(m.from ? addrFull(m.from) : '')}<br>` +
    `<b>${w.sent}:</b> ${esc(date)}<br><b>${w.to}:</b> ${esc(m.to.map(addrFull).join('; '))}<br>` +
    (m.cc.length ? `<b>Cc:</b> ${esc(m.cc.map(addrFull).join('; '))}<br>` : '') +
    `<b>${w.subject}:</b> ${esc(m.subject)}</div><br><blockquote style="margin:0 0 0 4px;padding-left:10px;border-left:3px solid #b5c4df">${m.html}</blockquote>`
}

const sig = (a: Account) => (a.signature ? `<br><div class="dm-signature">${a.signature}</div>` : '')
const fmt = (a: { name?: string | null; address: string }) => (a.name ? `"${a.name.replace(/"/g, '')}" <${a.address}>` : a.address)

export function replyOf(m: Message, account: Account, all: boolean): ComposeInit {
  const me = account.email.toLowerCase()
  const base = m.replyTo.length ? m.replyTo : m.from ? [m.from] : []
  const to = base.map(fmt)
  const cc = all ? [...m.to, ...m.cc].filter(a => a.address.toLowerCase() !== me && !base.some(b => b.address === a.address)).map(fmt) : []
  return {
    account: account.id, to, cc, bcc: [],
    subject: prefixed(m.subject, false, account),
    html: '<div><br></div>' + sig(account) + quote(m, account),
    inReplyTo: m.messageId, references: [...m.references, ...(m.messageId ? [m.messageId] : [])],
    replyFolder: m.folder, replyUid: m.uid,
  }
}

/** Als Anlage weiterleiten: die Original-Mail haengt als .eml an, der Text bleibt leer. */
export function forwardAsAttachmentOf(m: Message, account: Account): ComposeInit {
  return {
    account: account.id, to: [], cc: [], bcc: [],
    subject: prefixed(m.subject, true, account),
    html: '<div><br></div>' + sig(account),
    forwardFolder: m.folder, forwardUid: m.uid, forwardAsAttachment: (m.subject || 'Nachricht') + '.eml',
  }
}

export function forwardOf(m: Message, account: Account): ComposeInit {
  return {
    account: account.id, to: [], cc: [], bcc: [],
    subject: prefixed(m.subject, true, account),
    html: '<div><br></div>' + sig(account) + quote(m, account),
    forwardFolder: m.folder, forwardUid: m.uid, forwardAttachments: m.attachments,
  }
}

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 },
  bar: { display: 'flex', alignItems: 'center', gap: '6px', padding: '8px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  field: { display: 'flex', alignItems: 'center', gap: '8px', padding: '0 16px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, minHeight: '40px' },
  label: { width: '52px', color: tokens.colorNeutralForeground3, flexShrink: 0 },
  subject: { flex: 1, border: 'none', outline: 'none', background: 'transparent', fontSize: '15px', color: tokens.colorNeutralForeground1, fontFamily: 'inherit', padding: '8px 0' },
  atts: { display: 'flex', flexWrap: 'wrap', gap: '6px', padding: '8px 16px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  att: { display: 'flex', alignItems: 'center', gap: '6px', padding: '3px 4px 3px 10px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, fontSize: '13px' },
  spacer: { flex: 1 },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-6px' },
})

export const Compose = forwardRef<ComposeHandle, {
  init: ComposeInit; accounts: Account[]; onClose: (sent: boolean, queued?: QueuedSend) => void
  /** In ein eigenes Fenster auskoppeln (nur im Hauptfenster) */
  onPopOut?: () => void
  /** Meldet ungesicherte Aenderungen nach aussen (z. B. fuer Rueckfrage vor neuem Entwurf) */
  onDirty?: (dirty: boolean) => void
  /** Nach dem Speichern eines Entwurfs */
  onSaved?: () => void
}>(function Compose({ init, accounts, onClose, onDirty, onSaved, onPopOut }, ref) {
  const s = useStyles()
  const { notify, fail } = useApp()
  const { prefs } = usePrefs()
  const font = { fontFamily: prefs.composeFont || 'Aptos, Calibri, "Segoe UI", Arial, sans-serif', fontSize: prefs.composeSize || '11pt' }
  const [account, setAccount] = useState(init.account)
  const [to, setTo] = useState(init.to)
  const [cc, setCc] = useState(init.cc)
  const [bcc, setBcc] = useState(init.bcc)
  const [showCc, setShowCc] = useState(init.cc.length > 0 || init.bcc.length > 0)
  const [subject, setSubject] = useState(init.subject)
  const [files, setFiles] = useState<File[]>(init.files ?? [])
  const [fwd, setFwd] = useState<Attachment[]>(init.forwardAttachments ?? [])
  const [asAttachment, setAsAttachment] = useState(init.forwardAsAttachment)
  const [important, setImportant] = useState(init.important ?? false)
  const [receipt, setReceipt] = useState(init.receipt ?? false)
  const [busy, setBusy] = useState(false)
  const [draftUid, setDraftUid] = useState(init.draftUid)
  // Konto, in dem der Entwurf draftUid gerade liegt
  const [draftAccount, setDraftAccount] = useState(init.draftAccount ?? init.account)
  const [dirty, setDirty] = useState(init.dirty ?? false)
  const sending = useRef(false)
  useEffect(() => { onDirty?.(dirty) }, [dirty]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => () => onDirty?.(false), []) // eslint-disable-line react-hooks/exhaustive-deps
  const [dragging, setDragging] = useState(false)
  const editor = useRef<RichEditorHandle>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const initialHtml = useRef(init.html || '<div><br></div>' + sig(accounts.find(a => a.id === init.account) ?? accounts[0]))

  const form = (when?: { sendAt?: string; delaySeconds?: number }) => {
    // replyUid/forwardUid/draftUid gehoeren zum Ursprungskonto. Liegt der Entwurf schon in einem
    // anderen Konto (Absender gewechselt und gespeichert), zaehlt fuer draftUid dieses Konto;
    // die Bezuege auf die Ursprungsmail passen dann nicht mehr und bleiben weg.
    const moved = draftUid != null && draftAccount !== init.account
    const f = new FormData()
    f.append('json', JSON.stringify({
      sourceAccount: moved ? draftAccount : init.account,
      to: to.join(', '), cc: cc.join(', '), bcc: bcc.join(', '), subject,
      // Standardschrift mitschicken - sonst sieht der Empfaenger die Schrift seines Programms
      html: `<div style="font-family:${font.fontFamily.replace(/"/g, "'")};font-size:${font.fontSize}">${editor.current?.html() ?? ''}</div>`,
      inReplyTo: init.inReplyTo, references: init.references ?? [],
      replyFolder: moved ? undefined : init.replyFolder, replyUid: moved ? undefined : init.replyUid,
      forwardFolder: moved ? undefined : init.forwardFolder, forwardUid: moved ? undefined : init.forwardUid,
      forwardAttachments: moved ? [] : fwd.map(a => a.index),
      forwardAsAttachment: !moved && !!asAttachment,
      draftUid, highPriority: important, readReceipt: receipt,
      sendAt: when?.sendAt, delaySeconds: when?.delaySeconds,
    }))
    files.forEach(file => f.append('files', file, file.name))
    return f
  }

  const send = async (at?: Date) => {
    // Sperre gegen mehrfaches Senden (Strg+Enter, Doppelklick)
    if (sending.current) return
    if (!to.length && !cc.length && !bcc.length) { notify(t('Kein Empfänger'), undefined, 'warning'); return }
    if (!subject.trim() && !confirm(t('Ohne Betreff senden?'))) return
    const text = editor.current?.text() ?? ''
    if (/anhang|angehängt|anbei|attach|enclosed/i.test(text.split(/Von:|From:/)[0]) && files.length + fwd.length === 0
        && !confirm(t('Im Text ist von einem Anhang die Rede, es ist aber keiner dabei. Trotzdem senden?'))) return
    sending.current = true
    setBusy(true)
    try {
      const snapshot: ComposeInit = {
        ...init, account, to, cc, bcc, subject, html: editor.current?.html() ?? '',
        forwardAttachments: fwd, forwardAsAttachment: asAttachment, files, important, receipt, draftUid, draftAccount, dirty: true,
      }
      const delay = at ? undefined : Math.max(0, Math.min(30, prefs.sendDelay ?? 10))
      const r = await api.form(`/api/mail/${account}/send`, form({ sendAt: at?.toISOString(), delaySeconds: delay }))
      if (r?.queued && at) { notify(t('Geplant'), t('Geht raus: {when}', { when: fmtDate(r.sendAt) })); onClose(true); return }
      if (r?.queued) { onClose(true, { id: r.queued, sendAt: r.sendAt, snapshot }); return }
      // Gesendet, aber z. B. die Kopie in "Gesendet" hat nicht geklappt
      if (r?.warning) notify(t('Gesendet – mit Hinweis'), r.warning, 'warning')
      else notify(t('Gesendet'))
      onClose(true)
    } catch (e) { fail(e) } finally { sending.current = false; setBusy(false) }
  }

  const save = async (quiet = false) => {
    try {
      const target = account
      const r = await api.form(`/api/mail/${target}/draft`, form())
      setDraftUid(r.uid ?? undefined)
      setDraftAccount(target)
      setDirty(false)
      if (!quiet) notify(t('Entwurf gespeichert'))
      onSaved?.()
    } catch (e) { if (!quiet) fail(e) }
  }
  // Immer die aktuelle save-Funktion, sonst speichert der Timer veraltete Felder
  const saveRef = useRef(save)
  saveRef.current = save

  // Alle zwei Minuten als Entwurf sichern, wenn sich etwas geaendert hat
  useEffect(() => {
    if (!dirty) return
    const t = setTimeout(() => saveRef.current(true), 120000)
    return () => clearTimeout(t)
  }, [dirty])

  useImperativeHandle(ref, () => ({
    flush: async () => {
      if (!dirty || sending.current) return false
      try {
        const r = await api.form(`/api/mail/${account}/draft`, form())
        setDraftUid(r.uid ?? undefined); setDraftAccount(account); setDirty(false)
        onSaved?.()
        return true
      } catch (e) { fail(e); return false }
    },
    snapshot: () => ({
      ...init, account, to, cc, bcc, subject, html: editor.current?.html() ?? '',
      forwardAttachments: fwd, forwardAsAttachment: asAttachment, files, important, receipt, draftUid, draftAccount, dirty,
    }),
  }))

  const close = () => {
    if (dirty && !confirm(t('Verwerfen? Ungesicherte Änderungen gehen verloren.'))) return
    onClose(false)
  }

  const addFiles = (list: FileList | null) => {
    if (!list) return
    setFiles(f => [...f, ...Array.from(list)])
    setDirty(true)
  }

  const total = files.reduce((n, f) => n + f.size, 0) + fwd.reduce((n, a) => n + a.size, 0)

  return (
    <div className={`${s.root} ${dragging ? s.drop : ''}`}
      onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
      onDragLeave={() => setDragging(false)}
      onDrop={e => { if (e.dataTransfer.files.length) { e.preventDefault(); setDragging(false); addFiles(e.dataTransfer.files) } }}
      onKeyDown={e => { if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); send() } }}>
      <div className={s.bar}>
        <div style={{ display: 'flex' }}>
          <Button appearance="primary" icon={busy ? <Spinner size="tiny" /> : <Send20Regular />} disabled={busy} onClick={() => send()}
            style={{ borderTopRightRadius: 0, borderBottomRightRadius: 0 }}>{t('Senden')}</Button>
          <SendLaterMenu onPick={when => send(when)} trigger={
            <Button appearance="primary" icon={<ChevronDown20Regular />} disabled={busy} aria-label={t('Später senden')} title={t('Später senden')}
              style={{ minWidth: 0, borderTopLeftRadius: 0, borderBottomLeftRadius: 0, borderLeft: `1px solid ${tokens.colorNeutralStrokeOnBrand}` }} />
          } />
        </div>
        <Button icon={<Attach20Regular />} onClick={() => fileInput.current?.click()}>{t('Anfügen')}</Button>
        <Button icon={<Save20Regular />} onClick={() => save()}>{t('Entwurf')}</Button>
        <Tooltip content={t('Wichtigkeit: hoch')} relationship="label">
          <Button appearance={important ? 'primary' : 'subtle'} icon={<Important20Regular />} onClick={() => setImportant(v => !v)} />
        </Tooltip>
        <Checkbox label={t('Lesebestätigung')} checked={receipt} onChange={(_, d) => setReceipt(!!d.checked)} />
        <div className={s.spacer} />
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Strg+Enter sendet')}</Text>
        {onPopOut && (
          <Tooltip content={t('In eigenem Fenster öffnen')} relationship="label">
            <Button appearance="subtle" icon={<WindowNew20Regular />} onClick={onPopOut} />
          </Tooltip>
        )}
        <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={close} aria-label={t('Schließen')} />
      </div>
      <input ref={fileInput} type="file" multiple hidden onChange={e => { addFiles(e.target.files); e.target.value = '' }} />
      {accounts.length > 1 && (
        <div className={s.field}>
          <span className={s.label}>{t('Von')}</span>
          <Dropdown value={(() => { const a = accounts.find(x => x.id === account); return a ? `${a.displayName || a.name} <${a.email}>` : '' })()}
            selectedOptions={[account]} onOptionSelect={(_, d) => { if (d.optionValue && d.optionValue !== account) { setAccount(d.optionValue); setDirty(true) } }} appearance="underline" style={{ minWidth: 320 }}>
            {accounts.map(a => <Option key={a.id} value={a.id}>{`${a.displayName || a.name} <${a.email}>`}</Option>)}
          </Dropdown>
        </div>
      )}
      <div className={s.field}>
        <span className={s.label}>{t('An')}</span>
        <AddressInput value={to} onChange={v => { setTo(v); setDirty(true) }} autoFocus={to.length === 0} />
        {!showCc && <Link onClick={() => setShowCc(true)}>Cc/Bcc</Link>}
      </div>
      {showCc && (
        <>
          <div className={s.field}><span className={s.label}>Cc</span><AddressInput value={cc} onChange={v => { setCc(v); setDirty(true) }} /></div>
          <div className={s.field}><span className={s.label}>Bcc</span><AddressInput value={bcc} onChange={v => { setBcc(v); setDirty(true) }} /></div>
        </>
      )}
      <div className={s.field}>
        <input className={s.subject} placeholder={t('Betreff')} value={subject} onChange={e => { setSubject(e.target.value); setDirty(true) }} />
      </div>
      {(files.length > 0 || fwd.length > 0 || !!asAttachment) && (
        <div className={s.atts}>
          {asAttachment && (
            <span className={s.att}>✉ {asAttachment}
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} onClick={() => setAsAttachment(undefined)} />
            </span>
          )}
          {fwd.map(a => (
            <span key={'f' + a.index} className={s.att}>{a.name} <Text size={200}>({fmtSize(a.size)})</Text>
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} onClick={() => setFwd(x => x.filter(y => y.index !== a.index))} />
            </span>
          ))}
          {files.map((f, i) => (
            <span key={i} className={s.att}>{f.name} <Text size={200}>({fmtSize(f.size)})</Text>
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} onClick={() => setFiles(x => x.filter((_, j) => j !== i))} />
            </span>
          ))}
          <Text size={200} style={{ alignSelf: 'center', color: total > 20e6 ? tokens.colorPaletteRedForeground1 : tokens.colorNeutralForeground3 }}>
            {total > 20e6
              ? t('zusammen {size} – viele Server nehmen nur bis 20 MB an', { size: fmtSize(total) })
              : t('zusammen {size}', { size: fmtSize(total) })}
          </Text>
        </div>
      )}
      <RichEditor ref={editor} initial={initialHtml.current} onChange={() => setDirty(true)} areaStyle={font}
        lang={accounts.find(x => x.id === account)?.language === 'en' ? 'en' : 'de'} />
    </div>
  )
})
