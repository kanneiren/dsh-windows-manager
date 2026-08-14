# Performance

## Goal

The manager should be negligible beside the Node-based DSH runtime and a browser. It must not use GPU acceleration, write continuously to disk, or trade process-safety checks for a lower benchmark number.

## Stable-State Results

Test environment: Windows 11, 32 logical processors, one verified DSH instance running on port 3080. The final process was allowed to settle for 15 seconds and sampled once per second for 60 seconds.

| Metric | Before optimization | Final |
| --- | ---: | ---: |
| Median working set | 118.61 MB | 65.66 MB |
| Median private memory | 70.38 MB | 30.16 MB |
| Median handles | 1068 | 521 |
| Median threads | 19 | 16 |
| Average CPU, one-core equivalent | about 0.86% | 0.129% |
| Average CPU, all 32 logical processors | about 0.027% | 0.004% |
| GPU engine or memory context | not measured | none allocated |
| Stable disk transfer over an additional 30 seconds | not measured | 0 bytes |

Working set includes shared and file-backed .NET Framework and WinForms pages that Windows can reclaim. Private memory is the more useful approximation of memory dedicated to the manager.

The only steady network activity for a running instance is a loopback HTTP health check every five seconds. With the current roughly 12 KB HTML response, this is approximately 2.4 KB/s over `127.0.0.1`, not internet traffic. Automatic external update checks run at most once per 24 hours.

## Optimizations

- The one-second UI timer remains for responsive cross-process action signals.
- Starting instances are inspected every second; stable states use five-second heavy probes.
- A verified command line is reused while PID, process start time, and image path remain unchanged.
- Windows service lookup is deferred until conflict inspection or termination validation.
- Unchanged tray text and icons are not reassigned on every timer tick.
- Stable state files and logs are not rewritten when PID and port are unchanged.

## Reproduce

Install and start the candidate manager, keep the DSH state unchanged, wait for startup update checks to settle, then run:

```powershell
.\scripts\Measure-Performance.ps1 -DurationSeconds 60
```

Compare runs on the same machine, port state, instance count, and sampling duration. Median CPU can be zero because work occurs in short five-second bursts; use the reported average CPU for comparisons.

GPU allocation can be checked by filtering the Windows `GPU Engine` and `GPU Process Memory` counter instances for the manager PID. The final build creates no matching counter instance.

## Deliberate Limits

The manager does not force garbage collection or trim its working set. Those techniques can make Task Manager display a smaller number while increasing pauses and page faults.

Reading an external process command line uses the documented WMI interface once per new process identity and then caches the immutable result. Undocumented native process-information classes could reduce a small amount of CLR/WMI overhead but are explicitly subject to change in future Windows versions, so they are not used for a security fingerprint.
