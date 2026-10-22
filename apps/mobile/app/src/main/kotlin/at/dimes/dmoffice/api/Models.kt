package at.dimes.dmoffice.api

import kotlinx.serialization.Serializable

/** Datenformate der dmOffice-Web-API (web/src/api.ts). Unbekannte Felder werden ignoriert. */

@Serializable data class Addr(val name: String? = null, val address: String = "") {
    val text: String get() = if (!name.isNullOrBlank()) name else address
    val full: String get() = if (!name.isNullOrBlank()) "$name <$address>" else address
}

@Serializable data class MailAccount(val id: String, val name: String = "", val email: String = "", val displayName: String = "", val signature: String = "")

@Serializable data class Folder(
    val path: String, val name: String = "", val delimiter: String = "/", val special: String? = null,
    val selectable: Boolean = true, val unread: Int = 0, val total: Int = 0,
)

@Serializable data class MailItem(
    val id: Long = 0, val account: String = "", val folder: String = "", val uid: Long = 0, val messageId: String? = null,
    val subject: String = "", val from: Addr? = null, val to: List<Addr> = emptyList(), val cc: List<Addr> = emptyList(),
    val date: String = "", val size: Long = 0, val seen: Boolean = false, val flagged: Boolean = false,
    val answered: Boolean = false, val forwarded: Boolean = false, val hasAttachments: Boolean = false,
    val preview: String = "", val spam: Double? = null, val cached: Boolean = false, val keywords: List<String> = emptyList(),
    /** Prio-Posteingang: true Relevant, false Sonstige, null noch nicht eingeordnet (zaehlt als Relevant). */
    val focus: Boolean? = null,
)

/** Prio-Posteingang: eingeschaltet? Ungelesene je Reiter. */
@Serializable data class FocusInfo(val enabled: Boolean = false, val focused: Int = 0, val other: Int = 0)

/** Kategorie einer Mail (IMAP-Schlagwort) mit Name und Farbe aus den Einstellungen. */
@Serializable data class MailCategory(val key: String = "", val name: String = "", val color: String = "#0F6CBD")

/** E-Mail-Nachverfolgung: gekennzeichnete Mail mit Faelligkeit (yyyy-MM-dd) oder ohne. */
@Serializable data class FollowUp(val mail: MailItem = MailItem(), val due: String? = null)

@Serializable data class MessagesPage(val total: Int = 0, val page: Int = 0, val size: Int = 0, val items: List<MailItem> = emptyList())

@Serializable data class Attachment(val index: Int, val name: String = "", val mime: String = "", val size: Long = 0, val inline: Boolean = false)

@Serializable data class Message(
    val account: String = "", val uid: Long = 0, val folder: String = "", val subject: String = "", val from: Addr? = null,
    val to: List<Addr> = emptyList(), val cc: List<Addr> = emptyList(), val bcc: List<Addr> = emptyList(), val replyTo: List<Addr> = emptyList(),
    val date: String = "", val html: String = "", val hasRemoteImages: Boolean = false, val text: String = "",
    val attachments: List<Attachment> = emptyList(), val messageId: String? = null, val inReplyTo: String? = null,
    val references: List<String> = emptyList(), val seen: Boolean = false, val flagged: Boolean = false,
    val imagesAllowed: Boolean = false, val imagesTrusted: Boolean = false,
    /** Junk: Bilder und Links gesperrt, bis die Mail als "Kein Junk" im Posteingang liegt. */
    val junk: Boolean = false,
    /** Newsletter: Abmeldewege aus List-Unsubscribe (wie im Web). */
    val unsubscribe: Unsub? = null,
    /** Phishing-Merkmale vom Server (Mail/PhishCheck.cs): Warnbalken wie im Web. */
    val phish: List<PhishFinding>? = null,
)

/** kind: auth (domain, detail dmarc|spf), link (shown, target), lookalike (domain, like, detail sender|link|mixed),
 *  newsender (domain = Absenderadresse, target). strong: allein schon Grund zur Warnung. */
@Serializable data class PhishFinding(
    val kind: String = "", val strong: Boolean = false, val domain: String? = null, val shown: String? = null,
    val target: String? = null, val like: String? = null, val detail: String? = null,
)

@Serializable data class Unsub(val url: String? = null, val mailto: String? = null, val oneClick: Boolean = false)

@Serializable data class AccountStatus(val running: Boolean = false, val error: String? = null, val unread: Int = 0, val pendingBodies: Int = 0)
@Serializable data class MailStatus(val version: Long = 0, val accounts: Map<String, AccountStatus> = emptyMap())

@Serializable data class FlagsBody(val folder: String, val uids: List<Long>, val seen: Boolean? = null, val flagged: Boolean? = null)
@Serializable data class UidsBody(val folder: String, val uids: List<Long>)
@Serializable data class MoveBody(val folder: String, val uids: List<Long>, val target: String)

/** Entwurf zum Senden - als Feld "json" im Multipart, wie Compose.tsx form(). */
@Serializable data class Draft(
    val to: String = "", val cc: String = "", val bcc: String = "", val subject: String = "", val html: String = "",
    val inReplyTo: String? = null, val references: List<String> = emptyList(),
    val replyFolder: String? = null, val replyUid: Long? = null,
    val forwardFolder: String? = null, val forwardUid: Long? = null, val forwardAttachments: List<Int> = emptyList(),
    val forwardAsAttachment: Boolean = false, val draftUid: Long? = null, val highPriority: Boolean = false, val readReceipt: Boolean = false,
    /** Aus der Dateiablage anhaengen (Pfade); als Freigabelink: Pfade bzw. Namen der hochgeladenen Dateien. */
    val filePaths: List<String> = emptyList(), val linkPaths: List<String> = emptyList(), val linkUploads: List<String> = emptyList(),
)

// ---------------- Kontakte ----------------

@Serializable data class TypedValue(val type: String = "", val value: String = "")
@Serializable data class Address(val type: String = "", val street: String = "", val city: String = "", val region: String = "", val zip: String = "", val country: String = "") {
    val text: String get() = listOf(street, listOf(zip, city).filter { it.isNotBlank() }.joinToString(" "), region, country).filter { it.isNotBlank() }.joinToString(", ")
}

@Serializable data class Contact(
    val id: String = "", val book: Long = 0, val uri: String = "", val etag: String = "", val uid: String? = null,
    val displayName: String = "", val prefix: String = "", val given: String = "", val additional: String = "", val family: String = "",
    val suffix: String = "", val nickname: String = "", val org: String = "", val department: String = "", val title: String = "",
    val emails: List<TypedValue> = emptyList(), val phones: List<TypedValue> = emptyList(), val addresses: List<Address> = emptyList(),
    val urls: List<String> = emptyList(), val birthday: String? = null, val note: String = "", val categories: List<String> = emptyList(),
    val photo: String? = null,
    /** VIP: Benachrichtigungen "nur VIPs" und Prio-Posteingang (wie im Web). */
    val vip: Boolean = false,
) {
    val name: String get() = displayName.ifBlank { listOf(given, family).filter { it.isNotBlank() }.joinToString(" ") }.ifBlank { org }.ifBlank { emails.firstOrNull()?.value ?: "" }
    /** Buchstabe fuer die Gruppierung: Nachname, sonst Anzeigename. */
    val sortKey: String get() = (family.ifBlank { name }).trim().lowercase()
}

@Serializable data class Collection(val id: Long, val uri: String = "", val name: String = "", val color: String? = null, val count: Int = 0)

// ---------------- Notizen ----------------

@Serializable data class NoteItem(
    val id: Long, val syncId: String? = null, val folder: String? = null, val title: String = "", val snippet: String = "",
    val color: String? = null, val modified: String = "", val created: String = "", val important: Boolean = false,
    val attachments: Int = 0, val archived: String? = null, val deleted: String? = null,
)
@Serializable data class NotesList(val archived: Int = 0, val trashed: Int = 0, val items: List<NoteItem> = emptyList())
/** Ordner der Notizen; der Server liefert sync_id als syncId. */
@Serializable data class NoteFolder(val syncId: String, val name: String = "", val parent: String? = null, val sortOrder: Double = 0.0)
@Serializable data class NoteAttachment(val name: String = "", val mime: String = "", val size: Long = 0, val blob: String = "")
/** archived, deleted: Zeitpunkt des Weglegens (wie in notizi), null = aktiv. */
@Serializable data class NoteDetail(
    val id: Long, val syncId: String? = null, val folder: String? = null, val customTitle: String? = null, val title: String = "",
    val color: String? = null, val important: Boolean = false, val modified: String = "", val created: String = "",
    val archived: String? = null, val deleted: String? = null, val blocks: List<String> = emptyList(),
    val protected: List<Boolean> = emptyList(), val readOnly: Boolean = false, val attachments: List<NoteAttachment> = emptyList(),
    val pending: Boolean = false, val version: String = "",
)
@Serializable data class NoteSaveResult(val saved: Boolean = false, val version: String = "")
@Serializable data class NoteMeta(val customTitle: String? = null, val clearCustomTitle: Boolean = false, val color: String? = null, val important: Boolean? = null, val folder: String? = null)
