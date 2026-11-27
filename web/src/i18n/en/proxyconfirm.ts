// Reverse-Proxys im Security Center (Kennung proxyconfirm, 2.2; web/src/admin/SecurityProxies.tsx, DmOffice.Server/Core/ProxyConfirm.cs)
export const proxyconfirm: Record<string, string> = {
  'Reverse-Proxys': 'Reverse proxies',
  'automatisch übernommen': 'adopted automatically',
  'nicht bestätigt': 'not confirmed',
  'deine Verbindung': 'your connection',
  'Stand': 'Status',
  'Host': 'Host',
  'Anfragen': 'Requests',
  'Zuerst': 'First seen',
  'Als Reverse-Proxy bestätigen?': 'Confirm as reverse proxy?',
  'Von {address} gelten danach die Kopfzeilen X-Forwarded-For, -Proto und -Host: Die Adresse, die dieser Absender nennt, gilt als Adresse des Besuchers. Nur bestätigen, wenn es dein eigener Reverse-Proxy ist.':
    'X-Forwarded-For, -Proto and -Host from {address} will then be accepted: the address this sender names counts as the visitor’s address. Only confirm if it is your own reverse proxy.',
  '{address} aus der Liste entfernen?': 'Remove {address} from the list?',
  '{address} gilt danach nicht mehr als Reverse-Proxy. Kommen Anfragen weiter über ihn, sieht der Server nur noch seine Adresse statt der des Besuchers.':
    '{address} will no longer count as a reverse proxy. If requests keep coming through it, the server only sees its address instead of the visitor’s.',
  'Entfernen?': 'Remove?',
  'Als Reverse-Proxy bestätigen': 'Confirm as reverse proxy',
  'Aus der Proxy-Liste entfernen': 'Remove from proxy list',
  'Es gilt allein DMOFFICE_TRUSTED_PROXIES: {nets}': 'Only DMOFFICE_TRUSTED_PROXIES applies: {nets}',
  'Die Liste ist nicht aktiv.': 'The list is not active.',
  'Steht dmOffice hinter einem Reverse-Proxy, nennt dieser die Adresse des Besuchers in X-Forwarded-For. Das gilt nur von bestätigten Proxys und von dieser Maschine selbst; bei allen anderen Absendern zählt ihre eigene Adresse, auch für Sperren und Bremsen.':
    'If dmOffice runs behind a reverse proxy, the proxy names the visitor’s address in X-Forwarded-For. This is accepted only from confirmed proxies and from this machine itself; for all other senders their own address counts, also for blocks and rate limits.',
  'Bis {time} werden Proxys, die schon vor dem Update galten, automatisch übernommen.': 'Until {time}, proxies that were already accepted before the update are adopted automatically.',
  'Bisher hat kein Absender X-Forwarded-*-Kopfzeilen geschickt.': 'No sender has sent X-Forwarded-* headers so far.',
  // Meldungen des Servers
  'Diesen Absender gibt es nicht in der Liste.': 'This sender is not in the list.',
  'Die Liste der Reverse-Proxys ist nicht aktiv.': 'The list of reverse proxies is not active.',
  'Keine gültige Adresse.': 'Not a valid address.',
}
