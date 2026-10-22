# Third-party components

[Deutsch](THIRD-PARTY-NOTICES.de.md) · **English**

dmOffice uses the following libraries. They are released under free licenses that allow their use in dmOffice; each requires that the license notice is shipped along – this file does that. The full license texts are in the packages themselves (npm, NuGet) and at the addresses given.

## Web interface (web/)

| Component | Version | License | Source |
|---|---|---|---|
| Fluent UI React (`@fluentui/react-components`) | 9.74.9 | MIT | https://github.com/microsoft/fluentui |
| Fluent UI System Icons (`@fluentui/react-icons`) | 2.0.344 | MIT | https://github.com/microsoft/fluentui-system-icons |
| FullCalendar (`@fullcalendar/*`) | 6.1.22 | MIT | https://github.com/fullcalendar/fullcalendar |
| React, React DOM | 18.3.1 | MIT | https://github.com/facebook/react |
| node-qrcode (`qrcode`) | 1.5.4 | MIT | https://github.com/soldair/node-qrcode |

The icons are the Fluent UI System Icons published by Microsoft under the MIT license, expressly released for free use. Microsoft product names and logos are trademarks of Microsoft and are not used by dmOffice.

## Server (DmOffice.Server/)

| Component | Version | License | Source |
|---|---|---|---|
| MailKit, MimeKit | 4.18.1 | MIT | https://github.com/jstedfast/MailKit |
| HtmlSanitizer | 9.2.1039 | MIT | https://github.com/mganss/HtmlSanitizer |
| AngleSharp (via HtmlSanitizer) | 1.x | MIT | https://github.com/AngleSharp/AngleSharp |
| Ical.Net | 4.3.1 | MIT | https://github.com/ical-org/ical.net |
| NodaTime (via Ical.Net) | 3.x | Apache-2.0 | https://github.com/nodatime/nodatime |
| Microsoft.Data.Sqlite | 10.0 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (`bundle_e_sqlcipher`) | 2.1.11 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLCipher Community Edition (in SQLitePCLRaw) | – | BSD-style (Zetetic) | https://github.com/sqlcipher/sqlcipher |
| SQLite | – | public domain | https://sqlite.org/copyright.html |
| ASP.NET Core / .NET, System.Security.Cryptography.Pkcs | 10 | MIT | https://github.com/dotnet |
| SkiaSharp (thumbnails) | 4.150.1 | MIT | https://github.com/mono/SkiaSharp |
| Skia (in SkiaSharp) | – | BSD-3-Clause | https://skia.org |
| PDFtoImage (first PDF page as thumbnail) | 5.4.0 | MIT | https://github.com/sungaila/PDFtoImage |
| PDFium (via `bblanchon.PDFium.*`) | 152 | BSD-3-Clause / Apache-2.0 | https://pdfium.googlesource.com/pdfium, https://github.com/bblanchon/pdfium-binaries |
| PdfPig (text from PDF, full-text search) | 0.1.16 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| Open XML SDK (`DocumentFormat.OpenXml`, text from Word/Excel/PowerPoint) | 3.5.1 | MIT | https://github.com/dotnet/Open-XML-SDK |

## Programs in the Docker image (text recognition)

For text recognition (OCR) in the full-text search, the Dockerfile installs programs from the package sources of the base image. dmOffice calls them as separate processes and does not link against them.

| Component | License | Source |
|---|---|---|
| Tesseract OCR (`tesseract-ocr`) with German/English language data (tessdata_fast) | Apache-2.0 | https://github.com/tesseract-ocr/tesseract |
| Leptonica (via Tesseract) | BSD-2-Clause-style | http://leptonica.org |
| Poppler (`poppler-utils`, pdftoppm) | GPL-2.0-or-later | https://poppler.freedesktop.org |
| libheif (`libheif-examples`, heif-dec) | LGPL-3.0 (library), MIT (example programs) | https://github.com/strukturag/libheif |

## Own components

`vendor/Notizi.Core` comes from the notizi project by the same author. Like dmOffice it is licensed under the AGPL-3.0.

## Compatibility with the AGPL-3.0

dmOffice itself is licensed under the GNU AGPL-3.0 (see `LICENSE`). All components listed above and the remaining bundled npm dependencies are under MIT, Apache-2.0, ISC, 0BSD or BSD-style licenses, or are in the public domain. These licenses allow redistribution as part of an AGPL-3.0 work; Apache-2.0 is expressly compatible with version 3 of the GPL/AGPL. The programs in the Docker image (Poppler under GPL-2.0-or-later, libheif under LGPL-3.0) are only called as separate processes and are shipped unchanged with their Debian/Ubuntu packages, including the license texts (`/usr/share/doc/<package>/copyright`).
