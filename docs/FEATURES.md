# Feature Guide

## Product Scope

`DeepSeek Harness Manager` is a Windows 11 notification-area application for starting, discovering, opening, updating, and safely stopping one or more local DeepSeek Harness Web instances.

## Current Capabilities

| Area | Behavior |
| --- | --- |
| Installation | Per-user LocalAppData installation from source scripts or the npm CLI |
| Launch | Desktop shortcut, tray double-click, npm CLI, or direct EXE action |
| Runtime adapters | Global npm, fixed-version npx, and Git source checkout |
| Discovery | Authoritative DSH IPC status/events when the bridge is connected; combined HTTP content and process-command fingerprints remain as fallback |
| Ports | Configurable preferred port, bounded fallback selection, conflict details |
| Frontends | `web` opens the local Web UI; `oh-dsh` and `custom` are reserved and fail explicitly instead of falling back to web |
| Runtime adapter | `IRuntimeAdapter` boundary with Windows and WSL2 implementations; WSL uses bridge-first state, loopback TCP transport, attached-instance adoption/stop, and no Manager software inside WSL |
| WSL optional | Disabled by default; `wsl detect/enable/disable/status` and `configure --runtime wsl --wsl-distro <name>` provide explicit detection and switching without polling. Auto-selection ignores Docker Desktop/Rancher/Podman helper distros and prefers the configured, default, running, or best-known general-purpose distro |
| Multi-instance | Independent menu, status, port, workspace, runtime, and optional DSH home |
| Configuration | Separate menu actions for manager `config.json` and each instance's DSH settings directory |
| Shutdown | Authenticated versioned DSH IPC bridge (`ping`/`getStatus`/`getRuntimeInfo`/`shutdown`) and guarded manual termination fallback |
| Updates | 24-hour checks across the npm `latest` and `next` dist-tags, confirmed installation against a freshly re-checked target, isolated compatibility smoke test, and verified rollback |
| Performance | Event-driven process and IPC monitoring for Manager-owned DSH; fallback WMI/HTTP probes only when the bridge is unavailable |
| Languages | English, Simplified Chinese, and startup-time Windows language selection |
| Tray optional | `TrayEnabled=false` keeps the same EXE running Core + Supervisor + Runtime Bridge + Manager Control API without a tray icon |
| Tray layout | Top instance selector with state; common actions stay top-level; updates/diagnostics/logs are grouped in expandable submenus |
| Status | Tray state, details dialog with IPC state/version/home, logs, and npm CLI JSON or text output |
| Diagnostics | Copy diagnostics, separate Manager/DSH log actions, and `dsh-windows-manager diagnostics [--json]` |
| Manager API | Local named-pipe Manager Control Protocol v1 (`getVersion`/`getStatus`/`listInstances`/`start`/`stop`/`restart`/`open`) for CLI and future frontends |
| Plugin discovery | Opens the GitHub `dsh-plugin` topic |
| Troubleshooting | URL, dual fingerprints, process identity, output/error logs, and a documented decision tree |
| Upgrade | Replaces runtime files while preserving configuration and mutable data |
| Uninstall | Removes application and shortcut; data purge is explicit |

## User Entry Points

The default Start Menu shortcut performs `tray`: it opens only the Manager notification-area icon without starting DSH. An opt-in desktop shortcut uses the same action. `open` remains available from the tray menu and CLI when the user explicitly wants DSH plus the Web UI.

The tray menu exposes all instance operations. With multiple instances, each instance has a named submenu.

The npm CLI supports:

```text
install, uninstall, open, start, stop, restart, exit, status
```

`open`, `start`, `stop`, `restart`, and `exit` target the configured `DefaultInstanceId`. When the Manager is running, the CLI forwards every action through the local Manager Control pipe; otherwise it starts the Manager. `status` lists every configured instance and merges authoritative Manager state when the control pipe is available.

## Configuration Boundaries

The first install creates one Windows Web instance with `TrayEnabled=true`. Initial runtime, workspace, source root, and preferred port can be selected from the installer or npm CLI. `RuntimeType` (`windows` | `wsl`) and `Frontend` (`web` | reserved `oh-dsh`/`custom`) are stored per instance; `wsl` uses WslRuntimeAdapter and `oh-dsh`/`custom` fail explicitly until implemented. Setting `TrayEnabled=false` runs the same EXE headless. Existing configuration is never replaced by a later install.

Adding, removing, or changing instances currently requires editing `config.json` and restarting the manager. There is no graphical instance editor yet.

## Deliberately Excluded

- No MSI, NSIS, or separate Setup executable; installation stays scriptable through npm, an agent, or `Install.cmd`.
- No Windows service or administrator-mode installation.
- No automatic startup registration; optional explicit `configure --autostart true` only.
- No silent update installation.
- No automatic termination of unknown port owners.
- No unrestricted network binding.
- No full-port-range scan for external DSH processes.
- No automatic taskbar pinning.
- No commercial code-signing certificate; current builds are unsigned.

These exclusions preserve explicit user control, local-only networking, and auditable process behavior.
