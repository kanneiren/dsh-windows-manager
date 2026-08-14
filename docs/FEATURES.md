# Feature Guide

## Product Scope

`DeepSeek Harness Manager` is a Windows 11 notification-area application for starting, discovering, opening, updating, and safely stopping one or more local DeepSeek Harness Web instances.

## Current Capabilities

| Area | Behavior |
| --- | --- |
| Installation | Per-user LocalAppData installation from source scripts or the npm CLI |
| Launch | Desktop shortcut, tray double-click, npm CLI, or direct EXE action |
| Runtime adapters | Global npm, fixed-version npx, and Git source checkout |
| Discovery | Combined HTTP content and process-command fingerprints |
| Ports | Configurable preferred port, bounded fallback selection, conflict details |
| Multi-instance | Independent menu, status, port, workspace, runtime, and optional DSH home |
| Shutdown | Authenticated Cordis `ctx.appExit(0)` bridge with guarded manual fallback |
| Updates | 24-hour checks, confirmed installation, isolated compatibility smoke test, and verified rollback |
| Performance | One-second command response with five-second stable health probes and cached immutable process fingerprints |
| Languages | English, Simplified Chinese, and startup-time Windows language selection |
| Status | Tray state, details dialog, logs, and npm CLI JSON or text output |
| Troubleshooting | URL, dual fingerprints, process identity, output/error logs, and a documented decision tree |
| Upgrade | Replaces runtime files while preserving configuration and mutable data |
| Uninstall | Removes application and shortcut; data purge is explicit |

## User Entry Points

The desktop shortcut performs `open`: it adopts and opens a verified running instance or starts the default instance and waits for readiness.

The tray menu exposes all instance operations. With multiple instances, each instance has a named submenu.

The npm CLI supports:

```text
install, uninstall, open, start, stop, restart, exit, status
```

`open`, `start`, `stop`, and `restart` target the configured `DefaultInstanceId`. Other instances are controlled through their tray submenu. `status` lists every configured instance.

## Configuration Boundaries

The first install creates one Web instance. Initial runtime, workspace, source root, and preferred port can be selected from the installer or npm CLI. Existing configuration is never replaced by a later install.

Adding, removing, or changing instances currently requires editing `config.json` and restarting the manager. There is no graphical instance editor yet.

## Deliberately Excluded

- No MSI, NSIS, or separate Setup executable; installation stays scriptable through npm, an agent, or `Install.cmd`.
- No Windows service or administrator-mode installation.
- No automatic startup registration.
- No silent update installation.
- No automatic termination of unknown port owners.
- No unrestricted network binding.
- No full-port-range scan for external DSH processes.
- No automatic taskbar pinning.
- No commercial code-signing certificate in the current release.

These exclusions preserve explicit user control, local-only networking, and auditable process behavior.
