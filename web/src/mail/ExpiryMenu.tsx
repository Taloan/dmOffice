import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, FluentProvider, Input,
  Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, tokens,
} from '@fluentui/react-components'
import { ArrowUndo20Regular, Delete20Regular, DeleteDismiss20Regular, HourglassHalf20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { api, featureOn } from '../api'
import { fmtDate, fmtDay, fmtTime } from '../shell/hooks'
import { askConfirm } from '../shell/context'
import { darkTheme, lightTheme, storedTheme } from '../theme'
import { t } from '../i18n'

// Entfernen nach Zeit (Server: Mail/MailExpiry.cs): in den Papierkorb nach … oder vernichten nach …,
// ausgefuehrt vom Mail-Sync auf dem Server - auch ohne offenes Geraet.

export type ExpiryKind = 'trash' | 'destroy'
export interface Expiry { account: string; folder: string; uid: number; messageId?: string; at: string; kind: ExpiryKind; subject: string }
type Target = { account: string; folder: string; uid: number; messageId?: string }

// Eine Liste fuer Mailliste, Lesebereich und Mail im eigenen Fenster
let cache: Expiry[] = []
const listeners = new Set<() => void>()

export function reloadExpiries() {
  // Release-Liste: „Entfernen nach Zeit“ in dieser Version nicht freigeschaltet
  if (!featureOn('mailexpiry')) return Promise.resolve()
  return api.get<Expiry[]>('/api/mail/expiries').then(list => { cache = list; listeners.forEach(f => f()) }).catch(() => {})
}

/** Die geplanten Eintraege; laedt neu, wenn sich <paramref name="version"/> aendert (Mail-Status). */
export function useExpiries(version?: number) {
  const [, force] = useState(0)
  useEffect(() => {
    const f = () => force(n => n + 1)
    listeners.add(f)
    return () => { listeners.delete(f) }
  }, [])
  useEffect(() => { reloadExpiries() }, [version])
  return cache
}

/** Der naechste Zeitpunkt fuer diese Mail - nach Message-ID (bleibt beim Verschieben), sonst Ordner und UID. */
export function expiryOf(list: Expiry[], i: Target): Expiry | undefined {
  return list
    .filter(e => e.account === i.account && (e.messageId && i.messageId ? e.messageId === i.messageId : e.folder === i.folder && e.uid === i.uid))
    .sort((a, b) => a.at.localeCompare(b.at))[0]
}

const when = (iso: string) => { const d = new Date(iso); return `${fmtDay(d)} ${fmtTime(d)}` }

export function expiryText(e: Expiry) {
  return e.kind === 'destroy'
    ? t('Wird am {when} vernichtet', { when: when(e.at) })
    : t('Wird am {when} in den Papierkorb verschoben', { when: when(e.at) })
}

function groups(targets: Target[]) {
  const g = new Map<string, { account: string; folder: string; uids: number[] }>()
  for (const i of targets) {
    const k = i.account + '|' + i.folder
    if (!g.has(k)) g.set(k, { account: i.account, folder: i.folder, uids: [] })
    g.get(k)!.uids.push(i.uid)
  }
  return [...g.values()]
}

const inDays = (n: number) => { const d = new Date(); d.setDate(d.getDate() + n); d.setSeconds(0, 0); return d }

/** Einplanen; „Vernichten“ erst nach einem klaren Hinweis. Gibt zurueck, ob eingeplant wurde. */
export async function setExpiry(targets: Target[], kind: ExpiryKind, at: Date): Promise<boolean> {
  if (!targets.length) return false
  if (kind === 'destroy' && !await askConfirm(t('Endgültig vernichten?'),
    (targets.length === 1
      ? t('Die Mail wird am {when} auf dem Mailserver endgültig gelöscht und aus dmOffice entfernt. Sie kommt nicht in den Papierkorb und lässt sich danach nicht wiederherstellen.', { when: when(at.toISOString()) })
      : t('{n} Mails werden am {when} auf dem Mailserver endgültig gelöscht und aus dmOffice entfernt. Sie kommen nicht in den Papierkorb und lassen sich danach nicht wiederherstellen.', { n: targets.length, when: when(at.toISOString()) }))
    + ' ' + t('Kopien auf anderen Geräten, beim Absender oder in Sicherungen des Mail-Anbieters erreicht dmOffice nicht. Bis dahin kannst du es zurücknehmen.'),
    t('Vernichten'))) return false
  for (const g of groups(targets)) await api.post(`/api/mail/${g.account}/expire`, { folder: g.folder, uids: g.uids, at: at.toISOString(), kind })
  await reloadExpiries()
  return true
}

export async function cancelExpiry(targets: Target[]) {
  for (const g of groups(targets)) await api.post(`/api/mail/${g.account}/expire/cancel`, { folder: g.folder, uids: g.uids })
  await reloadExpiries()
}

/** Eigener Zeitpunkt („am …“) als Dialog, wie askConfirm ausserhalb des Menues. */
function askWhen(kind: ExpiryKind): Promise<Date | null> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    const done = (r: Date | null) => { root.unmount(); host.remove(); resolve(r) }
    const dark = storedTheme() === 'dark' || (storedTheme() === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
    const p = (n: number) => String(n).padStart(2, '0')
    const d = inDays(7)
    const initial = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
    function Ask() {
      const [value, setValue] = useState(initial)
      return (
        <Dialog open onOpenChange={(_, x) => !x.open && done(null)}>
          <DialogSurface style={{ maxWidth: 360 }}>
            <DialogBody>
              <DialogTitle>{kind === 'destroy' ? t('Vernichten am …') : t('In den Papierkorb am …')}</DialogTitle>
              <DialogContent>
                <Field label={t('Datum und Uhrzeit')}>
                  <Input type="datetime-local" value={value} onChange={(_, x) => setValue(x.value)} />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => done(null)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" disabled={!value || new Date(value) <= new Date()} onClick={() => done(new Date(value))}>{t('OK')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )
    }
    root.render(<FluentProvider theme={dark ? darkTheme : lightTheme}><Ask /></FluentProvider>)
  })
}

/** Menue-Eintraege „In den Papierkorb nach …“, „Vernichten nach …“ und „Zurücknehmen“ - fuer Kontextmenue und Lesebereich.
 * onDone: nach dem Einplanen oder Zuruecknehmen (Menue schliessen, Meldung). */
export function ExpiryMenuItems({ targets, onClose, onDone, onError }: {
  targets: Target[]; onClose?: () => void; onDone?: (what: 'set' | 'cancel') => void; onError: (e: unknown) => void
}) {
  const planned = useExpiries()
  const any = targets.some(i => expiryOf(planned, i))
  if (!featureOn('mailexpiry')) return null
  const pick = async (kind: ExpiryKind, at: Date | null) => {
    onClose?.()
    if (!at) return
    try { if (await setExpiry(targets, kind, at)) onDone?.('set') } catch (e) { onError(e) }
  }
  const sub = (kind: ExpiryKind) => (
    <MenuList>
      <MenuItem onClick={() => pick(kind, inDays(1))}>{t('1 Tag')}</MenuItem>
      <MenuItem onClick={() => pick(kind, inDays(7))}>{t('7 Tage')}</MenuItem>
      <MenuItem onClick={() => pick(kind, inDays(30))}>{t('30 Tage')}</MenuItem>
      <MenuDivider />
      <MenuItem onClick={async () => { onClose?.(); pick(kind, await askWhen(kind)) }}>{t('Am …')}</MenuItem>
    </MenuList>
  )
  return <>
    <Menu>
      <MenuTrigger disableButtonEnhancement><MenuItem icon={<Delete20Regular />}>{t('In den Papierkorb nach …')}</MenuItem></MenuTrigger>
      <MenuPopover>{sub('trash')}</MenuPopover>
    </Menu>
    <Menu>
      <MenuTrigger disableButtonEnhancement><MenuItem icon={<DeleteDismiss20Regular />}>{t('Vernichten nach …')}</MenuItem></MenuTrigger>
      <MenuPopover>{sub('destroy')}</MenuPopover>
    </Menu>
    {any && (
      <MenuItem icon={<ArrowUndo20Regular />} onClick={async () => {
        onClose?.()
        try { await cancelExpiry(targets); onDone?.('cancel') } catch (e) { onError(e) }
      }}>{t('Entfernen nach Zeit zurücknehmen')}</MenuItem>
    )}
  </>
}

/** In der Mailliste: Sanduhr mit Zeitpunkt, der ganze Satz als Tooltip; Vernichten in Rot. */
export function ExpiryTag({ e, className }: { e: Expiry; className?: string }) {
  return (
    <span className={className} title={expiryText(e)} style={e.kind === 'destroy' ? { color: tokens.colorPaletteRedForeground1 } : undefined}>
      ⌛ {fmtDate(e.at)}
    </span>
  )
}

export const ExpiryIcon = HourglassHalf20Regular
