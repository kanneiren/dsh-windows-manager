# DeepSeek Harness Manager

[中文](README.md) | [**English**](README.en.md)

[![Windows CI](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml)

`DeepSeek Harness Manager` is a local tray controller for Windows 11. It starts, opens, stops, restarts, and updates DeepSeek Harness (DSH), and displays its port, process, version, and runtime status.

DSH (the npm package `@deepseek-ai/dsh`) is the program that provides the Web UI and Agent capabilities. This project installs and controls DSH on Windows; it does not include or replace DSH itself. Installing the manager creates a desktop shortcut named `DSH Manager`. Double-click it to start DSH or open the DSH Web UI. This is an independent, unofficial third-party manager; it is not affiliated with or endorsed by DeepSeek.

The manager runs with the current user's permissions and, by default, makes DSH listen only on `127.0.0.1`. It does not register a Windows service or automatically terminate unknown processes. The interface uses .NET Framework 4.8 WinForms and does not depend on PowerShell 7, Electron, or a third-party tray framework. The current `0.2.0` build has an EXE of about 115 kB and an npm tarball of about 150 kB.

## Project Documentation

- [Features and Boundaries](docs/FEATURES.md)
- [Project Architecture](docs/ARCHITECTURE.md)
- [Security Design and Vulnerability Reporting](SECURITY.en.md)
- [Web UI Troubleshooting](docs/TROUBLESHOOTING.md)
- [Performance Benchmarks and Reproduction](docs/PERFORMANCE.md)
- [Contribution and Release Process](CONTRIBUTING.en.md)
- [Coding Agent Guidelines](AGENTS.md)

## Features

- Double-click the desktop shortcut: if DSH is already running, open its Web UI directly; otherwise, start it, wait until it is ready, and then open the Web UI.
- Closing the browser does not stop DSH. The tray icon continues to show the actual process status.
- Right-click the tray icon to open, start, stop, or restart DSH; view status and version information; check for updates; open logs; or exit the manager.
- Verifies both the HTTP page and the process command instead of identifying DSH solely by `node.exe` or a port number.
- If another program is using port 3080, you can choose a free port, view details about the owner, or terminate the unknown process after explicit confirmation.
- Uses a DSH Runtime Bridge plugin and an authenticated named pipe for graceful shutdown plus authoritative PID, port, version, and lifecycle events; legacy shutdown messages remain compatible.
- Supports global npm installations, pinned-version npx installations, and Git source checkouts.
- The configuration model supports multiple profiles/instances, but creates only one Web instance by default.

## Installation and Uninstallation

### Agent Prompts for Installation and Uninstallation

To install, send the following prompt to a coding Agent with terminal access:

```text
Install the latest DeepSeek Harness Manager for the current Windows user: confirm that Node.js 18+ and npm are available, then run npx --yes dsh-windows-manager install; if the official registry fails, record the original registry and temporarily switch the registry to https://registry.npmmirror.com before continuing. After installation, confirm that the desktop shortcut exists, then run npx --yes dsh-windows-manager status --json and wait until managerRunning and webUiVerified for the default instance are true. Do not request administrator privileges, overwrite an existing config.json, or delete user data. Finally, report the installation result and any registry changes.
```

To uninstall, send the following prompt to the Agent:

```text
Uninstall DeepSeek Harness Manager for the current user: run npx --yes dsh-windows-manager uninstall to remove the application and desktop shortcut while preserving the configuration, logs, and any running DSH process; if the global CLI was installed, also run npm uninstall --global dsh-windows-manager. Do not use --purge-data or terminate DSH without my explicit confirmation. Finally, report what was removed and what was preserved.
```

The project does not provide an additional MSI, NSIS, or Setup installer. DSH itself depends on Node.js/npm, while the manager only needs to copy files into the current user's directory, create the initial configuration, and create a shortcut. Using the npm CLI, an Agent, or the source repository's `Install.cmd` keeps the release size and maintenance surface as small as possible.

From the source repository, double-click:

```text
Install.cmd
```

Installation location:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager\app
```

Configuration and logs:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager
```

The desktop shortcut launches `DeepSeekHarnessManager.exe` directly. Normal use does not display a terminal window.

The installation directory contains only the EXE, language packs, runtime adapter, icons, and documentation required to run the software. It does not contain `src`, `tests`, or build scripts. Configuration, state, and logs are stored outside the application directory, so an in-place installation does not delete user data by default.

### npm Command-Line Installation

Run it directly with:

```text
npx --yes dsh-windows-manager install
```

Alternatively, install the command globally first:

```text
npm install --global dsh-windows-manager
dsh-windows-manager install
```

Installing the npm package alone does not modify the system through `postinstall`. The application is copied and the desktop shortcut is created only when you explicitly run the `install` subcommand.

Common commands:

```text
dsh-windows-manager install --no-launch
dsh-windows-manager install --port 4000
dsh-windows-manager open
dsh-windows-manager start
dsh-windows-manager stop
dsh-windows-manager restart
dsh-windows-manager status
dsh-windows-manager uninstall
dsh-windows-manager uninstall --purge-data
```

`start` starts DSH without opening a page, while `open` starts DSH and opens the Web UI. By default, `uninstall` preserves the configuration and logs; only `--purge-data` removes everything.

`3080` is only the default port for new instances; it is not hardcoded. For a new installation, use `--port 4000` to select another port. Re-running the installation command does not overwrite the configuration of an existing installation. Instead, open the manager configuration file from the tray menu, edit the instance's `PreferredPort` in `config.json`, then exit and restart the manager. The manager explicitly passes `--port` to DSH. A manually started external DSH process can be safely adopted only when its port matches the instance configuration.

### Uninstallation

For an npm or npx installation, run:

```text
npx --yes dsh-windows-manager uninstall
```

For an installation from source, double-click `Uninstall.cmd` in the source repository.

By default, uninstallation removes the application directory and desktop shortcut, but preserves the entire data directory, including configuration, state, logs, runtime state, and update records. It also does not stop any running DSH process.

To remove all data:

```text
npx --yes dsh-windows-manager uninstall --purge-data
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Uninstall.ps1 -PurgeData
```

The second command is only for use from the source repository. If the CLI was installed globally, you can also run `npm uninstall --global dsh-windows-manager` to remove the global command.

### Network Access in Mainland China

The official npm registry is not necessarily unavailable in mainland China, but requests may time out, connections may reset, or downloads may be slow. Check it first:

```text
npm ping --registry=https://registry.npmjs.org
```

If the official registry is unavailable, switch the current user's npm registry to npmmirror:

```text
npm config set registry https://registry.npmmirror.com
npx --yes dsh-windows-manager install
```

User-level npm configuration is recommended here instead of adding `--registry` to a single `npx` command. When no global DSH installation is available, the manager also uses npx to download `@deepseek-ai/dsh`, and later user-confirmed npm updates also require an available registry. A mirror may briefly lag while synchronizing a new version. If a newly released version cannot be found, retry later or temporarily switch back to the official registry.

## Opening DSH

- Double-click the `DSH Manager` desktop shortcut: if DSH is already running, its Web UI opens directly; otherwise, DSH starts first and the Web UI opens when it is ready.
- Double-click the tray icon to open the default instance's Web UI.
- Run `npx --yes dsh-windows-manager open` from the command line to perform the same action as the desktop shortcut.

Closing the browser does not stop DSH. To start the service without opening a browser, run `npx --yes dsh-windows-manager start`.

## DSH Runtime Selection

### Automatic Selection

The default `Runtime` is `auto`, which detects local runtimes in this order:

1. The globally installed npm command `dsh.cmd`.
2. The configured Git source directory.
3. `npx.cmd`.

Local detection checks only files and PATH. It does not run npm or Git, and it does not access the network.

### Pinned-Version npx

npx mode uses:

```text
npx --yes @deepseek-ai/dsh@<PinnedVersion> ...
```

It does not silently switch to the latest version on every launch. `PinnedVersion` changes only after the user confirms an update.

### Git Source

Source users can run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Install.ps1 `
  -Runtime source `
  -SourceRoot C:\path\to\deepseek-harness
```

The source adapter validates `.git`, `package.json`, `pnpm-lock.yaml`, and `apps\cli`, then starts DSH with `pnpm dsh`. The source checkout must first complete the officially required `pnpm install` and `pnpm run build` steps.

## Tray Menu

- `Status`: running, starting, stopped, port conflict, updating, or error status.
- `Version`: the current DSH version and any available newer version.
- `Open Web UI`: open the current instance's URL.
- `Start Harness`: start the instance without automatically opening a page.
- `Stop Harness`: attempt a graceful shutdown through the Cordis bridge first.
- `Restart Harness`: restart after a graceful shutdown.
- `Check for updates`: ignore the cache and check immediately.
- `Install available update`: appears only when a newer version is found and requires another confirmation.
- `Status details`: show the port, PID, path, fingerprint, workspace, and logs.
- `Open workspace`: open the instance's working directory.
- `Open DSH settings file`: open the instance's `DSH_HOME` directory containing `settings.yaml` without launching a YAML editor.
- `DSH plugin marketplace`: open the GitHub plugin discovery page.
- `Open manager configuration file`: open the manager's `%LOCALAPPDATA%\DeepSeekHarnessManager\config.json`.
- `Open logs`: open the log directory.
- `Language / 语言`: switch between following the Windows setting, Simplified Chinese, and English.
- `About`: show the manager version and .NET runtime.
- `Exit manager (leave DSH running)`: exit only the tray manager and leave the DSH service running.

With multiple instances, each instance has its own submenu.

## Update Policy

- When the manager starts, it determines whether an automatic check is due.
- While the manager remains running, it also checks when 24 hours have elapsed since the last automatic attempt.
- Timing is based on the last actual automatic check attempt. A manual check postpones the next automatic check for 24 hours.
- If fewer than 24 hours have elapsed since the last automatic attempt, only the local cache is read.
- npm Registry HTTPS requests time out after 6 seconds.
- Git source `ls-remote` requests time out after 15 seconds.
- Automatic checks do not retry. A failed check also enters the 24-hour cooldown to prevent repeated requests during network failures.
- A manual `Check for updates` bypasses the cache.
- Updates are never installed silently and always require user confirmation.
- A global npm installation runs `npm install --global` for the exact target version.
- An npx installation updates only the pinned version in the configuration.
- A source installation runs `git pull --ff-only`, `pnpm install --frozen-lockfile`, and `pnpm run build` only when the Git working tree is clean.
- After an update, the manager uses a random local port and an isolated `DSH_HOME` to start DSH with the actual runtime arguments, verifies both HTTP and process fingerprints, and then shuts it down gracefully through Cordis.
- A failed compatibility test triggers a rollback followed by another verification: global npm restores the exact previous version, npx restores the previous pinned version, and a source installation restores the previous commit and rebuilds only if the working tree is still clean.
- Update transactions are written to `%LOCALAPPDATA%\DeepSeekHarnessManager\updates`. Logs are deleted only after the updated or restored version passes verification; they are retained for troubleshooting if rollback fails.

## Background Performance

The manager responds to external command signals once per second. Manager-launched instances with the DSH IPC bridge connected no longer run periodic WMI, process-enumeration, port, or HTTP polling: process liveness comes from the Windows process-handle exit event, and runtime state comes from authenticated named-pipe events. Fallback discovery remains for external adoption, unavailable plugins, protocol mismatch, startup, and diagnostics.

On the current test machine with 32 logical processors, the median during stable operation fell from about `118.61 MB` of working set, `70.38 MB` of private memory, `1068` handles, and `19` threads to about `65.66 MB`, `30.16 MB`, `521`, and `16`, respectively. After optimization, average whole-system CPU usage over 60 seconds was about `0.004%`. These are the 0.1.0 baseline numbers; re-run `Measure-Performance.ps1` for the event-driven 0.2.0 candidate as described in the [performance documentation](docs/PERFORMANCE.md). The process did not allocate a GPU context, and an additional 30-second stable sample showed no disk reads or writes.

## Graceful Shutdown

When the manager starts DSH, it appends a dynamic `--patch` that loads `windows-lifecycle.mjs`:

The bridge is now a versioned runtime protocol, not a single-purpose shutdown channel. The manager keeps one authenticated IPC connection and can issue `ping`, `getStatus`, `getRuntimeInfo`, and `shutdown` while receiving `ready`, `stopping`, and `exiting` events. Status and runtime info report PID, actual listening port, DSH version, profile, and DSH home from inside the DSH process; the plugin does not fabricate values it cannot obtain.


1. The plugin creates a random named pipe restricted to the local machine.
2. The manager sends a shutdown request containing a random 256-bit token.
3. The plugin calls DSH's `ctx.appExit(0)`.
4. DSH waits up to 5 seconds for the entire Cordis plugin tree to run `dispose`.
5. DSH exits after sessions, file watchers, terminals, and the HTTP service finish cleaning up.

A named pipe is not a network port and is not exposed to the local network or the internet. An externally started DSH process that did not load the companion plugin can still be adopted and opened, but stopping it explicitly prompts whether to use forced termination as a fallback.

The original single-purpose `{"action":"shutdown","token":"..."}` message is still accepted so DSH processes launched by older Manager versions remain stoppable.

The `plugins/deepseek-harness-web` directory also declares `dsh.bundle.patch`, so the same module can be installed as a regular DSH plugin package. Without a configured `pipeName` and `token` it stays inert and opens no unauthenticated pipe.



## Port Safety

For an unknown process occupying a port, the manager displays its PID, name, path, start time, and associated Windows services.

Before terminating the process, the manager verifies again that:

- The port owner still has the same PID.
- The PID's start time and image path have not changed.
- It is not a system PID, the manager itself, a process from another Windows session, or a process in a Windows system directory.
- It is not a process hosting a Windows service.

The manager first attempts a normal window close. If that fails, it asks for a second confirmation before forcing termination. It never automatically terminates an unknown process or automatically requests administrator privileges.

## Multiple Instances

`config.example.json` demonstrates a configuration in which an npm instance for daily use and a source development instance run at the same time. Each instance should use a separate port.

For strong isolation, configure a different `DshHome` for each instance. This prevents parallel processes from sharing active session state. An empty value uses the `DSH_HOME` environment variable, then defaults to `~/.dsh` when that variable is also empty. `Open DSH settings file` follows the same rule and opens the directory containing `settings.yaml`; it does not directly open YAML or `.credentials.yaml`, which may contain secrets.

The manager creates only one tray icon. With one configured instance, its actions are shown directly. With multiple instances, each instance appears as a separate submenu under its `Name`, with its own status, version, open, start, stop, restart, update, details, workspace, and DSH settings-file actions.

There is currently no graphical interface for adding instances. Open the manager configuration file from the tray menu, add configurations with unique `Id` and `PreferredPort` values under `Instances` in `config.json`, then exit and restart the manager. The desktop shortcut, tray-icon double-click, and the CLI's `open`, `start`, `stop`, and `restart` commands operate only on `DefaultInstanceId` by default. `dsh-windows-manager status` lists all instances.

## Permissions and Security Software

- Every launch uses the current user's `asInvoker` permissions from the application manifest and does not request UAC administrator authorization.
- The application manifest is fixed to `asInvoker`; routine use does not request UAC.
- Installation and configuration are both located in the current user's directories.
- The manager does not register a Windows service, configure startup, modify the firewall, or listen on `0.0.0.0`.
- If the global npm directory on another computer requires administrator privileges, the update fails and displays an error instead of elevating automatically.
- The program currently has no commercial code-signing certificate. Software such as 360 or Defender SmartScreen may display heuristic prompts when it first runs, starts Node, creates a named pipe, or terminates a process at the user's request.
- After copying the source, run `Build.cmd` locally to produce a reproducible local build. Eliminating the "Unknown publisher" warning for official releases requires a trusted code-signing certificate.

A first-run warning from SmartScreen or security software is not the same as UAC elevation. If a user asks to terminate a system process or a process from another session, or if the global npm directory requires administrator write access, the manager does not elevate automatically: the safety policy rejects the former, and the latter reports an update failure.

## Build and Test

```text
Build.cmd
Test.cmd
```

Test coverage includes:

- C# 5 / .NET Framework 4.8 builds.
- JSON plugins and configuration.
- SemVer and the 24-hour update cache.
- IPv4/IPv6 port-to-PID mapping.
- Dual HTTP and process fingerprints.
- npm, npx, and source runtime adapters.
- Adoption of an already-running instance on port 3080.
- Versioned named-pipe protocol, authentication, status queries, lifecycle events, and legacy shutdown compatibility.
- Starting a real DSH instance on a random port and shutting it down gracefully through Cordis.
- Post-update random-port compatibility smoke tests and rollback transactions for global npm, npx, and source installations.
- Isolated npm CLI installation, in-place upgrades, status queries, configuration preservation, and uninstallation.

## GitHub Actions

On every push, Pull Request, and manual trigger, `.github/workflows/windows-ci.yml` provisions a temporary GitHub-hosted `windows-latest` virtual machine. The workflow installs a pinned test version of DSH, runs `scripts\Test.ps1`, checks the contents of the npm release package, and retains the Windows build artifacts for seven days.

It affects only automated validation on GitHub. It does not remain on a user's computer or change a local installation. Public repositories can use GitHub Actions directly; actual quotas and concurrency limits depend on GitHub's current account policies.

## Icon Source

The black whale SVG comes from the official DeepSeek Harness repository:

`https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/web/public/favicon.svg`

The upstream repository is licensed under MIT. The EXE and tray-state icons retain that design. The `DSH Manager` desktop shortcut uses a separate generated icon based on the whale with an added manager badge. The repository and release package include every prebuilt icon directly; users do not need to generate them.

## License

[MIT](LICENSE)
