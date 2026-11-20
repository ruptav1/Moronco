param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$taskRoot = Join-Path $PSScriptRoot ('artifacts\web-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskCases = @(
    @{ Name = 'website'; Input = 'example.com'; Url = '^https://example\.com/'; Title = 'Example Domain' },
    @{ Name = 'search'; Input = 'thời tiết Hà Nội'; Url = '^https://search\.brave\.com/search\?'; Title = '' },
    @{ Name = 'youtube'; Input = 'youtube.com'; Url = '^https://(www\.)?youtube\.com/'; Title = 'YouTube' }
)
$taskResults = @()
foreach ($taskCase in $taskCases) {
    $taskData = Join-Path $taskRoot $taskCase.Name
    $taskArgs = @('--smoke=browse', '--seconds=40', ('"--input=' + $taskCase.Input + '"'), ('"--data-dir=' + $taskData + '"'))
    $taskApp = Start-Process -FilePath (Join-Path $PSScriptRoot 'release\Moronco.exe') -ArgumentList $taskArgs -PassThru -WindowStyle Hidden
    if (-not $taskApp.WaitForExit(50000)) {
        Stop-Process -Id $taskApp.Id -ErrorAction SilentlyContinue
        throw "$($taskCase.Name) test timed out."
    }
    $taskApp.Refresh()
    $taskLogFile = Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -Filter "*-$($taskApp.Id).log" -ErrorAction SilentlyContinue
    $taskLog = if ($taskLogFile) { Get-Content -LiteralPath $taskLogFile.FullName -Raw } else { '' }
    if ($taskApp.ExitCode -ne 0 -or $taskLog -notmatch 'NAVIGATION success=True' -or $taskLog -match 'CONTENT_STOPPED|ENGINE_START_FAILED|FATAL|PROCESS_FAILED') {
        throw "$($taskCase.Name) failed (exit $($taskApp.ExitCode)). Log: $taskLog"
    }
    $taskProbeLine = @(($taskLog -split '\r?\n') | Where-Object { $_ -match ' PAGE_PROBE ' }) | Select-Object -Last 1
    if (-not $taskProbeLine) { throw 'No rendered DOM probe was recorded.' }
    $taskProbe = (($taskProbeLine -split ' PAGE_PROBE ', 2)[1] | ConvertFrom-Json) | ConvertFrom-Json
    if ($taskProbe.url -notmatch $taskCase.Url -or $taskProbe.ready -ne 'complete' -or $taskProbe.bodyLength -lt 30) {
        throw "Unexpected document for $($taskCase.Name): $($taskProbe | ConvertTo-Json -Compress)"
    }
    if ($taskCase.Title -and $taskProbe.title -notmatch $taskCase.Title) { throw 'Unexpected document title.' }
    if ($taskCase.Name -eq 'search' -and ($taskProbe.searchQuery -ne $taskCase.Input -or $taskProbe.linkCount -lt 2)) { throw 'Search results did not preserve the typed query.' }
    $taskResults += [pscustomobject]@{ Case = $taskCase.Name; Input = $taskCase.Input; Title = $taskProbe.title; Url = $taskProbe.url; BodyLength = $taskProbe.bodyLength; LinkCount = $taskProbe.linkCount; Log = $taskLogFile.FullName }
    Write-Output "PASS $($taskCase.Name): $($taskProbe.title) ($($taskProbe.bodyLength) text characters)"
}
$taskResults | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\browsing-validation.json') -Encoding UTF8
Write-Output 'PASS actual website, Vietnamese search results and YouTube homepage in the single-file browser. Media playback/calls were not tested.'
