import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'win32') {
  console.log('SKIP packed npm installation test (Windows only)');
  process.exit(0);
}

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const tarball = path.resolve(process.argv[2] || path.join(projectRoot, 'dsh-windows-manager-0.2.0.tgz'));
assert.ok(fs.existsSync(tarball), `npm tarball not found: ${tarball}`);

const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-manager-packed-'));
const dataRoot = path.join(temporaryRoot, 'data');
const installRoot = path.join(dataRoot, 'app');
const environment = {
  ...process.env,
  DSH_MANAGER_INSTALL_ROOT: installRoot,
  DSH_MANAGER_DATA_ROOT: dataRoot,
  DSH_MANAGER_SHORTCUT_PATH: path.join(temporaryRoot, 'Desktop', 'DSH Manager.lnk')
};
const npmCli = process.env.npm_execpath && process.env.npm_execpath.endsWith('.js')
  ? process.env.npm_execpath
  : path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
assert.ok(fs.existsSync(npmCli), `npm CLI not found: ${npmCli}`);
const npm = [process.execPath, npmCli];

function execute(commandArgs) {
  const args = npm.slice(1).concat(['exec', '--yes', `--package=${tarball}`, '--', 'dsh-windows-manager']).concat(commandArgs);
  return spawnSync(npm[0], args, {
    cwd: temporaryRoot,
    env: environment,
    encoding: 'utf8',
    windowsHide: true
  });
}

try {
  let result = execute(['install', '--no-launch', '--no-shortcut', '--workspace', temporaryRoot]);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.ok(fs.existsSync(path.join(installRoot, 'DeepSeekHarnessManager.exe')));
  assert.ok(fs.existsSync(path.join(installRoot, 'README.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'README.en.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'SECURITY.en.md')));
  assert.ok(fs.existsSync(path.join(installRoot, 'CONTRIBUTING.en.md')));
  assert.ok(fs.existsSync(path.join(dataRoot, 'config.json')));

  result = execute(['status', '--json']);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal(JSON.parse(result.stdout).installed, true);

  result = execute(['uninstall', '--purge-data', '--no-shortcut']);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal(fs.existsSync(dataRoot), false);
  console.log(`PASS packed npm install via npm exec (${path.basename(tarball)})`);
} finally {
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}
