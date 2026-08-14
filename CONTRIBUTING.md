# Contributing

## Requirements

- Windows 11 or a compatible Windows environment with .NET Framework 4.8.
- Node.js 18 or newer and npm.
- Chrome or Edge only when regenerating icon assets.
- The DSH version declared by `plugins/deepseek-harness-web/plugin.json` for the real integration test.

The application intentionally compiles with the .NET Framework C# 5 compiler. Do not introduce newer C# syntax without an explicit runtime and toolchain migration decision.

## Build and Test

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
```

`Test.ps1` is the authoritative test entry point used by Windows CI. It compiles the application and tests, runs C# coverage scenarios, starts a real DSH instance on a temporary port, verifies graceful shutdown, tests the Node named-pipe bridge, and exercises npm CLI installation in isolated directories.

For idle-performance changes, install the candidate build, allow startup work to settle, then run:

```powershell
.\scripts\Measure-Performance.ps1 -DurationSeconds 30
```

Compare median working set, private memory, handles, threads, and average CPU under the same DSH state and machine conditions.

Validate the distributable package separately:

```powershell
npm pack
node .\tests\npm-package.test.mjs .\dsh-windows-manager-0.1.0.tgz
```

## Change Rules

- Prefer the smallest change that preserves the security invariants in `AGENTS.md` and `SECURITY.md`.
- Add or update a test for observable behavior changes.
- Update user-facing documentation in the same change.
- Keep English and Simplified Chinese locale key sets identical.
- Do not commit `dist/`, npm tarballs, logs, local configuration, or user data.
- Do not bundle secrets, npm tokens, signing certificates, or machine-specific paths.

## Versioning

The npm version in `package.json`, lockfile version, `AssemblyInformationalVersion`, `AssemblyFileVersion`, and release tag must agree. DSH's `BundledVersion` is independent and should only change when compatibility has been tested.

## Pull Requests

A pull request should explain the user-visible change, security implications, tests performed, and documentation updated. Windows CI must pass before merge.

## Release Checklist

1. Confirm the npm package name and GitHub repository metadata.
2. Run the complete test suite on Windows.
3. Run `npm pack` and inspect the files allowlist.
4. Install from the generated tarball into an isolated directory.
5. Perform a real per-user upgrade and verify that `config.json` is unchanged.
6. Verify both locale files, plugin payload, desktop shortcut, tray process, and DSH Web fingerprint.
7. Scan tracked files for credentials and private data.
8. Create a GitHub release artifact and checksums.
9. Publish npm only after the GitHub tag and release are final.

The package fixes its publication target to `https://registry.npmjs.org/` through `publishConfig`. This does not change the registry users select for installation and prevents a maintainer's download mirror from becoming the accidental publish target.
