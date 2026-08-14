[CmdletBinding()]
param([switch]$SkipTests)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $projectRoot 'dist'
$assets = Join-Path $projectRoot 'assets'
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw '.NET Framework C# compiler was not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $assets 'deepseek-whale.ico'))) { throw 'The prebuilt application icon is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $assets 'dsh-manager-shortcut.ico'))) { throw 'The prebuilt shortcut icon is missing.' }
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
[System.IO.Directory]::CreateDirectory($dist) | Out-Null

$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Management.dll')
$common = @('/nologo','/noconfig','/langversion:5','/platform:anycpu','/optimize+') + $references

& $csc @common /target:winexe "/win32manifest:$projectRoot\app.manifest" "/win32icon:$assets\deepseek-whale.ico" "/out:$dist\DeepSeekHarnessManager.exe" @sources
if ($LASTEXITCODE -ne 0) { throw "Application compilation failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $projectRoot 'DeepSeekHarnessManager.exe.config') -Destination (Join-Path $dist 'DeepSeekHarnessManager.exe.config') -Force
foreach ($document in @('README.md', 'README.en.md', 'config.example.json', 'THIRD_PARTY_NOTICES.md', 'LICENSE', 'SECURITY.md', 'SECURITY.en.md', 'CONTRIBUTING.md', 'CONTRIBUTING.en.md', 'AGENTS.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination (Join-Path $dist $document) -Force
}
$distAssets = Join-Path $dist 'assets'
[System.IO.Directory]::CreateDirectory($distAssets) | Out-Null
foreach ($icon in @('dsh-manager-shortcut.ico', 'deepseek-whale-running.ico', 'deepseek-whale-starting.ico', 'deepseek-whale-stopped.ico', 'deepseek-whale-conflict.ico', 'deepseek-whale-error.ico')) {
    Copy-Item -LiteralPath (Join-Path $assets $icon) -Destination (Join-Path $distAssets $icon) -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'plugins') -Destination $dist -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'locales') -Destination $dist -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $dist -Recurse -Force

if (-not $SkipTests) {
    $testSources = $sources + @((Join-Path $projectRoot 'tests\TestProgram.cs'))
    & $csc @common /target:exe /main:DeepSeekHarnessManager.Tests.TestProgram "/out:$dist\DeepSeekHarnessManager.Tests.exe" @testSources
    if ($LASTEXITCODE -ne 0) { throw "Test compilation failed with exit code $LASTEXITCODE." }
}

Get-ChildItem -LiteralPath $dist | Select-Object Name, Length, LastWriteTime
