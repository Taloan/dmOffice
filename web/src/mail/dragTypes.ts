/** Datentyp, der beim Ziehen von Mails das Quellpostfach verrät (Typen sind beim Überfahren lesbar, die Daten erst beim Loslassen). */
export const mailAccountType = (account: string) => 'application/x-dmoffice-mail-account-' + account.toLowerCase().replace(/[^a-z0-9-]/g, '')
