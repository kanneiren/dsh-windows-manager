# Manager Plugin Contract

The host recursively discovers `plugins/**/plugin.json`. Each manifest declares all product-specific behavior; the WinForms host contains no DeepSeek package name, process regex, HTTP marker, runtime command, registry endpoint, or Cordis module path.

Schema version 1 fields:

- `Id`, `DisplayName`, optional `MarketplaceUrl`, `DefaultPort`, `FallbackPortCount`.
- `Probe.UrlTemplate` and required response `Markers`.
- `ProcessPatterns` used only for identification and safe-stop eligibility.
- `Runtimes` containing command candidates, requirements, arguments, working directory and version file.
- `Update` containing npm and Git sources.
- `Companion` containing an optional Cordis lifecycle module.

Supported tokens:

- `{appDir}`
- `{pluginDir}`
- `{commandDir}`
- `{sourceRoot}`
- `{workspace}`
- `{profile}`
- `{pinnedVersion}`
- `{patchPath}`
- `{port}`

`{commandDir}` is the directory containing the resolved runtime command. It is available after `CommandCandidates` resolution for requirements, arguments, the working directory, and `VersionFile`, but not inside `CommandCandidates`. The global runtime uses it so custom npm prefixes resolve the correct package version.

Plugin manifests are trusted executable configuration. The Cordis companion is loaded into DSH with the same authority as other Harness plugins. Install plugins only from trusted sources.
