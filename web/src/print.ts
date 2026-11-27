/**
 * Druckt ein HTML-Dokument über einen unsichtbaren Rahmen in der eigenen Seite.
 * Ein eigenes Fenster (window.open + print) bringt die installierte PWA zum Absturz;
 * der Rahmen bleibt im selben Fenster und funktioniert im Browser wie in der PWA.
 */
export async function printHtml(html: string): Promise<void> {
  const frame = document.createElement('iframe')
  frame.setAttribute('aria-hidden', 'true')
  frame.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden'
  document.body.appendChild(frame)
  const win = frame.contentWindow, doc = frame.contentDocument
  if (!win || !doc) { frame.remove(); return }
  doc.open(); doc.write(html); doc.close()
  // Erst drucken, wenn Bilder (QR-Codes, Fotos) geladen sind
  const imgs = Array.from(doc.images)
  await Promise.all(imgs.map(i => i.complete ? null : new Promise(ok => { i.onload = i.onerror = ok })))
  let gone = false
  const remove = () => { if (!gone) { gone = true; frame.remove() } }
  win.addEventListener('afterprint', () => setTimeout(remove, 0))
  win.focus()
  win.print()
  // Falls afterprint ausbleibt: Rahmen spätestens nach einer Minute entfernen
  setTimeout(remove, 60000)
}
