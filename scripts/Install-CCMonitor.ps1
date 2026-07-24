param(
    [switch]$SkipVsCodeExtension,
    [switch]$NoStart,
    [switch]$SkipHooks,
    [switch]$SkipShortcut,
    [switch]$SkipStopRunningApp,
    [string]$InstallDirectory = ""
)

$ErrorActionPreference = "Stop"

$packageRoot = $PSScriptRoot
$packageAppPath = Join-Path $packageRoot "CCMonitor.App.exe"
if (-not (Test-Path -LiteralPath $packageAppPath)) {
    throw "The release package does not contain CCMonitor.App.exe."
}

$packageProductVersion = (Get-Item -LiteralPath $packageAppPath).VersionInfo.ProductVersion
$versionMatch = [regex]::Match($packageProductVersion, '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?')
if (-not $versionMatch.Success) {
    throw "Could not determine the release version from CCMonitor.App.exe: $packageProductVersion"
}

$releaseVersion = $versionMatch.Value
$expectedExtension = "ccmonitor.cc-monitor-terminal-bridge@$releaseVersion"
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

foreach ($componentName in @("CCMonitor.Hook.exe", "CCMonitor.StatusLine.exe")) {
    $componentPath = Join-Path $installDirectory $componentName
    if (-not (Test-Path -LiteralPath $componentPath)) {
        throw "$componentName was not copied to $installDirectory"
    }

    $componentVersion = (Get-Item -LiteralPath $componentPath).VersionInfo.ProductVersion
    if (-not $componentVersion.StartsWith($releaseVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected $componentName version after copy: $componentVersion"
    }
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

    function ConvertTo-ClaudeShellCommand([string]$ExecutablePath) {
        $fullPath = [System.IO.Path]::GetFullPath($ExecutablePath).Replace('\', '/')
        if ($fullPath -match '^([A-Za-z]):/(.*)$') {
            $fullPath = "/$($Matches[1].ToLowerInvariant())/$($Matches[2])"
        }
        return "'$fullPath'"
    }

    $settingsPath = Join-Path $env:USERPROFILE ".claude\settings.json"
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $expectedHookCommand = ConvertTo-ClaudeShellCommand (Join-Path $installDirectory "CCMonitor.Hook.exe")
    $expectedStatusLineCommand = ConvertTo-ClaudeShellCommand (Join-Path $installDirectory "CCMonitor.StatusLine.exe")
    $installedHookCommands = @(
        foreach ($eventProperty in $settings.hooks.PSObject.Properties) {
            foreach ($entry in @($eventProperty.Value)) {
                foreach ($hook in @($entry.hooks)) {
                    if ($hook.type -eq "command") {
                        [string]$hook.command
                    }
                }
            }
        }
    )
    $staleHookCommands = @(
        $installedHookCommands |
            Where-Object {
                $_ -match 'CCMonitor\.Hook\.exe' -and
                -not $_.Equals($expectedHookCommand, [System.StringComparison]::OrdinalIgnoreCase)
            }
    )
    if (-not ($installedHookCommands -contains $expectedHookCommand) -or $staleHookCommands.Count -gt 0) {
        throw "Claude Code hooks were not exclusively updated to $expectedHookCommand"
    }
    if ($settings.statusLine.command -ne $expectedStatusLineCommand) {
        throw "Claude Code StatusLine was not updated to $expectedStatusLineCommand"
    }
    Write-Host "Claude Code hooks and status line now point to this build."
}

if (-not $SkipVsCodeExtension) {
    $vsixPath = Join-Path $installDirectory "cc-monitor-terminal-bridge-$releaseVersion.vsix"
    if (-not (Test-Path -LiteralPath $vsixPath)) {
        throw "The expected VS Code Terminal Bridge VSIX is missing: $vsixPath"
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

    & $codeCli --install-extension $vsixPath --force
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
    function Set-CcMonitorShortcut([string]$ShortcutPath) {
        $shortcutDirectory = Split-Path -Parent $ShortcutPath
        New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $appPath
        $shortcut.WorkingDirectory = $installDirectory
        $shortcut.IconLocation = "$appPath,0"
        $shortcut.Description = "CC Monitor $releaseVersion"
        $shortcut.Save()

        $savedShortcut = $shell.CreateShortcut($ShortcutPath)
        if (-not $savedShortcut.TargetPath.Equals($appPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Shortcut was not updated to the new build: $ShortcutPath"
        }
    }

    $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    [void]$shortcutPaths.Add((Join-Path $startMenu "CC Monitor.lnk"))

    $desktopDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::DesktopDirectory)
    if (-not [string]::IsNullOrWhiteSpace($desktopDirectory)) {
        [void]$shortcutPaths.Add((Join-Path $desktopDirectory "CC Monitor.lnk"))
    }

    foreach ($possibleDesktop in @(
        (Join-Path $env:USERPROFILE "Desktop"),
        $(if ($env:OneDrive) { Join-Path $env:OneDrive "Desktop" }),
        $(if ($env:OneDriveConsumer) { Join-Path $env:OneDriveConsumer "Desktop" })
    )) {
        if ($possibleDesktop) {
            $possibleShortcut = Join-Path $possibleDesktop "CC Monitor.lnk"
            if (Test-Path -LiteralPath $possibleShortcut) {
                [void]$shortcutPaths.Add($possibleShortcut)
            }
        }
    }

    foreach ($shortcutPath in $shortcutPaths) {
        Set-CcMonitorShortcut $shortcutPath
    }
    Write-Host "Updated $($shortcutPaths.Count) Start menu/Desktop shortcut(s)."
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

    $unexpectedProcesses = @(
        Get-Process CCMonitor.App -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Path -and
                -not $_.Path.Equals($appPath, [System.StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($unexpectedProcesses.Count -gt 0) {
        throw "An older CC Monitor process is still running after installation."
    }
}

Write-Host ""
Write-Host "CC Monitor installed to: $installDirectory"
Write-Host "App version: $installedProductVersion"
Write-Host "Run 'Developer: Reload Window' in every open VS Code window."
