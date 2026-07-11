'use strict';

const CONTRACT_VERSION = 'ems.workflow-event/v1';
const EVENT_TYPES = new Set(['started', 'progress', 'action', 'terminal']);
const TERMINAL_OUTCOMES = new Set([
  'succeeded',
  'succeeded_with_findings',
  'rejected',
  'auth_required',
  'cancelled',
  'internal_error',
]);

function isIdentifier(value, maxLength, allowColon) {
  if (typeof value !== 'string' || value.length < 1 || value.length > maxLength) return false;
  if (!/[A-Za-z0-9]/.test(value[0])) return false;
  for (const char of value) {
    if (/[A-Za-z0-9._-]/.test(char)) continue;
    if (allowColon && char === ':') continue;
    return false;
  }
  return true;
}

function assertWorkflowId(value) {
  if (!isIdentifier(value, 128, true)) {
    throw new TypeError('workflowId must be 1-128 ASCII identifier characters');
  }
}

function assertStage(value) {
  if (!isIdentifier(value, 64, false)) {
    throw new TypeError('stage must be 1-64 ASCII identifier characters');
  }
}

class WorkflowEventWriter {
  constructor({ workflowId, stage, output = process.stdout, now = () => new Date() }) {
    assertWorkflowId(workflowId);
    assertStage(stage);
    if (!output || typeof output.write !== 'function') throw new TypeError('output must be writable');
    if (typeof now !== 'function') throw new TypeError('now must be a function');

    this.workflowId = workflowId;
    this.stage = stage;
    this.output = output;
    this.now = now;
    this.seq = 0;
    this.terminalWritten = false;
  }

  started(message) {
    return this.#write('started', this.stage, optionalMessage(message));
  }

  progress(progress, stage = this.stage) {
    assertStage(stage);
    if (!progress || typeof progress !== 'object' || Array.isArray(progress)) {
      throw new TypeError('progress must be an object');
    }
    return this.#write('progress', stage, { progress });
  }

  action(action, message) {
    if (!isIdentifier(action, 64, false)) throw new TypeError('action must be an ASCII identifier');
    return this.#write('action', this.stage, { action, ...optionalMessage(message) });
  }

  terminal(outcome, exitCode, message) {
    if (!TERMINAL_OUTCOMES.has(outcome)) throw new TypeError(`unknown terminal outcome: ${outcome}`);
    if (!Number.isInteger(exitCode) || exitCode < 0 || exitCode > 255) {
      throw new TypeError('exitCode must be an integer between 0 and 255');
    }
    if ((outcome === 'succeeded') !== (exitCode === 0)) {
      throw new TypeError('only succeeded may use exitCode 0');
    }
    return this.#write('terminal', this.stage, {
      outcome,
      exitCode,
      ...optionalMessage(message),
    });
  }

  #write(type, stage, fields) {
    if (!EVENT_TYPES.has(type)) throw new TypeError(`unknown event type: ${type}`);
    if (this.terminalWritten) throw new Error('cannot emit an event after terminal');

    const value = this.now();
    const timestamp = value instanceof Date ? value.toISOString() : String(value);
    const event = {
      contractVersion: CONTRACT_VERSION,
      workflowId: this.workflowId,
      seq: ++this.seq,
      timestamp,
      type,
      stage,
      ...fields,
    };
    this.output.write(JSON.stringify(event) + '\n');
    if (type === 'terminal') this.terminalWritten = true;
    return event;
  }
}

function optionalMessage(message) {
  return typeof message === 'string' && message.trim() ? { message: message.trim() } : {};
}

module.exports = {
  CONTRACT_VERSION,
  EVENT_TYPES,
  TERMINAL_OUTCOMES,
  WorkflowEventWriter,
  assertStage,
  assertWorkflowId,
  isIdentifier,
};
