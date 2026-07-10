const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin, output: process.stdout });

function SEP() { console.log('\x1b[90m' + '─'.repeat(48) + '\x1b[0m'); }

function prompt(q) {
  return new Promise(resolve => {
    try { process.stdin.resume(); } catch {}
    rl.question(q, resolve);
  });
}

function closePrompt() {
  rl.close();
}

function clearScreen() {
  process.stdout.write('\u001b[2J\u001b[0;0H');
}

function cb(on) { return on ? '[x]' : '[ ]'; }

function visLen(s) {
  let w = 0;
  for (const ch of s) {
    const c = ch.codePointAt(0);
    if (c >= 0x4E00 && c <= 0x9FFF) w += 2;
    else if (c >= 0x3000 && c <= 0x303F) w += 2;
    else if (c >= 0xFF00 && c <= 0xFFEF) w += 2;
    else w += 1;
  }
  return w;
}

function padVis(s, w) {
  const c = visLen(s);
  return c >= w ? s : s + ' '.repeat(w - c);
}

function titleBar(t) {
  console.log('\n  ' + t);
  SEP();
}

async function confirmScreen(title, lines) {
  SEP();
  console.log('  ' + title);
  for (const l of lines) console.log('  ' + l);
  SEP();
  const ans = await prompt('  确认？[Y/q]: ');
  return ans.trim().toUpperCase();
}

module.exports = {
  SEP,
  prompt,
  closePrompt,
  clearScreen,
  cb,
  visLen,
  padVis,
  titleBar,
  confirmScreen,
};
