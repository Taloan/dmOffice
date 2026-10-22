// Markdown lesbar darstellen - ohne Bibliothek und ohne innerHTML: Ueberschriften, Absaetze, Listen, Zitate,
// Codebloecke, Trennlinien, fett/kursiv/Code/Links (nur http, https, mailto). Alles andere bleibt Text.
import { tokens } from '@fluentui/react-components'

const SAFE = /^(https?:|mailto:)/i

function inline(text: string, key = 0): React.ReactNode[] {
  const out: React.ReactNode[] = []
  // `code`, **fett**, *kursiv* / _kursiv_, [text](url), <https://...>
  const re = /(`[^`]+`)|(\*\*[^*]+\*\*)|(__[^_]+__)|(\*[^*\s][^*]*\*)|(_[^_\s][^_]*_)|(\[[^\]]+\]\([^)\s]+\))|(<https?:\/\/[^>\s]+>)/g
  let last = 0
  let m: RegExpExecArray | null
  let n = key * 1000
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const s = m[0]
    if (m[1]) out.push(<code key={n++} style={{ fontFamily: 'Consolas, monospace', background: tokens.colorNeutralBackground3, padding: '0 4px', borderRadius: 4 }}>{s.slice(1, -1)}</code>)
    else if (m[2] || m[3]) out.push(<strong key={n++}>{inline(s.slice(2, -2), n)}</strong>)
    else if (m[4] || m[5]) out.push(<em key={n++}>{inline(s.slice(1, -1), n)}</em>)
    else if (m[6]) {
      const i = s.indexOf('](')
      const label = s.slice(1, i), url = s.slice(i + 2, -1)
      out.push(SAFE.test(url) ? <a key={n++} href={url} target="_blank" rel="noopener noreferrer">{label}</a> : s)
    } else if (m[7]) {
      const url = s.slice(1, -1)
      out.push(<a key={n++} href={url} target="_blank" rel="noopener noreferrer">{url}</a>)
    }
    last = m.index + s.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

export function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  const blocks: React.ReactNode[] = []
  let i = 0, k = 0
  while (i < lines.length) {
    const line = lines[i]
    if (/^\s*```/.test(line)) {
      const code: string[] = []
      i++
      while (i < lines.length && !/^\s*```/.test(lines[i])) code.push(lines[i++])
      i++
      blocks.push(<pre key={k++} style={{ background: tokens.colorNeutralBackground3, padding: 12, borderRadius: 6, overflowX: 'auto', fontFamily: 'Consolas, monospace', fontSize: 13 }}>{code.join('\n')}</pre>)
      continue
    }
    const h = /^(#{1,6})\s+(.*)$/.exec(line)
    if (h) {
      const Tag = `h${h[1].length}` as 'h1'
      blocks.push(<Tag key={k++} style={{ margin: '18px 0 8px' }}>{inline(h[2], k)}</Tag>)
      i++; continue
    }
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { blocks.push(<hr key={k++} />); i++; continue }
    if (/^\s*>/.test(line)) {
      const quote: string[] = []
      while (i < lines.length && /^\s*>/.test(lines[i])) quote.push(lines[i++].replace(/^\s*>\s?/, ''))
      blocks.push(<blockquote key={k++} style={{ borderLeft: `3px solid ${tokens.colorNeutralStroke1}`, margin: '8px 0', padding: '2px 12px', color: tokens.colorNeutralForeground2 }}>{inline(quote.join(' '), k)}</blockquote>)
      continue
    }
    const li = /^\s*([-*+]|\d+[.)])\s+(.*)$/.exec(line)
    if (li) {
      const ordered = /\d/.test(li[1])
      const items: React.ReactNode[] = []
      while (i < lines.length) {
        const m = /^\s*([-*+]|\d+[.)])\s+(.*)$/.exec(lines[i])
        if (!m) break
        // Aufgabenliste: - [ ] / - [x]
        const task = /^\[([ xX])\]\s+(.*)$/.exec(m[2])
        items.push(<li key={items.length}>{task ? <><input type="checkbox" checked={task[1] !== ' '} readOnly style={{ marginRight: 6 }} />{inline(task[2], k)}</> : inline(m[2], k)}</li>)
        i++
      }
      blocks.push(ordered ? <ol key={k++}>{items}</ol> : <ul key={k++}>{items}</ul>)
      continue
    }
    if (!line.trim()) { i++; continue }
    const para: string[] = []
    while (i < lines.length && lines[i].trim() && !/^(#{1,6}\s|\s*```|\s*>|\s*([-*+]|\d+[.)])\s)/.test(lines[i])) para.push(lines[i++])
    blocks.push(<p key={k++} style={{ margin: '8px 0' }}>{inline(para.join(' '), k)}</p>)
  }
  return <div style={{ lineHeight: 1.6, maxWidth: 820, margin: '0 auto' }}>{blocks}</div>
}
