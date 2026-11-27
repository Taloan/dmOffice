import { Divider, Tab, TabList, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Children, cloneElement, createContext, isValidElement, useContext, useState } from 'react'
import { createPortal } from 'react-dom'
import { RibbonHeader, RibbonSlot, RibbonViewSlot } from '../shell/Ribbon'

// Gemeinsamer Aufbau der Einstellungen wie in der Verwaltung: oben das Menüband mit „Start“ und „Ansicht“ (SettingsFrame),
// je Seite Überschrift (Name der Seite) und Reiter, dann der Inhalt. Formulare höchstens 720 px, Tabellen volle Breite.
const FORM = '720px'
const useStyles = makeStyles({
  // Alles, was keine Tabelle enthält, bleibt lesbar schmal; Tabellen (auch in Seiten-Rahmen) nutzen die ganze Breite
  section: { display: 'flex', flexDirection: 'column', gap: '10px', '& > *:not(:has(table))': { maxWidth: FORM } },
  hint: { color: tokens.colorNeutralForeground3 },
  title: { display: 'block', margin: 0, padding: '20px 32px 4px', '@media (max-width: 767px)': { padding: '12px 14px 4px' } },
  head: { flexShrink: 0 },
  // Reiter der Seite unter der Überschrift, mit Linie darunter (wie in der Verwaltung); ohne Reiter nichts
  tabs: { padding: '0 22px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, ':empty': { display: 'none' }, '@media (max-width: 767px)': { padding: '0 4px' } },
  slot: { display: 'contents' },
  // Band behält seine Höhe auch auf Seiten ohne Aktionen; so springt der Inhalt beim Seitenwechsel nicht
  band: { display: 'flex', alignItems: 'stretch', gap: '2px', minHeight: '70px', '@media (max-width: 767px)': { display: 'contents' } },
  frame: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 },
  split: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0, position: 'relative' },
})

/** Breite des Inhalts: Kinder ohne Tabelle 720 px, mit Tabelle volle Breite (für den Inhaltsbereich der Einstellungen). */
export const formWidth = { '& > *:not(:has(table)):not([role=separator])': { maxWidth: FORM } } as const

/** Überschrift einer Seite; die Einstellungen setzen sie selbst aus dem Seitennamen (SettingsPageHead). */
export function PageTitle({ children }: { children: React.ReactNode }) {
  return <Text size={600} weight="semibold">{children}</Text>
}

const TabsSlot = createContext<HTMLElement | null>(null)

/**
 * Rahmen der Einstellungen wie in der Verwaltung: oben über allem das Menüband mit den Reitern „Start“ und „Ansicht“
 * (RibbonHeader), darunter die Spalten und die Seite. Die Seite legt ihre Aktionen mit RibbonActions ins Band,
 * Anzeige-Schalter mit RibbonViewActions in „Ansicht“.
 */
export function SettingsFrame({ onMenu, children }: { onMenu?: () => void; children: React.ReactNode }) {
  const s = useStyles()
  const [band, setBand] = useState<HTMLElement | null>(null)
  const [view, setView] = useState<HTMLElement | null>(null)
  return (
    <div className={s.frame}>
      <RibbonHeader area="settings" onMenu={onMenu}
        start={<div ref={setBand} className={s.band} />}
        view={<div ref={setView} className={s.slot} />} />
      <RibbonSlot.Provider value={band}><RibbonViewSlot.Provider value={view}>
        <div className={s.split}>{children}</div>
      </RibbonViewSlot.Provider></RibbonSlot.Provider>
    </div>
  )
}

/**
 * Kopf einer Einstellungsseite wie in der Verwaltung: Überschrift aus dem Seitennamen, darunter die Reiter der Seite
 * (PageTabs). Ohne Titel (Handy: dort steht er schon in der Zurück-Leiste) nur die Reiter.
 */
export function SettingsPageHead({ title, children }: { title?: string; children: (head: React.ReactNode) => React.ReactNode }) {
  const s = useStyles()
  const [tabs, setTabs] = useState<HTMLElement | null>(null)
  const head = (
    <div className={s.head}>
      {title && <Text as="h2" size={600} weight="semibold" className={s.title}>{title}</Text>}
      <div ref={setTabs} data-tabs="" className={s.tabs} />
    </div>
  )
  return <TabsSlot.Provider value={tabs}>{children(head)}</TabsSlot.Provider>
}

/** Reiter einer langen Einstellungsseite; stehen unter der Überschrift (wie die Reiter der Verwaltung). */
export function PageTabs({ tabs, value, onChange }: { tabs: [string, string][]; value: string; onChange: (v: string) => void }) {
  const el = useContext(TabsSlot)
  const bar = <TabList selectedValue={value} onTabSelect={(_, d) => onChange(d.value as string)}>
    {tabs.map(([k, l]) => <Tab key={k} value={k}>{l}</Tab>)}
  </TabList>
  return el ? createPortal(bar, el) : bar
}

// Gewählter Reiter je Seite bis zum Neuladen
const shownTab = new Map<string, string>()

/** Reiter einer Seite von außen wählen (Verweis wie #/settings/devices/pair); gilt beim nächsten Zeichnen. */
export const showTab = (page: string, tab: string) => { shownTab.set(page, tab) }

/** Reiter einer Seite samt gewähltem Reiter: [Schlüssel, Reiterzeile]. Die Reiterzeile gehört in den Inhalt der Seite. */
export function usePageTab(page: string, tabs: [string, string][]): [string, JSX.Element] {
  const [, setValue] = useState(0)
  const v = shownTab.get(page)
  const shown = v && tabs.some(x => x[0] === v) ? v : tabs[0][0]
  return [shown, <PageTabs key="tabs" tabs={tabs} value={shown} onChange={k => { shownTab.set(page, k); setValue(x => x + 1) }} />]
}

/** Ein Abschnitt mit Ueberschrift, durch eine Linie vom naechsten getrennt. */
export function Section({ title, hint, children, last }: { title: string; hint?: string; children: React.ReactNode; last?: boolean }) {
  const s = useStyles()
  return (
    <>
      <div className={s.section}>
        <Text size={400} weight="semibold">{title}</Text>
        {hint && <Text className={s.hint}>{hint}</Text>}
        {children}
      </div>
      {!last && <Divider />}
    </>
  )
}

export function Hint({ children }: { children: React.ReactNode }) {
  const s = useStyles()
  return <Text size={200} className={s.hint}>{children}</Text>
}

/** Teil einer Seite mit Reitern, dessen Abschnitt in einer eigenen Komponente steckt (SectionTabs). */
export function TabPart({ children }: { label: string; children: React.ReactNode }) {
  return <>{children}</>
}

/**
 * Lange Seite mit mehreren Abschnitten: je Abschnitt ein Reiter (Name = Überschrift des Abschnitts, bei TabPart dessen label),
 * gezeigt wird nur der gewählte. Was kein Abschnitt ist (Einleitung, Hinweise), steht über jedem Reiter. Abschnitte müssen
 * direkte Kinder sein.
 */
export function SectionTabs({ page, children }: { page: string; children: React.ReactNode }) {
  const items = Children.toArray(children).filter(isValidElement) as React.ReactElement<{ title?: string; label?: string; last?: boolean }>[]
  const name = (e: React.ReactElement<{ title?: string; label?: string }>) =>
    e.type === Section ? e.props.title : e.type === TabPart ? e.props.label : undefined
  const tabs = items.filter(name).map(e => [name(e)!, name(e)!] as [string, string])
  const [tab, tabBar] = usePageTab(page, tabs.length ? tabs : [['', '']])
  if (tabs.length < 2) return <>{children}</>
  return <>{tabBar}{items.map(e => !name(e) ? e : name(e) !== tab ? null : e.type === Section ? cloneElement(e, { last: true }) : e)}</>
}
