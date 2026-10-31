<#
Runs the real sts-character-sync.cmd on Windows against a locally started server: first download,
nothing changed, a character update, a locked folder (game running), sorting the game's mod list and
the script updating itself.
Used by CI on a Windows runner; locally: dotnet publish the app, then
    .\tests\Test-SyncScript.ps1 -App <publish folder>
#>
param([Parameter(Mandatory)] [string]$App)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo 'tests/DeveSTSCharacterSharer.Tests/Fixtures/TheJester-1.0.7z'
$work = Join-Path ([IO.Path]::GetTempPath()) "sts-sync-test-$PID"
$mods = Join-Path $work 'mods'
$script = Join-Path $mods 'sts-character-sync.cmd'
$server = 'http://localhost:5280'
# The game's settings live in %APPDATA%; the script gets a fake one.
$appData = Join-Path $work 'appdata'
$saves = Join-Path $appData 'SlayTheSpire2\steam\76561190000000001'
$settings = Join-Path $saves 'settings.save'
$originalSettings = @'
{
  "fps_limit": 60,
  "mod_settings": {
    "mod_list": [
      {
        "id": "Zeta",
        "is_enabled": true,
        "source": "workshop"
      },
      {
        "id": "TheJester",
        "is_enabled": false,
        "source": "mods_directory"
      },
      {
        "id": "STS2-RitsuLib",
        "is_enabled": true,
        "source": "workshop"
      },
      {
        "id": "BaseLib",
        "is_enabled": true,
        "source": "workshop"
      }
    ],
    "play_with_mods": true
  },
  "volume": 0.5
}
'@
New-Item -ItemType Directory -Force $mods, (Join-Path $work 'data'), $saves | Out-Null
[IO.File]::WriteAllText($settings, $originalSettings)

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAILED: $message" }
    Write-Host "ok: $message" -ForegroundColor Green
}

function Get-MatchCount([string]$text, [string]$pattern) { [regex]::Matches($text, $pattern, 'IgnoreCase').Count }

function Invoke-Sync([string]$arguments = '') {
    Push-Location $mods
    $realAppData = $env:APPDATA
    try {
        $env:APPDATA = $appData
        $output = cmd /c "sts-character-sync.cmd nopause $arguments" 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:APPDATA = $realAppData
        Pop-Location
    }
    Write-Host $output
    [pscustomobject]@{ Output = $output; ExitCode = $exitCode }
}

function Send-Character([string]$archive) {
    curl.exe -sSf -T $archive "$server/api/upload/$(Split-Path -Leaf $archive)" | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "upload of $archive failed" }
}

function Get-ModIds { ((Get-Content $settings -Raw | ConvertFrom-Json).mod_settings.mod_list | ForEach-Object { $_.id }) -join ' ' }

function Test-SettingsUntouched { [IO.File]::ReadAllText($settings) -eq $originalSettings }

function Get-JesterVersion { (Get-Content (Join-Path $mods 'TheJester/TheJester.json') -Raw | ConvertFrom-Json).version }

$env:PUBLIC_URL = $server
$env:ASPNETCORE_URLS = $server
$env:DATA_DIR = Join-Path $work 'data'
$serverProcess = Start-Process dotnet -ArgumentList "`"$(Join-Path $App 'DeveSTSCharacterSharer.dll')`"" -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $work 'server.log') -RedirectStandardError (Join-Path $work 'server-errors.log')
try {
    for ($i = 0; ; $i++) {
        try { Invoke-WebRequest "$server/api/manifest.txt" -UseBasicParsing | Out-Null; break }
        catch { if ($i -ge 60) { throw 'server did not start' }; Start-Sleep 1 }
    }

    Send-Character $fixture
    curl.exe -sSf -o $script "$server/sts-character-sync.cmd"

    Write-Host "`n=== first run downloads everything"
    $run = Invoke-Sync
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ($run.Output -match 'downloading\s+TheJester') 'TheJester is downloaded'
    Assert (Test-Path (Join-Path $mods 'TheJester/TheJester.pck')) 'TheJester/ is unpacked into the mods folder'
    $hash = ((curl.exe -sSf "$server/api/manifest.txt") -split '\|')[0]
    Assert ((Get-Content (Join-Path $mods 'TheJester/.character-sync') -Force -Raw).Trim() -eq $hash) 'the marker holds the server hash'
    Assert ($run.Output -notmatch 'Updating this script') 'an up to date script does not update itself'
    Assert ($run.Output -match 'Mod order: sorted') 'it says it sorted the mod list'
    Assert ((Get-ModIds) -eq 'BaseLib STS2-RitsuLib TheJester Zeta') 'libraries first, then the rest A-Z'
    Assert ((Get-Content $settings -Raw) -match '"volume": 0.5') 'the rest of settings.save is untouched'
    Assert ([IO.File]::ReadAllText("$settings.bak") -eq $originalSettings) 'the old order is kept as settings.save.bak'

    Write-Host "`n=== second run downloads nothing"
    $run = Invoke-Sync
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ($run.Output -match 'up to date\s+TheJester') 'TheJester is up to date'
    Assert ($run.Output -match 'Mod order: already sorted') 'the mod list is already sorted'

    Write-Host "`n=== a new version replaces the old folder"
    $v2 = Join-Path $work 'v2'
    Copy-Item (Join-Path $mods 'TheJester') (Join-Path $v2 'TheJester') -Recurse
    $json = Join-Path $v2 'TheJester/TheJester.json'
    (Get-Content $json -Raw) -replace '1\.0\.0', '1.1.0' | Set-Content $json -NoNewline
    Compress-Archive -Path (Join-Path $v2 'TheJester') -DestinationPath (Join-Path $work 'TheJester-1.1.zip')
    Send-Character (Join-Path $work 'TheJester-1.1.zip')
    $run = Invoke-Sync
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ($run.Output -match 'downloading\s+TheJester') 'the new version is downloaded'
    Assert ((Get-JesterVersion) -eq '1.1.0') 'TheJester is now 1.1.0'
    Assert (-not (Test-Path (Join-Path $mods 'TheJester.sync-old'))) 'no old folder is left behind'

    Write-Host "`n=== a folder in use (game running) is left intact"
    Send-Character $fixture
    $lock = [IO.File]::Open((Join-Path $mods 'TheJester/TheJester.pck'), 'Open', 'Read', 'None')
    try { $run = Invoke-Sync }
    finally { $lock.Dispose() }
    Assert ($run.ExitCode -ne 0) 'a failed character gives a non-zero exit code'
    Assert ($run.Output -match 'Is the game still running') 'it says why'
    Assert ($run.Output -match 'Mod order: not sorted, because not every character could be updated') 'the mod list is left alone after a failure'
    Assert ((Get-JesterVersion) -eq '1.1.0') 'the old version is untouched'
    Assert (Test-Path (Join-Path $mods 'TheJester/TheJester.pck')) 'no files went missing'
    $run = Invoke-Sync
    Assert ($run.ExitCode -eq 0 -and (Get-JesterVersion) -eq '1.0.0') 'once the game is closed it updates'

    Write-Host "`n=== nosort leaves the mod list alone"
    [IO.File]::WriteAllText($settings, $originalSettings)
    $run = Invoke-Sync 'nosort'
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ($run.Output -notmatch 'Mod order') 'nothing about the mod order'
    Assert (Test-SettingsUntouched) 'settings.save is untouched'

    Write-Host "`n=== the mod list isn't sorted while the game is running"
    $game = Join-Path $work 'SlayTheSpire2.exe'
    Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $game
    $gameProcess = Start-Process $game -ArgumentList '-n 60 127.0.0.1' -PassThru -WindowStyle Hidden
    try { $run = Invoke-Sync }
    finally { Stop-Process $gameProcess -Force -ErrorAction SilentlyContinue }
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ($run.Output -match 'not sorted while Slay the Spire 2 is running') 'it says why'
    Assert (Test-SettingsUntouched) 'settings.save is untouched'

    Write-Host "`n=== an older script updates itself, then syncs with the new version"
    $latest = [IO.File]::ReadAllBytes($script)
    # An "older version" of a different length, so continuing to read the overwritten file at
    # the old offset (what the restart trick avoids) would visibly run broken lines.
    $older = (Get-Content $script -Raw) -replace '(?m)^(set "SELF=)', "rem an older version`r`nrem with a few more lines`r`nrem than the current one`r`n`$1"
    [IO.File]::WriteAllText($script, $older)
    $run = Invoke-Sync 'force'
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ((Get-MatchCount $run.Output 'Updating this script') -eq 1) 'it updates itself exactly once'
    Assert ([Convert]::ToBase64String($latest) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($script))) 'the script is now the latest version'
    Assert ((Get-MatchCount $run.Output 'Done:') -eq 1) 'the sync ran exactly once, in the new version'
    Assert ($run.Output -match 'downloading\s+TheJester') 'arguments (force) are passed on to the new version'
    Assert ($run.Output -notmatch 'is not recognized|was unexpected') 'no broken lines were run'
    Assert ((Get-ModIds) -eq 'BaseLib STS2-RitsuLib TheJester Zeta') 'the mod list is sorted again'

    Write-Host "`nAll sync script checks passed." -ForegroundColor Green
}
finally {
    Stop-Process $serverProcess -Force -ErrorAction SilentlyContinue
    Get-Content (Join-Path $work 'server-errors.log') -ErrorAction SilentlyContinue | Write-Host
}
