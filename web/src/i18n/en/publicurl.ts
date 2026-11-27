// Öffentliche Adresse des Servers (Kennung publicurl, 2.2; web/src/admin/PublicUrlAdmin.tsx, DmOffice.Server/Core/PublicAddress.cs)
export const publicurl: Record<string, string> = {
  'Öffentliche Adresse': 'Public address',
  'Öffentliche Adresse des Servers': 'Public address of the server',
  'Unter dieser Adresse öffnen Empfänger Links, die dmOffice verschickt oder zum Weitergeben anzeigt: Abmeldelinks im Newsletter, Freigabelinks, Bestätigungsmails und Einladungen. Ohne Eintrag gilt die Adresse, unter der dmOffice gerade aufgerufen wird.':
    'Recipients open links that dmOffice sends or shows for sharing at this address: unsubscribe links in newsletters, share links, confirmation mails and invitations. Without an entry, the address dmOffice is currently opened at applies.',
  'Vorgegeben über die Umgebungsvariable DMOFFICE_PUBLIC_URL.': 'Set by the environment variable DMOFFICE_PUBLIC_URL.',
  'Nur Protokoll, Rechnername und Port, etwa https://office.example.de': 'Only protocol, host name and port, e.g. https://office.example.de',
  'DMOFFICE_PUBLIC_URL enthält keine gültige Adresse. Bis zur Korrektur gilt die Adresse darunter.':
    'DMOFFICE_PUBLIC_URL does not contain a valid address. Until it is corrected, the address below applies.',
  'Die Adresse beginnt mit http://. Mit https:// bleiben Links und Anmeldedaten unterwegs verschlüsselt.':
    'The address starts with http://. With https://, links and sign-in data stay encrypted on the way.',
  'Aktuell gilt: {url}': 'Currently in use: {url}',
  'Die Anmeldung über einen Identitätsanbieter (OIDC) und Rückrufe anderer Dienste verwenden die Adresse, unter der dmOffice aufgerufen wird.':
    'Sign-in through an identity provider (OIDC) and callbacks from other services use the address dmOffice is opened at.',
  // Meldungen des Servers (auch Prüfung im Browser)
  'Bitte eine vollständige Adresse mit https:// oder http:// angeben.': 'Please enter a complete address with https:// or http://.',
  'Bitte nur Protokoll, Rechnername und Port angeben, ohne Pfad.': 'Please enter only protocol, host name and port, without a path.',
  'Die Adresse ist über DMOFFICE_PUBLIC_URL vorgegeben.': 'The address is set by DMOFFICE_PUBLIC_URL.',
}
