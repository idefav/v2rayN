// Repository audit, separate from the standalone prototype tests.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../../../..');
const design = path.resolve(__dirname, '..');
const source = path.join(root, 'v2rayN/v2rayN.Desktop/Views');
const map = JSON.parse(fs.readFileSync(path.join(design, '../native-view-map.json'), 'utf8'));
const routes = new Set(JSON.parse(fs.readFileSync(path.join(design, 'coverage.json'), 'utf8')).families.flatMap(f => f.pages.map(p => p.id)));
const files = fs.readdirSync(source).filter(name => name.endsWith('.axaml')).sort();
assert.deepEqual(Object.keys(map.views).sort(), files, 'Every current AXAML view needs an explicit design mapping; stale entries are errors too.');
for (const entry of [...Object.values(map.views), ...Object.values(map.dynamicPages)]) {
  const [file, fragment] = entry.split('#');
  assert(fs.existsSync(path.join(design, file)), entry);
  if (fragment) assert(routes.has(fragment.split('/')[0]), entry);
}
const workspace = fs.readFileSync(path.join(source, 'MainWindow.Workspace.cs'), 'utf8');
for (const method of Object.keys(map.dynamicPages)) assert(workspace.includes(`${method}()`), method);
console.log(`PASS ${files.length} AXAML views and ${Object.keys(map.dynamicPages).length} dynamic pages have existing design targets.`);
