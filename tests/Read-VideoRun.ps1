# Offline validation only: this helper reads existing logs/CSV and starts no
# browser. Use the same main-content segment in every run, regardless of ads.
function Get-VideoRunMedian {
    param([double[]]$Values)
    $taskSorted = @($Values | Sort-Object)
    if ($taskSorted.Count -eq 0) { throw 'Cannot calculate a median without samples.' }
    $taskMid = [int][Math]::Floor($taskSorted.Count / 2)
    if ($taskSorted.Count % 2) { return $taskSorted[$taskMid] }
    return ($taskSorted[$taskMid - 1] + $taskSorted[$taskMid]) / 2
}

function Read-VideoRun {
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][int]$Round,
        [Parameter(Mandatory = $true)][ValidateSet('normal', 'images-reduced')][string]$Mode
    )
    try {
        $taskLogPath = (Resolve-Path -LiteralPath $LogPath -ErrorAction Stop).Path
        $taskLog = Get-Content -LiteralPath $taskLogPath -Raw -ErrorAction Stop
        if ($taskLog -match 'FATAL|PROCESS_FAILED|ENGINE_START_FAILED|VIDEO_PROBE_FAILED|ENGINE_OUTSIDE_BUDGET|BUDGET_SETUP_FAILED') {
            throw 'The run contains an engine or diagnostic failure.'
        }
        if ($taskLog -notmatch '(?m) EXIT\r?$') { throw 'The run has no clean EXIT marker.' }
        $taskProbes = @(($taskLog -split '\r?\n') | Where-Object { $_ -match ' VIDEO_PROBE ' } | ForEach-Object {
            ($_ -split ' VIDEO_PROBE ', 2)[1] | ConvertFrom-Json -ErrorAction Stop
        })
        $taskPlaying = @($taskProbes | Where-Object {
            $_.video -and -not $_.ad -and $_.contentId -eq 'YQHsXMglC9A' -and -not $_.paused `
                -and $_.ready -ge 2 -and $_.width -gt 0 -and $_.height -gt 0 -and -not $_.error
        })

        $taskRequestedStart = 40.0
        $taskRequestedEnd = 60.0
        $taskFirstIndex = -1
        $taskLastIndex = -1
        for ($taskIndex = 0; $taskIndex -lt $taskProbes.Count; $taskIndex++) {
            $taskProbe = $taskProbes[$taskIndex]
            if ($taskProbe.video -and -not $taskProbe.ad -and $taskProbe.contentId -eq 'YQHsXMglC9A' `
                -and $null -ne $taskProbe.time -and $taskProbe.time -ge $taskRequestedStart -and $taskProbe.time -le $taskRequestedEnd) {
                if ($taskFirstIndex -lt 0) { $taskFirstIndex = $taskIndex }
                $taskLastIndex = $taskIndex
            }
        }
        if ($taskFirstIndex -lt 0) { throw 'Requested main content never reached the 40..60 second measurement segment.' }
        # Preserve every probe between the endpoints. Filtering out pauses, ads,
        # frame resets, or wrong content here would hide a broken measurement.
        $taskWindow = @($taskProbes[$taskFirstIndex..$taskLastIndex])
        if ($taskWindow.Count -lt 8) { throw 'Fewer than eight probes cover the 40..60 second content segment.' }
        $taskPrevious = $null
        foreach ($taskProbe in $taskWindow) {
            if (-not $taskProbe.video -or $taskProbe.ad -or $taskProbe.contentId -ne 'YQHsXMglC9A' `
                -or $taskProbe.paused -or $taskProbe.ready -lt 2 -or $taskProbe.width -le 0 -or $taskProbe.height -le 0 `
                -or $taskProbe.error -or $null -eq $taskProbe.time -or $null -eq $taskProbe.elapsed_s `
                -or $taskProbe.time -lt $taskRequestedStart -or $taskProbe.time -gt $taskRequestedEnd) {
                throw 'The content measurement segment contains an ad, pause, unavailable picture, or unrelated content.'
            }
            if ($null -eq $taskProbe.totalFrames -or $null -eq $taskProbe.droppedFrames `
                -or $taskProbe.totalFrames -lt $taskProbe.droppedFrames -or $taskProbe.droppedFrames -lt 0) {
                throw 'Video frame counters are unavailable or invalid in the measurement segment.'
            }
            if ($null -ne $taskPrevious) {
                $taskFrameStep = $taskProbe.totalFrames - $taskPrevious.totalFrames
                $taskDropStep = $taskProbe.droppedFrames - $taskPrevious.droppedFrames
                if ($taskProbe.time -le $taskPrevious.time -or $taskProbe.elapsed_s -le $taskPrevious.elapsed_s `
                    -or $taskDropStep -lt 0 -or $taskFrameStep - $taskDropStep -le 0) {
                    throw 'Content time and non-dropped video frames did not advance continuously in the measurement segment.'
                }
            }
            $taskPrevious = $taskProbe
        }
        $taskContentAdvanced = $taskWindow[-1].time - $taskWindow[0].time
        if ($taskContentAdvanced -lt 14) { throw 'Content time advanced less than 14 seconds inside the measurement segment.' }
        $taskFrameDelta = $taskWindow[-1].totalFrames - $taskWindow[0].totalFrames
        $taskDroppedDelta = $taskWindow[-1].droppedFrames - $taskWindow[0].droppedFrames
        if ($taskFrameDelta - $taskDroppedDelta -lt 100) { throw 'Fewer than 100 non-dropped video frames advanced inside the measurement segment.' }
        $taskResolutions = @($taskWindow | ForEach-Object { "$($_.width)x$($_.height)" } | Select-Object -Unique)
        if ($taskResolutions.Count -ne 1) { throw 'Resolution changed during the 40..60 second content segment.' }

        $taskCsvFile = [IO.Path]::ChangeExtension($taskLogPath, '.csv')
        $taskRows = @(Import-Csv -LiteralPath $taskCsvFile -ErrorAction Stop)
        $taskMemory = @()
        $taskMatchedIndices = @{}
        $taskMaximumGap = 0.0
        foreach ($taskProbe in $taskWindow) {
            $taskNearestIndex = -1
            $taskNearestGap = [double]::PositiveInfinity
            for ($taskIndex = 0; $taskIndex -lt $taskRows.Count; $taskIndex++) {
                $taskRow = $taskRows[$taskIndex]
                if ([string]::IsNullOrWhiteSpace($taskRow.elapsed_s)) { continue }
                $taskGap = [double]$taskProbe.elapsed_s - [double]$taskRow.elapsed_s
                # Sample() runs immediately before the asynchronous video probe.
                # Never associate a future memory row or silently skip a nearer
                # incomplete row to obtain a more favorable valid measurement.
                if ($taskGap -ge 0 -and $taskGap -le 1 -and $taskGap -lt $taskNearestGap) {
                    $taskNearestGap = $taskGap
                    $taskNearestIndex = $taskIndex
                }
            }
            if ($taskNearestIndex -lt 0) { throw 'A video probe has no preceding memory row within one second.' }
            $taskRow = $taskRows[$taskNearestIndex]
            if ($taskRow.complete -ne 'True' -or $taskRow.private_working_set_complete -ne 'True' `
                -or [string]::IsNullOrWhiteSpace($taskRow.private_working_set_bytes)) {
                throw 'A matched memory row has incomplete process accounting or unavailable private working set.'
            }
            if ([double]$taskRow.private_working_set_bytes -lt 0 -or [double]$taskRow.job_private_commit_bytes -lt 0 `
                -or [double]$taskRow.working_set_sum_including_shared_bytes -lt 0) {
                throw 'A matched memory row contains invalid negative accounting.'
            }
            if (-not $taskMatchedIndices.ContainsKey($taskNearestIndex)) {
                $taskMatchedIndices[$taskNearestIndex] = $true
                $taskMemory += $taskRow
            }
            $taskMaximumGap = [Math]::Max($taskMaximumGap, $taskNearestGap)
        }
        if ($taskMemory.Count -lt 8) { throw 'Fewer than eight unique complete memory rows match the content measurement segment.' }
        $taskBlocked = ($taskMemory | ForEach-Object { [int]$_.blocked_youtube_images } | Measure-Object -Maximum).Maximum
        if ($Mode -eq 'normal' -and $taskBlocked -ne 0) { throw 'Normal mode unexpectedly blocked images.' }
        if ($Mode -eq 'images-reduced' -and $taskBlocked -eq 0) { throw 'Image mode did not intercept any eligible images.' }

        return [pscustomobject]@{
            Round = $Round; Mode = $Mode; Visibility = 'hidden'; Muted = $true
            Samples = $taskPlaying.Count; ContentSecondsAdvanced = $taskPlaying[-1].time - $taskPlaying[0].time
            Resolutions = @($taskPlaying | ForEach-Object { "$($_.width)x$($_.height)" } | Select-Object -Unique)
            FinalResolution = "$($taskPlaying[-1].width)x$($taskPlaying[-1].height)"
            MeasurementBasis = 'main-content-time'; RequestedContentWindowStart = $taskRequestedStart; RequestedContentWindowEnd = $taskRequestedEnd
            MeasurementContentStart = $taskWindow[0].time; MeasurementContentEnd = $taskWindow[-1].time
            MeasurementContentSecondsAdvanced = $taskContentAdvanced
            MeasurementWindowStart = $taskWindow[0].elapsed_s; MeasurementWindowEnd = $taskWindow[-1].elapsed_s
            MeasurementResolution = $taskResolutions[0]; MeasurementVideoSamples = $taskWindow.Count; MeasurementMemorySamples = $taskMemory.Count
            MaximumMemoryProbeGapSeconds = $taskMaximumGap
            FinalTotalFrames = $taskPlaying[-1].totalFrames; FinalDroppedFrames = $taskPlaying[-1].droppedFrames
            MeasurementFrames = $taskFrameDelta; MeasurementDroppedFrames = $taskDroppedDelta
            MeasurementNonDroppedFrames = $taskFrameDelta - $taskDroppedDelta
            PrivateRamMedianMB = (Get-VideoRunMedian @($taskMemory | ForEach-Object { [double]$_.private_working_set_bytes })) / 1000000
            PrivateCommitMedianMB = (Get-VideoRunMedian @($taskMemory | ForEach-Object { [double]$_.job_private_commit_bytes })) / 1000000
            WorkingSetSumMedianMB = (Get-VideoRunMedian @($taskMemory | ForEach-Object { [double]$_.working_set_sum_including_shared_bytes })) / 1000000
            BlockedImages = $taskBlocked; FinalLoadedImages = $taskProbes[-1].loadedImages; MeasurementLoadedImages = $taskWindow[-1].loadedImages
            Log = $taskLogPath; MemoryCsv = $taskCsvFile
        }
    }
    catch { throw "Video run validation failed: $($_.Exception.Message) Inspect $LogPath" }
}
