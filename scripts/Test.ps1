[CmdletBinding()]
param([switch]$SkipIntegration)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$testArguments = @($projectRoot)
if (-not $SkipIntegration) { $testArguments += '--integration' }
& (Join-Path $projectRoot 'dist\DeepSeekHarnessManager.Tests.exe') @testArguments
if ($LASTEXITCODE -ne 0) { throw 'C# test suite failed.' }

& node (Join-Path $projectRoot 'tests\bridge.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Node named-pipe test failed.' }

& node (Join-Path $projectRoot 'tests\cli.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'npm CLI integration test failed.' }

[pscustomobject]@{ Build = 'PASS'; CSharp = 'PASS'; CordisBridge = 'PASS'; NpmCli = 'PASS'; Integration = if ($SkipIntegration) { 'SKIPPED' } else { 'PASS' } }
