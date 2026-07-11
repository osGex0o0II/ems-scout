#!/usr/bin/env node
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const SNAPSHOT_VERSION = 'ems.collection-snapshot/v1';
const WORKFLOW_EVENT_VERSION = 'ems.workflow-event/v1';
const WORKFLOW_CONTROL_VERSION = 'ems.workflow-control/v1';
const REPORT_VERSION = 'ems.contract-audit/v1';
const MAX_SCHEMA_ERRORS = 100;
const CORE_TABLES = ['buildings', 'cards', 'pages', 'sub_areas'];

const SNAPSHOT_SCHEMA = require(path.join(ROOT, 'contracts', 'collection-snapshot-v1.schema.json'));
const WORKFLOW_EVENT_SCHEMA = require(path.join(ROOT, 'contracts', 'workflow-event-v1.schema.json'));
const WORKFLOW_CONTROL_SCHEMA = require(path.join(ROOT, 'contracts', 'workflow-control-v1.schema.json'));

function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function stableValue(value) {
  if (Array.isArray(value)) return value.map(stableValue);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(
    Object.keys(value).sort().map(key => [key, stableValue(value[key])])
  );
}

function stableStringify(value, space = 2) {
  return JSON.stringify(stableValue(value), null, space);
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isType(value, expected) {
  switch (expected) {
    case 'array': return Array.isArray(value);
    case 'integer': return Number.isInteger(value);
    case 'null': return value === null;
    case 'number': return typeof value === 'number' && Number.isFinite(value);
    case 'object': return isPlainObject(value);
    default: return typeof value === expected;
  }
}

function valuesEqual(left, right) {
  return stableStringify(left, 0) === stableStringify(right, 0);
}

function pointerValue(root, reference) {
  if (!reference.startsWith('#/')) throw new Error(`Only local schema references are supported: ${reference}`);
  return reference.slice(2).split('/').reduce((value, part) => {
    const key = part.replace(/~1/g, '/').replace(/~0/g, '~');
    return value && value[key];
  }, root);
}

function propertyPath(base, key) {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key)
    ? `${base}.${key}`
    : `${base}[${JSON.stringify(key)}]`;
}

function addSchemaError(errors, at, message) {
  if (errors.length < MAX_SCHEMA_ERRORS) errors.push(`${at}: ${message}`);
}

// This intentionally implements only the Draft 2020-12 keywords used by the
// checked-in contracts. The contracts remain authoritative and can also be
// consumed by full validators in C# or CI.
function validateSchema(value, schema, rootSchema = schema, at = '$', errors = []) {
  if (schema === true) return errors;
  if (schema === false) {
    addSchemaError(errors, at, 'value is forbidden by schema');
    return errors;
  }
  if (!schema || errors.length >= MAX_SCHEMA_ERRORS) return errors;

  if (schema.$ref) {
    const target = pointerValue(rootSchema, schema.$ref);
    if (!target) addSchemaError(errors, at, `unresolved schema reference ${schema.$ref}`);
    else validateSchema(value, target, rootSchema, at, errors);
  }

  if (Object.prototype.hasOwnProperty.call(schema, 'const') && !valuesEqual(value, schema.const)) {
    addSchemaError(errors, at, `must equal ${JSON.stringify(schema.const)}`);
  }
  if (schema.enum && !schema.enum.some(option => valuesEqual(value, option))) {
    addSchemaError(errors, at, `must be one of ${schema.enum.map(JSON.stringify).join(', ')}`);
  }

  if (schema.type) {
    const expected = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!expected.some(type => isType(value, type))) {
      addSchemaError(errors, at, `must have type ${expected.join(' or ')}`);
      return errors;
    }
  }

  if (schema.anyOf) {
    const valid = schema.anyOf.some(branch => {
      const branchErrors = [];
      validateSchema(value, branch, rootSchema, at, branchErrors);
      return branchErrors.length === 0;
    });
    if (!valid) addSchemaError(errors, at, 'must match at least one anyOf branch');
  }

  if (schema.oneOf) {
    const matches = schema.oneOf.filter(branch => {
      const branchErrors = [];
      validateSchema(value, branch, rootSchema, at, branchErrors);
      return branchErrors.length === 0;
    }).length;
    if (matches !== 1) addSchemaError(errors, at, 'must match exactly one oneOf branch');
  }

  if (schema.allOf) {
    for (const branch of schema.allOf) validateSchema(value, branch, rootSchema, at, errors);
  }

  if (schema.if) {
    const conditionErrors = [];
    validateSchema(value, schema.if, rootSchema, at, conditionErrors);
    if (conditionErrors.length === 0 && schema.then) {
      validateSchema(value, schema.then, rootSchema, at, errors);
    } else if (conditionErrors.length > 0 && schema.else) {
      validateSchema(value, schema.else, rootSchema, at, errors);
    }
  }

  if (typeof value === 'string') {
    if (schema.minLength !== undefined && value.length < schema.minLength) {
      addSchemaError(errors, at, `must contain at least ${schema.minLength} character(s)`);
    }
    if (schema.maxLength !== undefined && value.length > schema.maxLength) {
      addSchemaError(errors, at, `must contain at most ${schema.maxLength} character(s)`);
    }
    if (schema.pattern && !(new RegExp(schema.pattern)).test(value)) {
      addSchemaError(errors, at, `must match ${schema.pattern}`);
    }
    if (schema.format === 'date-time') {
      const isoDateTime = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
      if (!isoDateTime.test(value) || !Number.isFinite(Date.parse(value))) {
        addSchemaError(errors, at, 'must be an RFC 3339 date-time');
      }
    }
  }

  if (typeof value === 'number') {
    if (schema.minimum !== undefined && value < schema.minimum) {
      addSchemaError(errors, at, `must be >= ${schema.minimum}`);
    }
    if (schema.maximum !== undefined && value > schema.maximum) {
      addSchemaError(errors, at, `must be <= ${schema.maximum}`);
    }
  }

  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) {
      addSchemaError(errors, at, `must contain at least ${schema.minItems} item(s)`);
    }
    if (schema.uniqueItems) {
      const identities = value.map(item => stableStringify(item, 0));
      if (new Set(identities).size !== identities.length) addSchemaError(errors, at, 'items must be unique');
    }
    if (schema.items) {
      value.forEach((item, index) => validateSchema(item, schema.items, rootSchema, `${at}[${index}]`, errors));
    }
  }

  if (isPlainObject(value)) {
    const propertyCount = Object.keys(value).length;
    if (schema.minProperties !== undefined && propertyCount < schema.minProperties) {
      addSchemaError(errors, at, `must contain at least ${schema.minProperties} propert${schema.minProperties === 1 ? 'y' : 'ies'}`);
    }
    if (schema.maxProperties !== undefined && propertyCount > schema.maxProperties) {
      addSchemaError(errors, at, `must contain at most ${schema.maxProperties} properties`);
    }
    if (schema.required) {
      for (const key of schema.required) {
        if (!Object.prototype.hasOwnProperty.call(value, key)) {
          addSchemaError(errors, propertyPath(at, key), 'required property is missing');
        }
      }
    }
    if (schema.properties) {
      for (const key of Object.keys(schema.properties).sort()) {
        if (Object.prototype.hasOwnProperty.call(value, key)) {
          validateSchema(value[key], schema.properties[key], rootSchema, propertyPath(at, key), errors);
        }
      }
    }
    if (schema.additionalProperties === false) {
      const known = new Set(Object.keys(schema.properties || {}));
      for (const key of Object.keys(value).sort()) {
        if (!known.has(key)) addSchemaError(errors, propertyPath(at, key), 'additional property is not allowed');
      }
    }
  }

  return errors;
}

function normalizedValuePath(parts) {
  let result = '$';
  for (const part of parts) {
    if (typeof part === 'number') result += '[]';
    else result = propertyPath(result, part);
  }
  return result;
}

function exactValuePath(parts) {
  let result = '$';
  for (const part of parts) {
    if (typeof part === 'number') result += `[${part}]`;
    else result = propertyPath(result, part);
  }
  return result;
}

function analyzeJsonValues(value) {
  const counts = {
    emptyString: 0,
    hyphen: 0,
    null: 0,
    numericZero: 0,
    stringZero: 0,
    total: 0,
  };
  const byPath = new Map();
  const nameFloorPaths = [];
  let nameFloorCount = 0;

  function recordSentinel(kind, parts) {
    counts[kind] += 1;
    counts.total += 1;
    const key = normalizedValuePath(parts);
    if (!byPath.has(key)) {
      byPath.set(key, { emptyString: 0, hyphen: 0, null: 0, numericZero: 0, stringZero: 0, total: 0 });
    }
    const entry = byPath.get(key);
    entry[kind] += 1;
    entry.total += 1;
  }

  function visit(current, parts) {
    if (current === null) return recordSentinel('null', parts);
    if (current === '') return recordSentinel('emptyString', parts);
    if (current === '-') return recordSentinel('hyphen', parts);
    if (current === 0) return recordSentinel('numericZero', parts);
    if (current === '0') return recordSentinel('stringZero', parts);
    if (Array.isArray(current)) {
      current.forEach((item, index) => visit(item, [...parts, index]));
      return;
    }
    if (!isPlainObject(current)) return;
    for (const key of Object.keys(current).sort()) {
      const childParts = [...parts, key];
      if (key === '_nameFloor') {
        nameFloorCount += 1;
        if (nameFloorPaths.length < 25) nameFloorPaths.push(exactValuePath(childParts));
      }
      visit(current[key], childParts);
    }
  }

  visit(value, []);
  return {
    internalFields: {
      _nameFloor: {
        count: nameFloorCount,
        samplePaths: nameFloorPaths,
      },
    },
    sentinels: {
      byKind: counts,
      byPath: [...byPath.entries()]
        .sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0)
        .map(([valuePath, pathCounts]) => ({ path: valuePath, ...pathCounts })),
    },
  };
}

function topLevelVersions(values) {
  const versions = values
    .filter(isPlainObject)
    .filter(value => Object.prototype.hasOwnProperty.call(value, 'contractVersion'))
    .map(value => value.contractVersion)
    .filter(value => typeof value === 'string');
  return [...new Set(versions)].sort();
}

function schemaValidation(value, schema) {
  const errors = validateSchema(value, schema).sort();
  return {
    errorCount: errors.length,
    errors,
    schemaId: schema.$id,
    valid: errors.length === 0,
  };
}

function workflowStreamValidation(events, itemLabel) {
  const errors = [];
  events.forEach((event, index) => {
    const validation = schemaValidation(event, WORKFLOW_EVENT_SCHEMA);
    validation.errors.forEach(error => errors.push(`${itemLabel} ${index + 1} ${error}`));
  });
  if (!events.length) errors.push('stream must contain at least one event');
  if (events.length && events[0].type !== 'started') errors.push(`${itemLabel} 1 must have type started`);
  if (events.length && events.at(-1).type !== 'terminal') {
    errors.push(`${itemLabel} ${events.length} must have type terminal`);
  }
  const workflowId = events[0] && events[0].workflowId;
  let startedCount = 0;
  let terminalCount = 0;
  events.forEach((event, index) => {
    if (event.workflowId !== workflowId) errors.push(`${itemLabel} ${index + 1} changed workflowId`);
    if (event.seq !== index + 1) errors.push(`${itemLabel} ${index + 1} must have seq ${index + 1}`);
    if (event.type === 'started') startedCount += 1;
    if (event.type === 'terminal') terminalCount += 1;
  });
  if (startedCount !== 1) errors.push(`stream must contain exactly one started event; found ${startedCount}`);
  if (terminalCount !== 1) errors.push(`stream must contain exactly one terminal event; found ${terminalCount}`);
  const limitedErrors = errors.sort().slice(0, MAX_SCHEMA_ERRORS);
  return {
    errorCount: limitedErrors.length,
    errors: limitedErrors,
    schemaId: WORKFLOW_EVENT_SCHEMA.$id,
    valid: limitedErrors.length === 0,
  };
}

function classifyJson(value, documentFormat) {
  if (documentFormat === 'ndjson') {
    const versions = topLevelVersions(value);
    const known = value.length > 0 && value.every(
      event => isPlainObject(event) && event.contractVersion === WORKFLOW_EVENT_VERSION
    );
    return {
      contractValidation: known ? workflowStreamValidation(value, 'line') : null,
      shape: known ? 'workflow-event-stream/v1' : 'unknown-ndjson',
      topLevelContractVersion: versions.length === 1 ? versions[0] : null,
      topLevelContractVersions: versions,
      unknownShape: !known,
    };
  }

  if (isPlainObject(value) && value.contractVersion === SNAPSHOT_VERSION) {
    return {
      contractValidation: schemaValidation(value, SNAPSHOT_SCHEMA),
      shape: 'collection-snapshot/v1',
      topLevelContractVersion: SNAPSHOT_VERSION,
      topLevelContractVersions: [SNAPSHOT_VERSION],
      unknownShape: false,
    };
  }
  if (isPlainObject(value) && value.contractVersion === WORKFLOW_EVENT_VERSION) {
    return {
      contractValidation: schemaValidation(value, WORKFLOW_EVENT_SCHEMA),
      shape: 'workflow-event/v1',
      topLevelContractVersion: WORKFLOW_EVENT_VERSION,
      topLevelContractVersions: [WORKFLOW_EVENT_VERSION],
      unknownShape: false,
    };
  }
  if (isPlainObject(value) && value.contractVersion === WORKFLOW_CONTROL_VERSION) {
    return {
      contractValidation: schemaValidation(value, WORKFLOW_CONTROL_SCHEMA),
      shape: 'workflow-control/v1',
      topLevelContractVersion: WORKFLOW_CONTROL_VERSION,
      topLevelContractVersions: [WORKFLOW_CONTROL_VERSION],
      unknownShape: false,
    };
  }
  if (Array.isArray(value) && value.length > 0 && value.every(
    event => isPlainObject(event) && event.contractVersion === WORKFLOW_EVENT_VERSION
  )) {
    return {
      contractValidation: workflowStreamValidation(value, 'item'),
      shape: 'workflow-event-list/v1',
      topLevelContractVersion: WORKFLOW_EVENT_VERSION,
      topLevelContractVersions: [WORKFLOW_EVENT_VERSION],
      unknownShape: false,
    };
  }
  if (isPlainObject(value) && !Object.prototype.hasOwnProperty.call(value, 'contractVersion')) {
    if (Array.isArray(value.buildings)) {
      return {
        contractValidation: null,
        shape: 'legacy-collection-snapshot/v0',
        topLevelContractVersion: null,
        topLevelContractVersions: [],
        unknownShape: false,
      };
    }
    if (Array.isArray(value.targets)) {
      return {
        contractValidation: null,
        shape: 'legacy-recapture-snapshot/v0',
        topLevelContractVersion: null,
        topLevelContractVersions: [],
        unknownShape: false,
      };
    }
  }

  const versions = topLevelVersions(Array.isArray(value) ? value : [value]);
  return {
    contractValidation: null,
    shape: versions.length ? 'unknown-contract-version' : 'unknown-json',
    topLevelContractVersion: versions.length === 1 ? versions[0] : null,
    topLevelContractVersions: versions,
    unknownShape: true,
  };
}

function parseJsonDocument(buffer) {
  const text = buffer.toString('utf8').replace(/^\uFEFF/, '');
  try {
    return { documentFormat: 'json', value: JSON.parse(text) };
  } catch (jsonError) {
    const lines = text.split(/\r?\n/).map((line, index) => ({ line, number: index + 1 }))
      .filter(entry => entry.line.trim());
    if (!lines.length) throw jsonError;
    try {
      return {
        documentFormat: 'ndjson',
        value: lines.map(entry => JSON.parse(entry.line)),
      };
    } catch (lineError) {
      const error = new Error(`Invalid JSON or NDJSON: ${lineError.message}`);
      error.code = 'INVALID_JSON';
      throw error;
    }
  }
}

function auditJson(buffer) {
  const parsed = parseJsonDocument(buffer);
  const classification = classifyJson(parsed.value, parsed.documentFormat);
  return {
    documentFormat: parsed.documentFormat,
    ...classification,
    ...analyzeJsonValues(parsed.value),
  };
}

function runSqliteJson(sqliteBin, dbPath, sql) {
  // dbPath is always the private snapshot created by withSqliteSnapshot. A
  // writable open is required for archived WAL-mode databases that no longer
  // have -wal/-shm companions; all statements run with query_only enabled.
  const result = spawnSync(sqliteBin, ['-json', dbPath, `PRAGMA query_only=ON; ${sql}`], {
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
  if (result.error && result.error.code === 'ENOENT') return null;
  if (result.error) throw result.error;
  if (result.status !== 0) {
    const error = new Error(`sqlite3 failed: ${(result.stderr || result.stdout).trim()}`);
    error.code = 'SQLITE_READ_FAILED';
    throw error;
  }
  const output = result.stdout.trim();
  return output ? JSON.parse(output) : [];
}

function readSqliteWithCli(dbPath) {
  const sqliteBin = process.env.SQLITE3_BIN || 'sqlite3';
  const versionRows = runSqliteJson(sqliteBin, dbPath, 'PRAGMA user_version;');
  if (versionRows === null) return null;
  const objects = runSqliteJson(sqliteBin, dbPath, `
    SELECT type, name, tbl_name, sql
    FROM sqlite_schema
    WHERE name NOT LIKE 'sqlite_%'
    ORDER BY type, name
  `);
  const columns = runSqliteJson(sqliteBin, dbPath, `
    SELECT m.name AS table_name,
           p.cid,
           p.name,
           p.type,
           p."notnull" AS not_null,
           p.dflt_value,
           p.pk,
           p.hidden
    FROM sqlite_schema AS m
    JOIN pragma_table_xinfo(m.name) AS p
    WHERE m.type = 'table'
      AND m.name NOT LIKE 'sqlite_%'
    ORDER BY m.name, p.cid
  `);
  return {
    columns,
    objects,
    reader: 'sqlite3-cli',
    userVersion: Number(versionRows[0] && versionRows[0].user_version) || 0,
  };
}

function readSqliteWithNode(dbPath) {
  let DatabaseSync;
  try {
    ({ DatabaseSync } = require('node:sqlite'));
  } catch {
    return null;
  }
  // Match the CLI behavior: opening the private copy read-write lets SQLite
  // initialize missing WAL bookkeeping before query_only blocks mutations.
  const db = new DatabaseSync(dbPath);
  try {
    db.exec('PRAGMA query_only=ON');
    const version = db.prepare('PRAGMA user_version').get();
    const objects = db.prepare(`
      SELECT type, name, tbl_name, sql
      FROM sqlite_schema
      WHERE name NOT LIKE 'sqlite_%'
      ORDER BY type, name
    `).all();
    const columns = db.prepare(`
      SELECT m.name AS table_name,
             p.cid,
             p.name,
             p.type,
             p."notnull" AS not_null,
             p.dflt_value,
             p.pk,
             p.hidden
      FROM sqlite_schema AS m
      JOIN pragma_table_xinfo(m.name) AS p
      WHERE m.type = 'table'
        AND m.name NOT LIKE 'sqlite_%'
      ORDER BY m.name, p.cid
    `).all();
    return {
      columns: columns.map(row => ({ ...row })),
      objects: objects.map(row => ({ ...row })),
      reader: 'node:sqlite',
      userVersion: Number(version && version.user_version) || 0,
    };
  } finally {
    db.close();
  }
}

function normalizeSqliteData(raw) {
  const columnsByTable = new Map();
  for (const row of raw.columns) {
    if (!columnsByTable.has(row.table_name)) columnsByTable.set(row.table_name, []);
    columnsByTable.get(row.table_name).push({
      cid: Number(row.cid),
      defaultValue: row.dflt_value === undefined ? null : row.dflt_value,
      hidden: Number(row.hidden || 0),
      name: row.name,
      notNull: Boolean(row.not_null),
      primaryKey: Number(row.pk || 0),
      type: row.type || '',
    });
  }
  const tables = [...columnsByTable.entries()]
    .sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0)
    .map(([name, columns]) => ({ name, columns }));
  const objects = raw.objects.map(row => ({
    name: row.name,
    sql: row.sql === undefined ? null : row.sql,
    tableName: row.tbl_name,
    type: row.type,
  }));
  return { objects, tables };
}

function withSqliteSnapshot(dbPath, callback) {
  const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-contract-audit-'));
  const snapshotPath = path.join(tempDirectory, 'audit.db');
  try {
    fs.copyFileSync(dbPath, snapshotPath);
    const walPath = `${dbPath}-wal`;
    if (fs.existsSync(walPath)) fs.copyFileSync(walPath, `${snapshotPath}-wal`);
    return callback(snapshotPath);
  } finally {
    fs.rmSync(tempDirectory, { force: true, recursive: true });
  }
}

function auditSqlite(dbPath) {
  const raw = withSqliteSnapshot(dbPath, snapshotPath =>
    readSqliteWithCli(snapshotPath) || readSqliteWithNode(snapshotPath)
  );
  if (!raw) {
    const error = new Error('SQLite audit requires the sqlite3 CLI or a Node runtime with node:sqlite');
    error.code = 'SQLITE_READER_UNAVAILABLE';
    throw error;
  }
  const normalized = normalizeSqliteData(raw);
  const tableNames = normalized.tables.map(table => table.name);
  const missingCoreTables = CORE_TABLES.filter(name => !tableNames.includes(name));
  const fingerprintPayload = {
    objects: normalized.objects,
    tables: normalized.tables,
    userVersion: raw.userVersion,
  };
  return {
    shape: missingCoreTables.length ? 'unknown-sqlite' : 'ems-sqlite/core',
    topLevelContractVersion: null,
    topLevelContractVersions: [],
    unknownShape: missingCoreTables.length > 0,
    sqlite: {
      missingCoreTables,
      reader: raw.reader,
      schemaFingerprint: sha256(Buffer.from(stableStringify(fingerprintPayload, 0), 'utf8')),
      schemaObjectCount: normalized.objects.length,
      tables: normalized.tables,
      userVersion: raw.userVersion,
    },
  };
}

function displayPath(inputPath) {
  return path.normalize(inputPath).replace(/\\/g, '/');
}

function fileIdentity(filePath) {
  try {
    const stat = fs.statSync(filePath, { bigint: true });
    return {
      mtimeNs: stat.mtimeNs,
      sha256: sha256(fs.readFileSync(filePath)),
      size: stat.size,
    };
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

function sqliteCompanionIdentity(dbPath) {
  return {
    shm: fileIdentity(`${dbPath}-shm`),
    wal: fileIdentity(`${dbPath}-wal`),
  };
}

function sameFileIdentity(left, right) {
  if (left === null || right === null) return left === right;
  return left.size === right.size && left.mtimeNs === right.mtimeNs && left.sha256 === right.sha256;
}

function sameCompanionIdentity(left, right) {
  return sameFileIdentity(left.shm, right.shm) && sameFileIdentity(left.wal, right.wal);
}

function auditFile(inputPath) {
  const absolutePath = path.resolve(inputPath);
  const beforeStat = fs.statSync(absolutePath, { bigint: true });
  if (!beforeStat.isFile()) {
    const error = new Error('Input is not a regular file');
    error.code = 'NOT_A_FILE';
    throw error;
  }
  const beforeBytes = fs.readFileSync(absolutePath);
  const beforeHash = sha256(beforeBytes);
  const isSqlite = beforeBytes.length >= 16 && beforeBytes.subarray(0, 16).toString('binary') === 'SQLite format 3\u0000';
  const beforeCompanions = isSqlite ? sqliteCompanionIdentity(absolutePath) : null;
  const details = isSqlite ? auditSqlite(absolutePath) : auditJson(beforeBytes);
  const afterStat = fs.statSync(absolutePath, { bigint: true });
  const afterBytes = fs.readFileSync(absolutePath);
  const afterHash = sha256(afterBytes);
  const sizeUnchanged = beforeStat.size === afterStat.size;
  const mtimeUnchanged = beforeStat.mtimeNs === afterStat.mtimeNs;
  const contentUnchanged = beforeHash === afterHash;
  const companionsUnchanged = !isSqlite || sameCompanionIdentity(
    beforeCompanions,
    sqliteCompanionIdentity(absolutePath)
  );
  if (!sizeUnchanged || !mtimeUnchanged || !contentUnchanged || !companionsUnchanged) {
    const error = new Error('Input changed while it was being audited');
    error.code = 'INPUT_CHANGED_DURING_AUDIT';
    throw error;
  }
  return {
    byteLength: Number(beforeStat.size),
    kind: isSqlite ? 'sqlite' : 'json',
    path: displayPath(inputPath),
    readOnlyCheck: {
      ...(isSqlite ? { companionsUnchanged } : {}),
      contentUnchanged,
      mtimeUnchanged,
      sizeUnchanged,
    },
    sha256: beforeHash,
    ...details,
  };
}

function parseArguments(argv) {
  const paths = [];
  let positionalOnly = false;
  for (const arg of argv) {
    if (!positionalOnly && arg === '--') {
      positionalOnly = true;
    } else if (!positionalOnly && (arg === '--help' || arg === '-h')) {
      return { help: true, paths: [] };
    } else if (!positionalOnly && arg.startsWith('-')) {
      const error = new Error(`Unknown option: ${arg}`);
      error.code = 'INVALID_ARGUMENT';
      throw error;
    } else {
      paths.push(arg);
    }
  }
  return { help: false, paths };
}

function usage() {
  return [
    'Usage: node scripts/audit-contracts.js <json-or-sqlite> [...]',
    '',
    'Writes one deterministic JSON report to stdout without modifying inputs.',
    'Exit 0: recognized shapes; exit 1: read/parse failure; exit 2: unknown or invalid contract shape.',
  ].join('\n');
}

function main(argv = process.argv.slice(2)) {
  let args;
  try {
    args = parseArguments(argv);
  } catch (error) {
    process.stderr.write(`${error.message}\n${usage()}\n`);
    return 1;
  }
  if (args.help) {
    process.stdout.write(`${usage()}\n`);
    return 0;
  }
  if (!args.paths.length) {
    process.stderr.write(`${usage()}\n`);
    return 1;
  }

  let hasFailure = false;
  let hasContractFailure = false;
  const files = args.paths.map(inputPath => {
    try {
      const report = auditFile(inputPath);
      if (report.unknownShape || (report.contractValidation && !report.contractValidation.valid)) {
        hasContractFailure = true;
      }
      return report;
    } catch (error) {
      hasFailure = true;
      return {
        error: {
          code: error.code || 'AUDIT_FAILED',
          message: error.message,
        },
        path: displayPath(inputPath),
      };
    }
  });
  process.stdout.write(`${stableStringify({ files, reportVersion: REPORT_VERSION })}\n`);
  if (hasFailure) return 1;
  if (hasContractFailure) return 2;
  return 0;
}

if (require.main === module) process.exitCode = main();

module.exports = {
  REPORT_VERSION,
  SNAPSHOT_VERSION,
  WORKFLOW_EVENT_VERSION,
  WORKFLOW_CONTROL_VERSION,
  analyzeJsonValues,
  auditFile,
  auditJson,
  auditSqlite,
  classifyJson,
  main,
  stableStringify,
  validateSchema,
};
