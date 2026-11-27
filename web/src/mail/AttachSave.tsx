// Anhänge speichern (Kennung attsave): Hauptknopf mit Ziel aus den E-Mail-Einstellungen (eigene Ablage oder Download des Browsers),
// ⋯-Menü mit dem anderen Ziel, Hinweis „gespeichert in …“ mit Sprung in die Ablage.
import { Button, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, makeStyles, tokens } from '@fluentui/react-components'
import {
  ArrowDownload20Regular, Attach16Regular, Dismiss16Regular, FolderOpen20Regular, MoreHorizontal20Regular, Open20Regular, Save20Regular, SaveArrowRight20Regular,
} from '@fluentui/react-icons'
import type { ReactNode } from 'react'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'

export interface AttachSettings { attachSaveTarget: 'ablage' | 'browser'; attachSaveFolder: string | null; attachSaveAsk: boolean }
const DEFAULTS: AttachSettings = { attachSaveTarget: 'ablage', attachSaveFolder: null, attachSaveAsk: false }

// Einmal geladen, von allen Mails geteilt; Änderungen in den Einstellungen gehen an alle Zuhörer
let current: AttachSettings | null = null
let loading: Promise<void> | null = null
const listeners = new Set<(s: AttachSettings) => void>()
const publish = (s: AttachSettings) => { current = s; listeners.forEach(f => f(s)) }

export function useAttachSettings(): { settings: AttachSettings; save: (p: Partial<AttachSettings>) => Promise<void> } {
  const [s, setS] = useState<AttachSettings>(current ?? DEFAULTS)
  useEffect(() => {
    listeners.add(setS)
    if (!current) loading ??= api.get<AttachSettings>('/api/settings/').then(r => publish({
      attachSaveTarget: r.attachSaveTarget === 'browser' ? 'browser' : 'ablage', attachSaveFolder: r.attachSaveFolder ?? null, attachSaveAsk: !!r.attachSaveAsk,
    })).catch(() => { loading = null })
    else setS(current)
    return () => { listeners.delete(setS) }
  }, [])
  const save = async (p: Partial<AttachSettings>) => {
    publish({ ...(current ?? DEFAULTS), ...p })
    await api.put('/api/settings/general', {
      ...(p.attachSaveTarget != null && { attachSaveTarget: p.attachSaveTarget }),
      ...(p.attachSaveFolder !== undefined && { attachSaveFolder: p.attachSaveFolder ?? '' }),
      ...(p.attachSaveAsk != null && { attachSaveAsk: p.attachSaveAsk }),
    })
  }
  return { settings: s, save }
}

/** #/files/ordner/unterordner - die Ablage an diesem Ordner öffnen. */
export const filesHash = (dir: string) => '#/files/' + dir.split('/').filter(Boolean).map(encodeURIComponent).join('/')

const useStyles = makeStyles({
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', minWidth: '260px', maxWidth: '100%', padding: '8px 10px', cursor: 'pointer',
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, backgroundColor: tokens.colorNeutralBackground1,
    ':hover': { border: `1px solid ${tokens.colorBrandStroke1}` },
  },
  icon: {
    flexShrink: 0, width: '32px', height: '38px', borderRadius: tokens.borderRadiusMedium, display: 'flex', alignItems: 'center', justifyContent: 'center',
    backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2,
  },
  text: { minWidth: 0, flexGrow: 1 },
  name: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: '13px', fontWeight: tokens.fontWeightSemibold },
  sub: { fontSize: '12px', color: tokens.colorNeutralForeground3 },
  bar: {
    display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap', padding: '6px 10px', borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorStatusSuccessBackground1, color: tokens.colorStatusSuccessForeground1, fontSize: '13px',
  },
  head: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', width: '100%', fontSize: '12px', color: tokens.colorNeutralForeground3 },
})

/** Eine Anhangszeile: Name und Größe, Hauptknopf „In Ablage“ / „Herunterladen“, ⋯ mit dem Rest. Ein Klick auf die Zeile öffnet die Vorschau. */
export function AttachRow({ name, sub, target, onOpen, onSave, onSaveOther, onSaveAs, onOpenTab, onContext, extra }: {
  name: string; sub: ReactNode; target: 'ablage' | 'browser'
  onOpen: () => void; onSave: () => void; onSaveOther: () => void
  onSaveAs?: () => void; onOpenTab?: () => void; onContext?: (e: React.MouseEvent) => void
  /** Weitere Menüeinträge (z. B. „Ins DMS“) */
  extra?: ReactNode
}) {
  const s = useStyles()
  const intern = target === 'ablage'
  return (
    <div className={s.row} role="button" tabIndex={0} onClick={onOpen} onContextMenu={onContext}
      onKeyDown={e => { if (e.key === 'Enter' && e.target === e.currentTarget) onOpen() }} title={t('{name} – ansehen', { name })}>
      <div className={s.icon}><Attach16Regular /></div>
      <div className={s.text}><div className={s.name}>{name}</div><div className={s.sub}>{sub}</div></div>
      <Button appearance="primary" size="small" icon={intern ? <Save20Regular /> : <ArrowDownload20Regular />}
        onClick={e => { e.stopPropagation(); onSave() }} aria-label={t('{name} – {aktion}', { name, aktion: intern ? t('In Ablage') : t('Herunterladen') })}>
        {intern ? t('In Ablage') : t('Herunterladen')}
      </Button>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button appearance="subtle" size="small" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} onClick={e => e.stopPropagation()} />
        </MenuTrigger>
        <MenuPopover><MenuList>
          <MenuItem icon={intern ? <ArrowDownload20Regular /> : <Save20Regular />} onClick={onSaveOther}>{intern ? t('Herunterladen (Browser)') : t('In Ablage speichern')}</MenuItem>
          {onSaveAs && <MenuItem icon={<SaveArrowRight20Regular />} onClick={onSaveAs}>{t('Speichern unter …')}</MenuItem>}
          {onOpenTab && <MenuItem icon={<Open20Regular />} onClick={onOpenTab}>{t('In neuem Tab öffnen')}</MenuItem>}
          {extra && <><MenuDivider />{extra}</>}
        </MenuList></MenuPopover>
      </Menu>
    </div>
  )
}

/** Kopf der Anhangsliste: Anzahl, „Alle speichern“ und der Hinweis nach dem Speichern. */
export function AttachHead({ count, onAll, info, onClose }: { count: number; onAll: () => void; info: { text: string; dir: string | null } | null; onClose: () => void }) {
  const s = useStyles()
  return (
    <>
      <div className={s.head}>
        <span>{count === 1 ? t('1 Anhang') : t('{n} Anhänge', { n: count })}</span>
        {count > 1 && <Button appearance="transparent" size="small" onClick={onAll}>{t('Alle speichern')}</Button>}
      </div>
      {info && <div className={s.bar} role="status">
        <span>{info.text}</span>
        {info.dir != null && <Button appearance="transparent" size="small" icon={<FolderOpen20Regular />} onClick={() => { location.hash = filesHash(info.dir!) }}>{t('In Ablage anzeigen')}</Button>}
        <Button appearance="transparent" size="small" icon={<Dismiss16Regular />} aria-label={t('Schließen')} onClick={onClose} style={{ marginLeft: 'auto' }} />
      </div>}
    </>
  )
}
