import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, MessageBar, MessageBarBody, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, MailItem, q } from '../api'
import { t } from '../i18n'

interface Reason { token: string; p: number; spam: number; ham: number }
interface Why {
  score: number; junk: Reason[]; good: Reason[]; lists: { id: string; name: string }[]; nspam: number; nham: number
  /** Nach den festen Regeln (SpamSignals) - rule sagt, welche gegriffen hat */
  final: number | null; rule: string | null; verified: string | null
  /** Wie viele andere auf diesem Server die Mail als Junk gemeldet haben (JunkPrints) */
  shared?: number
  /** Erkennungsregeln des Admins: die mit der festen Wirkung und alle zutreffenden (SpamRules) */
  custom?: string | null; rules?: { id: string; name: string }[]
  /** Catchall-Alias bei den Regeln alias-blocked / alias-unknown */
  alias?: string | null
  /** Die übrigen Prüfungen: Absenderbestätigung, bekannter Absender, Verteiler, Urteil des eigenen Mailservers … */
  checks?: { dmarc: string | null; dkim: string[]; known: string | null; list: boolean; obfuscated: boolean; randomId: boolean; forwarded: string | null; server: string[]; freemailOrg?: boolean; notToMe?: boolean }
}

type Check = { state: 'ok' | 'bad' | 'info'; text: string }

/** Alle Prüfungen der Mail als Liste - bestanden, auffällig oder nur zur Einordnung. */
function checksOf(why: Why): Check[] {
  const c = why.checks
  if (!c) return []
  const out: Check[] = []
  if (c.dmarc === 'fail') out.push({ state: 'bad', text: t('Absenderprüfung (DMARC) gescheitert – die Absenderadresse ist vermutlich gefälscht') })
  else if (why.verified) out.push({ state: 'ok', text: t('Absender vom Mailserver bestätigt: {domain}', { domain: why.verified }) })
  else if (c.dkim.length) out.push({ state: 'info', text: t('Signiert von {domains}, aber nicht von der Absenderdomain', { domains: c.dkim.join(', ') }) })
  else out.push({ state: 'info', text: t('Absender nicht bestätigt (der Mailserver hat keine DMARC- oder DKIM-Prüfung mitgeschickt)') })
  if (c.forwarded) out.push({ state: 'info', text: t('Über {service} weitergeleitet', { service: c.forwarded }) })
  out.push(c.known === 'safe-list' ? { state: 'ok', text: t('Von dir als sicherer Absender eingetragen') }
    : c.known ? { state: 'ok', text: t('Bekannter Absender (Adressbuch oder schon von dir angeschrieben)') }
    : { state: 'info', text: t('Unbekannter Absender') })
  if (c.list) out.push({ state: 'info', text: t('Newsletter oder Verteiler mit Abmeldelink') })
  if (c.freemailOrg) out.push({ state: 'bad', text: t('Kostenloses Postfach, aber der Name klingt nach Firma oder Dienst') })
  if (c.notToMe) out.push({ state: c.freemailOrg ? 'bad' : 'info', text: t('Nicht an dich adressiert – in „An“ und „Cc“ steht keine deiner Adressen') })
  if (c.obfuscated) out.push({ state: 'bad', text: t('Verschleierter Absendername') })
  if (c.randomId) out.push({ state: 'bad', text: t('Zufallskennung im Betreff') })
  if ((why.shared ?? 0) > 0) out.push({ state: 'bad', text: t('{n}× von anderen auf diesem Server als Junk gemeldet', { n: why.shared ?? 0 }) })
  for (const r of why.rules ?? []) out.push({ state: 'bad', text: t('Erkennungsregel „{name}“ trifft zu', { name: r.name }) })
  if (why.lists.length) out.push({ state: 'bad', text: t('Steht auf Spamlisten: {lists}', { lists: why.lists.map(l => l.name).join(', ') }) })
  // Urteil des eigenen Mailservers (Spam-Kopfzeilen, nur die eigenen, oberen)
  for (const s of c.server) {
    const v = s.slice(s.indexOf(':') + 1)
    const state: Check['state'] = s === 'srv-spam:yes' || v === 'high' || v === 'vhigh' ? 'bad' : s === 'srv-spam:no' || v === 'neg' || v === 'low' ? 'ok' : 'info'
    out.push({ state, text: label(s, why.lists) })
  }
  if (!c.server.length) out.push({ state: 'info', text: t('Dein Mailserver hat kein eigenes Spam-Urteil mitgeschickt') })
  return out
}

/** Satz zur festen Regel, die die Einstufung bestimmt hat. */
function ruleText(why: Why): string | null {
  const p = why.final != null ? Math.round(why.final * 100) : 0
  switch (why.rule) {
    case 'trusted': return t('Der Absender steht in deinem Adressbuch oder hat schon Post von dir bekommen – darum gilt die Mail als gut ({p} %).', { p })
    case 'verified-brand': return t('Dein Mailserver hat bestätigt, dass die Mail wirklich von {domain} kommt – darum gilt sie als gut ({p} %).', { domain: why.verified ?? '', p })
    case 'obfuscated': return t('Der Absendername ist verschleiert (Buchstaben mit Abständen, unsichtbare Zeichen) – darum mindestens unsicher ({p} %).', { p })
    case 'obfuscated-random': return t('Der Absendername ist verschleiert, und der Betreff trägt eine Zufallskennung – darum Junk ({p} %).', { p })
    case 'server-spam': return t('Dein Mailserver hat die Mail als Spam gekennzeichnet – darum mindestens unsicher ({p} %).', { p })
    case 'freemail-org': return t('Ein kostenloses Postfach schreibt unter einem Firmennamen, und die Mail ist nicht an dich adressiert – darum mindestens unsicher ({p} %).', { p })
    case 'safe-list': return t('Du hast den Absender als sicher eingetragen, und dein Mailserver hat bestätigt, dass die Mail wirklich von {domain} kommt – darum gilt sie als gut ({p} %).', { domain: why.verified ?? '', p })
    case 'shared-junk': return (why.shared ?? 0) >= 2
      ? t('{n} andere auf diesem Server haben dieselbe Mail als Junk gemeldet – darum Junk ({p} %).', { n: why.shared ?? 0, p })
      : t('Jemand anderes auf diesem Server hat dieselbe Mail als Junk gemeldet – darum mindestens unsicher ({p} %).', { p })
    case 'custom-junk': return t('Die Erkennungsregel „{name}“ trifft zu – darum Junk ({p} %).', { name: why.custom ?? '', p })
    case 'custom-uncertain': return t('Die Erkennungsregel „{name}“ trifft zu – darum mindestens unsicher ({p} %).', { name: why.custom ?? '', p })
    case 'alias-blocked': return t('Die Mail ging an den gesperrten Catchall-Alias {a} – darum Junk ({p} %).', { a: why.alias ?? '', p })
    case 'alias-unknown': return t('Die Mail ging an eine Adresse deiner Catchall-Domain, die kein Alias ist – darum Junk ({p} %).', { p })
    case 'dmarc-fail': return t('Die Absenderadresse ist vermutlich gefälscht (DMARC gescheitert) – darum mindestens unsicher ({p} %).', { p })
    case 'few-learned': return t('Der Filter hat erst wenig gelernt. Bis er je 50 Junk- und gute Mails kennt, meldet er nur deutlichen Junk (ab 90 %) – darum gilt diese Mail als gut ({p} %).', { p })
    case 'verified-sender': return t('Dein Mailserver hat bestätigt, dass die Mail wirklich von {domain} kommt, und der Filter sieht sie nicht deutlich als Junk – darum gilt sie als gut ({p} %).', { domain: why.verified ?? '', p })
  }
  return null
}

const useStyles = makeStyles({
  cols: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: '16px', marginTop: '12px' },
  list: { display: 'flex', flexDirection: 'column', gap: '4px', marginTop: '4px' },
  row: { display: 'flex', gap: '8px', alignItems: 'baseline', fontSize: tokens.fontSizeBase200 },
  label: { flex: 1, minWidth: 0, overflowWrap: 'anywhere' },
  p: { fontVariantNumeric: 'tabular-nums', color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  mark: { width: '16px', flexShrink: 0, textAlign: 'center', fontWeight: tokens.fontWeightSemibold },
})

/** Merkmal des Spamfilters lesbar: „Absender …“, „Link zu …“, „Wort im Betreff …“, „Spamliste …“. */
function label(token: string, lists: Why['lists']): string {
  const i = token.indexOf(':')
  const kind = i > 0 ? token.slice(0, i) : ''
  const v = i > 0 ? token.slice(i + 1) : token
  switch (kind) {
    case 'subject': return t('Wort im Betreff „{v}“', { v })
    case 'from': return t('Absender {v}', { v })
    case 'from-domain': return t('Absenderdomain {v}', { v })
    case 'from-name': return t('Absendername enthält „{v}“', { v })
    case 'url': return t('Link zu {v}', { v })
    case 'url-domain': return t('Link zu einer Adresse unter {v}', { v })
    case 'attach-ext': return t('Anhang vom Typ {v}', { v })
    case 'attachments': return t('{v} Anhänge', { v })
    case 'to-count': return t('{v} Empfänger', { v })
    case 'has-html': return v === 'True' ? t('Mail mit HTML') : t('Mail ohne HTML')
    case 'has-text': return v === 'True' ? t('Mail mit Textteil') : t('Mail ohne Textteil')
    case 'x-mailer': return t('Verschickt mit {v}', { v })
    case 'rbl': return t('Steht auf der Spamliste {v}', { v: lists.find(l => l.id === v)?.name ?? v })
    case 'skip': return t('Sehr langes Wort')
    case 'srv-spam': return v === 'yes' ? t('Mailserver: Spam') : t('Mailserver: kein Spam')
    case 'srv-score': return t('Mailserver-Spamwert {v}', { v: ({ neg: t('negativ'), low: t('niedrig'), mid: t('mittel'), high: t('hoch'), vhigh: t('sehr hoch') } as Record<string, string>)[v] ?? v })
    case 'srv-scl': return t('Microsoft-Spamstufe {v}', { v: ({ low: t('niedrig'), mid: t('mittel'), high: t('hoch') } as Record<string, string>)[v] ?? v })
    case 'sig':
      if (v.startsWith('dkim:')) return t('Echt signiert von {v}', { v: v.slice(5) })
      if (v.startsWith('rule:')) return t('Erkennungsregel „{v}“', { v: v.slice(5) })
      switch (v) {
        case 'dmarc=pass': return t('Absender vom Mailserver bestätigt (DMARC)')
        case 'dmarc=fail': return t('Absenderprüfung (DMARC) gescheitert')
        case 'dmarc=none': return t('Keine Absenderprüfung (DMARC)')
        case 'verified-brand': return t('Nachweislich von einem bekannten Absender')
        case 'freemail-org': return t('Kostenloses Postfach mit Firmennamen')
        case 'not-to-me': return t('Nicht an dich adressiert')
        case 'obfuscated-name': return t('Verschleierter Absendername')
        case 'random-id': return t('Zufallskennung im Betreff')
        case 'list': return t('Verteiler mit Abmeldelink')
        case 'shared-junk': return t('Von anderen auf diesem Server als Junk gemeldet')
      }
      return v
    default: return t('Wort „{v}“', { v: token })
  }
}

/** „Warum?“ zur Einstufung einer Mail durch den lernenden Spamfilter. */
export function SpamWhy({ item, onClose }: { item: MailItem; onClose: () => void }) {
  const s = useStyles()
  const [why, setWhy] = useState<Why | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    api.get<Why>(`/api/mail/${item.account}/spam-why?` + q({ folder: item.folder, uid: item.uid })).then(setWhy).catch(e => setError(e?.message ?? String(e)))
  }, [item.account, item.folder, item.uid])
  const few = why && (why.nspam < 50 || why.nham < 50)
  const list = (title: string, rs: Reason[], junk: boolean) => (
    <div>
      <Text weight="semibold">{title}</Text>
      <div className={s.list}>
        {rs.length === 0 && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Nichts Auffälliges.')}</Text>}
        {rs.map(r => <div key={r.token} className={s.row}>
          <span className={s.label}>{label(r.token, why!.lists)}</span>
          <span className={s.p} title={t('{spam}× in Junk gelernt, {ham}× in guten Mails', { spam: r.spam, ham: r.ham })}>{Math.round((junk ? r.p : 1 - r.p) * 100)} %</span>
        </div>)}
      </div>
    </div>
  )
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{t('Warum diese Einstufung?')}</DialogTitle>
          <DialogContent>
            {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
            {!why && !error && <Spinner />}
            {why && <>
              <Text block>{t('Der lernende Filter bewertet diese Mail jetzt mit {p} % Junk-Wahrscheinlichkeit. Er hat bisher {spam} Junk-Mails und {ham} gute Mails gelernt.', { p: Math.round(why.score * 100), spam: why.nspam, ham: why.nham })}</Text>
              {ruleText(why) && <MessageBar intent={why.rule === 'trusted' || why.rule === 'verified-brand' || why.rule === 'safe-list' || why.rule === 'verified-sender' || why.rule === 'few-learned' ? 'success' : 'warning'} layout="multiline" style={{ marginTop: 8 }}>
                <MessageBarBody>{ruleText(why)}</MessageBarBody></MessageBar>}
              {checksOf(why).length > 0 && <div style={{ marginTop: 12 }}>
                <Text weight="semibold">{t('Prüfungen')}</Text>
                <div className={s.list}>
                  {checksOf(why).map((c, i) => <div key={i} className={s.row}>
                    <span className={s.mark} style={{ color: c.state === 'ok' ? tokens.colorPaletteGreenForeground1 : c.state === 'bad' ? tokens.colorPaletteRedForeground1 : tokens.colorNeutralForeground3 }}>
                      {c.state === 'ok' ? '✓' : c.state === 'bad' ? '✗' : '•'}</span>
                    <span className={s.label}>{c.text}</span>
                  </div>)}
                </div>
              </div>}
              {few && <MessageBar intent="info" style={{ marginTop: 8 }}><MessageBarBody>
                {t('Mit wenig Gelerntem ist der Filter oft unsicher. Jedes „Junk“ und „Kein Junk“ macht ihn treffsicherer.')}</MessageBarBody></MessageBar>}
              <div className={s.cols}>
                {list(t('Wörter und Merkmale für Junk'), why.junk, true)}
                {list(t('Wörter und Merkmale für eine gute Mail'), why.good, false)}
              </div>
            </>}
          </DialogContent>
          <DialogActions><Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
