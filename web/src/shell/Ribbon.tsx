import { Button, Menu, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ChevronLeft20Regular, ChevronRight20Regular, Eye20Regular, Navigation20Regular, SlideText20Regular } from '@fluentui/react-icons'
import { forwardRef, useCallback, useEffect, useRef, useState } from 'react'
import { t } from '../i18n'
import { usePrefs } from '../prefs'
import { useIsMobile } from './mobile'

// Menueband wie in Office: Symbol oben, Beschriftung darunter (bis zu zwei Zeilen), Gruppen mit Trennstrich.
// Bricht nicht um - wird es zu eng, schieben Pfeile links/rechts die Leiste weiter (wie "Scrollband rechts" in Outlook).

const useStyles = makeStyles({
  wrap: { position: 'relative', flexShrink: 0, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  ribbon: {
    display: 'flex', alignItems: 'stretch', gap: '2px', padding: '4px 8px', overflowX: 'auto', overflowY: 'hidden',
    scrollbarWidth: 'none', scrollBehavior: 'smooth',
  },
  scroll: {
    position: 'absolute', top: '4px', bottom: '4px', width: '22px', minWidth: '22px', padding: 0, zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium, boxShadow: tokens.shadow4,
  },
  button: {
    display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'flex-start', gap: '3px',
    minWidth: '56px', maxWidth: '86px', height: '66px', padding: '6px 6px 4px', flexShrink: 0,
  },
  // Nur Symbole: kleiner, quadratisch; nur Text: eine Zeile
  iconsOnly: { minWidth: '40px', maxWidth: '40px', height: '40px', padding: '8px', justifyContent: 'center' },
  textOnly: { height: '32px', maxWidth: 'none', padding: '4px 10px', justifyContent: 'center', '& span': { whiteSpace: 'nowrap', display: 'block' } },
  icon: { fontSize: '24px', height: '24px', display: 'inline-flex', alignItems: 'center', '& svg': { width: '24px', height: '24px' } },
  label: {
    fontSize: '12px', lineHeight: '14px', fontWeight: 400, textAlign: 'center', whiteSpace: 'normal',
    display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
  },
  divider: { width: '1px', margin: '8px 6px', backgroundColor: tokens.colorNeutralStroke2, flexShrink: 0 },
  tabs: { display: 'flex', gap: '2px', padding: '4px 10px 0', flexShrink: 0 },
  tab: {
    border: 'none', background: 'transparent', color: tokens.colorNeutralForeground2, fontFamily: 'inherit', fontSize: '13px',
    padding: '6px 10px 5px', cursor: 'pointer', position: 'relative', borderRadius: tokens.borderRadiusMedium,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, color: tokens.colorNeutralForeground1 },
  },
  tabOn: {
    color: tokens.colorNeutralForeground1, fontWeight: 600,
    '::after': { content: '""', position: 'absolute', left: '10px', right: '10px', bottom: 0, height: '2px', borderRadius: '1px', backgroundColor: tokens.colorBrandBackground },
  },
  pressed: { backgroundColor: tokens.colorNeutralBackground1Selected },
  // Handy: eine Zeile - Menue-Knopf, Symbolleiste (wischbar), Umschalter Start/Ansicht
  mobileRow: { display: 'flex', alignItems: 'center', minWidth: 0, borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, paddingLeft: '4px' },
  mobileWrap: { flex: 1, minWidth: 0, borderBottom: 'none' },
  mobileRibbon: { padding: '4px 2px' },
  mobileDivider: { margin: '10px 2px' },
})

export function Ribbon({ children, className, wrapClassName }: { children: React.ReactNode; className?: string; wrapClassName?: string }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const bar = useRef<HTMLDivElement>(null)
  const [edge, setEdge] = useState({ left: false, right: false })
  const check = useCallback(() => {
    const el = bar.current
    if (!el) return
    setEdge({ left: el.scrollLeft > 2, right: el.scrollLeft + el.clientWidth < el.scrollWidth - 2 })
  }, [])
  useEffect(() => {
    const el = bar.current
    if (!el) return
    check()
    const ro = new ResizeObserver(check)
    ro.observe(el)
    if (el.firstElementChild) ro.observe(el.firstElementChild)
    return () => ro.disconnect()
  }, [check])
  const by = (dir: number) => bar.current?.scrollBy({ left: dir * Math.max(120, bar.current.clientWidth * 0.6) })
  return (
    <div className={mergeClasses(s.wrap, wrapClassName)}>
      <div ref={bar} role="toolbar" className={mergeClasses(s.ribbon, mobile && s.mobileRibbon, className)} onScroll={check}>{children}</div>
      {edge.left && <Button className={s.scroll} style={{ left: 2 }} appearance="subtle" icon={<ChevronLeft20Regular />}
        onClick={() => by(-1)} aria-label={t('Menüband links')} title={t('Menüband links')} />}
      {edge.right && <Button className={s.scroll} style={{ right: 2 }} appearance="subtle" icon={<ChevronRight20Regular />}
        onClick={() => by(1)} aria-label={t('Menüband rechts')} title={t('Menüband rechts')} />}
    </div>
  )
}

/** Reiter ueber dem Menueband (Start, Ansicht) wie in Office. */
export function RibbonTabs({ tabs, value, onChange }: { tabs: [string, string][]; value: string; onChange: (v: string) => void }) {
  const s = useStyles()
  return (
    <div className={s.tabs} role="tablist">
      {tabs.map(([k, l]) => (
        <button key={k} role="tab" aria-selected={value === k} className={mergeClasses(s.tab, value === k && s.tabOn)} onClick={() => onChange(k)}>{l}</button>
      ))}
    </div>
  )
}

/**
 * Kopf eines Bereichs wie bei den Mails: Reiter "Start" und "Ansicht", darunter das Menueband.
 * Der gewaehlte Reiter bleibt je Bereich gespeichert.
 */
export function RibbonHeader({ area, start, view, before, after, onMenu }: {
  area: string; start: React.ReactNode; view: React.ReactNode; before?: React.ReactNode; after?: React.ReactNode
  /** Handy: Knopf links, der die Navigationsspalte aufklappt */
  onMenu?: () => void
}) {
  const { prefs, update } = usePrefs()
  const tab = prefs.ribbonTabs?.[area] ?? 'start'
  const mobile = useIsMobile()
  if (mobile) return (
    <MobileRibbonRow onMenu={onMenu} view={tab === 'view'} onView={() => update({ ribbonTabs: { ...prefs.ribbonTabs, [area]: tab === 'view' ? 'start' : 'view' } })}
      before={before} after={after}>
      {tab === 'view' ? view : start}
    </MobileRibbonRow>
  )
  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', minWidth: 0 }}>
        {before}
        <div style={{ flex: 1, minWidth: 0 }}>
          <RibbonTabs tabs={[['start', t('Start')], ['view', t('Ansicht')]]} value={tab}
            onChange={v => update({ ribbonTabs: { ...prefs.ribbonTabs, [area]: v as 'start' | 'view' } })} />
        </div>
        {after}
      </div>
      <Ribbon>{tab === 'view' ? <>{view}<RibbonDivider /><RibbonDisplayMenu /></> : start}</Ribbon>
    </>
  )
}

/**
 * Menueband auf dem Handy: eine Zeile mit Menue-Knopf (Navigationsspalte), den Knoepfen nur als Symbol
 * (seitlich wischbar) und rechts dem Umschalter zwischen Start und Ansicht.
 */
export function MobileRibbonRow({ onMenu, view, onView, before, after, children }: {
  onMenu?: () => void; view?: boolean; onView?: () => void; before?: React.ReactNode; after?: React.ReactNode; children: React.ReactNode
}) {
  const s = useStyles()
  return (
    <div className={s.mobileRow}>
      {onMenu && <Button appearance="subtle" icon={<Navigation20Regular />} onClick={onMenu} aria-label={t('Ordnerbereich')} title={t('Ordnerbereich')} />}
      {before}
      <Ribbon wrapClassName={s.mobileWrap}>{children}</Ribbon>
      {after}
      {onView && <Button appearance={view ? 'primary' : 'subtle'} icon={<Eye20Regular />} onClick={onView}
        aria-label={t('Ansicht')} title={t('Ansicht')} aria-pressed={view} style={{ marginRight: 4 }} />}
    </div>
  )
}

/**
 * Navigationsbereich links ein-/ausblenden, je Bereich gespeichert.
 * Auf dem Handy klappt er nur voruebergehend auf (dritter Wert schliesst ihn wieder).
 */
export function useSidePane(area: string): [boolean, () => void, () => void] {
  const { prefs, update } = usePrefs()
  const mobile = useIsMobile()
  const [drawer, setDrawer] = useState(false)
  useEffect(() => { if (!mobile) setDrawer(false) }, [mobile])
  if (mobile) return [drawer, () => setDrawer(o => !o), () => setDrawer(false)]
  const on = prefs.sidePane?.[area] ?? true
  return [on, () => update({ sidePane: { ...prefs.sidePane, [area]: !on } }), () => {}]
}

/** Menueband: Symbole und Text, nur Symbole oder nur Text - fuer alle Bereiche gleich. */
export function RibbonDisplayMenu() {
  const { prefs, update } = usePrefs()
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<SlideText20Regular />} label={t('Menüband')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ labels: [prefs.ribbonLabels ?? 'both'] }} onCheckedValueChange={(_, d) => update({ ribbonLabels: d.checkedItems[0] as any })}>
          <MenuItemRadio name="labels" value="both">{t('Symbole und Text')}</MenuItemRadio>
          <MenuItemRadio name="labels" value="icons">{t('Nur Symbole')}</MenuItemRadio>
          <MenuItemRadio name="labels" value="text">{t('Nur Text')}</MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
  )
}

export function RibbonDivider() {
  const s = useStyles()
  const mobile = useIsMobile()
  return <div className={mergeClasses(s.divider, mobile && s.mobileDivider)} role="separator" />
}

/**
 * Leichte Farbakzente fuer die haeufigen Knoepfe - wie in Outlook, damit man sie schneller findet.
 * Nach dem Symbol (Name ohne Groesse/Variante); alles andere bleibt neutral.
 */
const tints: Record<string, string> = {
  Archive: '#1F9E77', ShieldCheckmark: '#1F9E77', CheckmarkCircle: '#1F9E77',
  ShieldError: '#D13438', Flag: '#D13438', Note: '#D69A1E',
  FolderArrowRight: '#D69A1E', FolderOpen: '#D69A1E', FolderAdd: '#D69A1E', Star: '#D69A1E', Broom: '#D69A1E',
  ArrowReply: '#8B5CF6', ArrowReplyAll: '#8B5CF6',
  ArrowForward: '#2F7FD9', MailRead: '#2F7FD9', MailUnread: '#2F7FD9', Mail: '#2F7FD9', ArrowSync: '#2F7FD9',
  ArrowDownload: '#2F7FD9', ArrowUpload: '#2F7FD9', ArrowImport: '#2F7FD9', ArrowExport: '#2F7FD9',
  Clock: '#0E9AA7', CalendarAdd: '#0E9AA7', CalendarToday: '#0E9AA7', CalendarLtr: '#0E9AA7', ArrowReset: '#0E9AA7',
  Tag: '#E2782E', Rss: '#E2782E',
}
const tintOf = (icon: JSX.Element) => {
  const name: string | undefined = (icon.type as any)?.displayName
  return name ? tints[name.replace(/\d+(Regular|Filled)$/, '')] : undefined
}

type Props = Omit<React.ComponentProps<typeof Button>, 'icon' | 'children'> & {
  icon: JSX.Element
  label: string
  primary?: boolean
  /** Umschalter, der gerade an ist */
  pressed?: boolean
}

/** Ein Knopf im Menueband. Mit forwardRef, damit er als Ausloeser eines Menues dienen kann. */
export const RibbonButton = forwardRef<HTMLButtonElement, Props>(function RibbonButton({ icon, label, primary, pressed, className, ...rest }, ref) {
  const s = useStyles()
  // Auf dem Handy immer nur Symbole - die Beschriftung steht im Tooltip/title
  const mobile = useIsMobile()
  const labels = usePrefs().prefs.ribbonLabels ?? 'both'
  const mode = mobile ? 'icons' : labels
  return (
    <Button ref={ref} appearance={primary ? 'primary' : 'subtle'} className={mergeClasses(s.button, mode === 'icons' && s.iconsOnly, mode === 'text' && s.textOnly, pressed && s.pressed, className)} title={label} aria-pressed={pressed} {...(rest as any)}>
      {mode !== 'text' && <span className={s.icon} style={!primary && !rest.disabled && tintOf(icon) ? { color: tintOf(icon) } : undefined}>{icon}</span>}
      {mode !== 'icons' && <span className={s.label}>{label}</span>}
    </Button>
  )
})
