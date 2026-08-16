# Architecture

## System Context

`DeepSeek Harness Manager` is a per-user Windows tray process. It does not embed DSH. It resolves and launches an installed or source-built `@deepseek-ai/dsh` runtime, then opens the local Web UI in the user's browser.

```text
Desktop shortcut / npm CLI
            |
            v
DeepSeekHarnessManager.exe (Native Windows Supervisor)
  |         |          |
  |         |          +-- JSON configuration, state, and logs
  |         +------------- OS process-exit event + fallback HTTP/process probes
  +----------------------- DSH process on 127.0.0.1:<port>
                              |
                              +-- DSH Windows Manager Plugin (Cordis)
                                     |
                                     +-- authenticated local named pipe
                                     |   versioned protocol:
                                     |   ping / getStatus / getRuntimeInfo / shutdown
                                     |   events: ready / stopping / exiting
```

## Runtime Layout

Application files are installed under:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager\app
```

This directory contains the EXE, `.exe.config`, assets, locales, plugin manifests, the Cordis bridge, license, and user documentation. It intentionally excludes source code, tests, and build tools.

The EXE embeds the base whale icon. The runtime assets directory contains five small tray-state ICO files plus a separate whale-based manager icon for the desktop shortcut; source PNG and SVG files remain in the repository as source references but are not published in the runtime payload.

Mutable data is stored separately:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager
  config.json
  logs\
    wsl\<instance-id>\   ← Manager-launched WSL DSH stdout/stderr
  state\
  runtime\
  updates\
```

Separating application and data files allows upgrades and default uninstall operations to preserve user configuration and logs.

## Host Components

`Program` creates a per-user mutex. A second invocation forwards its action through the Manager Control pipe and exits; there is no named-event fallback and never a second manager. The process stays a single EXE: `Program` constructs `ManagerService` (core), starts `ManagerControlServer`, and then runs either `TrayFrontend` (`TrayEnabled=true`) or `HeadlessFrontend` (`TrayEnabled=false`) against the same `IManagerService`.

`ManagerService` implements `IManagerService` and owns the configured `InstanceController` list, lifecycle actions, 24-hour update scheduling, frontend-open actions, and configuration access. `TrayFrontend` owns only the notification icon, menus, language selection, balloons, and UI marshaling; it no longer constructs or calls controllers and update infrastructure directly.

The dependency direction is:

```text
TrayFrontend
    |
    v
IManagerService
    |
    v
ManagerService
    |
    v
DshSupervisor / InstanceController / UpdateManager / Runtime Bridge
```

`ManagerService`, `InstanceController`, `UpdateManager`, and process-safety code do not reference `NotifyIcon`, tray menus, `MessageBox`, or `IWin32Window`. In headless mode, `SilentManagerInteraction` handles decisions without UI and the same Manager Control pipe remains the only external interface. `HeadlessFrontend` keeps the WinForms message loop in the same EXE solely for timers and synchronization; it owns no tray resources. User decisions cross the `IManagerInteraction` boundary; `WinFormsManagerInteraction` in `Dialogs.cs` is the tray implementation, and `SilentManagerInteraction` exists for tests and future headless use. Both layers still compile into the single `DeepSeekHarnessManager.exe` process; this is logical decoupling only, not process separation.

The one-second WinForms timer exists for lifecycle ticking and to coalesce IPC/process notifications into UI updates. State detection is event-driven when possible:

- A manager-launched DSH process is monitored through the Windows `Process.Exited` event backed by the native process handle.
- When the DSH plugin is reachable, `IpcBridgeConnection` keeps an authenticated named-pipe connection open and receives `ready`, `stopping`, and `exiting` events plus authoritative status responses.
- WMI/HTTP/port inspection is no longer the primary stable-state path. It remains available for startup fallback, external adoption, plugin failure, protocol mismatch, diagnostics, and manual safe-termination checks.

`InstanceController` is the lifecycle state machine. It resolves a runtime, creates a temporary Cordis patch, starts DSH, connects the authenticated IPC bridge, waits for the authoritative `ready` event (using the bounded startup fallback cadence until the bridge connects), records state, opens the Web UI, and performs graceful shutdown.

`PluginCatalog` loads JSON declarations from `plugins/`. The current DSH plugin defines global npm, Git source, and pinned npx adapters without hard-coding those commands into the tray UI. The directory name is retained for installed-layout compatibility; conceptually each entry is an adapter plus its bundled DSH plugin. Renaming to `adapters/` would touch installed paths, persisted plugin ids, and packaging for little runtime benefit, so it remains a documented follow-up rather than an in-place rename.

## Instance and Port Model

`config.json` contains an `Instances` array. Each instance has a unique `Id` and `PreferredPort`, plus its runtime, workspace, profile, optional source checkout, optional `DshHome`, pinned version, `RuntimeType`, and `Frontend`.

`RuntimeType` is `windows` or `wsl`. Only `windows` is implemented; the field is reserved now so configuration and snapshots do not hard-code a Windows singleton. `Frontend` is `web`, `oh-dsh`, or `custom`; only `web` is implemented.

Instance state records `Ownership`: `managed` when this Manager launched the process and has full lifecycle control, `attached` when the process was started externally and was later discovered/adopted. A Manager restart keeps `managed` ownership only when the persisted PID still matches the verified process.

The manager always launches DSH with explicit `--host 127.0.0.1 --port <port>` arguments. Port `3080` is only the plugin default. An external DSH process on a custom port is adopted only when that port is configured for an instance and both fingerprints match.

If the configured port is occupied by another process, the user may cancel, choose a bounded fallback port, inspect the owner, or explicitly request safe termination. The selected fallback is recorded in instance state so the running process can be found again.

One configured instance produces a flat tray menu. Multiple instances produce one submenu per instance. Each instance menu resolves its own `DSH_HOME` and can open the directory containing `settings.yaml`; the global manager configuration item opens `config.json` directly. Desktop double-click and action commands target `DefaultInstanceId`; the npm `status` command reports all instances.

Use a separate `DshHome` for strong state isolation. An empty value inherits `DSH_HOME` and falls back to the upstream default `~/.dsh` when that environment variable is also empty.

## Configuration

The Manager keeps zero-config defaults for one Windows DSH + Web frontend. Advanced configuration is available through:

```text
dsh-windows-manager configure
dsh-windows-manager configure --runtime windows --frontend web --tray true --shortcut false --autostart true
```

The CLI edits `config.json` in place and preserves unknown fields. `--runtime` sets the default instance `RuntimeType`; `--frontend` sets `Frontend`; `--tray` sets `TrayEnabled`; `--shortcut` creates/removes the desktop shortcut; `--autostart` updates the per-user Run key only when explicitly requested. `--wsl-distro` enables and selects a WSL distro for `--runtime wsl`.

WSL is disabled by default. The CLI exposes explicit on-demand commands:

```text
wsl detect
wsl enable [--distro <name>]
wsl status [--json]
wsl disable
```

Detection runs `wsl.exe --status` and `wsl.exe --list --quiet` only when invoked; no background WSL polling exists.

## Runtime Adapter Boundary

`IRuntimeAdapter` is the boundary between the Supervisor and a concrete runtime host:

```text
RuntimeAdapters.Get(instance)
    → windows  WindowsRuntimeAdapter
    → wsl      reserved; throws "adapter not implemented"
```

A runtime adapter owns:

```text
Resolve(runtime command + arguments)
ResolveInstalledVersion
Start(IRuntimeProcess)
CaptureIdentity
Kill
```

`IRuntimeProcess` abstracts the native process handle so a future WSL adapter can expose a wsl.exe-launched process without changing `InstanceController` state transitions. Fallback discovery is intentionally not abstracted yet: the current Windows path still uses `PortMap`/`ProcessInspector`, and a future WSL implementation should use bridge-first state plus adapter-specific fallback rather than WMI PID guessing.

The Runtime Bridge remains protocol-oriented and transport-oriented separately:

```text
Manager Protocol ≠ Transport
Windows Native → named pipe
Future WSL     → loopback + strong authentication token (to be evaluated)
```

## WSL Adaptation

Implemented: Windows Manager manages a DSH process inside WSL2. No Manager binary, daemon, service, or supervisor is installed or run inside WSL; the only WSL-side component is the DSH process itself plus the generated DSH Runtime Bridge `--patch`. All management commands originate from `DeepSeekHarnessManager.exe` on Windows through `wsl.exe`. WSL1 and Linux-native management are out of scope for the first implementation.

Discovery and launch are on demand only, never steady polling:

```text
wsl.exe --list --quiet                 → distro list
wsl.exe -d <distro> -- bash -lic ...   → DSH detection / PATH / nvm setup
wsl.exe -d <distro> --cd <dir> -- bash -lic 'exec dsh ...' → launch
```

The WSL adapter keeps `exec` semantics so the Windows `wsl.exe` process lifetime mirrors DSH. The Windows `wsl.exe` process is the liveness handle; authoritative PID, port, version, and state come from the Runtime Bridge inside WSL. Linux PIDs are never guessed through WMI.

Transport:

```text
Windows Native → named pipe
WSL2           → loopback TCP + the same 256-bit launch token
```

The newline-delimited Runtime Bridge protocol stays unchanged. The Manager reserves/validates a loopback port on the Windows side, passes it through the generated per-launch patch, and revalidates bridge-reported PID/port before trusting it. DSH remains bound to loopback/localhost only; no LAN exposure.

Lifecycle:

```text
stop       → versioned bridge shutdown
fallback   → verified kill of the recorded Linux PID via wsl.exe
never      → default wsl.exe --terminate <distro> (too broad)
```

Externally started WSL DSH is adopted as `attached`. It is detected from its Linux PID and listening port, can be opened and monitored immediately, and can be stopped by revalidating the Linux PID/port and sending `kill` inside the distro after explicit confirmation. Full lifecycle control (restart/update) is available after the Manager starts its own WSL DSH with the Runtime Bridge.

Config additions planned:

```text
RuntimeType = wsl
WslDistro   = <distro selected from wsl.exe --list --quiet>
```

The distro is always user-selected/configured. The Manager never assumes Ubuntu or any fixed distribution name.

Workspace path conversion uses `wsl.exe wslpath`. Diagnostics report `runtimeType=wsl`, the Linux DSH PID from the bridge, and the Windows `wsl.exe` handle PID.

## Frontend Launch

`FrontendLauncher` resolves the configured instance `Frontend`:

```text
web    -> http://127.0.0.1:{port}/ from the plugin probe template
oh-dsh -> reserved; returns an explicit not-configured error, never falls back to web
custom -> reserved; returns an explicit not-configured error
```

Tray menu labels and `getStatus`/`listInstances` responses expose the configured frontend. Manager opens only the selected frontend; it does not implement Chat, Session, Terminal, TUI, or Desktop UI.

## IPC Bridge Protocol

The Runtime Bridge plugin has been upgraded from a single-purpose shutdown channel to a versioned runtime bridge. Every new message is one JSON object per line and carries `protocolVersion`, `messageType`, `requestId` (commands/responses), `type`, `payload`, and `error`.

Supported commands:

- `ping`
- `getStatus`
- `getRuntimeInfo`
- `shutdown`

Supported events:

- `ready` — the DSH-side plugin observed the Web server's actual listening port.
- `stopping` — shutdown was requested through the bridge or Cordis disposal began.
- `exiting` — the DSH exit path was entered after an accepted bridge shutdown.

Requests require the same 256-bit launch token; malformed messages, unsupported protocol versions, unknown commands, and unauthorized requests get explicit error responses. No unauthenticated local process can query or control DSH.

`getStatus`/`getRuntimeInfo` only report values the plugin can observe reliably: `process.pid`, the actual `webServer.port`, the configured launch profile, `DSH_HOME` resolution, and the DSH version found by walking from `process.argv[1]` to the `@deepseek-ai/dsh` package manifest. The version and port are omitted/null rather than guessed when unavailable.

`plugins/deepseek-harness-web` is also a formal DSH bundle (`package.json` declares `dsh.bundle.patch`), while Manager-launched instances continue to use the per-launch `--patch` so each process gets a unique pipe and token. The bundle entry is inert until configured, which keeps the dynamic patch as the compatibility layer during the DSH API preview.

## Manager Control Protocol

The Manager exposes a separate local named pipe for the npm CLI and future third-party frontends:

```text
\\.\pipe\dsh-windows-manager-control-{user-sid}
```

It is completely separate from the Manager↔DSH Runtime Bridge Protocol. The pipe is local-only, protected by a DACL that allows only the current Windows user, and is served by an async accept loop (`ManagerControlServer`) inside the primary Manager process. There is no TCP listener and no periodic status polling.

Protocol v1 commands:

```text
getVersion
getStatus
listInstances
start
stop
restart
open
exit
```

Every response carries `protocolVersion: 1`. Unknown commands, malformed JSON, and unsupported protocol versions receive explicit error responses. The protocol intentionally has no `runCommand`, PowerShell, npm proxy, or arbitrary file read/write commands.

A second Manager invocation is accepted only through the control pipe; it retries briefly while the primary starts and never creates a second Supervisor.

## State Source Selection

```text
                +-- Plugin IPC available
                |   authoritative status + lifecycle events
Manager ---------+
                +-- Plugin unavailable
                    fallback discovery
                    port map / HTTP markers / process fingerprint
```

For a Manager-owned process, the OS process handle is the liveness source and the bridge is the state source. Stable running instances no longer perform periodic WMI queries or loopback HTTP probes. Fallback discovery still runs when:

- no runtime bridge credentials were persisted;
- DSH was started externally without the plugin;
- the plugin failed to load;
- the bridge protocol is incompatible;
- the user asks for details, port-conflict diagnostics, or safe-termination validation.


## Responsibility Boundary

Native Supervisor keeps everything that must work after DSH dies:

- tray lifecycle and WinForms UI;
- start, bootstrap, and runtime resolution;
- restart orchestration (graceful stop -> observe process exit -> start);
- crash detection and cleanup of Manager-owned processes;
- update transaction, compatibility smoke test, and rollback;
- external process adoption;
- port-conflict diagnostics and guarded manual termination.

The DSH plugin only owns capabilities that require a live DSH:

- authenticated `shutdown` primitive via `ctx.appExit(0)`;
- authoritative `pid`, actual listening `port`, runtime state;
- `ping`/`getStatus`/`getRuntimeInfo` queries;
- `ready`, `stopping`, and `exiting` lifecycle events.

The plugin never restarts, updates, rolls back, or supervises DSH: if DSH crashes, the plugin dies with it.


## Lifecycle Flows

Open first prefers an authoritative bridge inspection when available; otherwise it probes the configured and persisted port. A verified running DSH is adopted and opened. Otherwise the manager resolves a runtime, creates the per-launch patch, starts DSH, connects the bridge, waits for the `ready` event or the startup fallback inspection, records PID and port, and opens the browser.

Readiness waits for up to 90 seconds after an explicit open or start request. Cleanup on timeout is limited to a process launched by that manager operation; an externally launched DSH is left running for the user to diagnose.

Start follows the same flow without opening the browser. Stop sends a versioned authenticated shutdown request over the per-launch named pipe; the DSH-side plugin validates the token and calls `ctx.appExit(0)`. The Manager then waits on the process handle. Manual termination is only offered after graceful shutdown is unavailable or fails. Restart is Supervisor-orchestrated: Manager stops DSH, observes process exit, then starts a new process.

Exit closes only the tray manager. It intentionally leaves DSH running so a later manager process can adopt it.

## Updates

Automatic checks are network reads only. Their result and actual attempt time are cached for 24 hours. A manual check bypasses the cache and resets the next automatic deadline.

Update execution always requires confirmation. Global npm updates install an exact selected version, npx updates change the configured pinned version, and source updates require a clean Git checkout before fast-forward pull, dependency installation, and build.

Every update is a transaction. The manager records the old version or source commit, applies the change, then starts the resolved runtime on a random loopback port with an isolated DSH home. Success requires HTTP and process fingerprints followed by authenticated Cordis shutdown. A failed update or smoke test rolls back and smoke-tests the restored runtime. Global npm restores the exact old version, npx restores its old pin, and source resets to the recorded commit only if the checkout remains clean. A failed rollback leaves a journal under the update data directory.

## Diagnostics

`IManagerService.GetDiagnosticsText()` builds a lightweight text snapshot from the live `ManagerSnapshot`: Manager version/PID, tray mode, and per instance state, ownership, PID, port, DSH version, frontend, DSH_HOME, working directory, Runtime Bridge state/version/protocol version, start time, last start result, and last exit reason.

The tray exposes `Copy diagnostics`, `Open Manager logs`, and `Open DSH logs`. The npm CLI exposes `dsh-windows-manager diagnostics [--json]`, which uses the Manager Control `getStatus` response when a primary is running and local config/state otherwise. There is no log database or Log Viewer.

## Packaging

`scripts/Build.ps1` creates a clean `dist/` directory. `package.json` publishes the CLI, `dist/` runtime payload, installer scripts, `docs/`, `AGENTS.md`, both contribution guides, both security policies, both README files, and `LICENSE`.

Installing the npm package has no `postinstall` side effect. The explicit CLI `install` command copies the packaged runtime into LocalAppData. This makes both global npm installation and temporary npx cache installation safe: the application does not depend on the npm cache after installation.

The project intentionally has no separate MSI or Setup executable. The npm CLI and PowerShell script are the installation boundary, which avoids adding a larger bootstrapper for a small per-user file copy.
