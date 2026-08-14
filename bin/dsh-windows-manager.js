#!/usr/bin/env node
'use strict';

const childProcess = require('child_process');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');

const packageRoot = path.resolve(__dirname, '..');
const metadata = require(path.join(packageRoot, 'package.json'));
const defaultDataRoot = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'DeepSeekHarnessManager');
const dataRoot = process.env.DSH_MANAGER_DATA_ROOT || defaultDataRoot;
const installRoot = process.env.DSH_MANAGER_INSTALL_ROOT || path.join(dataRoot, 'app');
const installedExe = path.join(installRoot, 'DeepSeekHarnessManager.exe');

function printHelp() {
  console.log(`DeepSeek Harness Manager ${metadata.version}

Usage:
  dsh-windows-manager install [options]
  dsh-windows-manager uninstall [--purge-data]
  dsh-windows-manager open|start|stop|restart|exit
  dsh-windows-manager status [--json]

Install options:
  --runtime <auto|global|npx|source>  Select the DSH runtime (default: auto)
  --source-root <path>               DeepSeek Harness source checkout
  --workspace <path>                 DSH working directory (default: current directory)
  --port <1-65535>                   Preferred DSH port (default: plugin setting)
  --no-launch                        Install without starting the manager
  --no-shortcut                      Do not create the desktop shortcut

Uninstall options:
  --purge-data                       Also remove configuration, state, and logs
  --no-shortcut                      Do not touch the desktop shortcut

General options:
  -h, --help                         Show this help
  -v, --version                      Show the package version`);
}

function fail(message, code) {
  console.error(`dsh-windows-manager: ${message}`);
  process.exitCode = code || 1;
}

function parseOptions(args, valueOptions, flagOptions) {
  const result = Object.create(null);
  for (let index = 0; index < args.length; index += 1) {
    const token = args[index];
    if (!token.startsWith('--')) throw new Error(`Unexpected argument: ${token}`);
    const separator = token.indexOf('=');
    const name = separator >= 0 ? token.slice(0, separator) : token;
    if (flagOptions.includes(name)) {
      if (separator >= 0) throw new Error(`${name} does not accept a value.`);
      result[name] = true;
      continue;
    }
    if (!valueOptions.includes(name)) throw new Error(`Unknown option: ${name}`);
    const value = separator >= 0 ? token.slice(separator + 1) : args[++index];
    if (!value || value.startsWith('--')) throw new Error(`${name} requires a value.`);
    result[name] = value;
  }
  return result;
}

function powershellPath() {
  const systemPowerShell = process.env.SystemRoot
    ? path.join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
    : '';
  return systemPowerShell && fs.existsSync(systemPowerShell) ? systemPowerShell : 'powershell.exe';
}

function runPowerShell(scriptName, args) {
  const scriptPath = path.join(packageRoot, 'scripts', scriptName);
  if (!fs.existsSync(scriptPath)) throw new Error(`The packaged installer is missing ${scriptName}.`);
  const result = childProcess.spawnSync(
    powershellPath(),
    ['-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath].concat(args),
    { stdio: 'inherit', windowsHide: true }
  );
  if (result.error) throw result.error;
  return result.status === null ? 1 : result.status;
}

function addTestPathOverrides(args) {
  if (process.env.DSH_MANAGER_INSTALL_ROOT) args.push('-InstallRoot', installRoot);
  if (process.env.DSH_MANAGER_DATA_ROOT) args.push('-DataRoot', dataRoot);
  if (process.env.DSH_MANAGER_SHORTCUT_PATH) args.push('-ShortcutPath', process.env.DSH_MANAGER_SHORTCUT_PATH);
}

function install(args) {
  const options = parseOptions(
    args,
    ['--runtime', '--source-root', '--workspace', '--port'],
    ['--no-launch', '--no-shortcut']
  );
  const runtime = options['--runtime'] || 'auto';
  if (!['auto', 'global', 'npx', 'source'].includes(runtime)) throw new Error(`Unsupported runtime: ${runtime}`);
  const port = options['--port'] === undefined ? 0 : Number(options['--port']);
  if (options['--port'] !== undefined && (!Number.isInteger(port) || port < 1 || port > 65535)) {
    throw new Error('--port must be an integer from 1 to 65535.');
  }
  const powershellArgs = [
    '-DistPath', path.join(packageRoot, 'dist'),
    '-Runtime', runtime,
    '-Workspace', path.resolve(options['--workspace'] || process.cwd())
  ];
  if (options['--source-root']) powershellArgs.push('-SourceRoot', path.resolve(options['--source-root']));
  if (port > 0) powershellArgs.push('-Port', String(port));
  if (options['--no-launch']) powershellArgs.push('-NoLaunch');
  if (options['--no-shortcut']) powershellArgs.push('-NoShortcut');
  addTestPathOverrides(powershellArgs);
  return runPowerShell('Install.ps1', powershellArgs);
}

function uninstall(args) {
  const options = parseOptions(args, [], ['--purge-data', '--no-shortcut']);
  const powershellArgs = [];
  if (options['--purge-data']) powershellArgs.push('-PurgeData');
  if (options['--no-shortcut']) powershellArgs.push('-NoShortcut');
  addTestPathOverrides(powershellArgs);
  return runPowerShell('Uninstall.ps1', powershellArgs);
}

function runManagerAction(action) {
  if (!fs.existsSync(installedExe)) {
    throw new Error(`DeepSeek Harness Manager is not installed. Run "dsh-windows-manager install" first.`);
  }
  const child = childProcess.spawn(installedExe, ['--action', action], {
    detached: true,
    stdio: 'ignore',
    windowsHide: true
  });
  child.unref();
  console.log(`Requested manager action: ${action}`);
  return 0;
}

function readJson(filePath) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch (_) {
    return null;
  }
}

function managerRunning() {
  if (!fs.existsSync(installedExe)) return false;
  const escapedPath = installedExe.replace(/'/g, "''");
  const command = [
    `$target = '${escapedPath}'`,
    "$items = @(Get-CimInstance Win32_Process -Filter \"Name='DeepSeekHarnessManager.exe'\" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $target, [System.StringComparison]::OrdinalIgnoreCase) })",
    "if ($items.Count -gt 0) { 'true' } else { 'false' }"
  ].join('; ');
  const result = childProcess.spawnSync(powershellPath(), ['-NoLogo', '-NoProfile', '-Command', command], {
    encoding: 'utf8',
    windowsHide: true
  });
  if (result.error || result.status !== 0) return null;
  return result.stdout.trim().toLowerCase() === 'true';
}

function probeWebUi(port, markers) {
  return new Promise((resolve) => {
    const request = http.get({ hostname: '127.0.0.1', port, path: '/', timeout: 1500 }, (response) => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', (chunk) => {
        if (body.length < 1024 * 1024) body += chunk;
      });
      response.on('end', () => {
        resolve(response.statusCode === 200 && markers.every((marker) => body.includes(marker)));
      });
    });
    request.on('timeout', () => request.destroy());
    request.on('error', () => resolve(false));
  });
}

async function status(args) {
  const options = parseOptions(args, [], ['--json']);
  const installed = fs.existsSync(installedExe);
  const result = {
    installed,
    installRoot,
    dataRoot,
    managerRunning: managerRunning(),
    instances: []
  };
  const config = readJson(path.join(dataRoot, 'config.json'));
  if (config && Array.isArray(config.Instances)) {
    result.instances = await Promise.all(config.Instances.map(async (instance) => {
      const state = readJson(path.join(dataRoot, 'state', `${instance.Id}.json`));
      const port = state && state.Port ? state.Port : instance.PreferredPort;
      const plugin = readJson(path.join(installRoot, 'plugins', instance.PluginId, 'plugin.json'));
      const markers = plugin && plugin.Probe && Array.isArray(plugin.Probe.Markers) ? plugin.Probe.Markers : [];
      return {
        id: instance.Id,
        name: instance.Name,
        runtime: instance.Runtime,
        port,
        recordedProcessId: state && state.ProcessId ? state.ProcessId : null,
        webUiVerified: installed && port > 0 && markers.length > 0 ? await probeWebUi(port, markers) : false
      };
    }));
  }
  if (options['--json']) {
    console.log(JSON.stringify(result, null, 2));
  } else {
    console.log(`Application: ${installed ? 'installed' : 'not installed'}`);
    console.log(`Install path: ${installRoot}`);
    console.log(`Tray manager: ${result.managerRunning === null ? 'unknown' : result.managerRunning ? 'running' : 'stopped'}`);
    for (const instance of result.instances) {
      console.log(`${instance.name} (${instance.id}): port ${instance.port}, Web UI ${instance.webUiVerified ? 'verified' : 'not verified'}`);
    }
  }
  return installed ? 0 : 3;
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length === 0 || args[0] === '--help' || args[0] === '-h' || args[0] === 'help') {
    printHelp();
    return 0;
  }
  if (args[0] === '--version' || args[0] === '-v') {
    console.log(metadata.version);
    return 0;
  }
  if (process.platform !== 'win32') throw new Error('This package supports Windows only.');
  const command = args.shift().toLowerCase();
  if (command === 'install' || command === 'i') return install(args);
  if (command === 'uninstall' || command === 'remove' || command === 'rm') return uninstall(args);
  if (command === 'status') return status(args);
  if (['open', 'start', 'stop', 'restart', 'exit'].includes(command)) {
    if (args.length > 0) throw new Error(`${command} does not accept additional arguments.`);
    return runManagerAction(command);
  }
  throw new Error(`Unknown command: ${command}. Run with --help for usage.`);
}

main().then((code) => {
  process.exitCode = code;
}).catch((error) => {
  fail(error && error.message ? error.message : String(error), 1);
});
