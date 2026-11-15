# Catch-all aliases

[Deutsch](catchall-aliase.de.md) · English

Give every shop, club or forum its own email address, like SimpleLogin – without a third-party service. You need a domain
of your own whose host accepts all addresses (catch-all) and forwards them to a mailbox that dmOffice already fetches.
dmOffice is not a mail server: it only sorts what arrives in that mailbox. Each address you create is an **alias**.

## Setting up

1. At the domain's host, set up a **catch-all** for the domain (e.g. `example.com`) that forwards everything to your
   mailbox (e.g. `me@example.net`). Without the catch-all, new aliases do not reach you.
2. In dmOffice: *Settings* → *Email* → *Catch-all aliases*. Enter the domain (several separated by commas) and choose
   the **mailbox where the emails arrive**.
3. Choose what happens with **unknown addresses**, i.e. emails to an address at the domain that is not an alias:
   - *Accept, into the domain's folder* (default).
   - *Straight to junk*: only your aliases get through.
4. Choose what **Block** means: *Move to junk* or *Delete (to the trash)*.

## Folders

dmOffice files the emails into real IMAP folders of the mailbox, so other mail programs see the same order:

- one folder per domain, named after it (`example.com`) – emails to unknown addresses land here;
- below it one folder per alias (`example.com/shop`).

Unknown addresses do not get a folder of their own, otherwise spam to made-up addresses would create folders. If the
server uses `.` as folder separator, the dot in the name becomes `_` (`example_com`); `*` and `%` are never used.

In dmOffice these folders do not appear in the mailbox's normal folder list but in their own section **Catch-all aliases**
in the mail sidebar (like the categories): each domain with the number of emails to addresses without an alias, below it
each alias with its note in brackets and its unread count, blocked ones with a lock; above the email list stands the address of
the alias with its note, or the domain. Right-click (web) or long-press (app) on an alias: block/unblock, edit note, copy
address, delete.

Emails are filed when new emails are classified, after the spam filter and the rules: what the spam filter sends to junk
or a rule moves elsewhere stays there.

## New alias

- In the mail view: *Catch-all alias* in the ribbon (web), or the **@** icon at the top of the mail list (Android app),
  then **+**.
- Choose the name yourself or keep the random one (readable, e.g. `fuchs-7k2`: a short German word, a hyphen,
  digit-letter-digit, without characters that are easy to confuse). Allowed are `a–z`, digits, `.`, `-` and `_`.
- Add a note **what for** ("prize draw DIY store"). After creating, the alias is copied to the clipboard and its folder is
  created in the mailbox.

## Creating an alias from an email

An email to an address without an alias shows "To unknown address x@example.com · **Create as alias**" in the list and the
reading view; the same is in the email's context menu (web) and menu (app). *Settings* → *Email* → *Catch-all aliases* also
lists these addresses. dmOffice asks for the note (filled in with the sender's name), creates the alias and its folder and
moves **all** emails to this address there from the domain's folder and the inbox.

## Which address an email went to

When fetching, dmOffice assigns every email in the mailbox to its address – also after forwarding. It looks at, in this order:
`Delivered-To`, `X-Original-To`, `Envelope-To`, `X-Envelope-To`, `X-Forwarded-To`, then `To` and `Cc`, and last
`Received: … for <…>`. The first address at one of the domains wins. Hosts write the original recipient into different headers;
the envelope headers come first because `To` shows the mailing list or nothing at all for Bcc.

The mail list and the reading view show it in small type, with a button: "via shop-9a4@example.com · Block".

## Overview per alias

*Settings* → *Email* → *Catch-all aliases* lists every alias with its note, when it was created, how many emails came in
and when the last one arrived. A click shows the most frequent senders – so you see who passed an alias on. The Android app
shows the same list.

## Blocking

One click on **Block** blocks an alias, another unblocks it – in the settings, at the email, or from the sidebar. The menu in
the settings offers *Block – to junk* and *Block – delete* regardless of the default.

Blocked means: **new** emails to this alias are moved to junk or to the trash on arrival – before everything else, also for
senders in your address book (the alias is blocked precisely because it was passed on). "Why?" on such an email names the
reason ("went to the blocked catch-all alias …"). The alias's folder with the older emails stays and is marked with a lock.

## Timed removal per alias

In the alias's menu (settings, Android app): *Timed removal …* – *Move to trash after …* or *Destroy after …* a number of
days. It applies to **new** emails to this alias from their arrival, with the same timed removal as on a single email:
the email shows an hourglass and it can be undone there until then. Destroying also deletes on the mail server, without the
trash; copies elsewhere (other devices, the sender, the provider's backups) are out of dmOffice's reach.

## Replying with the alias

Optional, off by default: *Reply with the alias as sender*. When replying to an email that came in via an alias, the sender
is then that alias; a switch in the compose window can turn it off for the individual email. The Message-ID then also
carries the alias's domain, not that of your mailbox.

Only turn this on if the **SMTP server of the mailbox allows this sender** and **SPF and DKIM of the domain are set up
for that server**. Otherwise the server rejects the email or it ends up in the recipient's spam. dmOffice sends via the
mailbox's SMTP server and cannot check this itself.

## Notes

- The settings are per user. Domain and mailbox are set in the web interface; the app can create, block, adopt and copy.
- *Delete …* (in the alias's context menu in the sidebar, in the settings and in the app) moves the emails in the folder of
  the alias to the domain's folder, deletes the folder of the alias on the mail server and then the alias with its overview.
  New emails to the address land in the domain's folder (or in junk, as set). If moving fails, the alias stays.
- For the assignment, dmOffice stores per email only the Message-ID, the address, the sender and the time
  (table `mail_alias_hit` in the user database).
