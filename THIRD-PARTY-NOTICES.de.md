# Fremde Bestandteile

**Deutsch** · [English](THIRD-PARTY-NOTICES.md)

dmOffice verwendet die folgenden Bibliotheken. Sie stehen unter freien Lizenzen, die eine Verwendung
in dmOffice erlauben; verlangt wird jeweils, dass der Lizenzhinweis mitgeliefert wird – das tut diese
Datei. Die vollständigen Lizenztexte liegen in den Paketen selbst (npm, NuGet) und unter den genannten
Adressen.

## Oberfläche (web/)

| Bestandteil | Version | Lizenz | Quelle |
|---|---|---|---|
| Fluent UI React (`@fluentui/react-components`) | 9.74.9 | MIT | https://github.com/microsoft/fluentui |
| Fluent UI System Icons (`@fluentui/react-icons`) | 2.0.344 | MIT | https://github.com/microsoft/fluentui-system-icons |
| FullCalendar (`@fullcalendar/*`) | 6.1.22 | MIT | https://github.com/fullcalendar/fullcalendar |
| React, React DOM | 18.3.1 | MIT | https://github.com/facebook/react |
| node-qrcode (`qrcode`) | 1.5.4 | MIT | https://github.com/soldair/node-qrcode |

Die Symbole sind die von Microsoft unter MIT veröffentlichten Fluent UI System Icons, ausdrücklich zur freien Verwendung freigegeben. Namen und Logos von Microsoft-Produkten sind Marken von Microsoft und werden von dmOffice nicht verwendet.

## Server (DmOffice.Server/)

| Bestandteil | Version | Lizenz | Quelle |
|---|---|---|---|
| MailKit, MimeKit | 4.18.1 | MIT | https://github.com/jstedfast/MailKit |
| HtmlSanitizer | 9.2.1039 | MIT | https://github.com/mganss/HtmlSanitizer |
| AngleSharp (über HtmlSanitizer) | 1.x | MIT | https://github.com/AngleSharp/AngleSharp |
| Ical.Net | 4.3.1 | MIT | https://github.com/ical-org/ical.net |
| NodaTime (über Ical.Net) | 3.x | Apache-2.0 | https://github.com/nodatime/nodatime |
| Microsoft.Data.Sqlite | 10.0 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (`bundle_e_sqlcipher`) | 2.1.11 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLCipher Community Edition (in SQLitePCLRaw) | – | BSD-artig (Zetetic) | https://github.com/sqlcipher/sqlcipher |
| SQLite | – | gemeinfrei | https://sqlite.org/copyright.html |
| ASP.NET Core / .NET, System.Security.Cryptography.Pkcs | 10 | MIT | https://github.com/dotnet |
| SkiaSharp (Vorschaubilder) | 4.150.1 | MIT | https://github.com/mono/SkiaSharp |
| Skia (in SkiaSharp) | – | BSD-3-Clause | https://skia.org |
| PDFtoImage (erste PDF-Seite als Vorschaubild) | 5.4.0 | MIT | https://github.com/sungaila/PDFtoImage |
| PDFium (über `bblanchon.PDFium.*`) | 152 | BSD-3-Clause / Apache-2.0 | https://pdfium.googlesource.com/pdfium, https://github.com/bblanchon/pdfium-binaries |
| PdfPig (Text aus PDF, Volltextsuche) | 0.1.16 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| Open XML SDK (`DocumentFormat.OpenXml`, Text aus Word/Excel/PowerPoint) | 3.5.1 | MIT | https://github.com/dotnet/Open-XML-SDK |

## Programme im Docker-Image (Texterkennung)

Für die Texterkennung (OCR) der Volltextsuche installiert das Dockerfile Programme aus den Paketquellen des
Basis-Images. dmOffice ruft sie als eigenständige Prozesse auf und linkt nicht gegen sie.

| Bestandteil | Lizenz | Quelle |
|---|---|---|
| Tesseract OCR (`tesseract-ocr`) mit Sprachdaten Deutsch/Englisch (tessdata_fast) | Apache-2.0 | https://github.com/tesseract-ocr/tesseract |
| Leptonica (über Tesseract) | BSD-2-Clause-artig | http://leptonica.org |
| Poppler (`poppler-utils`, pdftoppm) | GPL-2.0-or-later | https://poppler.freedesktop.org |
| libheif (`libheif-examples`, heif-dec) | LGPL-3.0 (Bibliothek), MIT (Beispielprogramme) | https://github.com/strukturag/libheif |

## Eigene Bestandteile

`vendor/Notizi.Core` stammt aus dem Projekt notizi desselben Autors.
Es steht wie dmOffice unter der AGPL-3.0.

## Verträglichkeit mit der AGPL-3.0

dmOffice selbst steht unter der GNU AGPL-3.0 (siehe `LICENSE`). Alle oben genannten Bestandteile und die
übrigen mitgebauten npm-Abhängigkeiten stehen unter MIT, Apache-2.0, ISC, 0BSD, BSD-artigen Lizenzen oder sind
gemeinfrei. Diese Lizenzen erlauben die Weitergabe als Teil eines AGPL-3.0-Werks; Apache-2.0 ist mit Version 3
der GPL/AGPL ausdrücklich vereinbar. Die Programme im Docker-Image (Poppler unter GPL-2.0-or-later, libheif unter
LGPL-3.0) werden nur als eigene Prozesse aufgerufen und mit ihren Debian/Ubuntu-Paketen samt Lizenztexten
(`/usr/share/doc/<paket>/copyright`) unverändert mitgeliefert.
