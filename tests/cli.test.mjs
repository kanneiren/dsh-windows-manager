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
const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-manager-cli-'));
const installRoot = path.join(temporaryRoot, 'app');
const dataRoot = path.join(temporaryRoot, 'data');
const shortcutPath = path.join(temporaryRoot, 'Desktop', 'DeepSeek Harness.lnk');
const environment = {
  ...process.env,
  DSH_MANAGER_INSTALL_ROOT: installRoot,
  DSH_MANAGER_DATA_ROOT: dataRoot,
  DSH_MANAGER_SHORTCUT_PATH: shortcutPath
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
  assert.equal(fs.existsSync(path.join(installRoot, 'assets', 'deepseek-whale.png')), false);
  assert.ok(fs.existsSync(path.join(installRoot, 'locales', 'zh-CN.json')));
  assert.ok(fs.existsSync(path.join(installRoot, 'LICENSE')));
  assert.ok(fs.existsSync(path.join(installRoot, 'SECURITY.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'docs', 'ARCHITECTURE.md')));
  assert.equal(fs.existsSync(shortcutPath), false);

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

  result = run(['uninstall', '--no-shortcut']);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(installRoot), false);
  assert.equal(fs.existsSync(configPath), true);

  result = run(['install', '--no-launch', '--no-shortcut', '--workspace', temporaryRoot]);
  assert.equal(result.status, 0, result.stderr);
  result = run(['uninstall', '--purge-data', '--no-shortcut']);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(dataRoot), false);

  result = run(['--version']);
  assert.equal(result.status, 0);
  assert.match(result.stdout, /^0\.1\.0\s*$/);
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
