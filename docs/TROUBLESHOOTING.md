# Web UI Troubleshooting

Use this guide when the browser does not open, the page does not load, or the tray does not report the instance as running.

## 1. Collect the Current Status

Open the tray instance menu and select `Status details`. Record the configured port, Web UI URL, PID, process path, HTTP fingerprint, process fingerprint, DSH IPC bridge state, output log, error log, and last error.

From a terminal or coding agent, run:

```text
dsh-windows-manager status --json
```

If the CLI was not installed globally, use:

```text
npx --yes dsh-windows-manager status --json
```

Do not post an unreviewed status report or log publicly. Paths, workspace names, and DSH output can contain private information.

## 2. Interpret the Result

| Observation | Meaning | Next action |
| --- | --- | --- |
| `installed` is false | The Windows application is absent | Run the explicit install command |
| `managerRunning` is false | The tray process is not running | Start from the shortcut or run `start` |
| No process owns the configured port | DSH is stopped or exited before listening | Start it, then inspect the error log |
| Process fingerprint false | Another process owns the port | Inspect the conflict details; do not force-end it blindly |
| Process fingerprint true, HTTP false | DSH is starting, unhealthy, or serving unexpected content | Wait up to 90 seconds, then inspect both DSH logs |
| HTTP 200 but required markers missing | The port serves another page or upstream Web output changed | Verify the URL, process, and DSH version |
| Both fingerprints true | DSH is healthy from the manager's perspective | Investigate browser, proxy, extension, or cached-page behavior |
| IPC bridge connected, DSH state `ready` | DSH itself confirms the listening port, PID, version, profile, and home | Treat this as the authoritative running state; use fallback probes only for comparison |

## 3. Test the Local URL Directly

Use the exact URL shown in status details, for example:

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:3080/ -TimeoutSec 5
```

The manager deliberately uses `127.0.0.1`, not a LAN address. A corporate proxy, VPN, browser proxy extension, or security product should bypass loopback traffic. Test another browser or a private window if the HTTP probe succeeds but the browser page fails.

When the manager asks Windows to open a verified URL, it records the action in `manager.log`. If the Windows URL handler throws an error, the manager displays the URL for manual opening without changing the healthy DSH state to an application error.

If the UI remains on a loading screen but the same URL works in an InPrivate or Incognito window, the DSH process is not the cause. In the affected browser, close stale DSH tabs and try `Ctrl+Shift+R`. If that fails, open DevTools, select `Application` > `Storage`, and clear site data only for the current `127.0.0.1:<port>` origin. Then disable extensions that intercept localhost scripts or requests and verify that proxy or VPN rules bypass `127.0.0.1`.

Clearing browser site data resets browser-side workspace and current-session pointers. It does not delete DSH's server-side session logs. Do not delete `%LOCALAPPDATA%\DeepSeekHarnessManager` or `~/.dsh` to solve a browser-profile problem.

## 4. Inspect Port Ownership

Replace `3080` with the configured or active port:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 3080 | Format-List LocalAddress,LocalPort,OwningProcess
Get-CimInstance Win32_Process -Filter "ProcessId=<PID>" | Format-List ProcessId,ExecutablePath,CommandLine
```

Do not terminate the owner based only on its name. The manager's conflict dialog performs additional PID, start-time, path, session, system-directory, service-host, and port-owner checks.

## 5. Read the Logs

Use `Open logs` from the tray menu. Manager-launched instances create timestamped `.out.log` and `.err.log` files under:

```text
%LOCALAPPDATA%\DeepSeekHarnessManager\logs
```

The manager itself writes `manager.log`. Common causes include an unavailable runtime command, npm registry failure, invalid source checkout, workspace access failure, port binding failure, a DSH startup exception, or an upstream CLI/API change.

Log retention is bounded automatically at every manager startup: `manager.log` rolls over at 1 MB into `manager.log.1` (one archive kept), and instance `.out`/`.err` pairs older than 14 days or beyond the newest 20 pairs are deleted. A rolled-over `manager.log.1` does not appear in the current log tail, but remains in the same folder for comparison.

An externally launched DSH process has its own output destination, so the manager may not have its startup logs.

## 6. Check the Runtime

For a global npm runtime:

```text
dsh --version
npm view @deepseek-ai/dsh version
```

For npx, confirm npm can access the configured registry. In China mainland networks, test both the configured source and a trusted mirror as described in `README.md`.

For a source runtime, ensure `.git`, `package.json`, `pnpm-lock.yaml`, and `apps\cli` exist and the official dependency installation and build have completed.

## 7. Recover Safely

Use tray `Restart Harness` when the process is verified and the manager has a graceful shutdown bridge. If graceful shutdown is unavailable, review the warning and process identity before accepting manual termination.

Changing `PreferredPort` requires stopping the old instance and restarting the manager. Do not add multiple instances with the same preferred port.

Reinstalling the manager replaces application files but preserves `config.json`, state, and logs. Use data purge only when the user explicitly accepts permanent deletion.

## 8. Report a Reproducible Problem

Include the manager version, DSH version, runtime type, configured and active port, two fingerprint results, sanitized error tail, and exact reproduction steps. State whether direct `Invoke-WebRequest` succeeds. Follow `SECURITY.md` for security-sensitive reports.
