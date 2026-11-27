// Verschlüsselt große Upload-Teile für den Umschlag (e2e.ts) neben der Seite: Base64, JSON und AES-GCM eines 8-MB-Teils
// hielten sonst die Oberfläche jeweils fast eine Sekunde an (Abbrechen griff erst danach). Zähler und Zusatzdaten vergibt
// weiter die Seite; hier wird nur gerechnet.
interface Job { id: number; key: CryptoKey; aad: Uint8Array; method: string; path: string; headers: Record<string, string>; body: Blob | null }

// Nur in Workern vorhanden (die Typen hier kennen nur DOM)
declare const FileReaderSync: { new(): { readAsDataURL(b: Blob): string } }

const b64 = (b: Blob) => {
  const u = new FileReaderSync().readAsDataURL(b)
  return u.slice(u.indexOf(',') + 1)
}

self.onmessage = async (e: MessageEvent<Job>) => {
  const { id, key, aad, method, path, headers, body } = e.data
  try {
    const inner = new TextEncoder().encode(JSON.stringify({ method, path, headers, body: body ? b64(body) : null }))
    const nonce = crypto.getRandomValues(new Uint8Array(12))
    const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad as BufferSource }, key, inner)
    // Als Blob zurück: den kann die Seite ohne Kopie senden (ein Byte-Feld kopierte XMLHttpRequest.send im Hauptthread)
    ;(self as unknown as Worker).postMessage({ id, sealed: new Blob([nonce, cipher]) })
  } catch (err) {
    ;(self as unknown as Worker).postMessage({ id, error: String(err) })
  }
}
