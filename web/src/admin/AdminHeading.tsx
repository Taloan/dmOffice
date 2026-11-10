import { Text } from '@fluentui/react-components'

/** Ueberschrift eines Abschnitts in der Verwaltung - deutlich groesser als der Text darunter, mit Luft nach oben. */
export function AdminHeading({ children, first }: { children: React.ReactNode; first?: boolean }) {
  return <Text as="h3" size={500} weight="semibold" style={{ margin: first ? 0 : '18px 0 0' }}>{children}</Text>
}
