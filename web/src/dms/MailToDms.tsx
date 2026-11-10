// DMS im Mail-Bereich: „Ins DMS ablegen“ in der Leiste (mit Auswahl des Ablageprofils, wenn es mehrere gibt), am Anhang und der
// Hinweis „abgelegt“. Die Mail ruft das DMS auf (/api/mail/{acc}/to-dms); das DMS kennt die Mail nur als .eml bzw. Datei.
import { Button, Link, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, Tooltip, tokens } from '@fluentui/react-components'
import { Attach20Regular, DocumentBulletList16Regular, DocumentBulletList20Regular, Mail20Regular, MailAttach20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, featureOn, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { RibbonButton } from '../shell/Ribbon'
import { Profile } from './model'

interface MailRef { account: string; folder: string; uid: number }
type What = 'whole' | 'attachments' | 'both'

let cache: Promise<Profile[]> | null = null
/** Ablageprofile (einmal je Seite geladen). */
function useProfiles(): Profile[] {
  const [list, setList] = useState<Profile[]>([])
  useEffect(() => {
    if (!featureOn('dms')) return
    cache ??= api.get<Profile[]>('/api/dms/profiles?' + q({ lang })).catch(() => { cache = null; return [] })
    cache.then(setList)
  }, [])
  return list
}

async function file(items: MailRef[], what: What, profile: string | null, parts?: number[]) {
  let n = 0, dup = 0
  for (const i of items) {
    const r = await api.post<{ files: { duplicate: boolean }[] }>(`/api/mail/${i.account}/to-dms`, {
      folder: i.folder, uid: i.uid, whole: what !== 'attachments', attachments: !parts && what !== 'whole', parts, profile,
    })
    n += r.files.filter(f => !f.duplicate).length
    dup += r.files.filter(f => f.duplicate).length
  }
  return { n, dup }
}

/** Knopf „Ins DMS ablegen“ für die Leiste des Mail-Bereichs, mit Auswahl wie bei „Nachverfolgen“. */
export function DmsMailRibbon({ items, disabled }: { items: MailRef[]; disabled: boolean }) {
  const { fail, notify } = useApp()
  const profiles = useProfiles()
  if (!featureOn('dms')) return null
  const go = async (what: What, profile: string | null) => {
    try {
      const { n, dup } = await file(items, what, profile)
      notify(n || dup ? [n === 1 ? t('1 Dokument im DMS abgelegt') : t('{n} Dokumente im DMS abgelegt', { n }), dup ? t('{d} schon vorhanden', { d: dup }) : ''].filter(Boolean).join(', ') : t('Nichts zum Ablegen (keine Anhänge)'))
    } catch (e) { fail(e) }
  }
  const choices = (profile: string | null) => [
    <MenuItem key="w" icon={<Mail20Regular />} onClick={() => go('whole', profile)}>{t('Ganze Mail')}</MenuItem>,
    <MenuItem key="a" icon={<Attach20Regular />} onClick={() => go('attachments', profile)}>{t('Nur Anhänge')}</MenuItem>,
    <MenuItem key="b" icon={<MailAttach20Regular />} onClick={() => go('both', profile)}>{t('Mail und Anhänge')}</MenuItem>,
  ]
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <RibbonButton icon={<DocumentBulletList20Regular />} label={t('Ins DMS')} disabled={disabled} />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {profiles.length <= 1 ? choices(profiles[0]?.id ?? null) : profiles.map(p => (
            <Menu key={p.id}>
              <MenuTrigger disableButtonEnhancement><MenuItem>{p.name}{p.default ? ' · ' + t('Standard') : ''}</MenuItem></MenuTrigger>
              <MenuPopover><MenuList>{choices(p.id)}</MenuList></MenuPopover>
            </Menu>
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
}

/** Kleiner Knopf am Anhang, rechts neben „Herunterladen“ und abgetrennt: diesen Anhang ins DMS ablegen (bei mehreren Ablageprofilen mit Auswahl). */
export function DmsAttachmentButton({ mail, index, name }: { mail: MailRef; index: number; name: string }) {
  const { fail, notify } = useApp()
  const profiles = useProfiles()
  if (!featureOn('dms')) return null
  const go = async (profile: string | null) => {
    try {
      const { n } = await file([mail], 'attachments', profile, [index])
      notify(n ? t('„{name}“ im DMS abgelegt', { name }) : t('„{name}“ ist schon im DMS', { name }))
    } catch (e) { fail(e) }
  }
  const button = (onClick?: () => void) => (
    <Tooltip content={t('Ins DMS ablegen')} relationship="label">
      <Button appearance="transparent" size="small" icon={<DocumentBulletList16Regular />} onClick={e => { e.stopPropagation(); onClick?.() }} />
    </Tooltip>
  )
  // Trennstrich davor, damit der Knopf nicht mit „Herunterladen“ verwechselt wird
  const apart = { display: 'inline-flex', alignItems: 'center', alignSelf: 'stretch', marginLeft: 4, paddingLeft: 4, borderLeft: `1px solid ${tokens.colorNeutralStroke2}` }
  if (profiles.length <= 1) return <span style={apart}>{button(() => go(profiles[0]?.id ?? null))}</span>
  return (
    <span style={apart}>
      <Menu>
        <MenuTrigger disableButtonEnhancement><span onClick={e => e.stopPropagation()}>{button()}</span></MenuTrigger>
        <MenuPopover><MenuList>{profiles.map(p => <MenuItem key={p.id} onClick={() => go(p.id)}>{p.name}</MenuItem>)}</MenuList></MenuPopover>
      </Menu>
    </span>
  )
}

/** Hinweis in der Lese-Ansicht: diese Mail (oder Anhänge daraus) liegt schon im DMS. */
export function DmsFiledHint({ account, messageId }: { account: string; messageId?: string }) {
  const [count, setCount] = useState(0)
  useEffect(() => {
    setCount(0)
    if (!featureOn('dms') || !messageId) return
    api.get<unknown[]>('/api/dms/by-ref?' + q({ ref: `${account}|${messageId}` })).then(r => setCount(r.length)).catch(() => {})
  }, [account, messageId])
  if (!count) return null
  return (
    <Text size={200} style={{ color: tokens.colorNeutralForeground3, display: 'flex', alignItems: 'center', gap: 4 }}>
      <DocumentBulletList16Regular /> {t('Im DMS abgelegt ({n})', { n: count })} · <Link onClick={() => { location.hash = '#/dms' }}>{t('Öffnen')}</Link>
    </Text>
  )
}
