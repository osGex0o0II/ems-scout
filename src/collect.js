const { runCollectTui } = require('./tui/flows');
const { closePrompt } = require('./tui/ui');

runCollectTui()
  .then(() => closePrompt())
  .catch(e => {
    console.error('Fatal:', e);
    closePrompt();
    process.exit(1);
  });
