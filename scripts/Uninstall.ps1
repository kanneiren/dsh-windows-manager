[CmdletBinding()]
param(
    [switch]$PurgeData,
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [string]$ShortcutPath = '',
    [switch]$NoShortcut
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
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $true }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        if (-not [string]::IsNullOrWhiteSpace($shortcut.TargetPath) -and
            [string]::Equals([System.IO.Path]::GetFullPath($shortcut.TargetPath), [System.IO.Path]::GetFullPath($Executable), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $Path -Force
        }
        return $true
    } catch {
        return $false
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
$usesDefaultShortcut = [string]::IsNullOrWhiteSpace($ShortcutPath)
$desktop = [Environment]::GetFolderPath('Desktop')
$resolvedShortcutPath = if ($usesDefaultShortcut) { Join-Path $desktop 'DSH Manager.lnk' } else { [System.IO.Path]::GetFullPath($ShortcutPath) }
if (-not $NoShortcut -and (Test-Path -LiteralPath $resolvedShortcutPath)) { Remove-Item -LiteralPath $resolvedShortcutPath -Force }
$legacyShortcutRemoved = $true
if (-not $NoShortcut -and $usesDefaultShortcut) {
    $legacyShortcutRemoved = Remove-ShortcutIfTarget -Path (Join-Path $desktop 'DeepSeek Harness.lnk') -Executable $installedExe
}
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
if ($PurgeData -and (Test-Path -LiteralPath $dataRoot)) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }

[pscustomobject]@{
    ApplicationRemoved = -not (Test-Path -LiteralPath $installRoot)
    ShortcutRemoved = $NoShortcut -or ($legacyShortcutRemoved -and -not (Test-Path -LiteralPath $resolvedShortcutPath))
    DataPreserved = -not $PurgeData
    Note = 'Running DeepSeek Harness processes are intentionally left running.'
}
