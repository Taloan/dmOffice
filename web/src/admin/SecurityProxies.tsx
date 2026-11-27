import { Button, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Checkmark20Regular, Delete20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t, locale } from '../i18n'
import { askConfirm } from '../shell/context'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { AdminHeading } from './AdminHeading'
import { AdminCol, AdminTable, pickCol } from './AdminTable'

interface Proxy { address: string; state: 'auto' | 'confirmed' | 'seen'; first: string; last: string; hits: number; host: string | null; by: string | null; since: string | null }
interface Data { active: boolean; env: string | null; learnUntil: string | null; learning: boolean; via: string | null; list: Proxy[] }

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  warn: { color: tokens.colorPaletteDarkOrangeForeground1 },
})

const when = (iso: string | null) => iso ? new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }) : ''
const stateName = (p: Proxy) => p.state === 'auto' ? t('automatisch übernommen') : p.state === 'confirmed' ? t('bestätigt') : t('nicht bestätigt')

/**
 * Verwaltung → Sicherheit → Reverse-Proxys (Kennung proxyconfirm): Absender mit X-Forwarded-*-Kopfzeilen. Nur bestätigte (und die beim
 * ersten Start nach dem Update übernommenen) gelten als Proxy; von allen anderen bleibt die Adresse des Absenders maßgeblich.
 */
export function SecurityProxies({ ribbon }: { ribbon: boolean }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<Data | null>(null)
  const [sel, setSel] = useState<string | null>(null)
  const load = () => api.get<Data>('/api/admin/security/proxies').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!data) return null
  const picked = data.list.find(p => p.address === sel) ?? null

  const confirm = async (p: Proxy) => {
    if (!(await askConfirm(t('Als Reverse-Proxy bestätigen?'), t('Von {address} gelten danach die Kopfzeilen X-Forwarded-For, -Proto und -Host: Die Adresse, die dieser Absender nennt, gilt als Adresse des Besuchers. Nur bestätigen, wenn es dein eigener Reverse-Proxy ist.', { address: p.address }), t('Bestätigen')))) return
    try { await api.post('/api/admin/security/proxies/confirm', { address: p.address }); notify(t('Bestätigt')); load() } catch (e) { fail(e) }
  }
  const remove = async (p: Proxy) => {
    const text = p.state === 'seen' ? t('{address} aus der Liste entfernen?', { address: p.address })
      : t('{address} gilt danach nicht mehr als Reverse-Proxy. Kommen Anfragen weiter über ihn, sieht der Server nur noch seine Adresse statt der des Besuchers.', { address: p.address })
    if (!(await askConfirm(t('Entfernen?'), text, t('Entfernen')))) return
    try { await api.del('/api/admin/security/proxies?' + q({ address: p.address })); setSel(null); notify(t('Entfernt')); load() } catch (e) { fail(e) }
  }

  const cols: AdminCol<Proxy>[] = [
    ...(ribbon ? [pickCol<Proxy, string>({ id: p => p.address, selected: sel, onChange: setSel })] : []),
    { key: 'address', label: () => t('Adresse'), width: 170, sort: p => p.address, render: p => <>{p.address}{p.address === data.via ? <span className={s.muted}> · {t('deine Verbindung')}</span> : null}</> },
    { key: 'state', label: () => t('Stand'), width: 190, sort: p => p.state, render: p => <span className={p.state === 'seen' ? s.warn : undefined}>{stateName(p)}{p.by ? <span className={s.muted}> · {t('von {who}', { who: p.by })}</span> : null}</span> },
    { key: 'host', label: () => t('Host'), width: 0, sort: p => p.host ?? '', render: p => p.host },
    { key: 'hits', label: () => t('Anfragen'), width: 90, sort: p => p.hits, render: p => p.hits },
    { key: 'first', label: () => t('Zuerst'), width: 130, sort: p => Date.parse(p.first), render: p => when(p.first) },
    { key: 'last', label: () => t('Zuletzt'), width: 130, sort: p => Date.parse(p.last), render: p => when(p.last) },
    ...(ribbon ? [] : [{
      key: 'act', label: () => '', width: 200, fixed: true, render: (p: Proxy) => <>
        {p.state === 'seen' && <Button size="small" onClick={() => confirm(p)}>{t('Bestätigen')}</Button>}
        <Button size="small" appearance="subtle" onClick={() => remove(p)}>{t('Entfernen')}</Button>
      </>,
    }]),
  ]

  return <>
    {ribbon && data.active && <RibbonActions>
      <RibbonButton icon={<Checkmark20Regular />} label={t('Als Reverse-Proxy bestätigen')} disabled={picked?.state !== 'seen'} onClick={() => picked && confirm(picked)} />
      <RibbonButton icon={<Delete20Regular />} label={t('Aus der Proxy-Liste entfernen')} disabled={!picked} onClick={() => picked && remove(picked)} />
    </RibbonActions>}
    <AdminHeading>{t('Reverse-Proxys')}</AdminHeading>
    {data.env
      ? <Text size={200} className={s.muted}>{t('Es gilt allein DMOFFICE_TRUSTED_PROXIES: {nets}', { nets: data.env })}</Text>
      : !data.active ? <Text size={200} className={s.muted}>{t('Die Liste ist nicht aktiv.')}</Text>
      : <>
        <Text size={200} className={s.muted}>{t('Steht dmOffice hinter einem Reverse-Proxy, nennt dieser die Adresse des Besuchers in X-Forwarded-For. Das gilt nur von bestätigten Proxys und von dieser Maschine selbst; bei allen anderen Absendern zählt ihre eigene Adresse, auch für Sperren und Bremsen.')}</Text>
        {data.learning && data.learnUntil && <Text size={200}>{t('Bis {time} werden Proxys, die schon vor dem Update galten, automatisch übernommen.', { time: when(data.learnUntil) })}</Text>}
        {data.list.length === 0 ? <Text className={s.muted}>{t('Bisher hat kein Absender X-Forwarded-*-Kopfzeilen geschickt.')}</Text>
          : <AdminTable id="security.proxies" cols={cols} rows={data.list} rowKey={p => p.address} initialSort={{ col: 'last', desc: true }}
            onRowClick={ribbon ? p => setSel(p.address) : undefined} active={ribbon ? picked?.address ?? null : undefined} />}
      </>}
  </>
}
