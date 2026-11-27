// Mailkonto deaktivieren (Kennung mailaccountoff, settings/MailAccountActive.tsx) und Meldungen des Servers dazu
export const mailaccountoff: Record<string, string> = {
  "Aktiv": "Active",
  "Deaktiviert": "Disabled",
  "deaktiviert": "disabled",
  "Ein deaktiviertes Konto ruft dmOffice nicht ab, sendet nichts darüber und zeigt es im Mailbereich nicht an. Mails und Einstellungen bleiben; nach dem Aktivieren holt der Abgleich nur Neues.":
    "dmOffice does not fetch a disabled account, sends nothing through it and hides it in Mail. Messages and settings are kept; once enabled again, sync only fetches what is new.",
  "Standard-Postfach – solange es deaktiviert ist, gilt „{name}“ als Standard.": "Default mailbox – while it is disabled, “{name}” is the default.",
  "Standard-Postfach – solange es deaktiviert ist, gibt es kein aktives Mailkonto.": "Default mailbox – while it is disabled, there is no active mail account.",
  "Postausgang: {count} E-Mail(s) warten und gehen erst nach dem Aktivieren raus.": "Outbox: {count} email(s) waiting; they are sent once the account is enabled again.",
  "Catch-all läuft über dieses Konto und ruht.": "Catch-all runs through this account and is paused.",
  "Regeln: {count} betreffen dieses Konto und ruhen.": "Rules: {count} concern this account and are paused.",
  "Kalender, Kontakte und Aufgaben von Microsoft werden nicht abgeglichen.": "Calendars, contacts and tasks from Microsoft are not synced.",
  "Wiedervorlage und Entfernen nach Zeit: {count} Einträge ruhen.": "Snooze and remove after time: {count} entries are paused.",
  "DMS: Mailordner dieses Kontos als Quelle ruhen.": "DMS: mail folders of this account used as sources are paused.",
  "Dieses Mailkonto ist deaktiviert.": "This mail account is disabled.",
  "Dieses Mailkonto ist deaktiviert. Aktivieren lässt es sich unter Einstellungen › E-Mail › E-Mail-Konten.": "This mail account is disabled. You can enable it under Settings › Email › Email accounts.",
  "Mailkonto deaktiviert – die Mail geht raus, sobald es wieder aktiv ist.": "Mail account disabled – the email is sent once it is active again.",
}
