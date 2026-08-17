import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  isUserWslDistro,
  scoreUserWslDistro,
  parseWslVerboseOutput,
  selectPreferredWslDistro,
  userWslDistros
} from '../bin/wsl-distro-selection.js';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));

assert.equal(isUserWslDistro('docker-desktop'), false);
assert.equal(isUserWslDistro('docker-desktop-data'), false);
assert.equal(isUserWslDistro('Docker-Desktop'), false);
assert.equal(isUserWslDistro('rancher-desktop'), false);
assert.equal(isUserWslDistro('rancher-desktop-data'), false);
assert.equal(isUserWslDistro('podman-machine-default'), false);
assert.equal(isUserWslDistro('Ubuntu-24.04'), true);
assert.equal(isUserWslDistro('Debian'), true);

const verbose = [
  '  NAME              STATE           VERSION',
  '* docker-desktop    Stopped         2',
  '  Ubuntu-24.04      Running         2'
].join('\r\n');
assert.deepEqual(parseWslVerboseOutput(verbose), [
  { name: 'docker-desktop', state: 'Stopped', isDefault: true },
  { name: 'Ubuntu-24.04', state: 'Running', isDefault: false }
]);

const dockerAndUbuntu = ['docker-desktop', 'Ubuntu-24.04'];
const states = parseWslVerboseOutput(verbose);
assert.equal(selectPreferredWslDistro('', dockerAndUbuntu, states), 'Ubuntu-24.04');
assert.equal(selectPreferredWslDistro('', dockerAndUbuntu, null), 'Ubuntu-24.04');
assert.equal(userWslDistros(dockerAndUbuntu).join(','), 'Ubuntu-24.04');

assert.equal(selectPreferredWslDistro('', ['docker-desktop'], null), null);
assert.equal(selectPreferredWslDistro('', [], null), null);

assert.equal(selectPreferredWslDistro('', ['Debian', 'Ubuntu-22.04'], null), 'Ubuntu-22.04');
assert.equal(selectPreferredWslDistro('Debian', ['Debian', 'Ubuntu-22.04'], null), 'Debian');
assert.equal(selectPreferredWslDistro('missing', ['Debian', 'Ubuntu-22.04'], null), 'Ubuntu-22.04');
assert.equal(selectPreferredWslDistro('', ['MyCustomOne', 'MyCustomTwo'], null), null);

assert.equal(scoreUserWslDistro('Ubuntu-24.04') > scoreUserWslDistro('Debian'), true);

console.log(`PASS wsl distro selection unit tests (${path.basename(root)})`);
