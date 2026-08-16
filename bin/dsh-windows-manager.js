#!/usr/bin/env node
'use strict';

const childProcess = require('child_process');
const fs = require('fs');
const http = require('http');
const net = require('net');
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
  dsh-windows-manager diagnostics [--json]
  dsh-windows-manager configure [options]
  dsh-windows-manager wsl status|detect [--json]
  dsh-windows-manager wsl enable [--distro <name>]
  dsh-windows-manager wsl disable

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

Configure options:
  --runtime <windows|wsl>            Set the default instance runtime type
  --frontend <web|oh-dsh|custom>     Set the default instance frontend
  --tray <true|false>                Enable or disable the tray
  --shortcut <true|false>            Create or remove the desktop shortcut
  --autostart <true|false>           Enable or disable Start with Windows

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

async function runManagerAction(action) {
  if (!fs.existsSync(installedExe)) {
    throw new Error(`DeepSeek Harness Manager is not installed. Run "dsh-windows-manager install" first.`);
  }
  const info = managerInfo();
  if (info.running && info.sid) {
    try {
      const response = await requestControl(action, null, info.sid);
      if (response && response.ok === true) {
        console.log(`Manager accepted action: ${action}`);
        return 0;
      }
      if (response && response.error && response.error.message) {
        throw new Error(response.error.message);
      }
      throw new Error('The Manager returned an invalid control response.');
    } catch (error) {
      console.error(`dsh-windows-manager: ${error.message}`);
      return 1;
    }
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
    const text = fs.readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '');
    return JSON.parse(text);
  } catch (_) {
    return null;
  }
}

function managerInfo() {
  const info = { running: false, sid: null, unknown: false };
  if (!fs.existsSync(installedExe)) return info;
  const escapedPath = installedExe.replace(/'/g, "''");
  const command = [
    `$target = '${escapedPath}'`,
    "$items = @(Get-CimInstance Win32_Process -Filter \"Name='DeepSeekHarnessManager.exe'\" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $target, [System.StringComparison]::OrdinalIgnoreCase) })",
    "$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value",
    "'running=' + ($items.Count -gt 0) + '|sid=' + $sid"
  ].join('; ');
  const result = childProcess.spawnSync(powershellPath(), ['-NoLogo', '-NoProfile', '-Command', command], {
    encoding: 'utf8',
    windowsHide: true
  });
  if (result.error || result.status !== 0) {
    info.unknown = true;
    return info;
  }
  const output = result.stdout.trim();
  const runningMatch = /(?:^|\n)running=(true|false)/i.exec(output);
  const sidMatch = /sid=([A-Za-z0-9-]+)/i.exec(output);
  info.running = runningMatch ? runningMatch[1].toLowerCase() === 'true' : false;
  info.sid = sidMatch ? sidMatch[1] : null;
  return info;
}

function controlPipeName(sid) {
  return `\\\\.\\pipe\\dsh-windows-manager-control-${sid.replace(/-/g, '_')}`;
}

function requestControl(command, instanceId, sid) {
  return new Promise((resolve, reject) => {
    if (!sid) {
      reject(new Error('The current Windows user SID is unavailable.'));
      return;
    }
    let socket;
    try {
      socket = net.createConnection(controlPipeName(sid));
    } catch (error) {
      reject(error);
      return;
    }
    let responseText = '';
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error('The Manager control request timed out.'));
    }, 5000);
    socket.setEncoding('utf8');
    socket.once('connect', () => {
      const request = { protocolVersion: 1, command };
      if (instanceId) request.instanceId = instanceId;
      socket.write(`${JSON.stringify(request)}\n`);
    });
    socket.on('data', (chunk) => {
      responseText += chunk;
      if (responseText.length > 1024 * 1024) {
        socket.destroy();
        clearTimeout(timeout);
        reject(new Error('The Manager control response was too large.'));
      }
    });
    socket.once('close', () => {
      clearTimeout(timeout);
      try {
        resolve(JSON.parse(responseText));
      } catch (error) {
        reject(new Error('The Manager control response was invalid JSON.'));
      }
    });
    socket.once('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
  });
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
  const info = managerInfo();
  const config = readJson(path.join(dataRoot, 'config.json'));
  const result = {
    installed,
    installRoot,
    dataRoot,
    managerRunning: info.unknown ? null : info.running,
    trayEnabled: config && typeof config.TrayEnabled === 'boolean' ? config.TrayEnabled : true,
    managerPid: null,
    managerVersion: null,
    protocolVersion: null,
    instances: []
  };
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
        runtimeType: instance.RuntimeType || 'windows',
        frontend: instance.Frontend || 'web',
        ownership: state && state.Ownership ? state.Ownership : null,
        port,
        recordedProcessId: state && state.ProcessId ? state.ProcessId : null,
        webUiVerified: installed && port > 0 && markers.length > 0 ? await probeWebUi(port, markers) : false
      };
    }));
  }

  let controlStatus = null;
  if (info.running && info.sid) {
    try {
      controlStatus = await requestControl('getStatus', null, info.sid);
    } catch (_) {
      controlStatus = null;
    }
  }
  if (controlStatus && controlStatus.ok === true) {
    result.managerPid = controlStatus.managerPid || null;
    result.managerVersion = controlStatus.managerVersion || null;
    result.protocolVersion = controlStatus.protocolVersion || null;
    result.trayEnabled = typeof controlStatus.trayEnabled === 'boolean' ? controlStatus.trayEnabled : result.trayEnabled;
    if (Array.isArray(controlStatus.instances)) {
      for (const instance of result.instances) {
        const controlInstance = controlStatus.instances.find((item) => item.instanceId === instance.id);
        if (!controlInstance) continue;
        instance.state = controlInstance.state || null;
        instance.ownership = controlInstance.ownership || instance.ownership;
        instance.runtimeType = controlInstance.runtime || instance.runtimeType;
        instance.frontend = controlInstance.frontend || instance.frontend;
        instance.pid = controlInstance.pid || instance.recordedProcessId;
        instance.port = controlInstance.port || instance.port;
        instance.startedAt = controlInstance.startedAt || null;
        instance.runtimeBridgeState = controlInstance.runtimeBridgeState || null;
        instance.runtimeBridgeVersion = controlInstance.runtimeBridgeVersion || null;
        instance.webUiVerified = controlInstance.state === 'running';
      }
    }
  }

  if (options['--json']) {
    console.log(JSON.stringify(result, null, 2));
  } else {
    console.log(`Application: ${installed ? 'installed' : 'not installed'}`);
    console.log(`Install path: ${installRoot}`);
    console.log(`Tray manager: ${result.managerRunning === null ? 'unknown' : result.managerRunning ? 'running' : 'stopped'}`);
    if (result.managerPid) console.log(`Manager PID: ${result.managerPid}`);
    console.log(`Tray: ${result.trayEnabled ? 'enabled' : 'disabled'}`);
    for (const instance of result.instances) {
      const owner = instance.ownership ? ` (${instance.ownership})` : '';
      const state = instance.state ? `, ${instance.state}` : '';
      console.log(`${instance.name} (${instance.id}): port ${instance.port}${state}${owner}, Web UI ${instance.webUiVerified ? 'verified' : 'not verified'}`);
    }
  }
  return installed ? 0 : 3;
}

async function diagnostics(args) {
  const options = parseOptions(args, [], ['--json']);
  const installed = fs.existsSync(installedExe);
  const info = managerInfo();
  const config = readJson(path.join(dataRoot, 'config.json'));
  const managerLog = path.join(dataRoot, 'logs', 'manager.log');
  const dshLogDirectory = path.join(dataRoot, 'logs');
  const result = {
    installed,
    installRoot,
    dataRoot,
    managerRunning: info.unknown ? null : info.running,
    trayEnabled: config && typeof config.TrayEnabled === 'boolean' ? config.TrayEnabled : true,
    managerPid: null,
    managerVersion: null,
    protocolVersion: null,
    managerLog,
    dshLogDirectory,
    instances: []
  };

  let controlStatus = null;
  if (info.running && info.sid) {
    try {
      controlStatus = await requestControl('getStatus', null, info.sid);
    } catch (_) {
      controlStatus = null;
    }
  }
  if (controlStatus && controlStatus.ok === true) {
    result.managerPid = controlStatus.managerPid || null;
    result.managerVersion = controlStatus.managerVersion || null;
    result.protocolVersion = controlStatus.protocolVersion || null;
    result.trayEnabled = typeof controlStatus.trayEnabled === 'boolean' ? controlStatus.trayEnabled : result.trayEnabled;
    result.instances = Array.isArray(controlStatus.instances) ? controlStatus.instances : [];
  } else if (config && Array.isArray(config.Instances)) {
    result.instances = config.Instances.map((instance) => {
      const state = readJson(path.join(dataRoot, 'state', `${instance.Id}.json`));
      return {
        instanceId: instance.Id,
        displayName: instance.Name,
        state: null,
        runtime: instance.RuntimeType || 'windows',
        ownership: state && state.Ownership ? state.Ownership : null,
        pid: state && state.ProcessId ? state.ProcessId : null,
        port: state && state.Port ? state.Port : instance.PreferredPort,
        frontend: instance.Frontend || 'web',
        workingDirectory: instance.Workspace,
        dshHome: instance.DshHome || '',
        dshVersion: null,
        runtimeBridgeState: null,
        runtimeBridgeVersion: null,
        runtimeBridgeProtocolVersion: null,
        lastStartResult: null,
        lastExitReason: null
      };
    });
  }

  if (options['--json']) {
    console.log(JSON.stringify(result, null, 2));
  } else {
    console.log(`Application: ${installed ? 'installed' : 'not installed'}`);
    console.log(`Manager: ${result.managerRunning === null ? 'unknown' : result.managerRunning ? `running (PID ${result.managerPid || '?'})` : 'stopped'}`);
    console.log(`Manager log: ${managerLog}`);
    console.log(`DSH logs: ${dshLogDirectory}`);
    for (const instance of result.instances) {
      const id = instance.instanceId || instance.id;
      const name = instance.displayName || instance.name || id;
      const state = instance.state ? `, ${instance.state}` : '';
      const owner = instance.ownership ? `, ${instance.ownership}` : '';
      console.log(`${name} (${id}): runtime ${instance.runtime || 'windows'}${state}${owner}, PID ${instance.pid || '?'}, port ${instance.port || '?'}`);
    }
  }
  return installed ? 0 : 3;
}

function parseBoolOption(value, name) {
  if (value === undefined) return undefined;
  const normalized = String(value).toLowerCase();
  if (['true', '1', 'yes', 'y'].includes(normalized)) return true;
  if (['false', '0', 'no', 'n'].includes(normalized)) return false;
  throw new Error(`${name} expects true or false.`);
}

function wslInfo() {
  const result = {
    installed: false,
    defaultDistro: '',
    distros: [],
    statusText: ''
  };
  const status = childProcess.spawnSync('wsl.exe', ['--status'], {
    encoding: 'utf16le',
    windowsHide: true
  });
  if (status.error) return result;
  result.installed = status.status === 0;
  if (status.stdout) result.statusText = status.stdout.trim();
  const list = childProcess.spawnSync('wsl.exe', ['--list', '--quiet'], {
    encoding: 'utf16le',
    windowsHide: true
  });
  if (!list.error && list.status === 0 && list.stdout) {
    result.installed = true;
    result.distros = list.stdout.replace(/\0/g, '').split(/\r?\n/).map((value) => value.trim()).filter(Boolean);
  }
  return result;
}

async function wslStatus(args) {
  const options = parseOptions(args, [], ['--json']);
  if (!fs.existsSync(installedExe)) throw new Error(`DeepSeek Harness Manager is not installed. Run "dsh-windows-manager install" first.`);
  const config = readJson(path.join(dataRoot, 'config.json')) || {};
  const info = wslInfo();
  const wslInstances = Array.isArray(config.Instances)
    ? config.Instances.filter((item) => String(item.RuntimeType || '').toLowerCase() === 'wsl').map((item) => item.Id)
    : [];
  const result = {
    installed: info.installed,
    enabled: config.WslEnabled === true,
    defaultDistro: config.WslDefaultDistro || '',
    distros: info.distros,
    wslInstances,
    statusText: info.statusText
  };
  if (options['--json']) {
    console.log(JSON.stringify(result, null, 2));
  } else {
    console.log(`WSL: ${info.installed ? 'installed' : 'not installed'}`);
    console.log(`WSL support: ${result.enabled ? 'enabled' : 'disabled'}`);
    if (result.defaultDistro) console.log(`Default distro: ${result.defaultDistro}`);
    console.log(`Detected distros: ${result.distros.length ? result.distros.join(', ') : 'none'}`);
    if (wslInstances.length) console.log(`WSL instances: ${wslInstances.join(', ')}`);
  }
  return 0;
}

async function wslDetect(args) {
  const options = parseOptions(args, [], ['--json']);
  const info = wslInfo();
  if (options['--json']) {
    console.log(JSON.stringify(info, null, 2));
  } else {
    console.log(`WSL: ${info.installed ? 'installed' : 'not installed'}`);
    console.log(`Distros: ${info.distros.length ? info.distros.join(', ') : 'none'}`);
  }
  return info.installed ? 0 : 1;
}

async function wslEnable(args) {
  const options = parseOptions(args, ['--distro'], []);
  const configPath = path.join(dataRoot, 'config.json');
  const config = readJson(configPath);
  if (!config) throw new Error(`DeepSeek Harness Manager is not installed. Run "dsh-windows-manager install" first.`);
  const info = wslInfo();
  if (!info.installed) throw new Error('WSL is not installed or wsl.exe is unavailable. Install WSL and a distro first.');
  let distro = options['--distro'] ? String(options['--distro']).trim() : '';
  if (!distro) {
    if (info.distros.length === 1) distro = info.distros[0];
    else if (info.distros.length === 0) throw new Error('No WSL distros were detected.');
    else throw new Error(`Multiple WSL distros detected (${info.distros.join(', ')}). Choose one with --distro.`);
  }
  if (!info.distros.includes(distro)) throw new Error(`WSL distro was not detected: ${distro}. Detected: ${info.distros.join(', ') || 'none'}`);
  config.WslEnabled = true;
  config.WslDefaultDistro = distro;
  fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, 'utf8');
  console.log(`WSL support enabled with default distro: ${distro}`);
  return 0;
}

async function wslDisable(args) {
  parseOptions(args, [], []);
  const configPath = path.join(dataRoot, 'config.json');
  const config = readJson(configPath);
  if (!config) throw new Error(`DeepSeek Harness Manager is not installed. Run "dsh-windows-manager install" first.`);
  config.WslEnabled = false;
  fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, 'utf8');
  const wslInstances = Array.isArray(config.Instances)
    ? config.Instances.filter((item) => String(item.RuntimeType || '').toLowerCase() === 'wsl').map((item) => item.Id)
    : [];
  console.log('WSL support disabled.');
  if (wslInstances.length) console.warn(`Warning: these instances still use runtimeType=wsl and will fail until WSL is enabled again: ${wslInstances.join(', ')}`);
  return 0;
}

function question(query) {
  return new Promise((resolve) => {
    const readline = require('readline');
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    rl.question(query, (answer) => {
      rl.close();
      resolve(answer.trim());
    });
  });
}

function detectRuntimes() {
  const runtimes = [{ id: 'windows', label: 'Windows' }];
  const info = wslInfo();
  for (const distro of info.distros) runtimes.push({ id: 'wsl', label: `${distro} (WSL)` });
  return runtimes;
}

function detectFrontends() {
  const frontends = [{ id: 'web', label: 'DSH Web' }];
  const result = childProcess.spawnSync('where.exe', ['oh-dsh'], {
    encoding: 'utf8',
    windowsHide: true
  });
  if (!result.error && result.status === 0 && result.stdout && result.stdout.trim()) {
    frontends.push({ id: 'oh-dsh', label: 'oh-dsh Desktop' });
  }
  return frontends;
}

function shortcutExists() {
  const shortcutPath = process.env.DSH_MANAGER_SHORTCUT_PATH;
  if (shortcutPath) return fs.existsSync(shortcutPath);
  const result = childProcess.spawnSync(powershellPath(), [
    '-NoLogo', '-NoProfile', '-Command',
    `$desktop = [Environment]::GetFolderPath('Desktop'); Test-Path -LiteralPath (Join-Path $desktop 'DSH Manager.lnk')`
  ], { encoding: 'utf8', windowsHide: true });
  return !result.error && result.status === 0 && result.stdout.trim().toLowerCase() === 'true';
}

function setDesktopShortcut(enabled) {
  const icon = path.join(installRoot, 'assets', 'dsh-manager-shortcut.ico');
  const shortcutPath = process.env.DSH_MANAGER_SHORTCUT_PATH || null;
  const command = [
    `$target = '${installedExe.replace(/'/g, "''")}'`,
    `$icon = '${icon.replace(/'/g, "''")}'`,
    `$path = $env:DSH_MANAGER_SHORTCUT_PATH; if ([string]::IsNullOrWhiteSpace($path)) { $path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Manager.lnk' }`,
    'if (' + (enabled ? '$true' : '$false') + ') {',
    "  $dir = Split-Path -Parent $path; if (-not (Test-Path -LiteralPath $dir -PathType Container)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }",
    '  $shell = New-Object -ComObject WScript.Shell',
    '  $shortcut = $shell.CreateShortcut($path)',
    "  $shortcut.TargetPath = $target",
    "  $shortcut.Arguments = '--action open'",
    "  $shortcut.IconLocation = \"$icon,0\"",
    "  $shortcut.Description = 'Start or open DeepSeek Harness and manage it from the Windows notification area.'",
    '  $shortcut.Save()',
    '} else {',
    "  Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue",
    "  $legacy = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk'",
    "  if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue }",
    '}'
  ].join('; ');
  const result = childProcess.spawnSync(powershellPath(), ['-NoLogo', '-NoProfile', '-Command', command], {
    stdio: 'inherit',
    windowsHide: true
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error('Could not update the desktop shortcut.');
}

function setAutostart(enabled) {
  if (process.env.DSH_MANAGER_NO_REGISTRY === '1') return;
  const value = `"${installedExe}" --action open`;
  const command = enabled
    ? `New-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name 'DeepSeekHarnessManager' -Value '${value.replace(/'/g, "''")}'`
    : `Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name 'DeepSeekHarnessManager' -ErrorAction SilentlyContinue`;
  const result = childProcess.spawnSync(powershellPath(), ['-NoLogo', '-NoProfile', '-Command', command], {
    stdio: 'inherit',
    windowsHide: true
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error('Could not update the Start with Windows registry value.');
}

async function configure(args) {
  const options = parseOptions(
    args,
    ['--runtime', '--frontend', '--tray', '--shortcut', '--autostart', '--wsl-distro'],
    []
  );
  if (!fs.existsSync(installedExe)) {
    throw new Error(`DeepSeek Harness Manager is not installed. Run "dsh-windows-manager install" first.`);
  }
  const configPath = path.join(dataRoot, 'config.json');
  const config = readJson(configPath);
  if (!config || !Array.isArray(config.Instances) || config.Instances.length === 0) {
    throw new Error('The Manager configuration is missing or has no instances.');
  }
  const defaultId = config.DefaultInstanceId || config.Instances[0].Id;
  const instance = config.Instances.find((item) => item.Id === defaultId) || config.Instances[0];

  let runtimeType;
  let frontend;
  let tray;
  let shortcut;
  let autostart;
  let wslDistro;
  if (Object.keys(options).length > 0) {
    runtimeType = options['--runtime'] ? String(options['--runtime']).toLowerCase() : undefined;
    frontend = options['--frontend'] ? String(options['--frontend']).toLowerCase() : undefined;
    tray = parseBoolOption(options['--tray'], '--tray');
    shortcut = parseBoolOption(options['--shortcut'], '--shortcut');
    autostart = parseBoolOption(options['--autostart'], '--autostart');
    wslDistro = options['--wsl-distro'] ? String(options['--wsl-distro']).trim() : undefined;
  } else {
    const runtimes = detectRuntimes();
    const frontends = detectFrontends();
    console.log('Detected runtimes:');
    runtimes.forEach((item, index) => console.log(`${index + 1}. ${item.label}`));
    const runtimeAnswer = await question(`Select default runtime [1]: `) || '1';
    const runtimeIndex = Number(runtimeAnswer) - 1;
    runtimeType = runtimes[runtimeIndex] ? runtimes[runtimeIndex].id : runtimes[0].id;

    console.log('Detected frontends:');
    frontends.forEach((item, index) => console.log(`${index + 1}. ${item.label}`));
    const frontendAnswer = await question(`Select frontend [1]: `) || '1';
    const frontendIndex = Number(frontendAnswer) - 1;
    frontend = frontends[frontendIndex] ? frontends[frontendIndex].id : frontends[0].id;

    const trayDefault = typeof config.TrayEnabled === 'boolean' ? config.TrayEnabled : true;
    const trayAnswer = await question(`Enable tray? [${trayDefault ? 'Y' : 'y'}/n]: `) || (trayDefault ? 'y' : 'n');
    tray = !['n', 'no', 'false'].includes(trayAnswer.toLowerCase());

    const shortcutDefault = typeof config.DesktopShortcut === 'boolean' ? config.DesktopShortcut : shortcutExists();
    const shortcutAnswer = await question(`Create desktop shortcut? [y/${shortcutDefault ? 'N' : 'n'}]: `) || (shortcutDefault ? 'n' : 'n');
    shortcut = ['y', 'yes', 'true'].includes(shortcutAnswer.toLowerCase());

    const autostartDefault = typeof config.StartWithWindows === 'boolean' ? config.StartWithWindows : false;
    const autostartAnswer = await question(`Start with Windows? [${autostartDefault ? 'Y' : 'y'}/n]: `) || (autostartDefault ? 'y' : 'n');
    autostart = !['n', 'no', 'false'].includes(autostartAnswer.toLowerCase());
  }

  if (!['windows', 'wsl'].includes(runtimeType || instance.RuntimeType || 'windows')) {
    throw new Error('--runtime must be windows or wsl.');
  }
  if (!['web', 'oh-dsh', 'custom'].includes(frontend || instance.Frontend || 'web')) {
    throw new Error('--frontend must be web, oh-dsh, or custom.');
  }

  const finalRuntimeType = runtimeType || instance.RuntimeType || 'windows';
  if (finalRuntimeType === 'wsl') {
    if (!config.WslEnabled) config.WslEnabled = true;
    const currentDistro = instance.WslDistro || config.WslDefaultDistro || '';
    wslDistro = wslDistro || currentDistro;
    if (!wslDistro) {
      const info = wslInfo();
      if (!info.installed) throw new Error('WSL is not installed or wsl.exe is unavailable.');
      if (info.distros.length === 1) wslDistro = info.distros[0];
      else if (info.distros.length === 0) throw new Error('No WSL distros were detected.');
      else throw new Error(`Multiple WSL distros detected (${info.distros.join(', ')}). Specify --wsl-distro.`);
    }
    config.WslDefaultDistro = wslDistro;
    instance.WslDistro = wslDistro;
  }
  if (runtimeType) instance.RuntimeType = runtimeType;
  if (frontend) instance.Frontend = frontend;
  if (tray !== undefined) config.TrayEnabled = tray;
  if (shortcut !== undefined) config.DesktopShortcut = shortcut;
  if (autostart !== undefined) config.StartWithWindows = autostart;

  fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, 'utf8');
  if (shortcut !== undefined) setDesktopShortcut(shortcut);
  if (autostart !== undefined) setAutostart(autostart);

  const finalRuntime = instance.RuntimeType || 'windows';
  const finalFrontend = instance.Frontend || 'web';
  if (finalRuntime === 'wsl') {
    console.warn('dsh-windows-manager: Runtime type "wsl" is reserved; its adapter is not implemented yet.');
  }
  if (finalFrontend !== 'web') {
    console.warn(`dsh-windows-manager: Frontend "${finalFrontend}" is reserved and not implemented yet.`);
  }
  const distroSuffix = finalRuntime === 'wsl' ? ` wslDistro=${config.WslDefaultDistro || ''}` : '';
  console.log(`Configuration updated: runtime=${finalRuntime} frontend=${finalFrontend} tray=${config.TrayEnabled !== false} shortcut=${config.DesktopShortcut !== false} autostart=${config.StartWithWindows === true}${distroSuffix}`);
  console.log('Restart the Manager if it is running for runtime/frontend/tray changes to take effect.');
  return 0;
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
  if (command === 'diagnostics') return diagnostics(args);
  if (command === 'configure') return configure(args);
  if (command === 'wsl') {
    const sub = args.shift();
    if (sub === 'status') return wslStatus(args);
    if (sub === 'detect') return wslDetect(args);
    if (sub === 'enable') return wslEnable(args);
    if (sub === 'disable') return wslDisable(args);
    throw new Error(`Unknown wsl command: ${sub}. Use wsl status|detect|enable|disable.`);
  }
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
