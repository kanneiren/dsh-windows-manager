# Security Policy and Design

## Reporting a Vulnerability

After the GitHub repository is published, report suspected vulnerabilities through the repository's private Security Advisory interface. Do not include tokens, private logs, personal paths, or exploit details in a public issue.

Include the manager version, Windows version, runtime type, reproduction steps, expected behavior, and the smallest sanitized log excerpt needed to diagnose the problem.

## Trust Boundaries

The manager trusts its installed files, user-owned configuration, declared plugin manifests, and commands explicitly selected by the user. It does not trust an arbitrary process merely because it listens on the expected port or runs as `node.exe`.

The DSH Web UI is launched with `--host 127.0.0.1`. The lifecycle bridge uses a local Windows named pipe rather than a network port.

## Process Identification

A running instance is considered DSH only when the HTTP response contains declared DSH markers and the owning process command line matches declared DSH patterns. A single matching signal is insufficient for normal adoption.

Before ending any process, the manager reacquires the port owner and verifies PID, process start time, executable path, Windows session, system-directory location, and hosted Windows services. System processes, the manager itself, processes from another session, unverifiable paths, Windows-directory processes, and service hosts are protected.

Unknown processes are never terminated automatically. The user must request termination, and forced termination requires a second confirmation after a normal close attempt fails.

An externally started DSH process that does not become Web-ready is not automatically terminated on a readiness timeout. Startup cleanup is limited to a process launched by the current manager operation.

## Graceful Shutdown

Each manager-launched DSH instance receives a unique named-pipe name and a random 256-bit hexadecimal token through a generated local patch. A shutdown request must authenticate with that token before the DSH-side plugin calls `ctx.appExit(0)`.

Pipe names and tokens are launch-specific. They are not listening TCP endpoints. A failed or unavailable bridge does not silently fall back to killing the process.

## Installation and Permissions

The application manifest is `asInvoker`. Installation targets the current user's LocalAppData and desktop. The project does not register a Windows service, modify firewall rules, create an administrator task, or request elevation.

The npm package has no install lifecycle hook. Downloading it does not install or launch the Windows application; the user must explicitly invoke the CLI `install` command.

Configuration and logs are outside the replaceable application directory. Upgrade and default uninstall preserve them; complete deletion requires `--purge-data` or `-PurgeData`.

## Updates and Supply Chain

Automatic update checks never install code. Every update requires an explicit confirmation. npm updates select an exact version, npx instances retain a pinned version, and source updates refuse a dirty Git checkout.

After an update, a manager-owned smoke process runs on a random loopback port with an isolated DSH home. The update is accepted only after both fingerprints pass and authenticated graceful shutdown releases the port. Failure restores and re-tests the previous version. Source rollback uses the recorded commit only while the checkout remains clean; otherwise it preserves user changes and leaves a recovery journal instead of forcing a reset.

The npm package uses a files allowlist and a prepack validator. Windows CI performs a clean build, the complete test suite, tarball installation, and artifact generation. Release provenance should be enabled when npm publication is moved to GitHub Actions.

Package publication is pinned to the official `https://registry.npmjs.org/` endpoint. End users may still choose a trusted download mirror through their own npm configuration.

The executable is currently unsigned. Windows SmartScreen or endpoint security products may warn about a downloaded build. A reproducible local build reduces ambiguity but does not replace a trusted code-signing certificate.

## Known Residual Risks

- DSH is a developer-preview upstream dependency whose CLI, Web markers, or Cordis lifecycle API may change.
- Reading process command lines and port ownership depends on Windows APIs and can fail under unusual permissions or endpoint security controls.
- npm and Git availability depends on the user's network and configured registry or proxy.
- A malicious actor able to replace files inside the user's installed application directory already has equivalent user-level access.
- Product names and the upstream whale icon may be subject to their owner's trademark rights; the manager is not an official separate DeepSeek product.
