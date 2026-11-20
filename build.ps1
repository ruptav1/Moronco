param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$taskProject = $PSScriptRoot
$taskVersion = '1.0.4258.31'
$taskHash = '56F7F4B8BF9AEE4B8EFEFBBDD4F67D5F74EBD1B100ED0806DA71BF76AF481AA9'
$taskPackage = Join-Path $taskProject ".deps\quarantine\microsoft.web.webview2.$taskVersion.nupkg"
$taskSdk = Join-Path $taskProject ".deps\quarantine\webview2-$taskVersion"
if (-not (Test-Path -LiteralPath $taskPackage)) {
    if (-not $Restore) { throw 'SDK chưa có. Đọc DEPENDENCIES.md rồi chạy .\build.ps1 -Restore để tải đúng gói đã kiểm tra.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskPackage) -Force | Out-Null
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$taskVersion/microsoft.web.webview2.$taskVersion.nupkg" -OutFile $taskPackage
}
if ((Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash -ne $taskHash) { throw 'SDK SHA256 mismatch. Refusing build.' }
$taskPackageZip = Join-Path ([IO.Path]::GetTempPath()) "webview2-$taskVersion.zip"
Copy-Item -LiteralPath $taskPackage -Destination $taskPackageZip -Force
Expand-Archive -LiteralPath $taskPackageZip -DestinationPath $taskSdk -Force
Remove-Item -LiteralPath $taskPackageZip -Force
$taskCore = Join-Path $taskSdk 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$taskLoader = Join-Path $taskSdk 'runtimes\win-x64\native\WebView2Loader.dll'
foreach ($taskDll in @($taskCore, $taskLoader)) {
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskDll
    if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Microsoft signature validation failed: $taskDll"
    }
}
$taskOutput = Join-Path $taskProject 'dist'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework x64 C# compiler not found.' }
$taskIcon = Join-Path $taskProject 'assets\MiniumBrowser.ico'
if (-not (Test-Path -LiteralPath $taskIcon -PathType Leaf)) { throw 'Missing application icon: assets/MiniumBrowser.ico' }
$taskIconArguments = @("/win32icon:$taskIcon", "/resource:$taskIcon,MiniumBrowser.AppIcon.ico")
$taskSources = @('Program.cs', 'BrowserHost.cs', 'BrowserStore.cs', 'BrowserActions.cs', 'BrowserDiagnostics.cs', 'MemoryBudget.cs', 'SingleFileBootstrap.cs', 'AddressInput.cs', 'BrowserProtection.cs', 'BrowserSearch.cs', 'BrowserDeepSearch.cs', 'AutoUpdater.cs') | ForEach-Object { Join-Path $taskProject "src\$_" }
foreach ($taskSource in $taskSources) {
    if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Required source is missing: $taskSource" }
}
$taskUiRoot = Join-Path $taskProject 'ui'
foreach ($taskRequiredUi in @('index.html', 'app.css', 'app.js')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskUiRoot $taskRequiredUi) -PathType Leaf)) { throw "Required UI file is missing: $taskRequiredUi" }
}
$taskUiEntries = @(Get-ChildItem -LiteralPath $taskUiRoot -Recurse -Force)
foreach ($taskUiEntry in $taskUiEntries) {
    if (($taskUiEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "UI payload cannot contain links: $($taskUiEntry.FullName)" }
}
$taskUiFiles = @($taskUiEntries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
$taskUiPrefix = [IO.Path]::GetFullPath($taskUiRoot).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
$taskUiResources = @()
foreach ($taskUiFile in $taskUiFiles) {
    if (-not $taskUiFile.FullName.StartsWith($taskUiPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'UI asset path escapes the UI directory.' }
    $taskUiRelative = $taskUiFile.FullName.Substring($taskUiPrefix.Length).Replace('\', '/')
    if ($taskUiRelative -match '(^|/)\.\.?(/|$)|[:,]') { throw "Unsupported UI payload path: $taskUiRelative" }
    $taskUiResources += "/resource:$($taskUiFile.FullName),MiniumUi.$taskUiRelative"
}
& $taskCompiler /nologo /target:winexe /main:MiniumBrowser.Program /platform:x64 /optimize+ /codepage:65001 "/out:$taskOutput\Moronco.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$taskCore" $taskIconArguments $taskSources
if ($LASTEXITCODE -ne 0) { throw "Compiler failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $taskCore, $taskLoader -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskSdk 'LICENSE.txt') -Destination (Join-Path $taskOutput 'WebView2-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $taskSdk 'NOTICE.txt') -Destination (Join-Path $taskOutput 'WebView2-NOTICE.txt') -Force
$taskDistUi = Join-Path $taskOutput 'ui'
foreach ($taskUiFile in $taskUiFiles) {
    $taskUiRelative = $taskUiFile.FullName.Substring($taskUiPrefix.Length)
    $taskUiDestination = Join-Path $taskDistUi $taskUiRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskUiDestination) -Force | Out-Null
    Copy-Item -LiteralPath $taskUiFile.FullName -Destination $taskUiDestination -Force
}
@'
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup>
  <runtime><gcServer enabled="false" /></runtime>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $taskOutput 'Moronco.exe.config') -Encoding UTF8
Write-Output "Built: $taskOutput\Moronco.exe"
$taskRelease = Join-Path $taskProject 'release'
New-Item -ItemType Directory -Path $taskRelease -Force | Out-Null
$taskAdBlockZip = Join-Path $taskProject "assets\ublock-lite-2026.1006.1931.zip"
if (-not (Test-Path -LiteralPath $taskAdBlockZip)) { throw "Missing vetted uBlock Origin Lite package." }
if ((Get-FileHash -LiteralPath $taskAdBlockZip -Algorithm SHA256).Hash -ne "938CBA1EE5D266350011CFA93665B183A0FB8DE3E983B241AFAF99AEC6A7EA35") { throw "uBlock Origin Lite package hash mismatch." }
$taskAdBlockDist = Join-Path $taskOutput "extensions\ublock-lite"
Expand-Archive -LiteralPath $taskAdBlockZip -DestinationPath $taskAdBlockDist -Force
$taskEmbedded = @(
    "/resource:$taskAdBlockZip,MoroncoBundle.ublock-lite.zip",
    "/resource:$taskCore,MiniumBundle.Microsoft.Web.WebView2.Core.dll",
    "/resource:$taskLoader,MiniumBundle.WebView2Loader.dll",
    "/resource:$(Join-Path $taskSdk 'LICENSE.txt'),MiniumBundle.WebView2-LICENSE.txt",
    "/resource:$(Join-Path $taskSdk 'NOTICE.txt'),MiniumBundle.WebView2-NOTICE.txt"
)
& $taskCompiler /nologo /target:winexe /main:MiniumBrowser.SingleFileBootstrap /platform:x64 /optimize+ /codepage:65001 "/out:$taskRelease\Moronco.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$taskCore" $taskIconArguments $taskEmbedded $taskUiResources $taskSources
if ($LASTEXITCODE -ne 0) { throw "Single-file compiler failed: $LASTEXITCODE" }
Write-Output "Single EXE: $taskRelease\Moronco.exe"
