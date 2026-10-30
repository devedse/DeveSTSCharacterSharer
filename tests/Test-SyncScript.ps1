<#
Runs the real sts-character-sync.cmd on Windows against a locally started server: first download,
nothing changed, a character update, a locked folder (game running) and the script updating itself.
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
New-Item -ItemType Directory -Force $mods, (Join-Path $work 'data') | Out-Null

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAILED: $message" }
    Write-Host "ok: $message" -ForegroundColor Green
}

function Get-MatchCount([string]$text, [string]$pattern) { [regex]::Matches($text, $pattern, 'IgnoreCase').Count }

function Invoke-Sync([string]$arguments = '') {
    Push-Location $mods
    try {
        $output = cmd /c "sts-character-sync.cmd nopause $arguments" 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    Write-Host $output
    [pscustomobject]@{ Output = $output; ExitCode = $exitCode }
}

function Send-Character([string]$archive) {
    curl.exe -sSf -T $archive "$server/api/upload/$(Split-Path -Leaf $archive)" | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "upload of $archive failed" }
}

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

    Write-Host "`n=== second run downloads nothing"
    $run = Invoke-Sync
    Assert ($run.ExitCode -eq 0) 'exit code 0'
    Assert ($run.Output -match 'up to date\s+TheJester') 'TheJester is up to date'

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
    Assert ((Get-JesterVersion) -eq '1.1.0') 'the old version is untouched'
    Assert (Test-Path (Join-Path $mods 'TheJester/TheJester.pck')) 'no files went missing'
    $run = Invoke-Sync
    Assert ($run.ExitCode -eq 0 -and (Get-JesterVersion) -eq '1.0.0') 'once the game is closed it updates'

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

    Write-Host "`nAll sync script checks passed." -ForegroundColor Green
}
finally {
    Stop-Process $serverProcess -Force -ErrorAction SilentlyContinue
    Get-Content (Join-Path $work 'server-errors.log') -ErrorAction SilentlyContinue | Write-Host
}
