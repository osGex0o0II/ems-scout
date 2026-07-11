#!/usr/bin/env node
'use strict';

const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const { constants } = require('node:os');
const readline = require('node:readline');
const { adaptLegacyLine } = require('./legacy-line-adapter');
const { WorkflowEventWriter, isIdentifier } = require('./workflow-event');

const TERMINAL_ACTIONS = new Set(['return', 'switch_to_cdp']);
const WORKFLOW_CONTROL_VERSION = 'ems.workflow-control/v1';
const CONTROL_PROPERTIES = new Set(['contractVersion', 'workflowId', 'timestamp', 'type', 'reason']);
const CANCELLATION_GRACE_MS = 5000;

function parseArguments(argv) {
  const separator = argv.indexOf('--');
  if (separator < 0 || separator === argv.length - 1) {
    throw new Error('expected child command after --');
  }

  const options = {
    workflowId: randomUUID(),
    stage: 'sidecar',
    exit2Outcome: 'rejected',
  };
  for (let index = 0; index < separator; index++) {
    const argument = argv[index];
    const [name, inlineValue] = splitOption(argument);
    if (!['--workflow-id', '--stage', '--exit-2-outcome'].includes(name)) {
      throw new Error(`unknown runner option: ${name}`);
    }
    const value = inlineValue ?? argv[++index];
    if (!value || value === '--') throw new Error(`missing value for ${name}`);
    if (name === '--workflow-id') options.workflowId = value;
    if (name === '--stage') options.stage = value;
    if (name === '--exit-2-outcome') options.exit2Outcome = value;
  }

  if (!['rejected', 'succeeded_with_findings'].includes(options.exit2Outcome)) {
    throw new Error('--exit-2-outcome must be rejected or succeeded_with_findings');
  }
  return {
    ...options,
    command: argv[separator + 1],
    commandArguments: argv.slice(separator + 2),
  };
}

function splitOption(argument) {
  const equals = argument.indexOf('=');
  return equals < 0
    ? [argument, null]
    : [argument.slice(0, equals), argument.slice(equals + 1)];
}

function mapTerminalOutcome({ code, signal, action, cancellationRequested, exit2Outcome }) {
  if (cancellationRequested || signal || action === 'return') return 'cancelled';
  if (action === 'switch_to_cdp') return 'auth_required';
  if (code === 0) return 'succeeded';
  if (code === 2) return exit2Outcome;
  if (code === 3) return 'auth_required';
  if (code === 4) return 'rejected';
  return 'internal_error';
}

function normalizedExitCode(code, signal, action, cancellationRequested = false) {
  if (cancellationRequested) return 130;
  if (action === 'switch_to_cdp' && code === 0) return 3;
  if (action === 'return' && code === 0) return 130;
  if (Number.isInteger(code)) return clampExitCode(code);
  const signalNumber = signal && constants.signals ? constants.signals[signal] : null;
  return Number.isInteger(signalNumber) ? clampExitCode(128 + signalNumber) : 1;
}

function parseWorkflowControl(line, expectedWorkflowId) {
  if (typeof line !== 'string' || !line.trim()) throw new Error('control line is empty');
  if (Buffer.byteLength(line, 'utf8') > 4096) throw new Error('control line exceeds 4096 bytes');

  let value;
  try {
    value = JSON.parse(line);
  } catch {
    throw new Error('control line is not valid JSON');
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('control command must be an object');
  }
  const extra = Object.keys(value).filter(name => !CONTROL_PROPERTIES.has(name));
  if (extra.length) throw new Error(`control command has unknown properties: ${extra.join(', ')}`);
  if (value.contractVersion !== WORKFLOW_CONTROL_VERSION) {
    throw new Error(`unsupported control contractVersion: ${String(value.contractVersion)}`);
  }
  if (!isIdentifier(value.workflowId, 128, true)) throw new Error('control workflowId is invalid');
  if (value.workflowId !== expectedWorkflowId) throw new Error('control workflowId does not match this workflow');
  if (typeof value.timestamp !== 'string' || !value.timestamp || !Number.isFinite(Date.parse(value.timestamp))) {
    throw new Error('control timestamp is invalid');
  }
  if (value.type !== 'cancel') throw new Error(`unsupported control type: ${String(value.type)}`);
  if (value.reason !== undefined &&
      (typeof value.reason !== 'string' || !value.reason.trim() || value.reason.length > 512)) {
    throw new Error('control reason must be a non-empty string of at most 512 characters');
  }
  return {
    type: value.type,
    reason: typeof value.reason === 'string' ? value.reason.trim() : 'Cancellation requested by caller',
  };
}

function clampExitCode(code) {
  if (code === 0) return 0;
  return Math.min(255, Math.max(1, code));
}

async function run(options, streams = process) {
  const writer = new WorkflowEventWriter({
    workflowId: options.workflowId,
    stage: options.stage,
    output: streams.stdout,
  });
  writer.started();

  let child;
  try {
    child = spawn(options.command, options.commandArguments, {
      cwd: options.cwd || process.cwd(),
      env: options.env || process.env,
      shell: false,
      windowsHide: true,
      // stdin is reserved for WorkflowControl messages consumed by this runner.
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    streams.stderr.write(`[sidecar] ${message}\n`);
    writer.terminal('internal_error', 1, message);
    return 1;
  }

  let action = null;
  let cancellationRequested = false;
  let cancellationTimer = null;
  let finalized = false;

  const requestCancellation = reason => {
    if (finalized || cancellationRequested) return;
    cancellationRequested = true;
    writer.action('cancel_requested', reason);
    try {
      child.kill('SIGINT');
    } catch {
      try { child.kill(); } catch {}
    }
    cancellationTimer = setTimeout(() => {
      try { child.kill('SIGKILL'); } catch {}
    }, CANCELLATION_GRACE_MS);
    cancellationTimer.unref();
  };

  const lineReader = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
  lineReader.on('line', line => {
    const adapted = adaptLegacyLine(line, options.stage);
    if (adapted.kind === 'progress') {
      writer.progress(adapted.progress, adapted.stage);
      return;
    }
    if (adapted.kind === 'action') {
      writer.action(adapted.action);
      if (TERMINAL_ACTIONS.has(adapted.action)) action = adapted.action;
      return;
    }
    if (adapted.kind === 'malformed') {
      streams.stderr.write(`[sidecar] malformed legacy ${adapted.marker} line: ${adapted.line}\n`);
      return;
    }
    streams.stderr.write(adapted.line + '\n');
  });
  child.stderr.pipe(streams.stderr, { end: false });

  let controlReader = null;
  if (streams.stdin && typeof streams.stdin.on === 'function') {
    controlReader = readline.createInterface({ input: streams.stdin, crlfDelay: Infinity });
    controlReader.on('line', line => {
      try {
        const control = parseWorkflowControl(line, options.workflowId);
        if (control.type === 'cancel') requestCancellation(control.reason);
      } catch (error) {
        streams.stderr.write(`[sidecar] rejected control command: ${error.message}\n`);
      }
    });
  }

  const signalHandlers = new Map();
  for (const signal of ['SIGINT', 'SIGTERM']) {
    const handler = () => requestCancellation(`Sidecar received ${signal}`);
    signalHandlers.set(signal, handler);
    process.on(signal, handler);
  }

  return await new Promise(resolve => {
    const finalize = (code, signal, error) => {
      if (finalized) return;
      finalized = true;
      if (cancellationTimer) clearTimeout(cancellationTimer);
      if (controlReader) controlReader.close();
      for (const [name, handler] of signalHandlers) process.removeListener(name, handler);

      const exitCode = normalizedExitCode(code, signal, action, cancellationRequested);
      const outcome = mapTerminalOutcome({
        code,
        signal,
        action,
        cancellationRequested,
        exit2Outcome: options.exit2Outcome,
      });
      const message = terminalMessage({ code, signal, action, error, cancellationRequested });
      writer.terminal(outcome, exitCode, message);
      resolve(exitCode);
    };

    child.once('error', error => {
      const message = error instanceof Error ? error.message : String(error);
      streams.stderr.write(`[sidecar] child process error: ${message}\n`);
      finalize(1, null, error);
    });
    child.once('close', (code, signal) => finalize(code, signal, null));
  });
}

function terminalMessage({ code, signal, action, error, cancellationRequested }) {
  if (error) return error instanceof Error ? error.message : String(error);
  if (cancellationRequested) return 'Workflow cancelled by caller';
  if (action === 'switch_to_cdp') return 'Manual Edge CDP login is required';
  if (action === 'return') return 'Workflow returned to the caller';
  if (signal) return `Child process ended by ${signal}`;
  if (code && code !== 0) return `Child process exited with code ${code}`;
  return undefined;
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const exitCode = await run(options);
  process.exitCode = exitCode;
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`[sidecar] ${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  mapTerminalOutcome,
  normalizedExitCode,
  parseArguments,
  parseWorkflowControl,
  run,
};
