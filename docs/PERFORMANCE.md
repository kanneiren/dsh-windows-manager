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

The table is the previously measured 0.1.0 baseline. The 0.2.0 event-driven refactor removes the steady fallback probes from the normal bridge-connected path; re-run `Measure-Performance.ps1` on the release candidate before publishing updated numbers.

For a manager-launched instance with the DSH IPC bridge connected, there is no steady polling network activity: liveness comes from the Windows process handle and state comes from named-pipe events. The old five-second loopback HTTP health check now runs only in fallback mode (external adoption, plugin unavailable, protocol mismatch, or startup before the bridge connects). Automatic external update checks still run at most once per 24 hours per configured instance/runtime.

## Optimizations

- The one-second UI timer remains for responsive cross-process action signals and coalescing background notifications.
- Manager-launched processes are monitored with the Windows process-exit event; no periodic existence check is used while the handle is alive.
- When the authenticated IPC bridge is connected, the Manager waits for `ready`, `stopping`, and `exiting` events and does not run steady WMI, process-enumeration, port, or HTTP probes.
- Fallback discovery remains available for externally launched DSH, old/absent plugins, protocol mismatch, diagnostics, and user actions. It uses one-second startup probes and five-second fallback probes only while it is the selected state source.
- A verified command line is captured once at launch and reused while PID, process start time, and image path remain unchanged.
- Windows service lookup is deferred until conflict display or termination validation.
- Unchanged tray text and icons are not reassigned on every timer tick.
- Stable state files and logs are not rewritten when PID and port are unchanged.

## Reproduce

Install and start the candidate manager, keep the DSH state unchanged, wait for startup update checks to settle, then run:

```powershell
.\scripts\Measure-Performance.ps1 -DurationSeconds 60
```

Compare runs on the same machine, port state, instance count, and sampling duration. Median CPU can be zero because an idle bridge-connected manager does little beyond the one-second signal timer; use the reported average CPU for comparisons.

GPU allocation can be checked by filtering the Windows `GPU Engine` and `GPU Process Memory` counter instances for the manager PID. The final build creates no matching counter instance.

## Deliberate Limits

The manager does not force garbage collection or trim its working set. Those techniques can make Task Manager display a smaller number while increasing pauses and page faults.

Reading an external process command line uses the documented WMI interface once per new fallback process identity and then caches the immutable result. Manager-owned instances capture the fingerprint once at launch and do not refresh it on a steady timer. Undocumented native process-information classes could reduce a small amount of CLR/WMI overhead but are explicitly subject to change in future Windows versions, so they are not used for a security fingerprint.
