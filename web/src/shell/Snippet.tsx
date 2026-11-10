import { makeStyles, tokens } from '@fluentui/react-components'

const useStyles = makeStyles({
  mark: { backgroundColor: tokens.colorPaletteYellowBackground2, color: 'inherit', borderRadius: '2px', padding: '0 1px' },
})

// Der Server markiert gefundene Woerter im Ausschnitt mit U+E000 (Anfang) und U+E001 (Ende) - Search/TextIndex.cs
const START = ''
const END = ''

/** Ausschnitt aus der Volltextsuche mit hervorgehobenen Treffern - als Text, nie als HTML. */
export function Snippet({ text }: { text: string }) {
  const s = useStyles()
  const parts: React.ReactNode[] = []
  // Vor dem ersten Treffer nur ein paar Woerter - in schmalen Listen bliebe er sonst hinter dem Zeilenende
  const first = text.indexOf(START)
  const cut = first > 40 ? text.lastIndexOf(' ', first - 25) : -1
  let rest = cut > 0 ? '…' + text.slice(cut + 1) : text
  let key = 0
  while (rest.length) {
    const a = rest.indexOf(START)
    if (a < 0) { parts.push(rest); break }
    const b = rest.indexOf(END, a + 1)
    if (a > 0) parts.push(rest.slice(0, a))
    const hit = b < 0 ? rest.slice(a + 1) : rest.slice(a + 1, b)
    parts.push(<mark key={key++} className={s.mark}>{hit}</mark>)
    rest = b < 0 ? '' : rest.slice(b + 1)
  }
  return <>{parts}</>
}
