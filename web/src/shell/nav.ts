import { makeStyles, tokens } from '@fluentui/react-components'

// Gemeinsame Zeilen fuer alle Navigationsspalten (Kalender, Aufgabenlisten, Adressbuecher, Notizordner):
// am Desktop kompakt wie Outlook (28 px je Zeile, keine Luecken dazwischen), auf dem Handy fingergerecht.
// Vorher hatte jeder Bereich eigene Abstaende (7-8 px Innenabstand, 2-12 px Luecke, im Kalender
// 32 px hohe Kontrollkaestchen) - daher die grossen Zeilenabstaende.
const MOBILE = '@media (max-width: 767px)'

export const useNavStyles = makeStyles({
  pane: {
    padding: '8px 6px', display: 'flex', flexDirection: 'column', gap: 0, overflowY: 'auto',
    [MOBILE]: { padding: '8px' },
  },
  row: {
    display: 'flex', alignItems: 'center', gap: '8px', minHeight: '28px', padding: '0 6px 0 8px', borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer', whiteSpace: 'nowrap', userSelect: 'none', color: tokens.colorNeutralForeground1, flexShrink: 0,
    '& > svg': { flexShrink: 0 },
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    // Knopf "Mehr" nur beim Zeigen (wie Outlook); per Tastatur und auf dem Handy immer
    '& .dm-more': { visibility: 'hidden' },
    ':hover .dm-more': { visibility: 'visible' },
    ':focus-within .dm-more': { visibility: 'visible' },
    [MOBILE]: { minHeight: '40px', '& .dm-more': { visibility: 'visible' } },
  },
  active: { backgroundColor: tokens.colorBrandBackground2, fontWeight: 600, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  // Ablageziel beim Ziehen
  drop: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-2px', backgroundColor: tokens.colorBrandBackground2 },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' },
  count: { flexShrink: 0, fontSize: '12px', color: tokens.colorNeutralForeground3, fontWeight: 400 },
  // Abschnittskopf ("Meine Kalender", "Ordner")
  section: {
    display: 'flex', alignItems: 'center', gap: '4px', minHeight: '28px', padding: '0 2px 0 8px', marginTop: '10px', flexShrink: 0,
    fontWeight: 600, fontSize: '12px', color: tokens.colorNeutralForeground3, textTransform: 'uppercase', letterSpacing: '.03em',
    [MOBILE]: { minHeight: '36px' },
  },
  first: { marginTop: 0 },
  // Kaestchen in der Farbe des Kalenders (statt Fluent-Checkbox mit 32 px Hoehe)
  check: {
    width: '14px', height: '14px', borderRadius: '3px', border: '1.5px solid', flexShrink: 0, boxSizing: 'border-box',
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', color: '#fff', fontSize: '11px', lineHeight: 1,
  },
  link: { minHeight: '28px', display: 'flex', alignItems: 'center', padding: '0 8px', flexShrink: 0, [MOBILE]: { minHeight: '40px' } },
})
