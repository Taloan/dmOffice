// Datum und Uhrzeit aus freiem Text lesen - wie Todoist: "Zahnarzt morgen Nachmittag",
// "Bericht nächsten Mittwoch Viertel nach 3", "call Tom tomorrow 3pm". Deutsch und Englisch.
// Ergebnis: das Datum (ggf. mit Uhrzeit) und der Titel ohne die erkannten Woerter.

export interface NaturalDate {
  /** Ortszeit; ohne Uhrzeit: Mitternacht des Tages */
  date: Date
  hasTime: boolean
  /** Titel ohne die Datumsangabe */
  title: string
  /** Der erkannte Text (fuer die Hervorhebung) */
  matched: string
}

const WEEKDAYS: Record<string, number> = {
  sonntag: 0, montag: 1, dienstag: 2, mittwoch: 3, donnerstag: 4, freitag: 5, samstag: 6, sonnabend: 6,
  sunday: 0, monday: 1, tuesday: 2, wednesday: 3, thursday: 4, friday: 5, saturday: 6,
}
const MONTHS: Record<string, number> = {
  januar: 0, jan: 0, jänner: 0, februar: 1, feb: 1, märz: 2, maerz: 2, mär: 2, april: 3, apr: 3, mai: 4, juni: 5, jun: 5, juli: 6, jul: 6,
  august: 7, aug: 7, september: 8, sep: 8, sept: 8, oktober: 9, okt: 9, november: 10, nov: 10, dezember: 11, dez: 11,
  january: 0, february: 1, march: 2, mar: 2, may: 4, june: 5, july: 6, october: 9, oct: 9, december: 11, dec: 11,
}
const NUMBERS: Record<string, number> = {
  ein: 1, eine: 1, einer: 1, einem: 1, zwei: 2, drei: 3, vier: 4, fünf: 5, fuenf: 5, sechs: 6, sieben: 7, acht: 8, neun: 9, zehn: 10, elf: 11, zwölf: 12, zwoelf: 12,
  a: 1, an: 1, one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10, eleven: 11, twelve: 12,
}
// Tageszeiten: Standarduhrzeit und ob eine kleine Stundenzahl nachmittags gemeint ist
const DAYPARTS: [RegExp, number, 'am' | 'pm' | null][] = [
  [/^(früh|frueh|morgens|morning|in the morning)$/, 9, 'am'],
  [/^(vormittags?|vormittag)$/, 10, 'am'],
  [/^(mittags?|noon|midday)$/, 12, null],
  [/^(nachmittags?|afternoon|in the afternoon)$/, 14, 'pm'],
  [/^(abends?|evening|in the evening|tonight)$/, 19, 'pm'],
  [/^(nachts?|night|at night)$/, 22, 'pm'],
]

const num = (w: string) => /^\d+$/.test(w) ? Number(w) : NUMBERS[w]
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate())
const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)

interface Hit { from: number; to: number }

/** Erkennt Datum/Uhrzeit in einem Titel. Null, wenn nichts Brauchbares darin steht. */
export function parseNaturalDate(text: string, now = new Date()): NaturalDate | null {
  // Woerter mit Position, klein geschrieben; Satzzeichen am Ende abtrennen
  const words: { w: string; from: number; to: number }[] = []
  const re = /[^\s,;]+/g
  let m: RegExpExecArray | null
  while ((m = re.exec(text))) {
    const raw = m[0]
    const trimmed = raw.replace(/[!?)]+$/, '')
    words.push({ w: trimmed.toLowerCase(), from: m.index, to: m.index + trimmed.length })
  }
  const used: Hit[] = []
  const take = (i: number, n: number) => { used.push({ from: words[i].from, to: words[i + n - 1].to }); for (let k = i; k < i + n; k++) words[k].w = '\u0000' }
  const w = (i: number) => words[i]?.w ?? ''

  let day: Date | null = null
  let hour: number | null = null
  let minute = 0
  let meridiem: 'am' | 'pm' | null = null
  let defaultHour: number | null = null

  for (let i = 0; i < words.length; i++) {
    const x = w(i)
    if (x === '\u0000') continue
    // ---------- Tage ----------
    if (!day) {
      if (x === 'heute' || x === 'today') { day = startOfDay(now); take(i, 1); continue }
      if (x === 'morgen' && !/^(früh|frueh)$/.test(w(i - 1)) || x === 'tomorrow') { day = addDays(now, 1); take(i, 1); continue }
      if (x === 'übermorgen' || x === 'uebermorgen') { day = addDays(now, 2); take(i, 1); continue }
      if (x === 'day' && w(i + 1) === 'after' && w(i + 2) === 'tomorrow') { day = addDays(now, 2); take(i, 3); continue }
      // "in 3 Tagen", "in zwei Wochen", "in a month"
      if (x === 'in' && num(w(i + 1)) !== undefined) {
        const n = num(w(i + 1))!
        const unit = w(i + 2)
        if (/^(tag|tagen|days?)$/.test(unit)) { day = addDays(now, n); take(i, 3); continue }
        if (/^(woche|wochen|weeks?)$/.test(unit)) { day = addDays(now, 7 * n); take(i, 3); continue }
        if (/^(monat|monaten|months?)$/.test(unit)) { day = new Date(now.getFullYear(), now.getMonth() + n, now.getDate()); take(i, 3); continue }
        if (/^(stunde|stunden|hours?)$/.test(unit)) {
          const t = new Date(now.getTime() + n * 3600000)
          day = startOfDay(t); hour = t.getHours(); minute = t.getMinutes(); take(i, 3); continue
        }
      }
      // "nächste Woche" -> Montag; "nächsten Monat" -> der 1.
      if (/^(nächste|naechste|kommende|next)$/.test(x) && /^(woche|week)$/.test(w(i + 1))) { day = addDays(now, ((8 - now.getDay()) % 7) || 7); take(i, 2); continue }
      if (/^(übernächste|uebernaechste)$/.test(x) && w(i + 1) === 'woche') { day = addDays(now, (((8 - now.getDay()) % 7) || 7) + 7); take(i, 2); continue }
      if (/^(nächsten|naechsten|kommenden|next)$/.test(x) && /^(monat|month)$/.test(w(i + 1))) { day = new Date(now.getFullYear(), now.getMonth() + 1, 1); take(i, 2); continue }
      if (/^(ende|end)$/.test(x) && /^(der|of)$/.test(w(i + 1)) && /^(woche|week)$/.test(w(i + 2))) { day = addDays(now, (5 - now.getDay() + 7) % 7 || 7); take(i, 3); continue }
      // Wochentag, auch "nächsten Mittwoch", "am Freitag", "diesen Samstag", "next friday"
      const wdIdx = WEEKDAYS[x] !== undefined ? i : null
      if (wdIdx !== null) {
        const target = WEEKDAYS[x]
        let delta = (target - now.getDay() + 7) % 7
        const prev = w(i - 1)
        if (delta === 0) delta = 7                       // "Montag" am Montag: naechste Woche
        day = addDays(now, delta)
        let n = 1, start = i
        if (/^(am|an|diesen|this|on|nächsten|naechsten|nächster|naechster|kommenden|next)$/.test(prev)) { start = i - 1; n = 2 }
        take(start, n)
        continue
      }
      // 24.10. / 24.10.2026 / 24.10.26 / 2026-10-24 / 10/24
      let d = x.match(/^(\d{1,2})\.(\d{1,2})\.(\d{2,4})?$/)
      if (d) {
        const y = d[3] ? (d[3].length === 2 ? 2000 + Number(d[3]) : Number(d[3])) : now.getFullYear()
        let cand = new Date(y, Number(d[2]) - 1, Number(d[1]))
        if (!d[3] && cand < startOfDay(now)) cand = new Date(y + 1, Number(d[2]) - 1, Number(d[1]))
        day = cand
        take(/^(am|bis|on|by)$/.test(w(i - 1)) ? i - 1 : i, /^(am|bis|on|by)$/.test(w(i - 1)) ? 2 : 1)
        continue
      }
      d = x.match(/^(\d{4})-(\d{2})-(\d{2})$/)
      if (d) { day = new Date(Number(d[1]), Number(d[2]) - 1, Number(d[3])); take(i, 1); continue }
      // "24. Oktober", "3 november", "oct 24", "24 oct"
      const dm = x.match(/^(\d{1,2})\.?$/)
      if (dm && MONTHS[w(i + 1)] !== undefined) {
        const mo = MONTHS[w(i + 1)]
        let cand = new Date(now.getFullYear(), mo, Number(dm[1]))
        if (cand < startOfDay(now)) cand = new Date(now.getFullYear() + 1, mo, Number(dm[1]))
        day = cand
        const withAm = /^(am|on)$/.test(w(i - 1))
        take(withAm ? i - 1 : i, withAm ? 3 : 2)
        continue
      }
      if (MONTHS[x] !== undefined && /^\d{1,2}(st|nd|rd|th)?$/.test(w(i + 1)) && x.length >= 3) {
        const mo = MONTHS[x]
        let cand = new Date(now.getFullYear(), mo, parseInt(w(i + 1)))
        if (cand < startOfDay(now)) cand = new Date(now.getFullYear() + 1, mo, parseInt(w(i + 1)))
        day = cand
        take(i, 2)
        continue
      }
    }
    // ---------- Uhrzeiten ----------
    if (hour === null) {
      // "Viertel nach 3", "viertel vor 4", "halb 4", "dreiviertel 4", "quarter past 3", "half past 3", "quarter to 4"
      if (/^viertel$/.test(x) && /^(nach|vor)$/.test(w(i + 1)) && num(w(i + 2)) !== undefined) {
        const h = num(w(i + 2))!
        hour = w(i + 1) === 'nach' ? h : h - 1; minute = w(i + 1) === 'nach' ? 15 : 45
        take(/^um$/.test(w(i - 1)) ? i - 1 : i, /^um$/.test(w(i - 1)) ? 4 : 3); continue
      }
      if (/^(halb)$/.test(x) && num(w(i + 1)) !== undefined) {
        hour = num(w(i + 1))! - 1; minute = 30
        take(/^um$/.test(w(i - 1)) ? i - 1 : i, /^um$/.test(w(i - 1)) ? 3 : 2); continue
      }
      if (/^(dreiviertel)$/.test(x) && num(w(i + 1)) !== undefined) {
        hour = num(w(i + 1))! - 1; minute = 45
        take(/^um$/.test(w(i - 1)) ? i - 1 : i, /^um$/.test(w(i - 1)) ? 3 : 2); continue
      }
      if (x === 'quarter' && /^(past|after|to)$/.test(w(i + 1)) && num(w(i + 2)) !== undefined) {
        const h = num(w(i + 2))!
        hour = w(i + 1) === 'to' ? h - 1 : h; minute = w(i + 1) === 'to' ? 45 : 15
        take(/^at$/.test(w(i - 1)) ? i - 1 : i, /^at$/.test(w(i - 1)) ? 4 : 3); continue
      }
      if (x === 'half' && w(i + 1) === 'past' && num(w(i + 2)) !== undefined) {
        hour = num(w(i + 2))!; minute = 30
        take(/^at$/.test(w(i - 1)) ? i - 1 : i, /^at$/.test(w(i - 1)) ? 4 : 3); continue
      }
      // 15:30, 15.30 Uhr, 15 Uhr, 3pm, 3:15pm, "um 9"
      let t = x.match(/^(\d{1,2})[:.](\d{2})(am|pm|h)?$/)
      if (t && Number(t[1]) < 24 && Number(t[2]) < 60 && (x.includes(':') || w(i + 1) === 'uhr' || /^(um|at)$/.test(w(i - 1)) || t[3])) {
        hour = Number(t[1]); minute = Number(t[2]); if (t[3] === 'am' || t[3] === 'pm') meridiem = t[3]
        let start = i, n = 1
        if (/^(um|at|gegen|ab)$/.test(w(i - 1))) { start = i - 1; n++ }
        if (w(i + 1) === 'uhr') n++
        take(start, n); continue
      }
      t = x.match(/^(\d{1,2})(am|pm|h)$/)
      if (t && Number(t[1]) <= 24) {
        hour = Number(t[1]); if (t[2] !== 'h') meridiem = t[2] as 'am' | 'pm'
        const withAt = /^(um|at|gegen|ab)$/.test(w(i - 1))
        take(withAt ? i - 1 : i, withAt ? 2 : 1); continue
      }
      if (/^\d{1,2}$/.test(x) && Number(x) <= 24 && (w(i + 1) === 'uhr' || /^(um|at|gegen)$/.test(w(i - 1)))) {
        hour = Number(x)
        if (/^(am|pm)$/.test(w(i + 1)) && w(i - 1) === 'at') meridiem = w(i + 1) as 'am' | 'pm'
        let start = i, n = 1
        if (/^(um|at|gegen)$/.test(w(i - 1))) { start = i - 1; n++ }
        if (w(i + 1) === 'uhr' || (meridiem && w(i + 1) === meridiem)) n++
        take(start, n); continue
      }
    }
    // ---------- Tageszeiten ----------
    const dp = DAYPARTS.find(([rx]) => rx.test(x))
    if (dp && (defaultHour === null)) {
      defaultHour = dp[1]
      if (dp[2]) meridiem = meridiem ?? dp[2]
      // "morgen früh": "früh" nach "morgen"
      take(i, 1)
      continue
    }
  }

  if (!day && hour === null && defaultHour === null) return null

  let hasTime = false
  if (hour !== null) {
    hasTime = true
    if (meridiem === 'pm' && hour < 12) hour += 12
    if (meridiem === 'am' && hour === 12) hour = 0
    // "Viertel nach 3" ohne Tageszeit: tagsueber gemeint (3 -> 15 Uhr), wie man es spricht
    if (meridiem === null && hour >= 1 && hour <= 7) hour += 12
  } else if (defaultHour !== null) {
    hour = defaultHour
    minute = 0
    hasTime = true
  }

  let base = day ?? startOfDay(now)
  // Nur eine Uhrzeit, die heute schon vorbei ist: morgen
  if (!day && hasTime && new Date(base.getFullYear(), base.getMonth(), base.getDate(), hour!, minute) < now) base = addDays(base, 1)
  const date = hasTime ? new Date(base.getFullYear(), base.getMonth(), base.getDate(), hour!, minute) : base

  // Titel ohne die erkannten Stellen; uebrig gebliebene Fuellwoerter am Rand weg
  const spans = used.sort((a, b) => a.from - b.from)
  let title = ''
  let pos = 0
  for (const h of spans) { title += text.slice(pos, h.from); pos = h.to }
  title += text.slice(pos)
  title = title.replace(/\s{2,}/g, ' ').replace(/\s+([,.;!?])/g, '$1').trim()
    .replace(/\s+(am|um|bis|ab|at|on|by)$/i, '').replace(/^(am|um|bis|at|on|by)\s+/i, '').replace(/[,;]\s*$/, '').trim()
  const matched = spans.map(h => text.slice(h.from, h.to)).join(' ')
  return { date, hasTime, title: title || text.trim(), matched }
}
