'use strict';

function normalize(value) {
  return String(value || '').trim();
}

function lower(value) {
  return normalize(value).toLowerCase();
}

function isUserWslDistro(value) {
  const name = lower(value);
  if (!name) return false;
  if (name === 'docker-desktop' || name === 'docker-desktop-data') return false;
  if (name.startsWith('docker-desktop-')) return false;
  if (name === 'rancher-desktop' || name === 'rancher-desktop-data') return false;
  if (name.startsWith('rancher-desktop-')) return false;
  if (name.startsWith('podman-machine-')) return false;
  return true;
}

function scoreUserWslDistro(value) {
  const name = lower(value);
  if (!name) return 0;
  if (name.startsWith('ubuntu')) return 100;
  if (name === 'debian' || name.startsWith('debian-')) return 90;
  if (name.startsWith('kali')) return 85;
  if (name.includes('suse')) return 80;
  if (name.includes('fedora') || name.includes('rocky') || name.includes('alma') ||
      name.includes('centos') || name.includes('rhel') || name.includes('oracle')) return 75;
  if (name.startsWith('arch') || name.includes('manjaro') || name.includes('endeavouros')) return 70;
  if (name.includes('alpine')) return 60;
  return 0;
}

function parseWslVerboseOutput(output) {
  const states = [];
  if (!output) return states;
  const lines = String(output).replace(/\0/g, '').split(/\r?\n/);
  for (const raw of lines) {
    let line = raw.trim();
    if (!line) continue;
    let isDefault = false;
    if (line[0] === '*') {
      isDefault = true;
      line = line.slice(1).trim();
    }
    const columns = line.split(/[ \t]+/).filter(Boolean);
    if (columns.length < 3) continue;
    const header = /^(name|名称)$/i.test(columns[0]) &&
      /^(state|状态)$/i.test(columns[columns.length - 2]) &&
      /^(version|版本)$/i.test(columns[columns.length - 1]);
    if (header) continue;
    states.push({
      name: columns.slice(0, -2).join(' '),
      state: columns[columns.length - 2],
      isDefault
    });
  }
  return states;
}

function findDistro(distros, value) {
  const target = lower(value);
  for (const distro of distros) {
    if (lower(distro) === target) return distro;
  }
  return null;
}

function containsDistro(distros, value) {
  return findDistro(distros, value) !== null;
}

function selectPreferredWslDistro(configured, detected, states) {
  const distros = Array.isArray(detected) ? detected : [];
  if (distros.length === 0) return null;

  if (normalize(configured)) {
    const configuredMatch = findDistro(distros, configured);
    if (configuredMatch) return configuredMatch;
  }

  const candidates = [];
  for (const distro of distros) {
    if (isUserWslDistro(distro) && !containsDistro(candidates, distro)) candidates.push(distro);
  }
  if (candidates.length === 0) return null;
  if (candidates.length === 1) return candidates[0];

  if (Array.isArray(states)) {
    for (const state of states) {
      if (!state || !state.isDefault || !normalize(state.name)) continue;
      const match = findDistro(candidates, state.name);
      if (match) return match;
    }

    const running = [];
    for (const state of states) {
      if (!state || !/^running$/i.test(normalize(state.state))) continue;
      const match = findDistro(candidates, state.name);
      if (match && !containsDistro(running, match)) running.push(match);
    }
    if (running.length === 1) return running[0];
  }

  let best = null;
  let bestScore = -1;
  let tie = false;
  for (const candidate of candidates) {
    const score = scoreUserWslDistro(candidate);
    if (score > bestScore) {
      best = candidate;
      bestScore = score;
      tie = false;
    } else if (score === bestScore) {
      tie = true;
    }
  }
  if (best !== null && !tie) return best;
  return null;
}

function userWslDistros(detected) {
  const result = [];
  if (!Array.isArray(detected)) return result;
  for (const distro of detected) {
    if (isUserWslDistro(distro) && !containsDistro(result, distro)) result.push(distro);
  }
  return result;
}

module.exports = {
  isUserWslDistro,
  scoreUserWslDistro,
  parseWslVerboseOutput,
  selectPreferredWslDistro,
  userWslDistros
};
