# Dreistufig: Node baut die Oberflaeche, das SDK den Server, das Laufzeit-Image traegt nur das Ergebnis.
# Die Oberflaeche liegt als wwwroot im Server. Alles Weitere (Daten, Schluessel) unter /data.
FROM node:26-alpine@sha256:8996bb5821d62a8fd9bb27c1cb07462f3fdce6e47d640c6a881ead0b064ce216 AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# Liste der Open-Source-Bibliotheken (public/third-party.json): npm-Teil frisch aus node_modules, der Rest wie eingecheckt
COPY tools/lizenzen/ /tools/lizenzen/
# vite.config.ts schreibt nach ../DmOffice.Server/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
# Versionsnummer fuer die Anzeige (Ordnerbereich, Verwaltung) - beim Bauen gesetzt
ARG VERSION=dev
ENV DMOFFICE_VERSION=$VERSION
LABEL org.opencontainers.image.title="dmOffice" \
      org.opencontainers.image.source="https://github.com/Taloan/dmOffice" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.description="Mail, calendar, contacts, tasks, notes and files - with its own CalDAV/CardDAV/WebDAV server, encrypted per user."
# Volltextsuche mit Texterkennung (DmOffice.Server/Search/Ocr.cs), alles lokal: tesseract (Apache-2.0) mit
# Deutsch und Englisch (tessdata_fast), pdftoppm (Poppler, GPL) rendert gescannte PDF-Seiten, heif-dec (libheif,
# LGPL) liest HEIC-Fotos, Ghostscript (AGPL) macht die durchsuchbaren PDFs vom DMS zu PDF/A, DejaVu-Schriften setzen
# Mails als PDF. Eigene Programme, als Prozess aufgerufen. Fehlen sie, ist nur der OCR-Schalter aus (ohne Ghostscript: PDF ohne /A).
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-deu tesseract-ocr-eng poppler-utils libheif-examples ghostscript fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
# Photo-Engine (photo-engine/): RAW-Entwicklung, Bearbeitungsvorschläge, Gesichter. Eigener Python-Prozess, startet beim ersten Auftrag
# (DmOffice.Server/Dav/PhotoEngineProcess.cs). Pakete in fester Version, die Lizenzdateien bleiben in der venv.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-venv \
 && rm -rf /var/lib/apt/lists/* \
 && python3 -m venv /opt/photo-engine/venv
COPY photo-engine/requirements-docker.txt /opt/photo-engine/
RUN /opt/photo-engine/venv/bin/pip install --no-cache-dir --no-compile -r /opt/photo-engine/requirements-docker.txt \
 && /opt/photo-engine/venv/bin/pip uninstall -y pip
COPY photo-engine/engine /opt/photo-engine/engine
COPY photo-engine/models /opt/photo-engine/models
ENV PHOTO_ENGINE_HOME=/opt/photo-engine
WORKDIR /app
COPY --from=build /app .
# Lizenz (AGPL-3.0) und Lizenzhinweise der fremden Bestandteile
COPY LICENSE THIRD-PARTY-NOTICES.md ./
# Android-App desselben Stands (Version 2, vom Build-Ablauf abgelegt; sonst leer): /app/dmoffice.apk
COPY app-apk/ ./apk/
ENV ASPNETCORE_URLS=http://+:8080 \
    DMOFFICE_DATA=/data \
    TZ=Europe/Berlin
# Start ohne Root-Rechte (docker-entrypoint.sh): übergibt /data einmalig dem Benutzer app und startet dmOffice als dieser
COPY docker-entrypoint.sh /usr/local/bin/dmoffice-start
RUN sed -i 's/$//' /usr/local/bin/dmoffice-start && chmod 755 /usr/local/bin/dmoffice-start
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/dmoffice-start"]
