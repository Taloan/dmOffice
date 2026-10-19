# dmOffice – Manual

Deutsche Fassung: [handbuch.md](handbuch.md)

dmOffice is an office in your browser: email, calendar, contacts, tasks, notes and files in one interface that works
like common office apps. It also includes its own CalDAV, CardDAV and WebDAV service, so your phone, tablet and Thunderbird
fetch their data straight from dmOffice.

The pictures show a fictional sample account ("Alex Berger"). They are regenerated from an empty database with
`tools/docs/make.sh`.

**Contents**

1. [Installation and first start](#1-installation-and-first-start)
2. [Email](#2-email)
3. [Calendar](#3-calendar)
4. [Contacts](#4-contacts)
5. [Tasks](#5-tasks)
6. [Notes](#6-notes)
7. [Files](#7-files)
8. [Connecting your phone, tablet and Thunderbird](#8-connecting-your-phone-tablet-and-thunderbird)
9. [Settings](#9-settings)
10. [Security](#10-security)
11. [Administration (Admin)](#11-administration-admin)
12. [Backup and moving](#12-backup-and-moving)
13. [Keyboard shortcuts](#13-keyboard-shortcuts)
14. [Frequently asked questions](#14-frequently-asked-questions)

---

## 1. Installation and first start

dmOffice runs as a Docker container. It is meant for Unraid but works on any Docker host.

**Unraid**

1. Search for **dmOffice** under *Apps* and install it. If the app is not listed there yet, see the
   [README](../README.md#unraid).
2. In the template, set **DMOFFICE_MASTER_KEY** – a long random string, e.g. from `openssl rand -hex 32`.
   Keep this key safe (password manager): **without it, your data can no longer be opened.**
3. Choose a port and click *Apply*.

**Docker Compose**

```yaml
services:
  dmoffice:
    image: ghcr.io/taloan/dmoffice:latest
    ports: ["8080:8080"]
    volumes: ["./data:/data"]
    environment:
      DMOFFICE_MASTER_KEY: "<long random string>"
      TZ: Europe/Berlin
    restart: unless-stopped
```

**First visit:** open `http://<server>:8080/`. On the very first visit, dmOffice creates the admin account.
After that, you sign in with your username and password.

![Sign-in](img/en/anmeldung.png)

For access on the move and for phones, dmOffice should sit behind a reverse proxy with HTTPS
(Nginx Proxy Manager, SWAG, Traefik). Some browser features (clipboard, desktop notifications)
are only available over HTTPS.

---

## 2. Email

![Inbox](img/en/mail.png)

On the left are the **Favourites** and the folders of each account, in the middle the message list, and on the right
the reading pane and – if turned on – the **To Do bar** with Calendar, People and Tasks.

### Setting up an account

*Settings* → *Account* → *Email accounts* → *Add account*. For well-known providers dmOffice fills in the
servers; otherwise enter the IMAP and SMTP servers. Check the settings with *Test connection*.

![Email accounts](img/en/einstellungen-konten.png)

For each account you can set:

| Setting | Meaning |
|---|---|
| **Period** | *All* or only the last few months are kept offline in dmOffice and are searchable. |
| **Fully store messages up to this size (MB)** | Smaller messages are stored in the database together with their attachments; larger ones are fetched when you open them. |
| **Save sent messages in "Sent Items"** | Off for Gmail – Gmail files the copy itself. |
| **Mail language** | German or English: determines *AW:/Re:*, *WG:/Fwd:*, the quote header and the spell check when replying. |
| **Signature** | Placed below the text when you compose. |

### Reading, sorting, filing

- **Dragging** messages onto a folder moves them; dragging them onto *Flagged* flags them.
- **Right-clicking** one or more messages opens the context menu with all actions.
- **Pictures from the internet** are blocked because they tell the sender that the message has been read.
  With *Show pictures ▾* you can allow them once, or permanently for the sender or the whole domain.
- **Search** at the top of the list: plain words, or targeted with `von:` (from), `an:` (to), `betreff:` (subject).
  With *All accounts* you search across all accounts.
- **Filter** above the list: Unread, Flagged, With attachments, or a category.

### Categories

![Categorize](img/en/mail-kategorien.png)

Categories are coloured keywords for messages. dmOffice stores them as an **IMAP keyword** on the
mail server – so they are also visible in Thunderbird or on a second dmOffice, as long as the same
keyword is used there.

- *Categorize ▾* in the ribbon or in the right-click menu.
- Manage them under *Settings* → *Mail* → *Categories*: name, colour, order. Renaming only changes the
  display; the messages keep their category.
- Gmail does not accept custom keywords; dmOffice reports this.

![Managing categories](img/en/einstellungen-kategorien.png)

### Snooze

![Snooze](img/en/mail-wiedervorlage.png)

Put a message aside for later: *Snooze* → *In 1 hour*, *This evening*, *Tomorrow*, *Next week* …
or a date of your own. The message disappears into the *Snooze* folder and reappears at the chosen time
**unread at the top of the Inbox**. With *Unsnooze* it comes back immediately.

### Writing

![New email](img/en/mail-verfassen.png)

- *New email*, *Reply*, *Reply all*, *Forward* or *Forward as attachment*.
- Recipients are suggested from contacts and earlier addresses as you type. An earlier suggestion can be forgotten
  with **×**; manage the list under *Settings* → *People*.
- Drafts are saved automatically. **Ctrl+Enter** sends.
- If the text mentions an attachment but none is attached, dmOffice asks.

### Doing more with a message

- **Create event from email** – creates an event with the subject and text of the message.
- **Save as note** – copies the message into your notes. At the top of the note is a link, *Mail in dmOffice
  öffnen*, that opens the message again – even if it has since been moved to another folder.

### Rules

*Settings* → *Mail* → *Rules*. A rule checks new messages in the Inbox (sender, recipient, subject,
body, header) and can move, copy, forward, mark as read, flag, assign a category
or delete. Every message runs through the rules **once** – even if it later lands in the Inbox again.

![Rules](img/en/einstellungen-regeln.png)

### Junk email

The junk filter uses a statistical method to learn from what you mark as *Junk* or *Not junk*. It only scores
and moves messages once it has learned 10 of each kind; until then everything stays in the inbox. *Learn from
folders* gets it there faster. Settings and thresholds are under
*Settings* → *Mail* → *Junk email*.

![Junk email](img/en/einstellungen-junk.png)

### Light and dark

The moon at the top right switches between light, dark and the system setting.

![Dark theme](img/en/mail-dunkel.png)

---

## 3. Calendar

![Calendar](img/en/kalender.png)

- Views: Day, Work week, Week, Month, List.
- Several coloured calendars, each of which can be shown or hidden individually.
- **Recurring events**: daily, weekdays, weekly, every two weeks, monthly, yearly. When you make a change, dmOffice
  asks whether you mean just this event or the whole series. If you move the whole series, cancelled and
  changed individual occurrences move with it.
- **Reminders** appear as a notification in the browser (allow notifications) and on all connected devices.
- **Import/export** as `.ics` via the calendar's menu – this is how calendars move over from Nextcloud or Google.
- If an event is changed on your phone at the same time, dmOffice warns you when you save instead of overwriting the change.

---

## 4. Contacts

![Contacts](img/en/kontakte.png)

Address books with photos, several phone numbers, addresses and birthdays. Fields that dmOffice does not edit itself
(e.g. PO box or additional phone types from the iPhone) are preserved when you save.
Import and export as `.vcf`.

---

## 5. Tasks

![Tasks](img/en/aufgaben.png)

*My Day*, *Important*, *Planned* and your own lists. Tasks are VTODO entries in a calendar and therefore show up
in any tasks app that supports CalDAV (e.g. jtx Board, OpenTasks, Reminders on the iPhone).

---

## 6. Notes

![Notes](img/en/notizen.png)

Notes with colour, folders, a star for *Important*, checkboxes to tick off, lists, code blocks and attachments.
Pictures from the clipboard or dragged in land directly in the text; other files become attachments. Links open
with Ctrl+click.

**With notizi:** dmOffice is one more device for the notes from **notizi**. Syncing runs through
notizi's end-to-end encrypted sync folder, which lives in dmOffice's WebDAV. Set it up under
*Settings* → *Notes*: enter the E2EE password and choose the database.

![notizi sync](img/en/einstellungen-notizi.png)

Tables and pictures from notizi are displayed but can only be edited in notizi on the PC – they are locked in
dmOffice. If another device changes a note while it is open in dmOffice, dmOffice warns you and offers
*Save as copy*.

Without notizi the notes work just the same, only without syncing.

---

## 7. Files

![Files](img/en/dateien.png)

Your own file storage – in the browser and via WebDAV (Windows Explorer, Finder, file managers on your phone).
Upload with the button or by dragging, including whole folders. The bottom shows how much is used; if the admin
has set a limit, a bar shows how much of it is in use.

---

## 8. Connecting your phone, tablet and Thunderbird

![Devices & sync](img/en/einstellungen-geraete.png)

Under *Settings* → *Account* → *Devices & sync*, create **a separate app password for each device**. The
login password deliberately does not work for these programs: an app password can be revoked individually if a
device is lost.

| Program | How |
|---|---|
| **Android (DAVx⁵)** | *Login with URL and user name*, URL `https://<host>/dav/`, username, app password. |
| **iPhone/iPad** | *Settings* → *Calendar* → *Accounts* → *Add Account* → *Other* → *Add CalDAV Account*; server: the host name only. For contacts do the same with *CardDAV account*. |
| **Thunderbird** | New calendar → *On the network* → address from *Devices & sync*. Address book: *Add CardDAV address book*. |
| **Windows Explorer** | *Map network drive* → `https://<host>/dav/files/<login>/` (HTTPS only). |
| **notizi** | *Settings* → *Sync* → *WebDAV*, address `https://<host>/dav/files/<login>/notizisync/`. |

---

## 9. Settings

Settings are laid out as in common office apps: the area on the left, the page in the middle, the content on the right.
At the top left there is a search across all pages.

| Page | Contents |
|---|---|
| **Language and time** | Display language (German/English, applies to the account on all devices), date and time format, time zone |
| **Appearance** | Light/dark, accent colour, text size |
| **Notifications** | Alerts for new mail and reminders, also as desktop notifications |
| **Message list** | Spacing, preview text, sender pictures |
| **Reading pane** | Reading pane on the right, at the bottom or off; when a message counts as read |
| **Folder pane** | Favourites, sorting |
| **Compose and reply** | Font and size, default *Reply* or *Reply all* |
| **Storage & statistics** | Size of the folders and the database, compact database |

![Language and time](img/en/einstellungen-sprache.png)

![Storage & statistics](img/en/einstellungen-speicher.png)

---

## 10. Security

![Security](img/en/einstellungen-sicherheit.png)

- **Encrypted:** Each user has their own encrypted database (SQLCipher) with its own key.
  Anyone who merely copies the data directory cannot read anything.
- **Two-step verification (TOTP):** *Settings* → *Security* → *Set up*, scan the QR code with an
  authenticator app (Aegis, 2FAS, Google/Microsoft Authenticator …). Print the **recovery codes**
  or store them in your password manager – each one works once.
- **Change password** signs out all other sessions.
- After several wrong attempts, dmOffice waits before accepting further sign-ins – per address and per
  username.

---

## 11. Administration (Admin)

![Admin](img/en/verwaltung.png)

Only for users with admin access:

- Create users, set passwords, grant or revoke admin access, delete (confirmed by typing the
  username).
- **Reset 2FA** if someone has lost their phone and recovery codes.
- **File storage:** a default for everyone (0 = unlimited) and a separate value per user (click
  the *Files* column; leave blank = default). The limit applies to WebDAV, the Files view and the
  notizi sync.
- A notice is shown as long as the master key is stored as a file in the data directory instead of in
  `DMOFFICE_MASTER_KEY`.

---

## 12. Backup and moving

- **Backing up:** the data directory (`/data`, on Unraid `appdata/dmoffice`) **and** the master key. It is
  safest with the container stopped; while it is running, first using *Settings* → *Storage & statistics* →
  *Compact database* helps keep the intermediate files small.
- **Moving:** copy the data directory to the new host and start the container with the same `DMOFFICE_MASTER_KEY`.
- **From Nextcloud:** export calendars as `.ics` and address books as `.vcf` and import them into dmOffice;
  switch notizi on all devices to the new WebDAV address.

---

## 13. Keyboard shortcuts

| Key | Action |
|---|---|
| Ctrl+N | New email |
| Ctrl+R / Ctrl+Shift+R | Reply / Reply all |
| Ctrl+Shift+F | Forward |
| Ctrl+Q / Ctrl+U | Mark as read / unread |
| Ins | Flag |
| Del | Delete |
| ↑ / ↓ | Previous / next message |
| Ctrl+Enter | Send (when composing) |
| Ctrl+S | Save note immediately |

---

## 14. Frequently asked questions

**Copying to the clipboard or desktop notifications do not work.**
Browsers only allow this over HTTPS. Open dmOffice through the reverse proxy with a certificate.

**My phone says "Login failed".**
Devices only accept an **app password**, not the login password.

**The database is very large.**
*Settings* → *Storage & statistics* shows which folders take up how much. Shorten the sync period
for each account and then use *Compact database*.

**I have lost the master key.**
Then the databases can no longer be opened. Messages remain on the mail server; calendars, contacts,
notes and files only on the connected devices.
