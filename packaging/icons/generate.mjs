// SVG sources are edited with apply_patch. This build step only emits binary
// platform assets; --check compares them in memory without modifying files.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const require = createRequire(import.meta.url);
const sharp = require(process.env.V2RAYN_SHARP_MODULE || 'sharp');
assert.equal(sharp.versions.sharp, '0.35.3', 'Use the pinned sharp version for reproducible assets');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const desktop = 'v2rayN/v2rayN.Desktop';
const wpf = 'v2rayN/v2rayN/Resources';
const check = process.argv.includes('--check');
const pngSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024];
const icoSizes = pngSizes.filter(size => size <= 256);
const source = await readFile(resolve(root, 'packaging/icons/app-icon.svg'), 'utf8');
const glyph = await readFile(resolve(root, desktop, 'Assets/BrandGlyph.svg'), 'utf8');
for (const svg of [source, glyph]) {
  assert(!/<(?:script|image|foreignObject|filter)\b/i.test(svg), 'Brand SVG must contain only local vectors');
  assert(!/\b(?:href|src)\s*=/i.test(svg), 'No external references in brand SVG');
}
const paths = svg => [...svg.matchAll(/<path\s[^>]*d="([^"]+)"/g)].map(match => match[1]);
assert.deepEqual(paths(source), paths(glyph), 'Application and interface must share the exact brand geometry');
assert(paths(glyph).length > 0, 'Brand has no paths');
assert(/viewBox="0 0 1024 1024"/.test(source));
assert(/viewBox="0 0 24 24"/.test(glyph));

async function raster(svg, size) {
  const png = await sharp(Buffer.from(svg), { density: 144 }).resize(size, size)
    .ensureAlpha().png({ compressionLevel: 9, palette: false }).toBuffer();
  const { data, info } = await sharp(png).raw().toBuffer({ resolveWithObject: true });
  assert.equal(info.width, size); assert.equal(info.height, size); assert.equal(info.channels, 4);
  for (const [x, y] of [[0, 0], [size - 1, 0], [0, size - 1], [size - 1, size - 1]])
    assert.equal(data[(y * size + x) * 4 + 3], 0, 'Icon corners must be transparent');
  assert(data.some((value, index) => index % 4 === 3 && value > 220), 'Icon must have a visible body');
  return png;
}

function ico(frames) {
  const header = Buffer.alloc(6 + frames.length * 16);
  header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  frames.forEach(([size, png], index) => {
    const p = 6 + index * 16;
    header[p] = header[p + 1] = size === 256 ? 0 : size;
    header.writeUInt16LE(1, p + 4); header.writeUInt16LE(32, p + 6);
    header.writeUInt32LE(png.length, p + 8); header.writeUInt32LE(offset, p + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...frames.map(([, png]) => png)]);
}

function icns(frames) {
  const chunks = frames.map(([type, png]) => {
    const header = Buffer.alloc(8); header.write(type, 0, 'ascii'); header.writeUInt32BE(8 + png.length, 4);
    return Buffer.concat([header, png]);
  });
  const header = Buffer.alloc(8); header.write('icns'); header.writeUInt32BE(8 + chunks.reduce((sum, b) => sum + b.length, 0), 4);
  return Buffer.concat([header, ...chunks]);
}

const outputs = new Map();
const app = new Map(await Promise.all(pngSizes.map(async size => [size, await raster(source, size)])));
const appIco = ico(icoSizes.map(size => [size, app.get(size)]));
outputs.set(`${desktop}/Assets/v2rayN.ico`, appIco);
outputs.set(`${wpf}/v2rayN.ico`, appIco);
outputs.set(`${desktop}/v2rayN.png`, app.get(256));
outputs.set(`${desktop}/v2rayN.icns`, icns([
  ['icp4', app.get(16)], ['icp5', app.get(32)], ['icp6', app.get(64)],
  ['ic07', app.get(128)], ['ic08', app.get(256)], ['ic09', app.get(512)], ['ic10', app.get(1024)],
  ['ic11', app.get(32)], ['ic12', app.get(64)], ['ic13', app.get(256)], ['ic14', app.get(512)],
]));

// Existing ESysProxyType indexes are policy indicators, not proof of a live
// connection. macOS keeps the separate observed P/T state renderer unchanged.
for (const [index, color] of ['#596579', '#126CE5', '#6E648C', '#9D5B10'].entries()) {
  const svg = glyph.replaceAll('currentColor', color);
  const frames = await Promise.all(icoSizes.map(async size => [size, await raster(svg, size)]));
  const encoded = ico(frames);
  outputs.set(`${desktop}/Assets/NotifyIcon${index + 1}.ico`, encoded);
  outputs.set(`${wpf}/NotifyIcon${index + 1}.ico`, encoded);
}

for (const [name, bytes] of outputs) {
  if (check) assert((await readFile(resolve(root, name))).equals(bytes), `Stale icon export: ${name}`);
  else await writeFile(resolve(root, name), bytes);
  console.log(createHash('sha256').update(bytes).digest('hex'), name);
}
const previewArg = process.argv.find(arg => arg.startsWith('--preview-dir='));
if (previewArg) {
  const directory = resolve(previewArg.slice('--preview-dir='.length));
  await mkdir(directory, { recursive: true });
  for (const [size, bytes] of app) await writeFile(resolve(directory, `app-${size}.png`), bytes);
}
console.log(`PASS: ${outputs.size} ${check ? 'verified' : 'exported'} platform assets; ${pngSizes.length} source sizes; exact shared geometry`);
