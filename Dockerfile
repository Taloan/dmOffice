# Dreistufig: Node baut die Oberflaeche, das SDK den Server, das Laufzeit-Image traegt nur das Ergebnis.
# Die Oberflaeche liegt als wwwroot im Server. Alles Weitere (Daten, Schluessel) unter /data.
FROM node:22-alpine AS web
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
# ohne Debugsymbole: keine Dateipfade des Bau-Rechners im Image
RUN dotnet publish DmOffice.Server/DmOffice.Server.csproj -c Release -o /app --no-restore -p:DebugType=none -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Versionsnummer fuer die Anzeige (Ordnerbereich, Verwaltung) - beim Bauen gesetzt
ARG VERSION=dev
ENV DMOFFICE_VERSION=$VERSION
LABEL org.opencontainers.image.title="dmOffice" \
      org.opencontainers.image.source="https://github.com/Taloan/dmOffice" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.description="Mail, Kalender, Kontakte, Aufgaben, Notizen und Dateien - mit eigenem CalDAV/CardDAV/WebDAV, verschluesselt je Benutzer."
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
