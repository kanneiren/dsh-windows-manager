# Architecture

## System Context

`DeepSeek Harness Manager` is a per-user Windows tray process. It does not embed DSH. It resolves and launches an installed or source-built `@deepseek-ai/dsh` runtime, then opens the local Web UI in the user's browser.

```text
Desktop shortcut / npm CLI
            |
            v
DeepSeekHarnessManager.exe
  |         |          |
  |         |          +-- JSON configuration, state, and logs
  |         +------------- HTTP and process fingerprint probes
  +----------------------- DSH process on 127.0.0.1:<port>
                               |
                               +-- Cordis lifecycle patch
                                      |
                                      +-- authenticated local named pipe
```

## Runtime Layout

Application files are installed under:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager\app
```

This directory contains the EXE, `.exe.config`, assets, locales, plugin manifests, the Cordis bridge, license, and user documentation. It intentionally excludes source code, tests, and build tools.

The EXE embeds the desktop shortcut icon. The runtime assets directory contains only five small tray-state ICO files; source PNG and SVG files remain in the repository as source references but are not published in the runtime payload.

Mutable data is stored separately:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager
  config.json
  logs\
  state\
  runtime\
  updates\
```

Separating application and data files allows upgrades and default uninstall operations to preserve user configuration and logs.

## Host Components

`Program` creates a per-user mutex and named Windows event handles. A second invocation signals the existing tray process for `open`, `start`, `stop`, `restart`, or `exit` instead of creating a second manager.

`TrayApplication` owns the notification icon, menus, one-second state timer, language selection, and 24-hour update schedule. It creates one `InstanceController` for each configured instance.

The one-second timer keeps cross-process action signals responsive. Heavy inspection runs every second only while an instance is starting and every five seconds in stable states. A verified command-line fingerprint is reused while PID, start time, and image path remain unchanged; Windows service lookup is deferred until conflict display or termination validation.

`InstanceController` is the lifecycle state machine. It resolves a runtime, creates a temporary Cordis patch, starts DSH, probes readiness, records state, opens the Web UI, and performs graceful shutdown.

`PluginCatalog` loads JSON declarations from `plugins/`. The current DSH plugin defines global npm, Git source, and pinned npx adapters without hard-coding those commands into the tray UI.

## Instance and Port Model

`config.json` contains an `Instances` array. Each instance has a unique `Id` and `PreferredPort`, plus its runtime, workspace, profile, optional source checkout, optional `DshHome`, and pinned version.

The manager always launches DSH with explicit `--host 127.0.0.1 --port <port>` arguments. Port `3080` is only the plugin default. An external DSH process on a custom port is adopted only when that port is configured for an instance and both fingerprints match.

If the configured port is occupied by another process, the user may cancel, choose a bounded fallback port, inspect the owner, or explicitly request safe termination. The selected fallback is recorded in instance state so the running process can be found again.

One configured instance produces a flat tray menu. Multiple instances produce one submenu per instance. Desktop double-click and action commands target `DefaultInstanceId`; the npm `status` command reports all instances.

Use a separate `DshHome` for strong state isolation. Leaving it empty intentionally shares the upstream default `~/.dsh` state.

## Lifecycle Flows

Open first probes the configured and persisted port. A verified running DSH is adopted and opened. Otherwise the manager resolves a runtime, loads the Cordis patch, starts DSH, waits for both fingerprints, records PID and port, and opens the browser.

Readiness waits for up to 90 seconds after an explicit open or start request. Cleanup on timeout is limited to a process launched by that manager operation; an externally launched DSH is left running for the user to diagnose.

Start follows the same flow without opening the browser. Stop sends a random 256-bit token over the per-launch named pipe. The DSH-side plugin validates the token and calls `ctx.appExit(0)`. Manual termination is only offered after graceful shutdown is unavailable or fails.

Exit closes only the tray manager. It intentionally leaves DSH running so a later manager process can adopt it.

## Updates

Automatic checks are network reads only. Their result and actual attempt time are cached for 24 hours. A manual check bypasses the cache and resets the next automatic deadline.

Update execution always requires confirmation. Global npm updates install an exact selected version, npx updates change the configured pinned version, and source updates require a clean Git checkout before fast-forward pull, dependency installation, and build.

Every update is a transaction. The manager records the old version or source commit, applies the change, then starts the resolved runtime on a random loopback port with an isolated DSH home. Success requires HTTP and process fingerprints followed by authenticated Cordis shutdown. A failed update or smoke test rolls back and smoke-tests the restored runtime. Global npm restores the exact old version, npx restores its old pin, and source resets to the recorded commit only if the checkout remains clean. A failed rollback leaves a journal under the update data directory.

## Packaging

`scripts/Build.ps1` creates a clean `dist/` directory. `package.json` exposes `bin/dsh-windows-manager.js` and publishes a files allowlist containing only the CLI, runtime payload, installer scripts, README, and license.

Installing the npm package has no `postinstall` side effect. The explicit CLI `install` command copies the packaged runtime into LocalAppData. This makes both global npm installation and temporary npx cache installation safe: the application does not depend on the npm cache after installation.

The project intentionally has no separate MSI or Setup executable. The npm CLI and PowerShell script are the installation boundary, which avoids adding a larger bootstrapper for a small per-user file copy.
