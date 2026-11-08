// Akzentfarbe vor dem ersten Bild: Tableiste (theme-color) und Symbol im Tab gleich in der gewählten Farbe statt erst Violett.
// Eigene Datei, weil die Sicherheitsregeln (CSP) keine Skripte im HTML erlauben. Gleiche Form wie brandLogo() in theme.ts.
(function () {
  var c = '#7B1DD3'
  try { var a = JSON.parse(localStorage.getItem('dmoffice.ui') || '{}').accent; if (/^#[0-9a-f]{6}$/i.test(a)) c = a } catch (e) { /* privat */ }
  var svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><rect width="108" height="108" rx="24" fill="' + c + '"/>'
    + '<path d="M27 24h30a24 24 0 0 1 24 24v12a24 24 0 0 1 -24 24H27z" fill="#fff"/>'
    + '<path d="M39 54l8 8 16-16" fill="none" stroke="' + c + '" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/></svg>'
  var meta = document.querySelector('meta[name="theme-color"]')
  if (meta) meta.setAttribute('content', c)
  var icon = document.querySelector('link[rel="icon"]')
  if (icon) icon.setAttribute('href', 'data:image/svg+xml,' + encodeURIComponent(svg))
})()
