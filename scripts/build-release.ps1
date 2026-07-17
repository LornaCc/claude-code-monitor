param(
    [string]$Version = "0.4.0",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

function Assert-NativeCommandSucceeded([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $repoRoot "artifacts"
$releaseName = "CCMonitor-v$Version-$Runtime"
$releaseDir = Join-Path $artifactsDir $releaseName
$archivePath = Join-Path $artifactsDir "$releaseName.zip"

New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
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
Assert-NativeCommandSucceeded "CCMonitor.App publish"

dotnet publish (Join-Path $repoRoot "src\CCMonitor.Hook\CCMonitor.Hook.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    --output $releaseDir
Assert-NativeCommandSucceeded "CCMonitor.Hook publish"

dotnet publish (Join-Path $repoRoot "src\CCMonitor.StatusLine\CCMonitor.StatusLine.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    --output $releaseDir
Assert-NativeCommandSucceeded "CCMonitor.StatusLine publish"

Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\install-hooks.ps1") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\uninstall-hooks.ps1") -Destination $releaseDir -Force

$extensionDir = Join-Path $repoRoot "vscode-extension"
Push-Location $extensionDir
try {
    & npm.cmd install --ignore-scripts
    Assert-NativeCommandSucceeded "VS Code extension dependency install"
    & npm.cmd run package -- $Version --out "cc-monitor-terminal-bridge-$Version.vsix"
    Assert-NativeCommandSucceeded "VS Code extension package"
}
finally {
    Pop-Location
}

$vsix = Get-ChildItem -LiteralPath $extensionDir -Filter "*.vsix" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -ne $vsix) {
    Copy-Item -LiteralPath $vsix.FullName -Destination $releaseDir -Force
}

Compress-Archive -Path (Join-Path $releaseDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Release package created: $archivePath"
