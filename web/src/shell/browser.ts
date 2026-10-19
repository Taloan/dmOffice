// Ersatz fuer Browser-Funktionen, die es nur unter HTTPS gibt (navigator.clipboard,
// crypto.randomUUID). dmOffice laeuft im eigenen Netz oft unter http://<ip>:<port>.

/** Text in die Zwischenablage. true, wenn es geklappt hat. */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (window.isSecureContext && navigator.clipboard) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch { /* weiter mit dem alten Weg */ }
  // Ohne HTTPS: unsichtbares Textfeld markieren und kopieren
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.top = '0'
  area.style.left = '0'
  area.style.opacity = '0'
  document.body.appendChild(area)
  const selection = document.getSelection()
  const previous = selection && selection.rangeCount > 0 ? selection.getRangeAt(0) : null
  area.focus()
  area.select()
  let ok = false
  try { ok = document.execCommand('copy') } catch { ok = false }
  area.remove()
  if (previous && selection) { selection.removeAllRanges(); selection.addRange(previous) }
  return ok
}

/** Zufaellige Kennung, 32 Hexzeichen - wie Guid.ToString("N") im Server. */
export function newId(): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('')
}

/** Text als Datei herunterladen. */
export function saveText(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }))
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
