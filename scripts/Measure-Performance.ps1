[CmdletBinding()]
param(
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 30,
    [ValidateRange(100, 60000)]
    [int]$IntervalMilliseconds = 1000,
    [int]$TargetProcessId = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($TargetProcessId -gt 0) {
    $process = Get-Process -Id $TargetProcessId -ErrorAction Stop
}
else {
    $process = @(Get-Process -Name 'DeepSeekHarnessManager' -ErrorAction Stop | Sort-Object StartTime -Descending)[0]
}

function Get-Median {
    param([double[]]$Values)
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return 0 }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

$samples = New-Object System.Collections.Generic.List[object]
$process.Refresh()
$previousCpu = $process.TotalProcessorTime
$previousTime = [DateTime]::UtcNow
$initialCpu = $previousCpu
$initialTime = $previousTime
$deadline = $previousTime.AddSeconds($DurationSeconds)

while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds $IntervalMilliseconds
    $process.Refresh()
    if ($process.HasExited) { throw 'DeepSeek Harness Manager exited during sampling.' }
    $now = [DateTime]::UtcNow
    $cpu = $process.TotalProcessorTime
    $wallMilliseconds = ($now - $previousTime).TotalMilliseconds
    $cpuMilliseconds = ($cpu - $previousCpu).TotalMilliseconds
    $samples.Add([pscustomobject]@{
        WorkingSetMb = $process.WorkingSet64 / 1MB
        PrivateMb = $process.PrivateMemorySize64 / 1MB
        Handles = $process.HandleCount
        Threads = $process.Threads.Count
        CpuOneCorePercent = if ($wallMilliseconds -gt 0) { 100 * $cpuMilliseconds / $wallMilliseconds } else { 0 }
        CpuAllCoresPercent = if ($wallMilliseconds -gt 0) { 100 * $cpuMilliseconds / $wallMilliseconds / [Environment]::ProcessorCount } else { 0 }
    })
    $previousCpu = $cpu
    $previousTime = $now
}

[pscustomobject]@{
    ProcessId = $process.Id
    DurationSeconds = $DurationSeconds
    Samples = $samples.Count
    LogicalProcessors = [Environment]::ProcessorCount
    MedianWorkingSetMb = [Math]::Round((Get-Median @($samples.WorkingSetMb)), 2)
    MedianPrivateMb = [Math]::Round((Get-Median @($samples.PrivateMb)), 2)
    MedianHandles = [Math]::Round((Get-Median @($samples.Handles)), 0)
    MedianThreads = [Math]::Round((Get-Median @($samples.Threads)), 0)
    MedianCpuOneCorePercent = [Math]::Round((Get-Median @($samples.CpuOneCorePercent)), 3)
    MedianCpuAllCoresPercent = [Math]::Round((Get-Median @($samples.CpuAllCoresPercent)), 4)
    AverageCpuOneCorePercent = [Math]::Round(100 * ($process.TotalProcessorTime - $initialCpu).TotalMilliseconds / ([DateTime]::UtcNow - $initialTime).TotalMilliseconds, 3)
    AverageCpuAllCoresPercent = [Math]::Round(100 * ($process.TotalProcessorTime - $initialCpu).TotalMilliseconds / ([DateTime]::UtcNow - $initialTime).TotalMilliseconds / [Environment]::ProcessorCount, 4)
}
