import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input,
  Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, makeStyles, tokens,
} from '@fluentui/react-components'
import { Clock20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { RibbonButton } from '../shell/Ribbon'
import { fmtTime } from '../shell/hooks'
import { locale, t } from '../i18n'

const useStyles = makeStyles({
  item: { display: 'flex', justifyContent: 'space-between', gap: '24px', minWidth: '240px' },
  when: { color: tokens.colorNeutralForeground3 },
})

const at = (d: Date, h: number, m = 0) => { const x = new Date(d); x.setHours(h, m, 0, 0); return x }
const addDays = (d: Date, n: number) => { const x = new Date(d); x.setDate(x.getDate() + n); return x }
const wd = (d: Date) => d.toLocaleDateString(locale, { weekday: 'short' })

/** Zeitvorschlaege wie im neuen Outlook - gleiche Zeitpunkte nur einmal. */
export function snoozeOptions(now = new Date()) {
  const list: { label: string; when: Date; hint: string }[] = []
  const add = (label: string, when: Date, sameDay = false) => {
    if (when <= now || list.some(x => x.when.getTime() === when.getTime())) return
    list.push({ label, when, hint: sameDay ? fmtTime(when) : `${wd(when)}, ${fmtTime(when)}` })
  }
  const inHours = (h: number) => { const x = new Date(now.getTime() + h * 3600000); x.setSeconds(0, 0); x.setMinutes(Math.ceil(x.getMinutes() / 15) * 15); return x }
  add(t('In 1 Stunde'), inHours(1), true)
  const later = inHours(3)
  later.setMinutes(0); later.setHours(later.getHours() + 1)
  if (later.toDateString() === now.toDateString()) add(t('Später heute'), later, true)
  if (now.getHours() < 17) add(t('Heute Abend'), at(now, 18), true)
  add(t('Morgen'), at(addDays(now, 1), 9))
  add(t('Morgen Nachmittag'), at(addDays(now, 1), 14))
  add(t('Übermorgen'), at(addDays(now, 2), 9))
  const day = now.getDay()
  if (day >= 1 && day <= 4) add(t('Dieses Wochenende'), at(addDays(now, 6 - day), 10))
  const monday = at(addDays(now, ((8 - day) % 7) || 7), 9)
  // Sonntag: "Morgen" ist schon Montag - dann die Woche danach
  add(t('Nächste Woche'), list.some(x => x.when.getTime() === monday.getTime()) ? addDays(monday, 7) : monday)
  add(t('In zwei Wochen'), at(addDays(now, 14), 9))
  const month = new Date(now.getFullYear(), now.getMonth() + 1, 1, 9)
  add(t('Nächster Monat'), month)
  return list
}

/** Menueband-Knopf "Wiedervorlage" mit Vorschlaegen und eigenem Datum. */
export function SnoozeMenu({ disabled, onSnooze }: { disabled: boolean; onSnooze: (until: Date) => void }) {
  const s = useStyles()
  const [custom, setCustom] = useState(false)
  const pad = (n: number) => String(n).padStart(2, '0')
  const tomorrow = at(addDays(new Date(), 1), 9)
  const [value, setValue] = useState(`${tomorrow.getFullYear()}-${pad(tomorrow.getMonth() + 1)}-${pad(tomorrow.getDate())}T09:00`)
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <RibbonButton icon={<Clock20Regular />} label={t('Wiedervorlage')} disabled={disabled} />
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            {snoozeOptions().map(o => (
              <MenuItem key={o.label} onClick={() => onSnooze(o.when)}>
                <span className={s.item}><span>{o.label}</span><span className={s.when}>{o.hint}</span></span>
              </MenuItem>
            ))}
            <MenuDivider />
            <MenuItem onClick={() => setCustom(true)}>{t('Datum und Uhrzeit wählen …')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {custom && (
        <Dialog open onOpenChange={(_, d) => !d.open && setCustom(false)}>
          <DialogSurface style={{ maxWidth: 360 }}>
            <DialogBody>
              <DialogTitle>{t('Wiedervorlage')}</DialogTitle>
              <DialogContent>
                <Field label={t('Wann soll die Mail wieder im Posteingang sein?')}>
                  <Input type="datetime-local" value={value} onChange={(_, d) => setValue(d.value)} />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setCustom(false)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" disabled={!value || new Date(value) <= new Date()}
                  onClick={() => { setCustom(false); onSnooze(new Date(value)) }}>{t('Erinnern')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </>
  )
}
