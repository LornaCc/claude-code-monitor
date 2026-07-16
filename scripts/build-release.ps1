param(
    [string]$Version = "0.1.0",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $repoRoot "artifacts"
$releaseName = "CCMonitor-v$Version-$Runtime"
$releaseDir = Join-Path $artifactsDir $releaseName
$archivePath = Join-Path $artifactsDir "$releaseName.zip"
$hookPublishDir = Join-Path $artifactsDir ".hook-publish"
$statusLinePublishDir = Join-Path $artifactsDir ".statusline-publish"

New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
foreach ($path in @($releaseDir, $hookPublishDir, $statusLinePublishDir)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

dotnet publish (Join-Path $repoRoot "src\CCMonitor.App\CCMonitor.App.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:Version=$Version `
    --output $releaseDir

dotnet publish (Join-Path $repoRoot "src\CCMonitor.Hook\CCMonitor.Hook.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $hookPublishDir

dotnet publish (Join-Path $repoRoot "src\CCMonitor.StatusLine\CCMonitor.StatusLine.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $statusLinePublishDir

Copy-Item -LiteralPath (Join-Path $hookPublishDir "CCMonitor.Hook.exe") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $statusLinePublishDir "CCMonitor.StatusLine.exe") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\install-hooks.ps1") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\uninstall-hooks.ps1") -Destination $releaseDir -Force

$vsix = Get-ChildItem -LiteralPath (Join-Path $repoRoot "vscode-extension") -Filter "*.vsix" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -ne $vsix) {
    Copy-Item -LiteralPath $vsix.FullName -Destination $releaseDir -Force
}

Remove-Item -LiteralPath $hookPublishDir -Recurse -Force
Remove-Item -LiteralPath $statusLinePublishDir -Recurse -Force
Compress-Archive -Path (Join-Path $releaseDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Release package created: $archivePath"
