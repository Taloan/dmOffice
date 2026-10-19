import { Divider, Text, makeStyles, tokens } from '@fluentui/react-components'

const useStyles = makeStyles({
  section: { display: 'flex', flexDirection: 'column', gap: '10px', maxWidth: '760px' },
  hint: { color: tokens.colorNeutralForeground3 },
})

/** Seitentitel wie in den Outlook-Einstellungen. */
export function PageTitle({ children }: { children: React.ReactNode }) {
  return <Text size={600} weight="semibold">{children}</Text>
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
