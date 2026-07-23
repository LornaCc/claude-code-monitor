param(
    [switch]$SkipVsCodeExtension,
    [switch]$NoStart,
    [switch]$SkipHooks,
    [switch]$SkipShortcut,
    [switch]$SkipStopRunningApp,
    [string]$InstallDirectory = ""
)

$ErrorActionPreference = "Stop"

$releaseVersion = "0.4.1"
$expectedExtension = "ccmonitor.cc-monitor-terminal-bridge@$releaseVersion"
$packageRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:LOCALAPPDATA "Programs\CCMonitor\$releaseVersion"
}

$installDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$appPath = Join-Path $installDirectory "CCMonitor.App.exe"

Write-Host "Installing CC Monitor $releaseVersion..."

if (-not $SkipStopRunningApp) {
    $runningApps = @(Get-Process CCMonitor.App -ErrorAction SilentlyContinue)
    if ($runningApps.Count -gt 0) {
        $runningApps | Stop-Process -Force
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $remainingApps = @(Get-Process CCMonitor.App -ErrorAction SilentlyContinue)
            if ($remainingApps.Count -eq 0) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)

        if ($remainingApps.Count -gt 0) {
            throw "An older CC Monitor instance could not be stopped."
        }
        Write-Host "Stopped $($runningApps.Count) older CC Monitor instance(s)."
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

$packageFullPath = [System.IO.Path]::GetFullPath($packageRoot).TrimEnd('\')
$installFullPath = $installDirectory.TrimEnd('\')
if (-not $packageFullPath.Equals($installFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    Get-ChildItem -LiteralPath $packageRoot -Force |
        Where-Object { $_.Name -notlike "*.zip" -and $_.Name -notlike "*.sha256.txt" } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $installDirectory -Recurse -Force
        }
}

if (-not (Test-Path -LiteralPath $appPath)) {
    throw "CCMonitor.App.exe was not copied to $installDirectory"
}

$installedProductVersion = (Get-Item -LiteralPath $appPath).VersionInfo.ProductVersion
if (-not $installedProductVersion.StartsWith($releaseVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected App version after copy: $installedProductVersion"
}

if (-not $SkipHooks) {
    $hookInstall = Start-Process -FilePath $appPath `
        -ArgumentList "--install-hooks" `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($hookInstall.ExitCode -ne 0) {
        throw "Hook installation failed with exit code $($hookInstall.ExitCode)"
    }
    Write-Host "Claude Code hooks and status line now point to this build."
}

if (-not $SkipVsCodeExtension) {
    $vsix = Get-ChildItem -LiteralPath $installDirectory -Filter "cc-monitor-terminal-bridge-*.vsix" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $vsix) {
        throw "The VS Code Terminal Bridge VSIX is missing."
    }

    $codeCli = $null
    foreach ($commandName in @("code.cmd", "code", "code-insiders.cmd", "code-insiders")) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $codeCli = $command.Source
            break
        }
    }

    if ($null -eq $codeCli) {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA "Programs\Microsoft VS Code\bin\code.cmd"),
            (Join-Path $env:LOCALAPPDATA "Programs\Microsoft VS Code Insiders\bin\code-insiders.cmd"),
            (Join-Path $env:ProgramFiles "Microsoft VS Code\bin\code.cmd"),
            (Join-Path $env:ProgramFiles "Microsoft VS Code Insiders\bin\code-insiders.cmd")
        )
        if (${env:ProgramFiles(x86)}) {
            $candidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft VS Code\bin\code.cmd"
        }

        $codeCli = $candidates |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
            Select-Object -First 1
    }

    if ($null -eq $codeCli) {
        throw "VS Code CLI was not found. Install the included VSIX manually, then run this installer again with -SkipVsCodeExtension."
    }

    & $codeCli --install-extension $vsix.FullName --force
    if ($LASTEXITCODE -ne 0) {
        throw "VS Code extension installation failed with exit code $LASTEXITCODE"
    }

    $installedExtensions = @(& $codeCli --list-extensions --show-versions)
    if ($LASTEXITCODE -ne 0 -or -not ($installedExtensions -contains $expectedExtension)) {
        throw "VS Code did not report the expected extension version: $expectedExtension"
    }
    Write-Host "VS Code Terminal Bridge verified: $expectedExtension"
}

if (-not $SkipShortcut) {
    $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    $shortcutPath = Join-Path $startMenu "CC Monitor.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $appPath
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.IconLocation = "$appPath,0"
    $shortcut.Save()
    Write-Host "Start menu shortcut updated."
}

if (-not $NoStart) {
    Start-Process -FilePath $appPath
    Start-Sleep -Milliseconds 800
    $newProcess = Get-Process CCMonitor.App -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.Equals($appPath, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $newProcess) {
        throw "CC Monitor $releaseVersion did not stay running."
    }
}

Write-Host ""
Write-Host "CC Monitor installed to: $installDirectory"
Write-Host "App version: $installedProductVersion"
Write-Host "Run 'Developer: Reload Window' in every open VS Code window."
