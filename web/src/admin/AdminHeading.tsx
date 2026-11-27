import { Text } from '@fluentui/react-components'
import { useAdminPart } from './AdminPage' // [feature:adminclean]

/** Ueberschrift eines Abschnitts in der Verwaltung - deutlich groesser als der Text darunter, mit Luft nach oben. */
export function AdminHeading({ children, first }: { children: React.ReactNode; first?: boolean }) {
  // Kennung adminclean: Reiter mit nur einem Abschnitt - der Reiter nennt ihn schon
  const solo = useAdminPart().solo // [feature:adminclean]
  if (first && solo) return null // [feature:adminclean]
  return <Text as="h3" size={500} weight="semibold" style={{ margin: first ? 0 : '18px 0 0' }}>{children}</Text>
}
