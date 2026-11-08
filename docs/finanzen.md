# Finances

[Deutsch](finanzen.de.md) · English

Finances is intended for small businesses in Germany under section 19 UStG that calculate their profit with a cash-basis income statement (EÜR). It records income and expenses, creates quotes and invoices and links receipts from the DMS. It is not a bookkeeping system and does not replace tax advice.

Finances is not enabled in this version. On a test server, enable it with `DMOFFICE_FEATURES=+finance`, then under Modules in the administration.

## Income and expenses

An entry counts towards the day the money was paid. Each entry can link a receipt from the DMS, including from the vault, which locks the document. Amount, date, title and correspondent of the document fill the fields that are still empty; the date only while it has not been changed. For a receipt in the vault, the vault must be open when linking and viewing it. The entry itself and the annual overview remain available while the vault is closed.

An entry can be changed or deleted while its year is open; the log records this. **Correct** creates a reversing entry with the opposite amount. Entries created from a payment can only be corrected this way.

**Close year** is available under Overview for a past year and cannot be undone. After that, only reversing entries in an open year are possible.

## Quotes, orders and invoices

A document starts as a draft and receives its number when it is **issued**. Drafts do not use up a number. Once issued, it can no longer be changed or deleted; the database rejects this as well. The PDF stays in Finances, and a locked copy goes to the DMS according to the default profile. If the profile points to the closed vault, the copy appears there the next time it is opened, not locked yet.

- **Change a quote or order:** New version creates a draft from the document.
- **Order from a quote:** Create order creates a draft with the same items that can be revised. The issued order is the order confirmation with its own number.
- **Invoice from a quote or order:** Create invoice, or "From quote or order" under Invoices, creates a draft with the same items.
- **Partial invoice:** for an issued order, as a percentage of the order total or a fixed amount. All partial invoices together must not exceed the order total.
- **Final invoice:** contains the order items and deducts each issued partial invoice with number and date. Open drafts of partial invoices must be issued or deleted first. After that, no further partial invoices are possible for the order; a deducted partial invoice can only be cancelled after the final invoice is cancelled.
- **Services:** entered in advance under Services, added to a document with "Add service" and adjustable there. Later changes to a service do not affect existing documents.
- **Record payment:** creates the income entry. Several payments per invoice are possible.
- **Cancel:** creates a cancellation invoice with its own number. The invoice remains. If a payment is recorded, correct that entry first.

Every document carries the note on the exemption under section 19 UStG. The texts are a template, not legal advice. Documents are in German only.

## Templates

Under Templates you can design the look of documents yourself. A template has a header and footer, which appear on every page, and the content. Blocks are text, image, recipient, details list, items, total, note under section 19 UStG, line, spacing, page break and up to four columns side by side. Change the order by dragging or with the arrows. The preview shows the result exactly as it breaks in the PDF, including across several pages.

Texts use placeholders in curly brackets, such as `{firma}`, `{nummer}` or `{seite}`. Text in square brackets is omitted if a placeholder in it is empty: `[USt-IdNr.: {ust_id}]`.

Recipient, items with the columns service, quantity and amount, total and the note under section 19 UStG are required; a template cannot be saved without them. This check only covers the structure: it remains your responsibility that a designed document carries all legally required details, for example your name, address and tax number in your own text.

Changes to a template apply to new documents. An issued document keeps its PDF.

## Customers

A customer has a name, an address and a customer number, which dmOffice can assign. The customer number is fixed in the document. Changes to a customer apply to new documents only.

## Numbers

Pattern and next number are under Settings. The year in the pattern comes from the document date; each year starts at 1. When switching from another program, the next number can be set. It must not decrease.

After a core backup is restored, nothing can be issued until the next numbers are confirmed. An older state does not know numbers assigned later. Compare the numbers with your latest documents first, for example in the DMS or the data backup.

## Locked documents in the DMS

A locked document has a blue frame; in the vault it also has the green frame around it. Its content cannot be changed and it cannot be deleted. This also applies to folders above it and to WebDAV, Nextcloud clients and Office. Details such as title and tags can still be changed, and it can still be moved or renamed.

Lock a document manually, or automatically by linking it in Finances. The lock stays until it is deliberately removed. While Finances refers to the document, it cannot be removed.

Limits:

- **External folders:** The lock only applies within dmOffice. The files can be changed outside dmOffice.
- **Vault:** The lock only applies while the vault is open and within dmOffice. The encrypted files can be changed outside dmOffice.
- **Deleting an account:** Locked documents and issued documents are deleted with the account.
- **Deleting the vault:** Locked documents in the vault are deleted too. References from Finances then lead nowhere.

Locking is not a promise of audit-proof archiving.

## Backup and export

- **Core backup:** contains Finances in full.
- **Data backup:** stores issued documents as PDF under `Finanzen/`, plus entries per year, document list, customers and log as CSV.
- **Overview:** annual overview as PDF and entries as CSV. The annual overview is not the official tax form.

## Not included yet

Mapping to the EÜR tax form, export for WISO Steuer, fixed assets and other special cases, e-invoicing, app.
