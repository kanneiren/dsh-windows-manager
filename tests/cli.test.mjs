import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'win32') {
  console.log('SKIP npm CLI integration test (Windows only)');
  process.exit(0);
}

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const cli = path.join(projectRoot, 'bin', 'dsh-windows-manager.js');
const expectedVersion = JSON.parse(fs.readFileSync(path.join(projectRoot, 'package.json'), 'utf8')).version;
const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-manager-cli-'));
const installRoot = path.join(temporaryRoot, 'app');
const dataRoot = path.join(temporaryRoot, 'data');
const shortcutPath = path.join(temporaryRoot, 'Desktop', 'DSH Manager.lnk');
const startMenuShortcutPath = path.join(temporaryRoot, 'StartMenu', 'DSH Manager.lnk');
const environment = {
  ...process.env,
  DSH_MANAGER_INSTALL_ROOT: installRoot,
  DSH_MANAGER_DATA_ROOT: dataRoot,
  DSH_MANAGER_SHORTCUT_PATH: shortcutPath,
  DSH_MANAGER_START_MENU_SHORTCUT_PATH: startMenuShortcutPath,
  DSH_MANAGER_NO_REGISTRY: '1'
};

function run(args) {
  return spawnSync(process.execPath, [cli].concat(args), {
    cwd: temporaryRoot,
    env: environment,
    encoding: 'utf8',
    windowsHide: true
  });
}

try {
  let result = run(['status', '--json']);
  assert.equal(result.status, 3, result.stderr);
  assert.equal(JSON.parse(result.stdout).installed, false);

  result = run(['install', '--no-launch', '--no-shortcut', '--workspace', temporaryRoot, '--port', '43123']);
  assert.equal(result.status, 0, result.stderr);
  assert.ok(fs.existsSync(path.join(installRoot, 'DeepSeekHarnessManager.exe')));
  assert.ok(fs.existsSync(path.join(installRoot, 'assets', 'deepseek-whale-running.ico')));
  assert.ok(fs.existsSync(path.join(installRoot, 'assets', 'dsh-manager-shortcut.ico')));
  assert.equal(fs.existsSync(path.join(installRoot, 'assets', 'deepseek-whale.png')), false);
  assert.ok(fs.existsSync(path.join(installRoot, 'locales', 'zh-CN.json')));
  assert.ok(fs.existsSync(path.join(installRoot, 'plugins', 'deepseek-harness-web', 'package.json')));
  assert.ok(fs.existsSync(path.join(installRoot, 'plugins', 'deepseek-harness-web', 'cordis.patch.yml')));
  assert.ok(fs.existsSync(path.join(installRoot, 'LICENSE')));
  assert.ok(fs.existsSync(path.join(installRoot, 'SECURITY.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'SECURITY.en.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'CONTRIBUTING.en.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'docs', 'ARCHITECTURE.md')));
  assert.equal(fs.existsSync(shortcutPath), false);
  assert.equal(fs.existsSync(startMenuShortcutPath), false);

  const configPath = path.join(dataRoot, 'config.json');
  const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
  assert.equal(config.Language, 'auto');
  assert.equal(config.Instances[0].PreferredPort, 43123);
  config.Instances[0].Workspace = 'preserved-workspace';
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2));

  result = run(['install', '--no-launch', '--no-shortcut', '--workspace', temporaryRoot]);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(fs.readFileSync(configPath, 'utf8')).Instances[0].Workspace, 'preserved-workspace');

  result = run(['status', '--json']);
  assert.equal(result.status, 0, result.stderr);
  const status = JSON.parse(result.stdout);
  assert.equal(status.installed, true);
  assert.equal(status.managerRunning, false);
  assert.equal(status.instances.length, 1);
  assert.equal(status.trayEnabled, true);
  assert.equal(status.instances[0].runtimeType, 'windows');
  assert.equal(status.instances[0].frontend, 'web');

  result = run(['diagnostics', '--json']);
  assert.equal(result.status, 0, result.stderr);
  const diagnostics = JSON.parse(result.stdout);
  assert.equal(diagnostics.installed, true);
  assert.equal(diagnostics.managerRunning, false);
  assert.equal(diagnostics.trayEnabled, true);
  assert.ok(diagnostics.managerLog);
  assert.ok(diagnostics.dshLogDirectory);

  result = run(['configure', '--runtime', 'windows', '--frontend', 'web', '--tray', 'false', '--shortcut', 'false', '--autostart', 'false']);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  const configured = JSON.parse(fs.readFileSync(configPath, 'utf8'));
  assert.equal(configured.TrayEnabled, false);
  assert.equal(configured.StartWithWindows, false);
  assert.equal(configured.DesktopShortcut, false);
  assert.equal(configured.Instances[0].RuntimeType, 'windows');
  assert.equal(configured.Instances[0].Frontend, 'web');

  result = run(['configure', '--runtime', 'wsl', '--wsl-distro', 'TestDistro']);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  const wslConfigured = JSON.parse(fs.readFileSync(configPath, 'utf8'));
  assert.equal(wslConfigured.WslEnabled, true);
  assert.equal(wslConfigured.WslDefaultDistro, 'TestDistro');
  assert.equal(wslConfigured.Instances[0].RuntimeType, 'wsl');
  assert.equal(wslConfigured.Instances[0].WslDistro, 'TestDistro');

  result = run(['wsl', 'status', '--json']);
  assert.equal(result.status, 0, result.stderr);
  const wslStatus = JSON.parse(result.stdout);
  assert.equal(wslStatus.enabled, true);
  assert.equal(wslStatus.defaultDistro, 'TestDistro');
  assert.ok(Array.isArray(wslStatus.distros));
  assert.deepEqual(wslStatus.wslInstances, ['web']);

  result = run(['wsl', 'detect', '--json']);
  assert.ok([0, 1].includes(result.status), result.stderr);
  const detected = JSON.parse(result.stdout);
  assert.equal(typeof detected.installed, 'boolean');
  assert.ok(Array.isArray(detected.distros));

  result = run(['wsl', 'disable']);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal(JSON.parse(fs.readFileSync(configPath, 'utf8')).WslEnabled, false);

  result = run(['configure', '--runtime', 'windows']);
  assert.equal(result.status, 0, result.stderr || result.stdout);

  result = run(['uninstall', '--no-shortcut']);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(installRoot), false);
  assert.equal(fs.existsSync(configPath), true);

  result = run(['install', '--no-launch', '--workspace', temporaryRoot]);
  assert.equal(result.status, 0, result.stderr);
  assert.ok(fs.existsSync(startMenuShortcutPath), 'default install should create the Start Menu shortcut');
  assert.equal(fs.existsSync(shortcutPath), false, 'default install should not create the desktop shortcut');

  result = run(['install', '--no-launch', '--desktop-shortcut', '--workspace', temporaryRoot]);
  assert.equal(result.status, 0, result.stderr);
  assert.ok(fs.existsSync(shortcutPath), '--desktop-shortcut should create the desktop shortcut');
  assert.equal(JSON.parse(fs.readFileSync(configPath, 'utf8')).DesktopShortcut, true);

  const inspectShortcut = spawnSync('powershell.exe', [
    '-NoLogo',
    '-NoProfile',
    '-Command',
    '$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($env:DSH_MANAGER_TEST_SHORTCUT); [pscustomobject]@{ TargetPath = $shortcut.TargetPath; Arguments = $shortcut.Arguments; IconLocation = $shortcut.IconLocation } | ConvertTo-Json -Compress'
  ], {
    encoding: 'utf8',
    env: { ...process.env, DSH_MANAGER_TEST_SHORTCUT: shortcutPath },
    windowsHide: true
  });
  assert.equal(inspectShortcut.status, 0, inspectShortcut.stderr);
  const shortcut = JSON.parse(inspectShortcut.stdout);
  assert.equal(fs.realpathSync.native(shortcut.TargetPath).toLowerCase(), fs.realpathSync.native(path.join(installRoot, 'DeepSeekHarnessManager.exe')).toLowerCase());
  assert.equal(shortcut.Arguments, '--action tray');
  assert.equal(fs.realpathSync.native(shortcut.IconLocation.replace(/,0$/, '')).toLowerCase(), fs.realpathSync.native(path.join(installRoot, 'assets', 'dsh-manager-shortcut.ico')).toLowerCase());

  result = run(['uninstall', '--purge-data']);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(dataRoot), false);
  assert.equal(fs.existsSync(shortcutPath), false);
  assert.equal(fs.existsSync(startMenuShortcutPath), false);

  result = run(['--version']);
  assert.equal(result.status, 0);
  assert.equal(result.stdout.trim(), expectedVersion);
  result = run(['unknown-command']);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /Unknown command/);
  result = run(['install', '--port', '70000', '--no-launch', '--no-shortcut']);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /1 to 65535/);

  console.log('PASS npm CLI install, upgrade, status, and uninstall');
} finally {
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}
