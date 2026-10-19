# Hilfen fuer Android-Screenshots (Blog-Bilder docs/img/blog/android-*.png) im Emulator.
# Ablauf:
#   1. tools/docs/serve-blog.sh            (Server mit Beispieldaten auf Port 5097, laeuft weiter)
#   2. Emulator starten, z. B. emulator -avd notizi-shots -port 5560
#   3. . tools/docs/android-shots.ps1; Setup-Emulator; Install-App; Pair-App
#   4. Bildschirme ansteuern (Tap/Swipe/Key) und mit Shot <name> speichern
# Gilt immer nur fuer den Emulator ($Device), nie fuer ein angeschlossenes Handy.
param([string]$Device = 'emulator-5560', [int]$Port = 5097)

$script:Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
$script:Dev = $Device
$script:Root = (Resolve-Path "$PSScriptRoot\..\..").Path
$script:Pkg = 'at.dimes.dmoffice.debug'

function A { & $script:Adb -s $script:Dev @args }

function Setup-Emulator {
  A shell settings put system time_12_24 24
  A shell cmd uimode night no
  A shell service call alarm 3 s16 Europe/Berlin | Out-Null
  # Uhrzeit des PCs uebernehmen und in der Statusleiste festhalten (Beispielmails sind relativ zu jetzt);
  # braucht ein Emulator-Image mit adb root. adb root startet adbd neu, daher danach adb reverse.
  A root | Out-Null; Start-Sleep 3
  A shell settings put global auto_time 0
  $now = Get-Date
  A shell date ($now.ToString('MMddHHmmyyyy.ss')) | Out-Null
  A reverse "tcp:$Port" "tcp:$Port" | Out-Null
  A shell settings put global sysui_demo_allowed 1
  A shell am broadcast -a com.android.systemui.demo -e command enter | Out-Null
  A shell am broadcast -a com.android.systemui.demo -e command clock -e hhmm $($now.ToString('HHmm')) | Out-Null
  A shell am broadcast -a com.android.systemui.demo -e command battery -e level 100 -e plugged false | Out-Null
  A shell am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4 -e fully true | Out-Null
  A shell am broadcast -a com.android.systemui.demo -e command network -e mobile show -e datatype none -e level 4 -e fully true | Out-Null
  A shell am broadcast -a com.android.systemui.demo -e command notifications -e visible false | Out-Null
}

function Install-App {
  $env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot'
  Push-Location "$script:Root\apps\mobile"; .\gradlew.bat :app:assembleDebug -q; Pop-Location
  A install -r "$script:Root\apps\mobile\app\build\outputs\apk\debug\app-debug.apk"
}

function Pair-App {
  $pass = (Get-Content "$script:Root\tools\docs\.data-serve\pass.txt").Trim()
  node "$script:Root\tools\docs\android-pair.mjs" "http://127.0.0.1:$Port" $pass
  A reverse "tcp:$Port" "tcp:$Port"
  A shell pm clear $script:Pkg | Out-Null
  A shell pm grant $script:Pkg android.permission.POST_NOTIFICATIONS
  $link = Get-Content "$script:Root\tools\docs\.data-serve\pair.txt"
  A shell am start -a android.intent.action.VIEW -d "'$link'" | Out-Null
}

# Am Ende: Uhrzeit wieder automatisch, Demo-Modus aus
function Reset-Emulator {
  A shell settings put global auto_time 1
  A shell am broadcast -a com.android.systemui.demo -e command exit | Out-Null
}

function Tap([int]$x, [int]$y) { A shell input tap $x $y; Start-Sleep -Milliseconds 900 }
function Swipe([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$ms = 400) { A shell input swipe $x1 $y1 $x2 $y2 $ms; Start-Sleep -Milliseconds 900 }
function Key([string]$k) { A shell input keyevent $k; Start-Sleep -Milliseconds 700 }

# Bildschirmfoto: ohne Namen in den Arbeitsordner (zum Ansehen), mit Namen nach docs/img/blog/android-<name>.png
function Shot([string]$name = '', [string]$tmp = "$env:TEMP\android-shot.png") {
  $out = if ($name) { "$script:Root\docs\img\blog\android-$name.png" } else { $tmp }
  cmd /c "`"$script:Adb`" -s $script:Dev exec-out screencap -p > `"$out`""
  $out
}
