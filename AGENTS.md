# Agent Guide

This file is the starting point for coding agents working on `dsh-windows-manager`.

## Read First

1. Read `README.md` (Chinese) or `README.en.md` (English) for user-facing behavior and installation instructions.
2. Read `docs/ARCHITECTURE.md` before changing lifecycle, plugins, ports, configuration, or packaging.
3. Read `SECURITY.md` before changing process detection, termination, updates, networking, or named pipes.
4. Read `CONTRIBUTING.md` before building, testing, or preparing a release.
5. Read `docs/TROUBLESHOOTING.md` before changing Web probes, readiness, diagnostics, or failure handling.
6. Read `docs/PERFORMANCE.md` before changing timers, polling, process inspection, HTTP probes, or UI refresh behavior.

## Names

- Product and desktop application: `DeepSeek Harness Manager`.
- Default desktop shortcut: `DSH Manager`.
- GitHub repository and npm package: `dsh-windows-manager`.
- Managed upstream runtime: `@deepseek-ai/dsh`.

Do not describe the manager package as the DSH runtime. They are separate packages with separate update paths.

## Required Invariants

- Backward compatibility is not a goal for now: the project has a single user and a single active installation/version. Prefer removing legacy fallback paths over preserving them.
- Keep normal operation in the current user context; do not add elevation or administrator requirements.
- Keep the GUI executable free of a console window.
- Bind managed DSH Web instances to `127.0.0.1` unless the product requirements explicitly change.
- Never identify DSH from a port or `node.exe` alone. Preserve HTTP and process-command fingerprints for fallback, and revalidate bridge-reported PID/port against Windows process identity before using IPC state authoritatively.
- Never terminate an unknown process automatically. Revalidate PID, start time, image path, session, services, and port ownership immediately before termination.
- Try the authenticated versioned Cordis runtime bridge before offering manual process termination.
- Never install updates silently. Network checks may be automatic; changes require explicit user confirmation.
- Keep post-update compatibility smoke testing and verified rollback intact for global npm, npx, and source runtimes.
- Preserve `%LOCALAPPDATA%\DeepSeekHarnessManager\config.json` and user data during install and upgrade.
- Treat `PreferredPort` as configuration. `3080` is only the default, and multiple instances must have unique ports.
- Keep instance `RuntimeType` (`windows` | reserved `wsl`) and `Frontend` (`web` | reserved `oh-dsh`/`custom`) in config and snapshots; only `windows` + `web` are implemented.
- `TrayEnabled=false` must keep one EXE and one primary process running Core + Supervisor + Runtime Bridge + Manager Control API; never spawn a separate daemon.
- `configure` must edit `config.json` in place and preserve unknown fields; `--autostart true` is the only path that writes the per-user Run key.
- `open()` must resolve the configured frontend through `FrontendLauncher`; reserved frontends fail explicitly and never silently fall back to Web.
- Keep `Language = auto` as a startup-time `CurrentUICulture` check, not a continuous watcher.
- Keep source compatible with C# 5 and .NET Framework 4.8.
- Keep the npm CLI dependency-free unless a dependency has a concrete, reviewed benefit.
- Commit working changes to git after each verified step (build and tests pass) so problems can be rolled back. Keep commits small and descriptive.
- Do not push to the remote, publish to GitHub or npm, or create releases unless the user explicitly requests it.

## File Map

- `src/Program.cs`: process entry point, single-instance mutex, Manager Control forwarding, and legacy external action signals.
- `src/ManagerService.cs`: `IManagerService` facade owning controllers, lifecycle orchestration, update scheduling, and frontend-open/configuration actions.
- `src/ManagerControlProtocol.cs`: Manager Control Protocol v1 wire format, per-user pipe name, version helper, and short-lived client.
- `src/ManagerControlServer.cs`: async local named-pipe server with current-user-only ACL; no TCP and no polling.
- `src/Interaction.cs`: `IManagerInteraction` user-decision boundary and silent implementation; keeps core UI-free for headless/WSL use.
- `src/Dialogs.cs`: WinForms frontend implementation of `IManagerInteraction` plus port-conflict and update-progress forms.
- `src/TrayFrontend.cs`: tray UI only; per-instance menus, language switching, notifications, and UI marshaling through `IManagerService`.
- `src/HeadlessFrontend.cs`: `TrayEnabled=false` single-process message loop; no tray icon, no notifications.
- `src/InstanceController.cs`: DSH discovery, start, stop, restart, event-driven state transitions, and IPC-bridge integration.
- `src/RuntimeAdapter.cs`: `IRuntimeAdapter` / `IRuntimeProcess` boundary and registry; only `WindowsRuntimeAdapter` is implemented.
- `src/IpcBridge.cs`: versioned named-pipe protocol client, runtime-info parsing, and persistent event connection.
- `src/PortProcess.cs`: port ownership, process identity, protection, and safe termination.
- `src/GracefulShutdown.cs`: authenticated named-pipe shutdown client with legacy compatibility.
- `src/UpdateManager.cs`: cached checks and confirmed update execution.
- `src/Configuration.cs`: config creation, normalization, and validation.
- `plugins/deepseek-harness-web/plugin.json`: DSH launch, probe, runtime, update, and bridge declarations.
- `plugins/deepseek-harness-web/cordis/windows-lifecycle.mjs`: DSH-side versioned runtime bridge plugin.
- `plugins/deepseek-harness-web/package.json` + `cordis.patch.yml`: formal installable DSH bundle metadata for the same plugin.
- `bin/dsh-windows-manager.js`: npm CLI including install, status, diagnostics, actions through Manager Control, and zero-config/`configure`.
- `scripts/Build.ps1`: deterministic runtime build.
- `scripts/Install.ps1`: per-user application installation.
- `scripts/Test.ps1`: complete local and CI test entry point.
- `scripts/Measure-Performance.ps1`: repeatable manager memory, handle, thread, and CPU sampler.

## Verification

Run the complete suite after behavioral changes:

```powershell
.\scripts\Test.ps1
```

Validate the exact npm payload and install it from the generated tarball:

```powershell
npm pack
node .\tests\npm-package.test.mjs .\dsh-windows-manager-0.3.0.tgz
```

Before a version change, update both `package.json` and `src/AssemblyInfo.cs`. Keep user-visible behavior synchronized across both README files, both security policies, both contribution guides, `docs/FEATURES.md`, and `docs/ARCHITECTURE.md` as applicable.
