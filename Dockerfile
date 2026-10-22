# Dreistufig: Node baut die Oberflaeche, das SDK den Server, das Laufzeit-Image traegt nur das Ergebnis.
# Die Oberflaeche liegt als wwwroot im Server. Alles Weitere (Daten, Schluessel) unter /data.
FROM node:25-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# vite.config.ts schreibt nach ../DmOffice.Server/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY vendor/Notizi.Core/Notizi.Core.csproj vendor/Notizi.Core/
COPY DmOffice.Server/DmOffice.Server.csproj DmOffice.Server/
RUN dotnet restore DmOffice.Server/DmOffice.Server.csproj
COPY vendor/ vendor/
COPY DmOffice.Server/ DmOffice.Server/
COPY --from=web /DmOffice.Server/wwwroot DmOffice.Server/wwwroot
# ohne Debugsymbole: keine Dateipfade des Bau-Rechners im Image.
# Native Bibliotheken (SQLCipher, SkiaSharp, PDFium) kommen fuer alle Plattformen mit (~550 MB) - im Image nur
# glibc-Linux auf x64/arm64/arm, und keine Debugsymbole der Pakete (libSkiaSharp.pdb allein ~90 MB)
RUN dotnet publish DmOffice.Server/DmOffice.Server.csproj -c Release -o /app --no-restore -p:DebugType=none -p:DebugSymbols=false \
 && find /app/runtimes -mindepth 1 -maxdepth 1 ! -name linux-x64 ! -name linux-arm64 ! -name linux-arm -exec rm -rf {} + \
 && find /app -name '*.pdb' -delete

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Versionsnummer fuer die Anzeige (Ordnerbereich, Verwaltung) - beim Bauen gesetzt
ARG VERSION=dev
ENV DMOFFICE_VERSION=$VERSION
LABEL org.opencontainers.image.title="dmOffice" \
      org.opencontainers.image.source="https://github.com/Taloan/dmOffice" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.description="Mail, calendar, contacts, tasks, notes and files - with its own CalDAV/CardDAV/WebDAV server, encrypted per user."
# Volltextsuche mit Texterkennung (DmOffice.Server/Search/Ocr.cs), alles lokal: tesseract (Apache-2.0) mit
# Deutsch und Englisch (tessdata_fast), pdftoppm (Poppler, GPL) rendert gescannte PDF-Seiten, heif-dec (libheif,
# LGPL) liest HEIC-Fotos. Eigene Programme, als Prozess aufgerufen. Fehlen sie, ist nur der OCR-Schalter aus.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-deu tesseract-ocr-eng poppler-utils libheif-examples \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Lizenz (AGPL-3.0) und Lizenzhinweise der fremden Bestandteile
COPY LICENSE THIRD-PARTY-NOTICES.md ./
ENV ASPNETCORE_URLS=http://+:8080 \
    DMOFFICE_DATA=/data \
    TZ=Europe/Berlin
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "DmOffice.Server.dll"]
