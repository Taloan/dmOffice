# Shared calendars
[Deutsch](geteilte-kalender.de.md) · English

A calendar – e.g. "Family" or "Meal plan" – can be shared with other users on the same server, either for **reading** or for
**editing**. The owner grants the access and can change or withdraw it at any time.

## Where things stand

| What | How it is stored today |
|---|---|
| Users | `data/users.json`: login, display name, password hash, and the database key **wrapped** with the master key (`UserRecord.WrappedKey`) |
| A user's data | one SQLCipher database per user (`UserDb`); the key is 32 random bytes, wrapped with the master key (`DMOFFICE_MASTER_KEY`, see `Vault`) |
| Calendars | table `collections` (`kind='calendar'`, `uri`, `name`, `color`, `components`, `synctoken`) in the **owner's** database; calendars and task lists are the same thing (`VEVENT,VTODO`) |
| Events | table `objects`: one row per event holding the text of the `.ics` file, plus `etag`, `first_start`/`last_end` (range queries) and `changes` (sync token, RFC 6578) |
| CalDAV | `Dav/DavServer.cs`: `/dav/calendars/<login>/<calendar>/<object>.ics`, always in the database of the signed-in user |
| Web app | `/api/pim/*`; `web/src/calendar` |
| Android app | syncs exclusively over CalDAV (`DavClient`, `SyncEngine`, sync token) – it has no calendar logic of its own on the server side |
| Shares | `data/shares.json` (`ShareStore`): already existed for **task lists** (owner, collection id, members) |

## The open question: encryption

*How does a second user get at the encrypted data while the owner is not signed in?*

User databases are encrypted **on the server** (protection of data at rest: copied files, backups, foreign access to the data
directory). The server can open every database because it knows the master key from the environment and uses it to unwrap all
database keys – including for the background services (mail sync, rules) that run without anyone being signed in. A user's
password does **not** protect the data cryptographically; it only controls access through the server.

It follows that sharing **does not break any encryption**. The calendar stays in the owner's database, and the server reads and
writes there on the recipient's behalf – after checking the share on **every** request. There are no copies and no keys handed
out, so revoking access takes effect immediately. This is the same architecture that already carries shared task lists
(`SharedLists.Resolve`).

You should know the limit this brings with it: whoever has the server and the master key (the administration) can reach all data,
with or without sharing. Per-user end-to-end encryption that even the administration cannot read would, with shared calendars,
only be possible through key exchange between users (a calendar key wrapped with each member's public key), and it does not fit
the current model (server-side background services, CalDAV with app passwords). That is **not** a decision made by this change.

## Implementation

**Shared source:** The collection stays with the owner. A recipient sees it under its own address `shared-<share id>` (web: negative
id `-<share id>`, as with task lists); its events are read from and written to the owner's database directly. ETag and sync token
are those of the collection itself – an event that Cleo changes has a new ETag for Anna and Ben immediately, and every sync client
picks it up with its next request.

**Permissions** (`shares.json`, `Share.Members` + `Share.ReadOnly`; older files without `ReadOnly` count as "Edit"):

| | Owner | Edit | Read | no share |
|---|---|---|---|---|
| View / export events | ✔ | ✔ | ✔ | – (404) |
| Create, change, delete, move events | ✔ | ✔ | – (403) | – (404) |
| Name, color, import, deleting the calendar | ✔ | – (403) | – (403) | – |
| Share and change permissions | ✔ | – | – | – |
| Stop showing (leave) | – | ✔ | ✔ | – |

Access is checked against `shares.json` on every web and CalDAV request. If the owner or the calendar is deleted,
`ShareStore.Forget` cleans up the shares.

**Web app:** right-click or menu on your own calendar → "Share…" (a dialog with Read/Edit per person; the same for task lists).
Shared calendars appear in the sidebar as "Name (from owner)" in the owner's color; read-only ones cannot be created in, dragged
or deleted, and have "Stop showing".

**CalDAV:** in the recipient's calendar home, `shared-<id>` appears with the display name "Name (from owner)", color and sync
token, and a `current-user-privilege-set` without write privilege when the calendar is shared read-only. Write attempts on
read-only calendars get 403; `DELETE` on the collection means "leave", and `PROPPATCH` (name/color) is reserved for the owner.
`MOVE`/`COPY` between your own and a shared calendar writes to the target first and then deletes the source.

**Android app:** needs no sharing logic of its own – the sync discovers shared calendars over CalDAV, with name and color.
A write attempt on a read-only calendar (403) is discarded in `SyncEngine` and restores the server state instead of aborting the
whole sync. Still open: the app still offers read-only calendars as a target in its editor (see below).

Known limitation: times without a time zone ("floating") and reminders are read and written in the **owner's** time zone.

## Calendar types

Every calendar has a **type** (`CalendarKinds.cs`, web app `web/src/calendar/kinds.ts`): `normal` (the default) or `meal`
("Meal plan"). The type decides in which view a calendar appears – its name does not.

- **Storage:** with the owner, in the key/value table (`pim.calkind.<id>`), not in the calendar itself. CalDAV, DAVx⁵, iOS and the
  Android app continue to see an ordinary calendar with ordinary events. No schema migration. The app does not know about the type
  (web only); should it use it later, `GET /api/pim/collections/calendars` returns it per calendar as `calendarKind`.
- **Shared calendars:** the type belongs to the owner (`PUT /api/pim/collections/{id}` with `calendarKind`, 403 for recipients) and
  applies to recipients too; it is also part of their calendar list. Meal plan calendars shared read-only cannot be written to.
- **Calendar list on the left:** grouped by type, with a collapsible heading per group ("My calendars", "Meal plan", …; empty
  groups other than the normal one are omitted). The checkboxes of a group apply to its view: day/week/month/list show only
  calendars of type `normal`, the meal plan shows exactly the ticked calendars of its group; the others are hidden there.
- **Registry:** `web/src/calendar/kinds.tsx` (`CALENDAR_KINDS`) describes, for each type, its name, group title, icon and – if it
  has one – ribbon button and planning view (a component taking `PlanProps`). The ribbon and the list are built from it.
- **Existing data:** on first access after the update (once per user, marker `pim.calkind.migrated`), every calendar without a type
  that contains at least one event in the category Frühstück/Mittagessen/Abendessen (breakfast/lunch/dinner) gets the type `meal`.
- **More types** (conceivable: shift schedule, absences, driving rota): add the name to `CalendarKinds.All` and `CALENDAR_KINDS`
  with group, icon and planning view (the view shows the ticked calendars of that type together). The entries stay ordinary events
  with their own category or an `X-DM-…` property, so that other calendar programs still display them legibly.

## Meal plan (implemented)

Calendar view → ribbon "Meal plan": one week with breakfast, lunch and dinner for each day, drawn from **all ticked calendars of
the type "Meal plan"** (see Calendar types), each entry in the color of its calendar – ideally a shared one that the family edits
together. In the plan the list on the left shows only these calendars and switches which of them are in the plan. When adding an
entry, the dialog asks where to put it if there are several writable meal plan calendars (default: the one used last, otherwise
the first); "Ingredients to shopping list" takes the recipes of all visible entries. Without a meal plan calendar, the view shows
a hint with "Create meal plan calendar"; the calendar's name does not matter.

- An entry is an ordinary event (the meal as category `Frühstück`/`Mittagessen`/`Abendessen`, with the time 8/12/18 o'clock only
  for the benefit of other calendar programs). **Free text:** the title. **Recipe:** additionally `X-DM-RECIPE` = the `sync_id` of
  the note; the title stays the recipe name, so other CalDAV programs show a legible event.
- **Recipes carry the tag `recipe` in notizi** (template "Recipe", recipe grabber; compared case-insensitively). The tag arrives
  in the sync format as `tags` on the note item (notizi `docs/konzept-sync.md`, section 4) and is stored in `notes.tags`
  (migration 17, NULL = a note from a device that predates tags). If the field is missing from the item, existing tags are left
  untouched; an empty list clears them; when writing back, `tags` goes along as it was read. Older, untagged recipes are still
  recognized by `Notes/Recipes.cs` from the shape of the template ("Ingredients"/"Preparation", "Recipe from …", a key-facts line).
  The folder name does not matter (`GET /api/notes/recipes`). The picker in the dialog searches these; the book icon on an entry
  opens the note. The bundled core library `vendor/Notizi.Core` has the tag lines for this from notizi's working copy (see
  `HERKUNFT.txt`) and is to be replaced by the published version when notizi 1.5.1 is released.
- Without write permission (shared read-only) the plan can still be viewed. It reloads when you return to the window.

For later: put the ingredients of a weekly plan on the shopping list in one go; recipes from shared note folders (today only
your own notes).

## Open decisions

1. **Encryption** – see above: the recommendation is to keep "the server opens the owner's database" (the same model as shared
   task lists, no additional key exchange).
2. **A "may only create events" permission** (without changing other people's) does not exist; recommendation: do not build it,
   a family does not need it.
3. **Android editor:** hide read-only calendars as a target there – this requires `calendars.readOnly` in Room (schema migration,
   reading `current-user-privilege-set`). Recommendation: a small step of its own, together with an app release.
