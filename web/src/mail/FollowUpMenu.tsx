import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input,
  Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, makeStyles, tokens,
} from '@fluentui/react-components'
import { Flag20Filled, Flag20Regular, CheckmarkCircle20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { RibbonButton } from '../shell/Ribbon'
import { locale, t } from '../i18n'

const useStyles = makeStyles({ when: { color: tokens.colorNeutralForeground3 } })

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const plus = (n: number) => { const d = new Date(); d.setDate(d.getDate() + n); return d }

/** Faelligkeiten wie Outlook: Heute, Morgen, Diese Woche (Freitag), Naechste Woche (Montag), ohne Datum. */
export function followUpOptions(): { label: string; due: string | null; hint: string }[] {
  const wd = (d: Date) => d.toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' })
  const today = new Date()
  const day = today.getDay()
  const friday = plus(day <= 5 ? 5 - day : 6)          // Samstag/Sonntag: Freitag der naechsten Woche
  const monday = plus(((8 - day) % 7) || 7)
  const list = [
    { label: t('Heute'), due: iso(today), hint: wd(today) },
    { label: t('Morgen'), due: iso(plus(1)), hint: wd(plus(1)) },
  ]
  if (iso(friday) !== iso(plus(1)) && iso(friday) !== iso(today)) list.push({ label: t('Diese Woche'), due: iso(friday), hint: wd(friday) })
  list.push({ label: t('Nächste Woche'), due: iso(monday), hint: wd(monday) })
  list.push({ label: t('Ohne Datum'), due: null as any, hint: '' })
  return list
}

/**
 * Menueintraege - fuer Menueband und Rechtsklick.
 * onCustom: "Datum waehlen" oeffnet den Dialog ausserhalb des Menues - ein Fluent-Menue haengt
 * seinen Inhalt beim Schliessen aus, ein Dialog darin verschwaende sofort wieder.
 */
export function FollowUpItems({ onPick, onDone, flagged, onCustom, due }: {
  onPick: (due: string | null) => void; onDone: () => void; flagged: boolean; onCustom?: () => void; due?: string | null
}) {
  const s = useStyles()
  const [custom, setCustom] = useState(false)
  return (
    <>
      <MenuList>
        {followUpOptions().map(o => (
          <MenuItem key={o.label} icon={flagged && due !== undefined && o.due === due ? <Flag20Filled color={tokens.colorPaletteRedForeground1} /> : <Flag20Regular />}
            secondaryContent={<span className={s.when}>{o.hint}</span>} onClick={() => onPick(o.due)}>{o.label}</MenuItem>
        ))}
        <MenuItem onClick={() => onCustom ? onCustom() : setCustom(true)}>{t('Datum wählen …')}</MenuItem>
        {flagged && <MenuDivider />}
        {flagged && <MenuItem icon={<CheckmarkCircle20Regular />} onClick={onDone}>{t('Als erledigt markieren')}</MenuItem>}
      </MenuList>
      {custom && <FollowUpDateDialog initial={due} onClose={v => { setCustom(false); if (v) onPick(v) }} />}
    </>
  )
}

/** Eigenes Faelligkeitsdatum. */
export function FollowUpDateDialog({ initial, onClose }: { initial?: string | null; onClose: (due: string | null) => void }) {
  const [value, setValue] = useState(initial || iso(plus(1)))
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(null)}>
      <DialogSurface style={{ maxWidth: 360 }}>
        <DialogBody>
          <DialogTitle>{t('Nachverfolgen bis')}</DialogTitle>
          <DialogContent>
            <Field label={t('Fällig am')}><Input type="date" value={value} onChange={(_, d) => setValue(d.value)} autoFocus /></Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!value} onClick={() => onClose(value)}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Menueband-Knopf "Nachverfolgen". */
export function FollowUpRibbon({ disabled, flagged, onPick, onDone }: { disabled: boolean; flagged: boolean; onPick: (due: string | null) => void; onDone: () => void }) {
  const [custom, setCustom] = useState(false)
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <RibbonButton icon={flagged ? <Flag20Filled color={tokens.colorPaletteRedForeground1} /> : <Flag20Regular />} label={t('Nachverfolgen')} disabled={disabled} />
        </MenuTrigger>
        <MenuPopover><FollowUpItems onPick={onPick} onDone={onDone} flagged={flagged} onCustom={() => setCustom(true)} /></MenuPopover>
      </Menu>
      {custom && <FollowUpDateDialog onClose={v => { setCustom(false); if (v) onPick(v) }} />}
    </>
  )
}

/** Faelligkeit als Knopf mit Fahne und Menue - fuer die Liste "E-Mail-Nachverfolgung". */
export function FollowUpDueButton({ due, onPick, onDone }: { due: string | null; onPick: (due: string | null) => void; onDone: () => void }) {
  const [custom, setCustom] = useState(false)
  const late = !!due && due < iso(new Date())
  const label = due ? new Date(due + 'T12:00').toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' }) : t('Ohne Datum')
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button size="small" appearance="subtle" icon={<Flag20Filled color={tokens.colorPaletteRedForeground1} />}
            style={late ? { color: tokens.colorPaletteRedForeground1 } : undefined} title={t('Fällig am')}>{label}</Button>
        </MenuTrigger>
        <MenuPopover>
          <FollowUpItems onPick={onPick} onDone={onDone} flagged due={due} onCustom={() => setCustom(true)} />
        </MenuPopover>
      </Menu>
      {custom && <FollowUpDateDialog initial={due} onClose={v => { setCustom(false); if (v) onPick(v) }} />}
    </>
  )
}
