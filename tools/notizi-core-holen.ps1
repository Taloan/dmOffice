# Holt Notizi.Core aus dem Nachbar-Checkout ..\notizi nach vendor\Notizi.Core.
# Das Docker-Image baut nur aus diesem Repo; darum liegt eine Kopie hier.
# Nach einer Aenderung am Abgleichformat in notizi: dieses Skript laufen lassen und committen.
param([string]$Notizi = (Join-Path $PSScriptRoot '..\..\notizi'))

$src = Join-Path $Notizi 'src\Notizi.Core'
$dst = Join-Path $PSScriptRoot '..\vendor\Notizi.Core'
if (-not (Test-Path $src)) { throw "Nicht gefunden: $src" }

if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item (Join-Path $src 'Notizi.Core.csproj') $dst
Copy-Item -Recurse (Join-Path $src 'Sync') $dst

$rev = git -C $Notizi rev-parse --short HEAD
Set-Content -Encoding utf8 (Join-Path $dst 'HERKUNFT.txt') "notizi $rev, geholt $(Get-Date -Format 'yyyy-MM-dd')"
Write-Host "Notizi.Core aus notizi $rev kopiert."
