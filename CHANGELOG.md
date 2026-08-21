# Changelog

All notable changes to this project are documented in this file.

## 0.4.0

### Added

- Tray `Diagnose port residue` action classifies an unavailable port on demand as stale manager state, a leftover DSH-related process, WSL port-forwarding residue, or an external process, then offers confirmed, immediately revalidated repairs: reset the manager state, clear the leftover process and restart on the original port, restart the distro (consequence labeled), or switch to an alternate port that skips other configured instances.

### Changed

- Registry update checks read the npm dist-tags document and offer the newer of the `latest` and `next` channels, so pre-releases published only to `next` remain visible.
- A confirmed update re-checks the target immediately; the confirmation dialog shows the refreshed version and says so when it moved since the last check, and nothing installs when no update remains.
- Source updates fetch the upstream branch and reset to the exact checked commit instead of `git pull --ff-only`, and both the update and the rollback remove untracked build output (`git clean -fd`) so generated files never block the next clean-tree precheck.
- The HTTP fingerprint relies on the served `__DSH_BOOT__` marker only, which DSH source builds also serve; the page title differs between official and source builds.

### Fixed

- DSH 0.1.1 compatibility: the runtime-bridge `--patch` argument is now placed before the profile alias (DSH 0.1.1 rejects launcher flags after `--profile web`), and every web launch passes `--no-open` so the manager keeps owning frontend opening and npx-wrapped launches no longer hang on the default-browser handoff.
- Track bundled DSH 0.1.1-rc.1.

## 0.3.2

### Changed

- Shortcuts and autostart now open the Manager tray only (`--action tray`) instead of starting DSH and a browser.
- `install` creates a Start Menu shortcut by default so `DSH Manager` is searchable from the Win key; the desktop shortcut is opt-in with `--desktop-shortcut`, and `--no-shortcut` creates none.
- Added the `dsh-windows-manager tray` command and `tray` Manager Control Protocol command for opening the tray without starting DSH.

## 0.3.1

### Fixed

- WSL distro auto-selection ignores Docker Desktop, Rancher Desktop, and Podman helper distros and prefers the configured, default, running, or best-known general-purpose distro, so `Open or start WSL DSH` works when Docker Desktop is installed.

### Documentation

- Slimmed both READMEs to a Windows/WSL2 scenario-focused overview and quick start; detailed installation, usage, update, and operations content moved to `docs/USAGE.md` and `docs/USAGE.zh-CN.md`.

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
