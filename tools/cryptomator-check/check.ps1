# Gegenprobe Cryptomator-Format (docs/tresor-cryptomator-konzept.md, Abschnitt 7) mit den Bibliotheken der installierten
# Cryptomator-App (Windows, C:\Program Files\Cryptomator). Übersetzt wird Check.java mit einem JDK ab 17; laufen muss es mit der
# Java-Laufzeit der App, weil deren Bibliotheken für eine neuere Fassung übersetzt sind. Die App hat dafür kein java.exe, nur
# ihren Starter: Er wird samt Einstellungsdatei in einen Arbeitsordner kopiert und auf Check.class gerichtet.
#   .\check.ps1 create <tresor> <passwort> <quelle>
#   .\check.ps1 dump <tresor> <passwort>
param([Parameter(Mandatory)][string]$Mode, [Parameter(Mandatory)][string]$Vault, [Parameter(Mandatory)][string]$Password, [string]$Source)

$ErrorActionPreference = 'Stop'
$app = if ($env:CRYPTOMATOR_HOME) { $env:CRYPTOMATOR_HOME } else { 'C:\Program Files\Cryptomator' }
$mods = Join-Path $app 'app\mods'
if (-not (Test-Path $mods)) { throw "Cryptomator nicht gefunden ($app) - CRYPTOMATOR_HOME setzen." }
$work = Join-Path ([IO.Path]::GetTempPath()) 'dmoffice-cryptomator-check'
$classes = Join-Path $work 'classes'
if (-not (Test-Path (Join-Path $work 'CmCheck.exe'))) {
    New-Item -ItemType Directory -Force (Join-Path $work 'app') | Out-Null
    Copy-Item (Join-Path $app 'Cryptomator (Debug).exe') (Join-Path $work 'CmCheck.exe')
    Copy-Item (Join-Path $app 'app\*.dll') (Join-Path $work 'app') -ErrorAction SilentlyContinue
    cmd /c mklink /J (Join-Path $work 'runtime') (Join-Path $app 'runtime') | Out-Null
}
$javac = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\javac.exe' } else { 'javac' }
& $javac --release 17 -encoding UTF-8 -d $classes (Join-Path $PSScriptRoot 'Check.java')
if ($LASTEXITCODE -ne 0) { throw 'Check.java ließ sich nicht übersetzen.' }
# Alle Bibliotheken der App bis auf JavaFX und die App selbst (cryptofs braucht u. a. dagger, caffeine, guava)
$jars = Get-ChildItem $mods -Filter *.jar | Where-Object { $_.Name -notmatch '^(javafx|cryptomator-|jfuse|integrations-(win|mac|linux)|webdav-nio|fuse-nio|dokany)' }
$cfg = @('[Application]', 'app.mainclass=Check', "app.classpath=$classes") + ($jars | ForEach-Object { "app.classpath=$($_.FullName)" }) +
    @('', '[JavaOptions]', 'java-options=-Dfile.encoding=utf-8', 'java-options=-Dstdout.encoding=utf-8')
[IO.File]::WriteAllLines((Join-Path $work 'app\CmCheck.cfg'), [string[]]$cfg, (New-Object Text.UTF8Encoding $false))
$argsList = @($Mode, $Vault, $Password)
if ($Source) { $argsList += $Source }
# Warnungen auf stderr (SLF4J) sind kein Fehler; entscheidend ist der Rückgabewert
$ErrorActionPreference = 'Continue'
& (Join-Path $work 'CmCheck.exe') @argsList 2>$null
if ($LASTEXITCODE -ne 0) { throw "Gegenprobe fehlgeschlagen ($LASTEXITCODE)." }
