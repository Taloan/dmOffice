// Mehrere Signaturen mit Zuordnung (Kennung mailsignatures)
export const mailsignatures: Record<string, string> = {
  'Signatur des Kontos': 'Account signature',
  'Passend zum Absender': 'Matching the sender',
  'Signaturen': 'Signatures',
  'Neue Signatur': 'New signature',
  '{n} löschen?': 'Delete {n}?',
  'Absender mit dieser Signatur bekommen wieder die Signatur ihres Mailkontos.': 'Senders with this signature get their mail account’s signature again.',
  'Zuordnung für einen Alias': 'Assignment for an alias',
  'Signaturen mit Namen anlegen und Absendern zuordnen. Beim Schreiben gilt die Signatur des Alias, sonst die der Domain, sonst die des Kontos. Ohne Zuordnung gilt die Signatur aus den Einstellungen des Mailkontos.': 'Create named signatures and assign them to senders. When writing, the alias’s signature applies, otherwise the domain’s, otherwise the account’s. Without an assignment, the signature from the mail account settings applies.',
  '1 Zuordnung': '1 assignment',
  '{n} Zuordnungen': '{n} assignments',
  'Noch keine Signatur.': 'No signature yet.',
  'Zuordnung': 'Assignment',
  'Jeder Absender hat höchstens eine Signatur. Einzelne Aliase gehen vor ihrer Domain, Domains vor dem Konto.': 'Each sender has at most one signature. Single aliases come before their domain, domains before the account.',
  'keine': 'none',
  'Zuordnung für einen Alias …': 'Assignment for an alias …',
}
