// Nachladen der Mailliste: bis 500 Zeilen wird die Liste mit wachsender Größe neu angefordert (100, 200, …, 500), danach
// seitenweise je 500 angehängt (page/size beim Server). Ob es beim Server noch mehr gibt, zählt nach seiner Antwort - nicht nach
// den angezeigten Zeilen, denn gerade gelöschte oder verschobene Mails blendet die Liste sofort aus, während der Server sie noch
// mitzählt.

/** Mehr liefert der Server je Anfrage nicht (MailApi: size 1..500). */
export const LIST_MAX = 500

/** Zeilen bis Seite page (0 = erste): 100, 200, …, 500, danach je Seite 500 mehr. */
export const listRows = (page: number) => page < 5 ? 100 * (page + 1) : LIST_MAX * (page - 3)

/** Anfragen beim Server für die ersten n Zeilen: eine bis 500, darüber Seiten zu je 500. */
export const listPlan = (n: number): { page: number; size: number }[] =>
  n <= LIST_MAX ? [{ page: 0, size: n }] : Array.from({ length: Math.ceil(n / LIST_MAX) }, (_, page) => ({ page, size: LIST_MAX }))

/** Gibt es beim Server noch Mails nach der Antwort auf page/size? delivered = Zeilen der Antwort, total = Gesamtzahl laut Server.
 *  Eine leere Folgeseite heißt: nichts mehr, auch wenn die Gesamtzahl (etwa durch oben eingereihte Mails) größer ist. */
export const hasMore = (page: number, size: number, delivered: number, total: number) =>
  page === 0 ? delivered < total : delivered > 0 && (page + 1) * size < total

/** Zeilen anhängen, schon vorhandene (gleiche id) nicht doppelt. */
export const mergeRows = <T extends { id: number }>(a: T[], b: T[]): T[] => {
  const have = new Set(a.map(i => i.id))
  return [...a, ...b.filter(i => !have.has(i.id))]
}
