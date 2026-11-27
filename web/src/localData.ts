/** Was ein Benutzer im Browser hinterlässt (Einstellungen, zuletzt geöffnete Ordner, Korb, Entwürfe, wartende
 *  Chat-Nachrichten, Umschlag-Schlüssel), damit der nächste am selben Rechner es nicht vorfindet.
 *  Bleibt: „Gerät merken“ des Tresors - das hat der Benutzer für dieses Gerät ausdrücklich gewählt - und was
 *  `keep` zusätzlich nennt. */
export function forgetLocalData(keep: (key: string) => boolean = () => false) {
  for (const name of ['localStorage', 'sessionStorage'] as const) {
    try {
      const store = window[name]
      for (const k of Object.keys(store)) if (!k.startsWith('dm.tresor.device.') && !keep(k)) store.removeItem(k)
    } catch { /* privater Modus */ }
  }
}

/** Ohne Anmeldung bleiben nur Sprache und Hell/Dunkel der Anmeldeseite. */
export const loginScreenKeys = (k: string) => k === 'dmoffice.lang' || k === 'dmoffice.theme'
