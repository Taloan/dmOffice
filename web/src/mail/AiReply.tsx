// „Antwort mit KI“ (Kennung mailaireply): Entwurf der KI für eine Antwort - im Menüband beim Lesen und beim Antworten.
// Das Ergebnis ist immer ein bearbeitbarer Entwurf; gesendet wird nur über „Senden“.
import { Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner } from '@fluentui/react-components'
import { Sparkle20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { t } from '../i18n'
import { askText } from '../shell/context'
import { RibbonButton } from '../shell/Ribbon'
import { useApp } from '../App'

/** Zwecke wie auf dem Server (Mail/MailAiReply.cs, Modes) */
export const aiReplyModes = (): [string, string][] => [
  ['accept', t('Zusagen')], ['decline', t('Absagen')], ['question', t('Nachfragen')], ['thanks', t('Danken')], ['custom', t('Eigene Anweisung …')],
]

let known: Promise<boolean> | null = null
/** Ist eine KI angebunden und für den Benutzer freigegeben? (einmal je Sitzung gefragt) */
export function useAiReply(): boolean {
  const [on, setOn] = useState(false)
  useEffect(() => {
    if (!featureOn('mailaireply')) return
    known ??= api.get<{ available: boolean }>('/api/mail/ai').then(r => !!r.available).catch(() => { known = null; return false })
    let alive = true
    known.then(v => { if (alive) setOn(v) })
    return () => { alive = false }
  }, [])
  return on
}

/** Text der KI als HTML-Absätze (Zeichen maskiert) */
export const aiHtml = (text: string) => text.split(/\n{2,}/).map(p => p.trim()).filter(Boolean)
  .map(p => '<div>' + p.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/\n/g, '<br>') + '</div><div><br></div>').join('')

/** Entwurf holen; null = abgebrochen. Bei „Eigene Anweisung …“ vorher nach der Anweisung fragen. */
export async function aiReplyDraft(source: { account: string; folder: string; uid: number }, mode: string): Promise<string | null> {
  let hint: string | null = null
  if (mode === 'custom') { hint = await askText(t('Antwort mit KI'), '', t('Wie soll die Antwort ausfallen?')); if (!hint?.trim()) return null }
  const r = await api.post<{ reply: string }>(`/api/mail/${encodeURIComponent(source.account)}/ai-reply`, { folder: source.folder, uid: source.uid, mode, hint })
  return r.reply
}

/** Menüband-Knopf mit den Zwecken; onPick erhält den fertigen Text */
export function AiReplyMenu({ source, disabled, onDraft }: {
  source: { account: string; folder: string; uid: number } | null
  disabled?: boolean
  onDraft: (text: string, mode: string) => void
}) {
  const { fail } = useApp()
  const [busy, setBusy] = useState(false)
  const pick = async (mode: string) => {
    if (!source) return
    setBusy(true)
    try { const text = await aiReplyDraft(source, mode); if (text) onDraft(text, mode) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return <Menu>
    <MenuTrigger disableButtonEnhancement>
      <RibbonButton icon={busy ? <Spinner size="tiny" /> : <Sparkle20Regular />} label={t('Antwort mit KI')} disabled={disabled || busy || !source} />
    </MenuTrigger>
    <MenuPopover><MenuList>
      {aiReplyModes().map(([k, label]) => <MenuItem key={k} onClick={() => pick(k)}>{label}</MenuItem>)}
    </MenuList></MenuPopover>
  </Menu>
}
