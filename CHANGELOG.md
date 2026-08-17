# Changelog

All notable changes to this project are documented in this file.

## Unreleased

### Fixed

- WSL distro auto-selection ignores Docker Desktop, Rancher Desktop, and Podman helper distros and prefers the configured, default, running, or best-known general-purpose distro, so `Open or start WSL DSH` works when Docker Desktop is installed.

## 0.3.0

### Architecture

- Core/tray logical separation with `IManagerService`; TrayFrontend only owns UI.
- Core is UI-free through `IManagerInteraction`; `SilentManagerInteraction` supports headless operation.
- `TrayEnabled=false` runs one EXE with Manager Core, Supervisor, Runtime Bridge, and Manager Control API.
- `IRuntimeAdapter` / `IRuntimeProcess` boundary with `WindowsRuntimeAdapter` and `WslRuntimeAdapter`.

### Instance model

- Managed/Attached ownership, `RuntimeType`, `Frontend`, PID, port, startedAt, Runtime Bridge state/version fields.
- Multi-instance log retention no longer hard-codes the `web` instance id.

### Manager Control Protocol v1

- Per-user named pipe with current-user-only ACL.
- Commands: `getVersion`, `getStatus`, `listInstances`, `start`, `stop`, `restart`, `open`, `openWsl`, `exit`.
- Secondary invocations forward through the pipe; no second Supervisor.

### WSL2

- Optional and disabled by default.
- `wsl status/detect/enable/disable/open` plus `configure --runtime wsl --wsl-distro`.
- `WslRuntimeAdapter`: global, npx, and source runtime resolution through `wsl.exe`.
- Runtime Bridge TCP transport with the same 256-bit token; no WMI PID guessing.
- Non-Ubuntu distros supported with `bash`/`sh` fallback and `/mnt`/`wsl.localhost` path fallback.
- Attached WSL DSH adoption with verified Linux PID/port stop.
- One-click `wsl open` starts WSL DSH on port 3088 (or nearest free port) and opens the Web UI.
- No Manager software installed inside WSL.

### Frontends and configuration

- `FrontendLauncher` resolves `web`; `oh-dsh` and `custom` fail explicitly.
- `configure` interactive/non-interactive for runtime/frontend/tray/shortcut/autostart.
- Diagnostics: Copy diagnostics, Manager/DSH log actions, `diagnostics --json`.
- Arbitrary Windows/WSL DSH port detection from the tray with attached-instance registration.
- Compact tray layout with current-instance selector and grouped submenus.

### Tests

- 34 C# tests, named-pipe and TCP Runtime Bridge tests, npm CLI integration tests, package validation, and real WSL adapter/detection/lifecycle tests.
- Performance: idle UI change-version short-circuit, shared HTTP probe, WSL path cache, and allocation reductions in port lookup.

## 0.2.1

- Expected CLI version derived from package.json in tests.
- Bounded manager and instance log retention.

## 0.2.0

- Event-driven DSH Runtime Bridge refactor with authenticated versioned protocol.
- Idle CPU approximately 0%.

## 0.1.0

- Initial Windows tray manager release.
