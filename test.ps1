param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-anime.ps1') -SkipBuild:$SkipBuild
