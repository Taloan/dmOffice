import { Tab, TabList, Text, makeStyles, tokens } from '@fluentui/react-components'
import { createContext, useContext } from 'react'

// Kennung adminclean: gemeinsamer Seitenkopf der Verwaltung. Oben der Name der Seite, darunter (bei mehreren Abschnitten)
// die Reiter. Seiten mit mehreren Abschnitten zeigen je Reiter nur ihren Teil (useAdminPart).

/** Einheitliche Breite der Formulare in der Verwaltung; Tabellen nutzen die volle Breite. */
export const ADMIN_FORM_WIDTH = 720

const useStyles = makeStyles({
  head: { display: 'flex', flexDirection: 'column', gap: '4px', flexShrink: 0 },
  tabs: { flexWrap: 'wrap', marginLeft: '-10px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
})

export interface AdminTabDef { key: string; label: string }

/** Name der Seite und, bei mehreren Abschnitten, die Reiter. */
export function AdminPageHead({ title, tabs, value, onChange }: { title: string; tabs?: AdminTabDef[]; value?: string; onChange?: (v: string) => void }) {
  const s = useStyles()
  return <div className={s.head}>
    <Text as="h2" size={600} weight="semibold" style={{ margin: 0 }}>{title}</Text>
    {tabs && tabs.length > 1 && <TabList className={s.tabs} selectedValue={value} onTabSelect={(_, d) => onChange?.(d.value as string)}>
      {tabs.map(x => <Tab key={x.key} value={x.key}>{x.label}</Tab>)}
    </TabList>}
  </div>
}

/**
 * Teil einer Seite, den der gewählte Reiter zeigt (null: alles wie bisher). solo: der Reiter hat nur einen Abschnitt,
 * dessen erste Zwischenüberschrift entfällt (der Reiter nennt ihn schon).
 */
export const AdminPartCtx = createContext<{ part: string | null; solo: boolean }>({ part: null, solo: false })
export const useAdminPart = () => useContext(AdminPartCtx)
/** Zeigt der Reiter diesen Abschnitt? Ohne Reiter (alte Verwaltung) immer. */
export function useShows() {
  const { part } = useAdminPart()
  return (...keys: string[]) => part == null || keys.includes(part)
}
