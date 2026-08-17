import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const metadata = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const required = [
  'dist/DeepSeekHarnessManager.exe',
  'dist/DeepSeekHarnessManager.exe.config',
  'dist/assets/dsh-manager-shortcut.ico',
  'dist/assets/deepseek-whale-running.ico',
  'dist/assets/deepseek-whale-stopped.ico',
  'dist/LICENSE',
  'dist/README.md',
  'dist/README.en.md',
  'dist/SECURITY.md',
  'dist/SECURITY.en.md',
  'dist/CONTRIBUTING.md',
  'dist/CONTRIBUTING.en.md',
  'dist/AGENTS.md',
  'dist/CHANGELOG.md',
  'dist/docs/ARCHITECTURE.md',
  'dist/docs/FEATURES.md',
  'dist/docs/TROUBLESHOOTING.md',
  'dist/docs/PERFORMANCE.md',
  'dist/docs/USAGE.md',
  'dist/docs/USAGE.zh-CN.md',
  'dist/locales/en-US.json',
  'dist/locales/zh-CN.json',
  'dist/plugins/deepseek-harness-web/plugin.json',
  'dist/plugins/deepseek-harness-web/package.json',
  'dist/plugins/deepseek-harness-web/cordis.patch.yml',
  'dist/plugins/deepseek-harness-web/cordis/windows-lifecycle.mjs',
  'bin/dsh-windows-manager.js',
  'scripts/Install.ps1',
  'scripts/Uninstall.ps1',
  'README.md',
  'README.en.md',
  'docs/USAGE.md',
  'docs/USAGE.zh-CN.md',
  'SECURITY.en.md',
  'CONTRIBUTING.en.md'
];

for (const relativePath of required) {
  if (!fs.existsSync(path.join(root, relativePath))) throw new Error(`Package file is missing: ${relativePath}`);
}
if (fs.existsSync(path.join(root, 'dist', 'DeepSeekHarnessManager.Tests.exe'))) {
  throw new Error('The npm runtime package must not include the test executable.');
}
for (const asset of fs.readdirSync(path.join(root, 'dist', 'assets'))) {
  if (!asset.endsWith('.ico')) throw new Error(`The runtime package contains a source-only image: ${asset}`);
}

const assemblyInfo = fs.readFileSync(path.join(root, 'src', 'AssemblyInfo.cs'), 'utf8');
if (!assemblyInfo.includes(`AssemblyInformationalVersion("${metadata.version}")`)) {
  throw new Error('package.json and AssemblyInfo.cs versions do not match.');
}

const english = JSON.parse(fs.readFileSync(path.join(root, 'dist', 'locales', 'en-US.json'), 'utf8'));
const chinese = JSON.parse(fs.readFileSync(path.join(root, 'dist', 'locales', 'zh-CN.json'), 'utf8'));
const englishKeys = Object.keys(english).sort();
const chineseKeys = Object.keys(chinese).sort();
if (JSON.stringify(englishKeys) !== JSON.stringify(chineseKeys)) {
  throw new Error('The English and Chinese locale keys do not match.');
}

console.log(`Package validation passed for ${metadata.name}@${metadata.version} (${required.length} required files).`);
