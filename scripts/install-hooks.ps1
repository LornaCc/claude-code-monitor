param(
    [string]$AppPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
    $AppPath = Join-Path $repoRoot "src\CCMonitor.App\bin\Release\net8.0-windows\win-x64\publish\CCMonitor.App.exe"
}

if (-not (Test-Path -LiteralPath $AppPath)) {
    throw "CCMonitor.App.exe was not found at: $AppPath"
}

& $AppPath --install-hooks
Write-Host "CC Monitor hooks installed."
