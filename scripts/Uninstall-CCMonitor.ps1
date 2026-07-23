$ErrorActionPreference = "Stop"

$appPath = Join-Path $PSScriptRoot "CCMonitor.App.exe"
Get-Process CCMonitor.App -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($PSScriptRoot, [System.StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

if (Test-Path -LiteralPath $appPath) {
    $uninstall = Start-Process -FilePath $appPath `
        -ArgumentList "--uninstall-hooks" `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "Hook removal failed with exit code $($uninstall.ExitCode)"
    }
}

$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\CC Monitor.lnk"
Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue

Write-Host "CC Monitor hooks and shortcut were removed."
Write-Host "You may now delete: $PSScriptRoot"
