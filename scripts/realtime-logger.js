'use strict';

const fs = require('fs');
const path = require('path');

function argValue(name) {
  const prefix = `--${name}=`;
  const hit = process.argv.find(a => a.startsWith(prefix));
  return hit ? hit.slice(prefix.length) : '';
}

function timestamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function sanitizeFilePart(value) {
  return String(value || 'realtime')
    .replace(/[\\/:*?"<>|]/g, '_')
    .replace(/\s+/g, '_')
    .slice(0, 80);
}

function normalizeChunk(chunk) {
  if (chunk === undefined || chunk === null) return '';
  const text = Buffer.isBuffer(chunk) ? chunk.toString('utf8') : String(chunk);
  return text.replace(/\r/g, '\n');
}

function installRealtimeLog(options = {}) {
  const explicit = argValue('log-file');
  const enabled = process.argv.includes('--log-file') || !!explicit;
  if (!enabled) return null;

  const root = path.resolve(__dirname, '..');
  const outDir = path.resolve(process.env.EMS_OUT_DIR || path.join(root, 'out'));
  fs.mkdirSync(outDir, { recursive: true });

  const defaultName = `${sanitizeFilePart(options.prefix || path.basename(process.argv[1] || 'realtime', '.js'))}_${timestamp()}.log`;
  const filePath = explicit && explicit !== '1' && explicit.toLowerCase() !== 'true'
    ? path.resolve(root, explicit)
    : path.join(outDir, defaultName);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });

  const stream = fs.createWriteStream(filePath, { flags: 'a' });
  const stdoutWrite = process.stdout.write.bind(process.stdout);
  const stderrWrite = process.stderr.write.bind(process.stderr);

  let teeing = false;
  function writeLog(chunk) {
    if (teeing) return;
    const text = normalizeChunk(chunk);
    if (!text) return;
    teeing = true;
    stream.write(text);
    teeing = false;
  }

  process.stdout.write = function patchedStdoutWrite(chunk, encoding, cb) {
    writeLog(chunk);
    return stdoutWrite(chunk, encoding, cb);
  };

  process.stderr.write = function patchedStderrWrite(chunk, encoding, cb) {
    writeLog(chunk);
    return stderrWrite(chunk, encoding, cb);
  };

  process.on('exit', () => {
    try { stream.end(); } catch {}
  });

  console.log(`[LOG] ${filePath}`);
  return filePath;
}

module.exports = { installRealtimeLog };
