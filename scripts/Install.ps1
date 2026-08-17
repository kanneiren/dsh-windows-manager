[CmdletBinding()]
param(
    [ValidateSet('auto', 'global', 'npx', 'source')]
    [string]$Runtime = 'auto',
    [string]$SourceRoot = '',
    [string]$Workspace = '',
    [ValidateRange(0, 65535)]
    [int]$Port = 0,
    [string]$DistPath = '',
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [string]$ShortcutPath = '',
    [string]$StartMenuShortcutPath = '',
    [switch]$NoShortcut,
    [switch]$DesktopShortcut,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SafeRoot {
    param([string]$Path, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) { throw "$Name must be an absolute path." }
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $volume = [System.IO.Path]::GetPathRoot($full).TrimEnd('\', '/')
    if ($full -eq $volume) { throw "$Name cannot be a drive root." }
}

function Stop-InstalledManager {
    param([string]$Executable)
    $running = @(Get-Process -Name 'DeepSeekHarnessManager' -ErrorAction SilentlyContinue | Where-Object {
        try { [string]::Equals([System.IO.Path]::GetFullPath($_.Path), $Executable, [System.StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($running.Count -gt 0 -and (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        $signal = Start-Process -FilePath $Executable -ArgumentList @('--action', 'exit') -PassThru
        if (-not $signal.WaitForExit(10000)) { throw 'The manager exit signal did not complete within 10 seconds.' }
    }
    foreach ($process in $running) {
        if (-not $process.WaitForExit(10000)) { throw 'DeepSeek Harness Manager did not exit within 10 seconds. Close it and retry.' }
    }
}

function Remove-ShortcutIfTarget {
    param([string]$Path, [string]$Executable)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        if (-not [string]::IsNullOrWhiteSpace($shortcut.TargetPath) -and
            [string]::Equals([System.IO.Path]::GetFullPath($shortcut.TargetPath), [System.IO.Path]::GetFullPath($Executable), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $Path -Force
        }
    } catch {
    }
}

function New-ManagerShortcut {
    param([string]$Path, [string]$Executable, [string]$Icon, [string]$WorkingDirectory)
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Executable
    $shortcut.Arguments = '--action tray'
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$Icon,0"
    $shortcut.Description = 'Open the DeepSeek Harness Manager tray icon without starting DSH.'
    $shortcut.Save()
}


$projectRoot = Split-Path -Parent $PSScriptRoot
$defaultDist = [string]::IsNullOrWhiteSpace($DistPath)
$dist = if ($defaultDist) { Join-Path $projectRoot 'dist' } else { [System.IO.Path]::GetFullPath($DistPath) }
$builtExe = Join-Path $dist 'DeepSeekHarnessManager.exe'
if ($defaultDist -and -not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { & (Join-Path $PSScriptRoot 'Build.ps1') }
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw 'The manager executable was not built.' }

if ([string]::IsNullOrWhiteSpace($Workspace)) { $Workspace = Split-Path -Parent $projectRoot }
$Workspace = [System.IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) { throw "Workspace does not exist: $Workspace" }
if ($Runtime -eq 'source') {
    if ([string]::IsNullOrWhiteSpace($SourceRoot)) { throw '-SourceRoot is required for the source runtime.' }
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    foreach ($required in @('.git', 'package.json', 'pnpm-lock.yaml', 'apps\cli')) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot $required))) { throw "The selected source checkout is missing $required" }
    }
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'Windows LocalApplicationData is unavailable.' }
$installRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) { Join-Path $localAppData 'DeepSeekHarnessManager\app' } else { [System.IO.Path]::GetFullPath($InstallRoot) }
$dataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) { Join-Path $localAppData 'DeepSeekHarnessManager' } else { [System.IO.Path]::GetFullPath($DataRoot) }
Assert-SafeRoot -Path $installRoot -Name 'InstallRoot'
Assert-SafeRoot -Path $dataRoot -Name 'DataRoot'
$installedExe = Join-Path $installRoot 'DeepSeekHarnessManager.exe'
Stop-InstalledManager -Executable $installedExe
[System.IO.Directory]::CreateDirectory($installRoot) | Out-Null

Copy-Item -LiteralPath $builtExe -Destination $installedExe -Force
Copy-Item -LiteralPath (Join-Path $dist 'DeepSeekHarnessManager.exe.config') -Destination (Join-Path $installRoot 'DeepSeekHarnessManager.exe.config') -Force
foreach ($document in @('README.md', 'README.en.md', 'config.example.json', 'THIRD_PARTY_NOTICES.md', 'LICENSE', 'SECURITY.md', 'SECURITY.en.md', 'CONTRIBUTING.md', 'CONTRIBUTING.en.md', 'AGENTS.md')) {
    Copy-Item -LiteralPath (Join-Path $dist $document) -Destination (Join-Path $installRoot $document) -Force
}
foreach ($directory in @('assets', 'plugins', 'locales', 'docs')) {
    $destination = Join-Path $installRoot $directory
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $dist $directory) -Destination $installRoot -Recurse -Force
}

$configPath = Join-Path $dataRoot 'config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    [System.IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $pluginManifest = Get-Content -LiteralPath (Join-Path $dist 'plugins\deepseek-harness-web\plugin.json') -Raw | ConvertFrom-Json
    $preferredPort = if ($Port -gt 0) { $Port } else { [int]$pluginManifest.DefaultPort }
    $config = [ordered]@{
        SchemaVersion = 1
        Language = 'auto'
        TrayEnabled = $true
        StartWithWindows = $false
        DesktopShortcut = $(-not $NoShortcut -and $DesktopShortcut)
        WslEnabled = $false
        WslDefaultDistro = ''
        DefaultInstanceId = 'web'
        Instances = @(
            [ordered]@{
                Id = 'web'
                Name = 'DeepSeek Harness'
                PluginId = 'deepseek-harness-web'
                Profile = 'web'
                Runtime = $Runtime
                RuntimeType = 'windows'
                WslDistro = ''
                Frontend = 'web'
                SourceRoot = $SourceRoot
                Workspace = $Workspace
                DshHome = ''
                PreferredPort = $preferredPort
                PinnedVersion = [string]$pluginManifest.Update.BundledVersion
            }
        )
    }
    [System.IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
}

$usesDefaultShortcut = [string]::IsNullOrWhiteSpace($ShortcutPath)
$desktop = [Environment]::GetFolderPath('Desktop')
$resolvedShortcutPath = if ($usesDefaultShortcut) { Join-Path $desktop 'DSH Manager.lnk' } else { [System.IO.Path]::GetFullPath($ShortcutPath) }

$usesDefaultStartMenuShortcut = [string]::IsNullOrWhiteSpace($StartMenuShortcutPath)
$programs = [Environment]::GetFolderPath('Programs')
$resolvedStartMenuShortcutPath = if ($usesDefaultStartMenuShortcut) {
    if ([string]::IsNullOrWhiteSpace($programs)) { throw 'Windows Start Menu Programs folder is unavailable.' }
    Join-Path $programs 'DSH Manager.lnk'
} else {
    [System.IO.Path]::GetFullPath($StartMenuShortcutPath)
}

$existingDesktopShortcut = $false
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try {
        $existingConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $existingDesktopShortcut = [bool]$existingConfig.DesktopShortcut
    } catch {
    }
}

$startMenuShortcutCreated = $null
$desktopShortcutCreated = $null
if (-not $NoShortcut) {
    $shortcutIcon = Join-Path $installRoot 'assets\dsh-manager-shortcut.ico'
    if (-not (Test-Path -LiteralPath $shortcutIcon -PathType Leaf)) { throw 'The prebuilt shortcut icon is missing.' }

    New-ManagerShortcut -Path $resolvedStartMenuShortcutPath -Executable $installedExe -Icon $shortcutIcon -WorkingDirectory $Workspace
    $startMenuShortcutCreated = $resolvedStartMenuShortcutPath

    if ($DesktopShortcut -or $existingDesktopShortcut) {
        if ($usesDefaultShortcut) { Remove-ShortcutIfTarget -Path (Join-Path $desktop 'DeepSeek Harness.lnk') -Executable $installedExe }
        New-ManagerShortcut -Path $resolvedShortcutPath -Executable $installedExe -Icon $shortcutIcon -WorkingDirectory $Workspace
        $desktopShortcutCreated = $resolvedShortcutPath
    }
}

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try {
        $storedConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $storedDesktopShortcut = [bool]$storedConfig.DesktopShortcut
        $shouldStoreDesktopShortcut = if ($NoShortcut) { $false } elseif ($DesktopShortcut) { $true } else { $storedDesktopShortcut }
        if ($shouldStoreDesktopShortcut -ne $storedDesktopShortcut) {
            $storedConfig.DesktopShortcut = $shouldStoreDesktopShortcut
            [System.IO.File]::WriteAllText($configPath, ($storedConfig | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
        }
    } catch {
    }
}

if (-not $NoLaunch) { Start-Process -FilePath $installedExe -ArgumentList @('--action', 'tray') }

[pscustomobject]@{
    Application = $installedExe
    StartMenuShortcut = $startMenuShortcutCreated
    DesktopShortcut = $desktopShortcutCreated
    Shortcut = $startMenuShortcutCreated
    Configuration = $configPath
    Runtime = $Runtime
    Workspace = $Workspace
}
