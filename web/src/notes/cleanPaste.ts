/**
 * Eingefuegtes oder hineingezogenes HTML auf das bringen, was eine Notiz kann:
 * Absaetze, Listen, Fett/Kursiv/Unterstrichen/Durchgestrichen, Farben, Links.
 * Alles andere (Bilder, Tabellen, Schaltflaechen, SVG, Stile der Seite) faellt weg
 * bzw. bleibt nur als Text - sonst entstehen Bloecke, die notizi nicht annimmt.
 */
const inline: Record<string, string> = { B: 'b', STRONG: 'b', I: 'i', EM: 'i', U: 'u', S: 's', STRIKE: 's', DEL: 's', CODE: 'span', SPAN: 'span', FONT: 'span', A: 'a', SUB: 'sub', SUP: 'sup' }
const blocks = new Set(['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE', 'SECTION', 'ARTICLE', 'HEADER', 'FOOTER', 'TR', 'DT', 'DD', 'FIGCAPTION'])
const drop = new Set(['SCRIPT', 'STYLE', 'SVG', 'svg', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'IMG', 'PICTURE', 'VIDEO', 'AUDIO', 'IFRAME', 'OBJECT', 'CANVAS', 'NOSCRIPT', 'TEMPLATE', 'HEAD', 'META', 'LINK', 'TITLE'])

const esc = (t: string) => t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

function style(el: HTMLElement): string {
  const out: string[] = []
  const c = el.style.color, bg = el.style.backgroundColor
  if (c) out.push(`color:${c}`)
  if (bg && bg !== 'transparent' && !bg.startsWith('rgba(0, 0, 0, 0')) out.push(`background-color:${bg}`)
  if (/bold|[6-9]00/.test(el.style.fontWeight)) out.push('font-weight:bold')
  if (el.style.fontStyle === 'italic') out.push('font-style:italic')
  return out.join(';')
}

function inlineHtml(n: Node): string {
  if (n.nodeType === 3) return esc((n.textContent ?? '').replace(/\s+/g, ' '))
  if (n.nodeType !== 1) return ''
  const el = n as HTMLElement
  if (drop.has(el.tagName)) return ''
  if (el.tagName === 'BR') return '<br/>'
  const inner = Array.from(el.childNodes).map(inlineHtml).join('')
  const tag = inline[el.tagName]
  if (!tag) return inner
  if (tag === 'a') {
    const href = el.getAttribute('href') ?? ''
    return /^(https?:|mailto:)/i.test(href) ? `<a href="${esc(href).replace(/"/g, '&quot;')}">${inner}</a>` : inner
  }
  if (tag === 'span') { const st = style(el); return st ? `<span style="${st}">${inner}</span>` : inner }
  return inner ? `<${tag}>${inner}</${tag}>` : ''
}

export function cleanPaste(html: string): string {
  const doc = new DOMParser().parseFromString(html, 'text/html')
  const out: string[] = []
  let line: string[] = []
  const flush = () => {
    const h = line.join('').trim()
    if (h.replace(/<br\/>/g, '').trim()) out.push(`<p>${h}</p>`)
    line = []
  }
  const walk = (n: Node) => {
    if (n.nodeType === 1) {
      const el = n as HTMLElement
      if (drop.has(el.tagName)) return
      if (el.tagName === 'UL' || el.tagName === 'OL') {
        flush()
        const items = Array.from(el.children).filter(c => c.tagName === 'LI')
          .map(li => `<li>${inlineHtml(li).trim()}</li>`).filter(x => x !== '<li></li>')
        const tag = el.tagName.toLowerCase()
        if (items.length) out.push(`<${tag}>${items.join('')}</${tag}>`)
        return
      }
      if (el.tagName === 'TD' || el.tagName === 'TH') {
        line.push(Array.from(el.childNodes).map(inlineHtml).join('').trim() + ' ')
        return
      }
      if (blocks.has(el.tagName) || el.tagName === 'TABLE' || el.tagName === 'LI') {
        flush()
        el.childNodes.forEach(walk)
        flush()
        return
      }
      if (inline[el.tagName] || el.tagName === 'BR') { line.push(inlineHtml(el)); return }
      el.childNodes.forEach(walk)
      return
    }
    if (n.nodeType === 3) line.push(inlineHtml(n))
  }
  doc.body.childNodes.forEach(walk)
  flush()
  return out.join('')
}

export function plainToHtml(text: string): string {
  return text.replace(/\r/g, '').split('\n').map(l => `<p>${esc(l) || '<br/>'}</p>`).join('')
}
