param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$taskRoot = Join-Path $PSScriptRoot ('artifacts\av-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskPython = 'C:\Users\Admin\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
$taskExecutable = Join-Path $PSScriptRoot 'release\Moronco.exe'
$taskServer = $null
$taskEvidence = [ordered]@{
    status = 'running'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    executable = $taskExecutable
    executableSha256 = (Get-FileHash -LiteralPath $taskExecutable -Algorithm SHA256).Hash
    artifactRoot = $taskRoot
    checks = @()
}

function Invoke-AnimeSmoke([string]$Mode, [string]$Data, [int]$Seconds, [string]$Url = '') {
    New-Item -ItemType Directory -Path $Data -Force | Out-Null
    $taskArgs = @("--smoke=$Mode", "--seconds=$Seconds", ('"--data-dir=' + $Data + '"'))
    if ($Url) { $taskArgs += ('"--url=' + $Url + '"') }
    $taskApp = Start-Process -FilePath $taskExecutable -ArgumentList $taskArgs -PassThru -WindowStyle Hidden
    try {
        if (-not $taskApp.WaitForExit(($Seconds + 20) * 1000)) { throw "$Mode smoke timed out (PID $($taskApp.Id))." }
        $taskApp.Refresh()
        $taskLogs = @(Get-ChildItem -LiteralPath (Join-Path $Data 'logs') -Filter "*-$($taskApp.Id).log" -ErrorAction SilentlyContinue)
        if ($taskLogs.Count -ne 1) { throw "Cannot identify $Mode smoke log. Inspect $Data." }
        $taskLogText = Get-Content -LiteralPath $taskLogs[0].FullName -Raw
        if ($taskApp.ExitCode -ne 0 -or $taskLogText -notmatch ' EXIT' -or $taskLogText -match 'FATAL|SMOKE_FAILED|UI_FAILED|JOB_FAILED|TAB_FAILED|COMMAND_FAILED|STATE_FAILED|SAVE_FAILED|PROCESS_FAILED|ALREADY_RUNNING') {
            throw "$Mode smoke failed (exit $($taskApp.ExitCode)): $taskLogText"
        }
        return [pscustomobject]@{ Mode = $Mode; ExitCode = $taskApp.ExitCode; Log = $taskLogs[0].FullName; Text = $taskLogText }
    }
    finally {
        $taskApp.Refresh()
        if (-not $taskApp.HasExited) { Stop-Process -Id $taskApp.Id -ErrorAction SilentlyContinue }
        $taskApp.Dispose()
    }
}

function Get-AnimeSnapshot([string]$LogText, [string]$Marker = 'UI_PROBE') {
    $taskLine = @($LogText -split '\r?\n' | Where-Object { $_ -match (' ' + [Regex]::Escape($Marker) + ' ') }) | Select-Object -Last 1
    if (-not $taskLine) { throw "Missing $Marker in browser log." }
    $taskSnapshot = ($taskLine -split (' ' + [Regex]::Escape($Marker) + ' '), 2)[1] | ConvertFrom-Json
    if ($taskSnapshot -is [string]) { $taskSnapshot = $taskSnapshot | ConvertFrom-Json }
    if ($taskSnapshot.ready -ne $true -or $taskSnapshot.preview -ne $false -or $taskSnapshot.tabCount -lt 1) {
        throw "UI was not connected to the native host: $($taskSnapshot | ConvertTo-Json -Compress)"
    }
    return $taskSnapshot
}

try {
    $taskStoreProbe = Join-Path $taskRoot 'BrowserStoreProbe.exe'
    & $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /reference:System.Web.Extensions.dll "/out:$taskStoreProbe" 'src\BrowserStore.cs' 'tests\BrowserStoreProbe.cs'
    if ($LASTEXITCODE -ne 0) { throw 'Store probe compilation failed.' }
    & $taskStoreProbe $taskRoot | Tee-Object -FilePath (Join-Path $taskRoot 'store-probe.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Store probe failed.' }
    $taskEvidence.checks += [ordered]@{ name = 'store'; status = 'pass'; log = (Join-Path $taskRoot 'store-probe.txt') }
    $taskAddressProbe = Join-Path $taskRoot 'AddressInputProbe.exe'
    & $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$taskAddressProbe" 'src\AddressInput.cs' 'tests\AddressInputProbe.cs'
    if ($LASTEXITCODE -ne 0) { throw 'Address probe compilation failed.' }
    & $taskAddressProbe | Tee-Object -FilePath (Join-Path $taskRoot 'address-probe.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Address probe failed.' }
    $taskEvidence.checks += [ordered]@{ name = 'address'; status = 'pass'; log = (Join-Path $taskRoot 'address-probe.txt') }

    & $taskCompiler /nologo "/out:$taskRoot\ProtectionProbe.exe" (Join-Path $PSScriptRoot 'tests\ProtectionProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Protection probe compilation failed.' }
    & (Join-Path $taskRoot 'ProtectionProbe.exe') (Join-Path $PSScriptRoot 'dist\Moronco.exe')
    if ($LASTEXITCODE -ne 0) { throw 'WARP status parsing failed.' }
    $taskEvidence.checks += [ordered]@{ name = 'warp-state'; status = 'pass'; cases = 10 }

    & $taskCompiler /nologo /reference:System.Web.dll "/out:$taskRoot\SearchProbe.exe" (Join-Path $PSScriptRoot 'tests\SearchProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Search probe compilation failed.' }
    & (Join-Path $taskRoot 'SearchProbe.exe') (Join-Path $PSScriptRoot 'dist\Moronco.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Search URL validation failed.' }
    $taskEvidence.checks += [ordered]@{ name = 'search-routing'; status = 'pass' }

    & $taskCompiler /nologo "/out:$taskRoot\DeepSearchProbe.exe" (Join-Path $PSScriptRoot 'tests\DeepSearchProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Deep search probe compilation failed.' }
    & (Join-Path $taskRoot 'DeepSearchProbe.exe') (Join-Path $PSScriptRoot 'dist\Moronco.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Deep search query validation failed.' }
    $taskEvidence.checks += [ordered]@{ name = 'deep-query'; status = 'pass' }

    $taskSearch = Invoke-AnimeSmoke -Mode 'search' -Data (Join-Path $taskRoot 'search-profile') -Seconds 35
    if ($taskSearch.Text -notmatch 'SEARCH_UI_PASS') { throw 'Native dual search or Brave AI preference failed.' }
    $taskEvidence.checks += [ordered]@{ name = 'search-ui'; status = 'pass'; log = $taskSearch.Log }
    Write-Output 'PASS dual search from home and address bar, retained query and Brave AI preference.'

    $taskUiData = Join-Path $taskRoot 'ui-profile'
    $taskUi = Invoke-AnimeSmoke -Mode 'ui' -Data $taskUiData -Seconds 28
    $taskUiSnapshot = Get-AnimeSnapshot $taskUi.Text
    $taskSettingsSnapshot = Get-AnimeSnapshot $taskUi.Text 'UI_SETTINGS_PROBE'
    if ($taskUi.Text -notmatch 'PROTECTION_UI_PASS' -or $taskUi.Text -notmatch 'ADBLOCK_FILTER_PASS' -or $taskUi.Text -notmatch 'UI_ACTIONS_PASS' -or $taskSettingsSnapshot.theme -ne 'gothic' -or $taskSettingsSnapshot.layout -ne 'vertical') { throw 'Native UI actions or theme/layout updates failed.' }
    $taskState = Get-Content -LiteralPath (Join-Path $taskUiData 'browser-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskState.settings.theme -ne 'gothic' -or $taskState.settings.wallpaper -ne 'default' -or $taskState.settings.tabLayout -ne 'vertical' -or $taskState.settings.accent -ne '#ab344a') { throw 'UI appearance settings did not persist.' }
    if (@($taskState.bookmarks | Where-Object { $_.title -eq 'Example <safe> & test' -and $_.url -eq 'https://example.com/' }).Count -ne 1) { throw 'UI bookmark did not persist safely.' }
    if ($taskState.notes -ne "Ghi chú thử`nTiếng Việt <script> không chạy" -or @($taskState.sessionTabs).Count -lt 3) { throw 'Notes or at least three session tabs did not persist.' }
    $taskEvidence.checks += [ordered]@{ name = 'ui-actions'; status = 'pass'; log = $taskUi.Log; snapshot = $taskUiSnapshot; settingsSnapshot = $taskSettingsSnapshot; persistedTabs = @($taskState.sessionTabs).Count }
    Write-Output 'PASS native UI actions and persisted settings, bookmark, notes and tabs.'

    $taskRestore = Invoke-AnimeSmoke -Mode 'restore' -Data $taskUiData -Seconds 7
    $taskRestoreSnapshot = Get-AnimeSnapshot $taskRestore.Text
    if ($taskRestore.Text -notmatch 'UI_RESTORE_PASS' -or $taskRestoreSnapshot.tabCount -lt 3 -or $taskRestoreSnapshot.layout -ne 'vertical') { throw 'Session restore did not preserve UI state.' }
    $taskEvidence.checks += [ordered]@{ name = 'restore'; status = 'pass'; log = $taskRestore.Log; snapshot = $taskRestoreSnapshot }
    Write-Output 'PASS session restored in a second browser process.'

    if (-not (Test-Path -LiteralPath $taskPython -PathType Leaf)) { throw 'Bundled Python is unavailable for the local integration fixture.' }
    $taskPortFile = Join-Path $taskRoot 'fixture-port.txt'
    $taskFixtureScript = Join-Path $PSScriptRoot 'tests\browser_fixture.py'
    $taskServer = Start-Process -FilePath $taskPython -ArgumentList @('-u', ('"' + $taskFixtureScript + '"'), '--port-file', ('"' + $taskPortFile + '"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskRoot 'fixture.stdout.log') -RedirectStandardError (Join-Path $taskRoot 'fixture.stderr.log')
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $taskPortFile) -and [DateTime]::UtcNow -lt $taskDeadline) {
        $taskServer.Refresh()
        if ($taskServer.HasExited) { throw "Local fixture exited $($taskServer.ExitCode)." }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $taskPortFile)) { throw 'Local fixture did not publish its bound port.' }
    $taskPort = 0
    if (-not [int]::TryParse((Get-Content -LiteralPath $taskPortFile -Raw).Trim(), [ref]$taskPort) -or $taskPort -lt 1 -or $taskPort -gt 65535) { throw 'Invalid local fixture port.' }
    $taskFixtureUrl = "http://127.0.0.1:$taskPort/one"
    $taskIntegrationData = Join-Path $taskRoot 'integration-profile'
    $taskIntegration = Invoke-AnimeSmoke -Mode 'integration' -Data $taskIntegrationData -Seconds 35 -Url $taskFixtureUrl
    [void](Get-AnimeSnapshot $taskIntegration.Text)
    $taskDownloadPath = Join-Path $taskIntegrationData 'downloads\fixture.txt'
    if ($taskIntegration.Text -notmatch 'INTEGRATION_PASS' -or $taskIntegration.Text -notmatch 'POPUP_PASS' -or -not (Test-Path -LiteralPath $taskDownloadPath -PathType Leaf)) { throw 'Browser navigation, popup or download integration failed.' }
    $taskDownloadedBytes = [IO.File]::ReadAllBytes($taskDownloadPath)
    if ([Text.Encoding]::UTF8.GetString($taskDownloadedBytes) -cne "Minium download fixture`n") { throw 'Downloaded fixture contents did not match exactly.' }
    $taskIntegrationState = Get-Content -LiteralPath (Join-Path $taskIntegrationData 'browser-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($taskIntegrationState.history | Where-Object { $_.url -eq "http://127.0.0.1:$taskPort/two" }).Count -lt 1 -or @($taskIntegrationState.bookmarks | Where-Object { $_.title -eq 'Fixture bookmark' }).Count -ne 1) { throw 'Browser integration history or bookmark was not persisted.' }
    $taskEvidence.checks += [ordered]@{ name = 'browser-integration'; status = 'pass'; log = $taskIntegration.Log; download = $taskDownloadPath; downloadBytes = $taskDownloadedBytes.Length; fixture = $taskFixtureUrl }
    Write-Output 'PASS navigation, back/forward, popup/opener, download, zoom, find, fullscreen, bridge isolation, bookmarks and history.'
    $taskEvidence.status = 'pass'
}
catch {
    $taskEvidence.status = 'fail'
    $taskEvidence.error = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $taskServer) {
        $taskServer.Refresh()
        if (-not $taskServer.HasExited) { Stop-Process -Id $taskServer.Id -ErrorAction SilentlyContinue }
        $taskServer.Dispose()
    }
    $taskEvidence.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $taskEvidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskRoot 'validation.json') -Encoding UTF8
    Write-Output "Validation evidence: $taskRoot"
}
