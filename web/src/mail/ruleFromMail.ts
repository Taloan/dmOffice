import { api, MailItem, Message, q } from '../api'

/** Was "Regel erstellen" aus der gewaehlten Mail mitgibt - die Regel-Einstellungen belegen damit die Bedingungen vor. */
export interface RuleSource { account: string; from: string; fromName: string; to: string; subject: string; listId: string }

const KEY = 'dmoffice.ruleFrom'

/** Betreff ohne Re:/AW:/Fwd:/WG: vorne - die Regel soll auch die Antworten treffen. */
const plainSubject = (s: string) => s.replace(/^\s*((re|aw|fwd?|wg|antw)\s*(\[\d+\])?\s*:\s*)+/i, '').trim()

/** List-Id "Name <id.example.org>": nur die Kennung, die bleibt gleich, wenn der Name wechselt. */
const plainListId = (s: string) => (/<([^>]+)>/.exec(s)?.[1] ?? s).trim()

/** Mail laden, Angaben merken und zu den Regeln wechseln - dort entsteht eine neue, vorbelegte Regel. */
export async function ruleFromMail(item: MailItem, loaded?: Message | null) {
  const m = loaded && loaded.uid === item.uid && loaded.folder === item.folder && loaded.account === item.account ? loaded
    : await api.get<Message>(`/api/mail/${item.account}/message?` + q({ folder: item.folder, uid: item.uid, images: false, markSeen: false }))
  const src: RuleSource = {
    account: item.account, from: m.from?.address ?? '', fromName: m.from?.name || m.from?.address || '',
    to: m.to[0]?.address ?? m.cc[0]?.address ?? '', subject: plainSubject(m.subject ?? ''), listId: plainListId(m.listId ?? ''),
  }
  try { sessionStorage.setItem(KEY, JSON.stringify(src)) } catch { /* ohne Speicher: leere Regel */ }
  location.hash = '#/settings/rules'
}

/** Einmal abholen (die Regel-Einstellungen legen daraus eine neue Regel an). */
export function takeRuleSource(): RuleSource | null {
  try {
    const raw = sessionStorage.getItem(KEY)
    sessionStorage.removeItem(KEY)
    return raw ? JSON.parse(raw) : null
  } catch { return null }
}

/** Wert einer Bedingung aus der Mail, je nach Feld. */
export function sourceValue(src: RuleSource, field: string): string {
  switch (field) {
    case 'from': return src.from
    case 'to': return src.to
    case 'subject': return src.subject
    case 'header:List-Id': return src.listId
    default: return ''
  }
}
