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

$process = Start-Process -FilePath $AppPath `
    -ArgumentList "--install-hooks" `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
if ($process.ExitCode -ne 0) {
    throw "CC Monitor hook installation failed with exit code $($process.ExitCode)."
}
Write-Host "CC Monitor hooks installed."
