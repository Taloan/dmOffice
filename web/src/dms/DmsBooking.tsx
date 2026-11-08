import { Dropdown, Field, Input, Option } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { moneyText, parseMoney } from '../finance/money'
import { askConfirm } from '../shell/context'
import { Doc } from './model'

/** Buchung in Finanzen, wie der Server sie zum Dokument meldet. Der Betrag gehört der Buchung, nicht dem Dokument. */
interface Booking { entry: number; kind: 'income' | 'expense'; amountCents: number; date: string; unassigned: boolean; closed: boolean; invoice: boolean }

/**
 * Feld „In Finanzen“ in der Bearbeitung eines Dokuments: als Einnahme oder Ausgabe kennzeichnen. Legt in Finanzen einen Eintrag mit der
 * Kategorie „Noch nicht zugeordnet“ an und fixiert das Dokument; Art und Betrag der Buchung lassen sich hier und in Finanzen ändern.
 * Nur, wenn Finanzen freigeschaltet ist.
 */
export function DmsBooking({ doc, tresor, onReload }: { doc: Doc; tresor: boolean; onReload: () => void }) {
  const { fail } = useApp()
  const [booking, setBooking] = useState<Booking | null | undefined>(undefined)
  const [amount, setAmount] = useState('')
  const area = tresor ? '?area=tresor' : ''
  const show = (b: Booking | null) => { setBooking(b); setAmount(b ? moneyText(b.amountCents) : '') }
  useEffect(() => {
    setBooking(undefined)
    api.get<{ booking: Booking | null }>(`/api/finance/dms/${doc.id}${area}`).then(r => show(r.booking)).catch(() => setBooking(null))
  }, [doc.id, tresor]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = async (kind: 'income' | 'expense' | null, amountCents?: number) => {
    if (!kind && booking && !await askConfirm(t('Buchung entfernen'), t('Der Eintrag in Finanzen wird gelöscht. Das Dokument bleibt fixiert.'), t('Entfernen'))) return
    try {
      const r = await api.put<{ booking: Booking | null }>(`/api/finance/dms/${doc.id}${area}`, { kind, amountCents: amountCents ?? null })
      show(r.booking)
      onReload()
    } catch (e) { fail(e); show(booking ?? null) }
  }

  if (booking === undefined) return null
  const locked = !!booking && (booking.closed || booking.invoice)
  const kindName = (k: string) => k === 'income' ? t('Einnahme') : k === 'expense' ? t('Ausgabe') : t('Nicht gebucht')
  const hints = !booking ? [t('Legt einen Eintrag in Finanzen an und fixiert das Dokument.')] : [
    booking.invoice ? t('Gehört zu einer Rechnung. Ändern lässt sich die Buchung nur in Finanzen.') : '',
    booking.closed ? t('Das Jahr ist abgeschlossen. Korrekturen nur in Finanzen.') : '',
    booking.unassigned ? t('Die Kategorie muss in Finanzen noch zugeordnet werden.') : '',
    doc.amount != null && doc.amount !== booking.amountCents ? t('Weicht vom Betrag des Dokuments ab.') : '',
  ].filter(Boolean)
  return (
    <Field label={t('In Finanzen')} hint={hints.join(' ')}>
      <div style={{ display: 'flex', gap: 6 }}>
        <Dropdown style={{ minWidth: 0, flex: 1 }} disabled={locked} value={kindName(booking?.kind ?? '')} selectedOptions={[booking?.kind ?? '']}
          onOptionSelect={(_, d) => { const k = (d.optionValue || null) as 'income' | 'expense' | null; if (k !== (booking?.kind ?? null)) save(k) }}>
          <Option value="">{t('Nicht gebucht')}</Option>
          <Option value="income">{t('Einnahme')}</Option>
          <Option value="expense">{t('Ausgabe')}</Option>
        </Dropdown>
        {booking && <Input style={{ width: 120 }} aria-label={t('Betrag in Finanzen')} value={amount} inputMode="decimal" contentAfter="€" disabled={locked}
          onChange={(_, d) => setAmount(d.value)}
          onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
          onBlur={() => {
            const cents = parseMoney(amount)
            if (cents == null || cents === 0) { setAmount(moneyText(booking.amountCents)); return }
            if (Math.abs(cents) !== booking.amountCents) save(booking.kind, Math.abs(cents))
          }} />}
      </div>
    </Field>
  )
}
