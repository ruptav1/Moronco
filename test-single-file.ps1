param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$taskId = [Guid]::NewGuid().ToString('N')
$taskRoot = Join-Path $PSScriptRoot "artifacts\single exe thử $taskId"
$taskPortable = Join-Path $taskRoot 'portable'
$taskData = Join-Path $taskRoot 'app data'
New-Item -ItemType Directory -Path $taskPortable -Force | Out-Null
$taskExe = Join-Path $taskPortable 'Moronco.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\Moronco.exe') -Destination $taskExe

function Invoke-BundleSmoke([string]$Mode, [string]$Data) {
    $taskArguments = @("--smoke=$Mode", '--seconds=8', ('"--data-dir=' + $Data + '"'))
    $taskRunning = Start-Process -FilePath $taskExe -WorkingDirectory $taskPortable -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
    if (-not $taskRunning.WaitForExit(25000)) {
        Stop-Process -Id $taskRunning.Id -ErrorAction SilentlyContinue
        throw 'Bundled app smoke timed out.'
    }
    $taskRunning.Refresh()
    if ($taskRunning.ExitCode -ne 0) { throw "Bundle $Mode failed with exit $($taskRunning.ExitCode)." }
    $taskLog = Get-ChildItem -LiteralPath (Join-Path $Data 'logs') -Filter "*-$($taskRunning.Id).log"
    if (@($taskLog).Count -ne 1) { throw 'Expected one log for this process.' }
    $taskText = Get-Content -LiteralPath $taskLog.FullName -Raw
    if ($taskText -notmatch ' EXIT' -or $taskText -match 'FATAL|BUDGET_SETUP_FAILED|ENGINE_OUTSIDE_BUDGET|SAMPLE_FAILED|ENGINE_START_FAILED|DllNotFoundException|BadImageFormatException|FileNotFoundException|FileLoadException') {
        throw "Invalid bundled $Mode run: $taskText"
    }
    if ($taskText -notmatch 'memory_limit=off') { throw 'Default browser unexpectedly has a memory limit.' }
    if ($Mode -eq 'blank' -and ($taskText -notmatch ' RUNTIME ' -or $taskText -notmatch 'NAVIGATION success=True' -or $taskText -match 'CONTENT_STOPPED')) {
        throw 'Blank page did not finish navigation successfully.'
    }
    Write-Output "PASS bundle $Mode startup/exit, pid=$($taskRunning.Id)"
}

Invoke-BundleSmoke 'native' $taskData
$taskCache = @(Get-ChildItem -LiteralPath (Join-Path $taskData 'runtime') -Directory)
if ($taskCache.Count -ne 1) { throw 'Expected one versioned payload cache.' }
$taskSdk = Join-Path $PSScriptRoot '.deps\quarantine\webview2-1.0.4258.31'
$taskPayload = @{
    'Microsoft.Web.WebView2.Core.dll' = 'lib\net462\Microsoft.Web.WebView2.Core.dll'
    'WebView2Loader.dll' = 'runtimes\win-x64\native\WebView2Loader.dll'
    'WebView2-LICENSE.txt' = 'LICENSE.txt'
    'WebView2-NOTICE.txt' = 'NOTICE.txt'
}
foreach ($taskFile in $taskPayload.Keys) {
    $taskActual = Get-FileHash -LiteralPath (Join-Path $taskCache[0].FullName $taskFile) -Algorithm SHA256
    $taskExpected = Get-FileHash -LiteralPath (Join-Path $taskSdk $taskPayload[$taskFile]) -Algorithm SHA256
    if ($taskActual.Hash -ne $taskExpected.Hash) { throw "Extracted hash mismatch: $taskFile" }
}
Write-Output 'PASS all four embedded files match vetted originals'
$taskCoreFile = Join-Path $taskCache[0].FullName 'Microsoft.Web.WebView2.Core.dll'
$taskBefore = (Get-Item -LiteralPath $taskCoreFile).LastWriteTimeUtc
Invoke-BundleSmoke 'blank' $taskData
if ((Get-Item -LiteralPath $taskCoreFile).LastWriteTimeUtc -ne $taskBefore) { throw 'Warm start unexpectedly rewrote the verified managed DLL.' }
Write-Output 'PASS warm cache reuse and WebView2 native loader'

# Two simultaneous first launches share a new cache and must both publish safely.
$taskConcurrentData = Join-Path $taskRoot 'concurrent data'
$taskJobs = @()
foreach ($taskIndex in 1..2) {
    $taskArgs = @('--smoke=native', '--seconds=5', ('"--data-dir=' + $taskConcurrentData + '"'))
    $taskJobs += Start-Process -FilePath $taskExe -WorkingDirectory $taskPortable -ArgumentList $taskArgs -WindowStyle Hidden -PassThru
}
foreach ($taskJob in $taskJobs) {
    if (-not $taskJob.WaitForExit(20000)) {
        foreach ($taskOwned in $taskJobs) { if (-not $taskOwned.HasExited) { Stop-Process -Id $taskOwned.Id -ErrorAction SilentlyContinue } }
        throw 'Concurrent extraction smoke timed out.'
    }
    $taskJob.Refresh()
    if ($taskJob.ExitCode -ne 0) { throw "Concurrent extraction failed: $($taskJob.ExitCode)" }
}
if (@(Get-ChildItem -LiteralPath $taskPortable -File).Count -ne 1) { throw 'Standalone folder has unexpected sidecar files.' }
Write-Output 'PASS concurrent extraction; portable folder still contains only the EXE'
$taskReport = @(
    'Single-file validation PASS',
    ('Executable: ' + $taskExe),
    ('SHA256: ' + (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash),
    'Verified: EXE-only folder with spaces/non-ASCII; cold/native startup; payload hashes; warm/blank engine startup; concurrent cache extraction; clean exits.',
    'Blank page navigation succeeded with RAM enforcement disabled. This is not a YouTube/Discord call or performance benchmark.',
    ('Evidence directory: ' + $taskRoot)
)
$taskReport | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\single-file-validation.txt') -Encoding UTF8
$taskReport | Write-Output
