# Manager Adapter and DSH Plugin Contract

The host recursively discovers `plugins/**/plugin.json`. Each manifest declares all product-specific adapter behavior; the WinForms host contains no DeepSeek package name, process regex, HTTP marker, runtime command, registry endpoint, or Cordis module path. The directory name `plugins/` is retained for on-disk compatibility; each entry is conceptually an **adapter plus a bundled DSH Runtime Bridge plugin**.

Schema version 1 fields:

- `Id`, `DisplayName`, optional `MarketplaceUrl`, `DefaultPort`, `FallbackPortCount`.
- `Probe.UrlTemplate` and required response `Markers`. These are fallback/adoption/diagnostic probes; they are not the steady-state source when the bridge is connected.
- `ProcessPatterns` used for identification and safe-stop eligibility.
- `Runtimes` containing command candidates, requirements, arguments, working directory and version file.
- `Update` containing npm and Git sources.
- `Companion` containing the Cordis bridge module, entry id, and `BridgeProtocolVersion`.

## Runtime Bridge Protocol

The Companion module in `cordis/windows-lifecycle.mjs` is a Cordis function plugin. Manager launches load it through a generated per-launch `--patch`; the same module can also be installed as a DSH bundle because `package.json` declares `dsh.bundle.patch`. Without a configured `pipeName` and `token`, the bundle entry stays inert.

New messages are newline-delimited JSON:

```json
{ "protocolVersion": 1, "messageType": "command", "requestId": "r1", "type": "getStatus", "token": "<64 hex>", "payload": {} }
```

Responses use `messageType: "response"`, echo `requestId`, and carry `ok`, `payload`, and `error`. Events use `messageType: "event"` with type `ready`, `stopping`, or `exiting`. Commands are restricted to `ping`, `getStatus`, `getRuntimeInfo`, and `shutdown`; no arbitrary command execution exists. Unsupported protocol versions, malformed JSON, unknown commands, and invalid tokens receive explicit errors.

Status payloads contain only values available inside DSH: `pid`, actual bound `port`, `host`, `dshVersion` when the `@deepseek-ai/dsh` manifest can be resolved from the running entry script, launched `profile`, resolved `dshHome`, `nodeVersion`, and `cwd`.

The original `{"action":"shutdown","token":"..."}` envelope remains accepted so a Manager can still stop a DSH process that was launched by an older Manager version.

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

Plugin manifests and Cordis companions are trusted executable configuration loaded with the same authority as other Harness plugins. Install adapters and DSH bundles only from trusted sources.
