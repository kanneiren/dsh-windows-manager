# DeepSeek Harness Manager

[中文](README.md) | [**English**](README.en.md)

[![Windows CI](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml)

**A DeepSeek Harness (DSH) tray manager for both Windows and WSL2.**

`DeepSeek Harness Manager` is a native Windows tray supervisor that installs, starts, opens, stops, restarts, and updates DSH either on Windows or inside a WSL2 distribution, while showing port, process, version, and running state. It is an independent third-party manager; it does not contain or replace DSH itself (npm package `@deepseek-ai/dsh`).

## Use Cases

- **Native Windows**: npm global, pinned npx, and Git source runtimes; the Start Menu/desktop shortcut opens only the tray, and the Web UI is started from the tray menu or CLI.
- **WSL2 Linux**: the Manager stays on Windows while DSH runs in the selected WSL2 distro. Helper distros from Docker Desktop / Rancher Desktop / Podman are ignored automatically, and a general-purpose distro such as Ubuntu or Debian is selected without installing anything inside WSL.
- **Tray resident**: closing the browser does not stop DSH; the tray menu offers open, start/stop, status, updates, logs, and language switching.
- **Multiple instances**: Windows and WSL instances can coexist, each with its own port, state, and lifecycle.

## Quick Start

Windows (PowerShell):

```powershell
npm install --global dsh-windows-manager
dsh-windows-manager install
dsh-windows-manager open
```

By default, install creates only a **Start Menu shortcut** (searchable with the Win key) and opens the tray without starting DSH. Add `--desktop-shortcut` for a desktop shortcut, or `--no-shortcut` to create no shortcuts at all.

WSL2 (run from a Windows terminal):

```powershell
dsh-windows-manager wsl detect
dsh-windows-manager wsl enable --distro Ubuntu-24.04
dsh-windows-manager wsl open
```

`wsl enable --distro` may be omitted: the Manager automatically ignores Docker Desktop helper distros and selects a general-purpose distro; the tray action `Open or start WSL DSH` works as well. For Mainland China networks, see the npmmirror section in the [usage guide](docs/USAGE.md).

## Project Documentation

| Document | Contents |
| --- | --- |
| [Installation and Usage Guide](docs/USAGE.md) | Install/uninstall, CLI, runtime selection, tray menu, update and operations details |
| [Feature Guide](docs/FEATURES.md) | Current capabilities, entry points, and deliberate exclusions |
| [Architecture](docs/ARCHITECTURE.md) | Components, configuration model, Runtime Bridge, WSL adaptation, and packaging |
| [Security and Vulnerability Reporting](SECURITY.md) | Threat model, boundaries, and reporting process |
| [Web UI Troubleshooting](docs/TROUBLESHOOTING.md) | Common issues, logs, and recovery steps |
| [Performance](docs/PERFORMANCE.md) | Steady-state resource usage and reproduction |
| [Contributing and Releases](CONTRIBUTING.md) | Build, test, release, and community conventions |
| [Coding Agent Guide](AGENTS.md) | Hard constraints for automated coding agents |

## License

[MIT](LICENSE)
