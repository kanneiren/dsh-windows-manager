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
- Keep `Language = auto` as a startup-time `CurrentUICulture` check, not a continuous watcher.
- Keep source compatible with C# 5 and .NET Framework 4.8.
- Keep the npm CLI dependency-free unless a dependency has a concrete, reviewed benefit.
- Do not publish to GitHub or npm, create releases, or commit changes unless the user explicitly requests it.

## File Map

- `src/Program.cs`: process entry point, single-instance mutex, external action signals.
- `src/TrayApplication.cs`: tray UI, per-instance menus, marketplace link, language switching, UI notification coalescing.
- `src/InstanceController.cs`: DSH discovery, start, stop, restart, event-driven state transitions, and IPC-bridge integration.
- `src/IpcBridge.cs`: versioned named-pipe protocol client, runtime-info parsing, and persistent event connection.
- `src/PortProcess.cs`: port ownership, process identity, protection, and safe termination.
- `src/GracefulShutdown.cs`: authenticated named-pipe shutdown client with legacy compatibility.
- `src/UpdateManager.cs`: cached checks and confirmed update execution.
- `src/Configuration.cs`: config creation, normalization, and validation.
- `plugins/deepseek-harness-web/plugin.json`: DSH launch, probe, runtime, update, and bridge declarations.
- `plugins/deepseek-harness-web/cordis/windows-lifecycle.mjs`: DSH-side versioned runtime bridge plugin.
- `plugins/deepseek-harness-web/package.json` + `cordis.patch.yml`: formal installable DSH bundle metadata for the same plugin.
- `bin/dsh-windows-manager.js`: npm CLI.
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
node .\tests\npm-package.test.mjs .\dsh-windows-manager-0.2.0.tgz
```

Before a version change, update both `package.json` and `src/AssemblyInfo.cs`. Keep user-visible behavior synchronized across both README files, both security policies, both contribution guides, `docs/FEATURES.md`, and `docs/ARCHITECTURE.md` as applicable.
