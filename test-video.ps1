param([switch]$SkipBuild, [int]$Seconds = 120, [int]$Rounds = 2)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if ($Seconds -lt 75 -or $Seconds -gt 120) { throw 'Seconds must be 75..120 to observe the 40..60 second main-content segment.' }
if ($Rounds -lt 1 -or $Rounds -gt 4) { throw 'Rounds must be 1..4.' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$taskRoot = Join-Path $PSScriptRoot ('artifacts\video-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskVideoUrl = 'https://www.youtube.com/watch?v=YQHsXMglC9A&pp=ygUFaGVsbG8%3D'
$taskResults = @()
. (Join-Path $PSScriptRoot 'tests\Read-VideoRun.ps1')
for ($taskRound = 1; $taskRound -le $Rounds; $taskRound++) {
    $taskModes = if ($taskRound % 2) { @('normal', 'images-reduced') } else { @('images-reduced', 'normal') }
    foreach ($taskMode in $taskModes) {
        $taskData = Join-Path $taskRoot "round-$taskRound-$taskMode"
        $taskArgs = @('--smoke=video', "--seconds=$Seconds", ('"--url=' + $taskVideoUrl + '"'), ('"--data-dir=' + $taskData + '"'))
        if ($taskMode -eq 'images-reduced') { $taskArgs += '--youtube-lite' }
        $taskApp = Start-Process -FilePath (Join-Path $PSScriptRoot 'release\Moronco.exe') -ArgumentList $taskArgs -PassThru -WindowStyle Hidden
        Write-Output "RUN round=$taskRound mode=$taskMode PID=$($taskApp.Id) data=$taskData (hidden, muted diagnostics; not visible-user RAM)"
        if (-not $taskApp.WaitForExit(($Seconds + 15) * 1000)) {
            Stop-Process -Id $taskApp.Id -ErrorAction SilentlyContinue
            throw 'Video diagnostic timed out.'
        }
        $taskApp.Refresh()
        $taskLogFile = Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -Filter "*-$($taskApp.Id).log" | Select-Object -First 1
        if (-not $taskLogFile) { throw "No diagnostic log: $taskData" }
        if ($taskApp.ExitCode -ne 0) {
            throw "Diagnostic failed, exit $($taskApp.ExitCode); inspect $($taskLogFile.FullName)"
        }
        $taskResult = Read-VideoRun -LogPath $taskLogFile.FullName -Round $taskRound -Mode $taskMode
        $taskResults += $taskResult
        $taskResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json') -Encoding UTF8
        Write-Output ($taskResult | ConvertTo-Json -Compress)
    }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'results.json') -Destination (Join-Path $PSScriptRoot 'artifacts\video-validation.json') -Force
Write-Output "PASS playback and scoped image interception. Compare resolution and visibility before interpreting memory. Results: $taskRoot"
