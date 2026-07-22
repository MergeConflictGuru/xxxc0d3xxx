const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

let rg = 'rg';
try {
  rg = require('@vscode/ripgrep').rgPath;
} catch {}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ccs-rg-smoke-'));
try {
  fs.writeFileSync(path.join(tmp, 'a.js'), 'TemporaryInstrumentKeyRanges\nTemporaryInstrumentKeyRanges\n');
  fs.writeFileSync(path.join(tmp, 'b.mjs'), 'TemporaryInstrumentKeyRanges\n');
  fs.writeFileSync(path.join(tmp, 'AGENT_NOTES.md'), 'TemporaryInstrumentKeyRanges\n');
  fs.mkdirSync(path.join(tmp, 'ignored'));
  fs.writeFileSync(path.join(tmp, 'ignored', 'c.js'), 'TemporaryInstrumentKeyRanges\n');
  fs.writeFileSync(path.join(tmp, '.ignore'), 'ignored/\n');

  const args = [
    '--json',
    '--line-number',
    '--column',
    '--with-filename',
    '--no-heading',
    '--color', 'never',
    '--no-messages',
    '--fixed-strings',
    '--ignore-case',
    '--regexp', 'TemporaryInstrumentKeyRanges',
    '.'
  ];

  const result = spawnSync(rg, args, { cwd: tmp, encoding: 'utf8' });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`rg exited ${result.status}: ${result.stderr || result.stdout}`);
  }

  const counts = new Map();
  let total = 0;
  for (const line of result.stdout.split(/\r?\n/)) {
    if (!line.trim()) continue;
    const event = JSON.parse(line);
    if (event.type !== 'match') continue;
    const file = event.data.path.text.replace(/^\.[/\\]/, '').replaceAll('\\', '/');
    const n = Array.isArray(event.data.submatches) ? event.data.submatches.length : 1;
    counts.set(file, (counts.get(file) || 0) + n);
    total += n;
  }

  const expected = { 'AGENT_NOTES.md': 1, 'a.js': 2, 'b.mjs': 1 };
  const actual = Object.fromEntries([...counts.entries()].sort());
  const expectedJson = JSON.stringify(Object.fromEntries(Object.entries(expected).sort()));
  const actualJson = JSON.stringify(actual);
  if (total !== 4 || actualJson !== expectedJson) {
    throw new Error(`unexpected smoke result. total=${total}, files=${actualJson}`);
  }

  console.log('smoke-rg ok: found js, mjs, md and respected .ignore');
} finally {
  fs.rmSync(tmp, { recursive: true, force: true });
}
