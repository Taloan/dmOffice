import {
  Badge, Button, Checkbox, Dropdown, Option, SearchBox, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, tokens,
} from '@fluentui/react-components'
import { Delete20Regular, PersonAdd20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { api, Collection } from '../api'
import { useApp } from '../App'
import { fmtDate } from '../shell/hooks'
import { t } from '../i18n'

interface Recent { name?: string | null; address: string; count: number; last: string; inContacts: boolean }

/** Adressvorschlaege (zuletzt angeschrieben): loeschen oder ins Adressbuch uebernehmen - einzeln oder mehrere. */
export function RecentAddresses() {
  const { fail, notify } = useApp()
  const [list, setList] = useState<Recent[] | null>(null)
  const [books, setBooks] = useState<Collection[]>([])
  const [book, setBook] = useState<number | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [q, setQ] = useState('')

  const load = () => api.get<Recent[]>('/api/pim/recent').then(setList).catch(fail)
  useEffect(() => {
    load()
    api.get<Collection[]>('/api/pim/collections/addressbooks').then(b => { setBooks(b); setBook(b[0]?.id ?? null) }).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const shown = useMemo(() => {
    const term = q.trim().toLowerCase()
    return (list ?? []).filter(r => !term || r.address.toLowerCase().includes(term) || (r.name ?? '').toLowerCase().includes(term))
  }, [list, q])

  const allPicked = shown.length > 0 && shown.every(r => picked.includes(r.address))
  const toggle = (a: string) => setPicked(p => (p.includes(a) ? p.filter(x => x !== a) : [...p, a]))

  const remove = async () => {
    if (!confirm(picked.length === 1 ? t('1 Adresse aus den Vorschlägen entfernen? Kontakte bleiben unberührt.') : t('{n} Adressen aus den Vorschlägen entfernen? Kontakte bleiben unberührt.', { n: picked.length }))) return
    try { await api.post('/api/pim/recent/delete', { addresses: picked }); setPicked([]); load() } catch (e) { fail(e) }
  }
  const add = async () => {
    try {
      const r = await api.post('/api/pim/recent/add', { addresses: picked, book })
      notify(r.added === 1 ? t('1 Kontakt angelegt') : t('{n} Kontakte angelegt', { n: r.added }), picked.length > r.added ? t('Adressen, die schon im Adressbuch stehen, wurden übersprungen.') : undefined)
      setPicked([]); load()
    } catch (e) { fail(e) }
  }

  if (!list) return null
  return (
    <>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <SearchBox placeholder={t('Vorschläge durchsuchen')} value={q} onChange={(_, d) => setQ(d.value)} style={{ minWidth: 240 }} />
        <Button icon={<PersonAdd20Regular />} disabled={!picked.length || !book} onClick={add}>{t('In Adressbuch übernehmen')}</Button>
        {books.length > 1 && (
          <Dropdown style={{ minWidth: 160 }} value={books.find(b => b.id === book)?.name ?? ''} selectedOptions={[String(book)]}
            onOptionSelect={(_, d) => setBook(Number(d.optionValue))}>
            {books.map(b => <Option key={b.id} value={String(b.id)}>{b.name}</Option>)}
          </Dropdown>
        )}
        <Button icon={<Delete20Regular />} disabled={!picked.length} onClick={remove}>{t('Löschen')}</Button>
        {picked.length > 0 && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('{n} ausgewählt', { n: picked.length })}</Text>}
      </div>
      {list.length === 0 ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Noch keine – sie entstehen beim Senden.')}</Text> : (
        <Table size="small" style={{ maxWidth: 900 }}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell style={{ width: 40 }}>
                <Checkbox checked={allPicked ? true : picked.length ? 'mixed' : false}
                  onChange={() => setPicked(allPicked ? [] : shown.map(r => r.address))} aria-label={t('Alle')} />
              </TableHeaderCell>
              <TableHeaderCell>{t('Name')}</TableHeaderCell>
              <TableHeaderCell>{t('Adresse')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 80 }}>{t('Anzahl')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 120 }}>{t('Zuletzt')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.map(r => (
              <TableRow key={r.address} onClick={() => toggle(r.address)} style={{ cursor: 'pointer' }}>
                <TableCell><Checkbox checked={picked.includes(r.address)} onChange={() => toggle(r.address)} onClick={e => e.stopPropagation()} /></TableCell>
                <TableCell>{r.name || '–'} {r.inContacts && <Badge size="small" appearance="tint" color="success">{t('Kontakt')}</Badge>}</TableCell>
                <TableCell>{r.address}</TableCell>
                <TableCell>{r.count}</TableCell>
                <TableCell>{fmtDate(r.last, false)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </>
  )
}
