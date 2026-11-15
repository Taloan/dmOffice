import { t, tError } from '../i18n'

/**
 * Fehler mehrerer Dateien für eine Meldung: je Grund eine Zeile mit Anzahl und höchstens drei Namen,
 * statt jede Datei einzeln aufzuzählen (bei vielen Fotos sonst eine bildschirmhohe Meldung).
 */
export function failedText(failed: { name: string; error: string }[]): string {
  const groups = new Map<string, string[]>()
  for (const f of failed) groups.set(f.error ?? '', [...(groups.get(f.error ?? '') ?? []), f.name])
  return [...groups].map(([error, names]) => {
    const which = names.length === 1 ? names[0]
      : t('{n} Dateien', { n: names.length }) + ' (' + names.slice(0, 3).join(', ') + (names.length > 3 ? ', …' : '') + ')'
    return which + ': ' + tError(error)
  }).join('\n')
}
