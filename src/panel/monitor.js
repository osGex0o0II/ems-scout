'use strict';

const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const { BLDG_ORDER } = require('../rules');
const {
  ensureHistorySchema,
  parseFloorValue,
  normalizeFloorLabel,
  floorLabelFromValue,
  resolveRunId,
  sourceForRun,
} = require('./history');

const ROOT = path.join(__dirname, '..', '..');
const DB_PATH = process.env.EMS_DB_PATH || path.join(ROOT, 'out', 'ac.db');

const STATUS_META = {
  not_in_system: { label: '未出现在系统', severity: 'OK', opened: false },
  visible_no_cards: { label: '可见但无卡片', severity: 'INFO', opened: false },
  all_offline: { label: '有卡但全离线', severity: 'P3', opened: false },
  all_off: { label: '有卡但全关机', severity: 'P2', opened: true },
  mixed_idle: { label: '有卡混合关机/离线', severity: 'P2', opened: true },
  found_on: { label: '发现开机', severity: 'P1', opened: true },
  data_incomplete: { label: '有卡但状态不完整', severity: 'P2', opened: true },
  collect_failed: { label: '采集失败', severity: 'P2', opened: false },
  invalid_target: { label: '配置无效', severity: 'P2', opened: false },
};

function openDb(options = {}) {
  if (!fs.existsSync(DB_PATH)) {
    throw new Error('Database not found: ' + DB_PATH);
  }
  return new Database(DB_PATH, options);
}

function ensureMonitorSchema(db) {
  ensureHistorySchema(db);
  db.exec(`
    CREATE TABLE IF NOT EXISTS monitor_groups (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL UNIQUE,
      area_label TEXT NOT NULL DEFAULT '',
      description TEXT NOT NULL DEFAULT '',
      priority TEXT NOT NULL DEFAULT '重点',
      group_kind TEXT NOT NULL DEFAULT 'custom',
      system_key TEXT,
      locked INTEGER NOT NULL DEFAULT 0,
      enabled INTEGER NOT NULL DEFAULT 1,
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    );

    CREATE TABLE IF NOT EXISTS monitor_group_items (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      group_id INTEGER NOT NULL,
      target_type TEXT NOT NULL DEFAULT 'floor',
      building TEXT NOT NULL,
      floor_label TEXT,
      floor_value REAL,
      sub_area_text TEXT,
      card_name TEXT,
      note TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      FOREIGN KEY(group_id) REFERENCES monitor_groups(id)
    );
    CREATE INDEX IF NOT EXISTS idx_monitor_group_items_group ON monitor_group_items(group_id);
    CREATE INDEX IF NOT EXISTS idx_monitor_group_items_target ON monitor_group_items(building, floor_value, sub_area_text, card_name);

    CREATE TABLE IF NOT EXISTS monitored_floors (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      building TEXT NOT NULL,
      floor_label TEXT NOT NULL,
      floor_value REAL,
      sub_area_text TEXT,
      expected_status TEXT NOT NULL DEFAULT '未开放',
      priority TEXT NOT NULL DEFAULT '重点',
      enabled INTEGER NOT NULL DEFAULT 1,
      note TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE UNIQUE INDEX IF NOT EXISTS idx_monitored_floors_key
      ON monitored_floors(building, floor_label, IFNULL(sub_area_text, ''));

    CREATE TABLE IF NOT EXISTS floor_monitor_snapshots (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      monitored_floor_id INTEGER NOT NULL,
      observed_at TEXT NOT NULL,
      status_code TEXT NOT NULL,
      status_label TEXT NOT NULL,
      severity TEXT NOT NULL,
      opened INTEGER NOT NULL DEFAULT 0,
      sub_area_count INTEGER NOT NULL DEFAULT 0,
      page_count INTEGER NOT NULL DEFAULT 0,
      card_count INTEGER NOT NULL DEFAULT 0,
      on_count INTEGER NOT NULL DEFAULT 0,
      off_count INTEGER NOT NULL DEFAULT 0,
      offline_count INTEGER NOT NULL DEFAULT 0,
      unknown_count INTEGER NOT NULL DEFAULT 0,
      real_temp_count INTEGER NOT NULL DEFAULT 0,
      run_id INTEGER,
      detail_json TEXT NOT NULL DEFAULT '{}',
      FOREIGN KEY(monitored_floor_id) REFERENCES monitored_floors(id)
    );
    CREATE INDEX IF NOT EXISTS idx_floor_monitor_snapshots_target
      ON floor_monitor_snapshots(monitored_floor_id, id DESC);

    CREATE TABLE IF NOT EXISTS floor_monitor_events (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      monitored_floor_id INTEGER NOT NULL,
      event_at TEXT NOT NULL,
      event_type TEXT NOT NULL,
      severity TEXT NOT NULL,
      previous_status TEXT,
      current_status TEXT NOT NULL,
      message TEXT NOT NULL,
      snapshot_id INTEGER,
      run_id INTEGER,
      acknowledged INTEGER NOT NULL DEFAULT 0,
      detail_json TEXT NOT NULL DEFAULT '{}',
      FOREIGN KEY(monitored_floor_id) REFERENCES monitored_floors(id),
      FOREIGN KEY(snapshot_id) REFERENCES floor_monitor_snapshots(id)
    );
    CREATE INDEX IF NOT EXISTS idx_floor_monitor_events_target
      ON floor_monitor_events(monitored_floor_id, id DESC);

    CREATE TABLE IF NOT EXISTS device_tags (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      card_name TEXT NOT NULL,
      building TEXT,
      tag TEXT NOT NULL,
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      UNIQUE(card_name, building, tag)
    );

    CREATE TABLE IF NOT EXISTS device_notes (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      card_name TEXT NOT NULL,
      building TEXT,
      note TEXT NOT NULL,
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      UNIQUE(card_name, building)
    );

    CREATE TABLE IF NOT EXISTS manual_overrides (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      card_name TEXT NOT NULL,
      building TEXT,
      field TEXT NOT NULL,
      value TEXT NOT NULL,
      reason TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      UNIQUE(card_name, building, field)
    );

    CREATE TABLE IF NOT EXISTS realtime_match_overrides (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      building TEXT NOT NULL,
      dev_id TEXT,
      floor_label TEXT,
      sub_area TEXT,
      page_name TEXT,
      realtime_name TEXT NOT NULL,
      action TEXT NOT NULL DEFAULT 'classify_only',
      target_card_id INTEGER,
      zuo_override TEXT,
      area_type_override TEXT,
      note TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE INDEX IF NOT EXISTS idx_realtime_match_overrides_dev
      ON realtime_match_overrides(building, dev_id);
    CREATE INDEX IF NOT EXISTS idx_realtime_match_overrides_identity
      ON realtime_match_overrides(building, floor_label, sub_area, page_name, realtime_name);
    CREATE UNIQUE INDEX IF NOT EXISTS ux_realtime_match_overrides_dev
      ON realtime_match_overrides(building, dev_id)
      WHERE IFNULL(dev_id, '') <> '';
    CREATE UNIQUE INDEX IF NOT EXISTS ux_realtime_match_overrides_identity
      ON realtime_match_overrides(building, floor_label, sub_area, page_name, realtime_name)
      WHERE IFNULL(dev_id, '') = '';
  `);
  try { db.exec('ALTER TABLE floor_monitor_snapshots ADD COLUMN run_id INTEGER'); } catch {}
  try { db.exec('ALTER TABLE floor_monitor_events ADD COLUMN run_id INTEGER'); } catch {}
  try { db.exec("ALTER TABLE monitor_groups ADD COLUMN group_kind TEXT NOT NULL DEFAULT 'custom'"); } catch {}
  try { db.exec('ALTER TABLE monitor_groups ADD COLUMN system_key TEXT'); } catch {}
  try { db.exec('ALTER TABLE monitor_groups ADD COLUMN locked INTEGER NOT NULL DEFAULT 0'); } catch {}
  try { db.exec('CREATE INDEX IF NOT EXISTS idx_floor_monitor_snapshots_run ON floor_monitor_snapshots(run_id, monitored_floor_id, id DESC)'); } catch {}
  try { db.exec('CREATE INDEX IF NOT EXISTS idx_floor_monitor_events_run ON floor_monitor_events(run_id, monitored_floor_id, id DESC)'); } catch {}
  ensureSystemGroups(db);
}

function normalizePriority(value) {
  return ['普通', '重点', '紧急'].includes(value) ? value : '重点';
}

function ensureSystemGroups(db) {
  const now = new Date().toISOString();
  const upsert = db.prepare(`
    INSERT INTO monitor_groups
      (name, area_label, description, priority, group_kind, system_key, locked, enabled, created_at, updated_at)
    VALUES (?, ?, ?, '重点', 'system', ?, 1, 1, ?, ?)
    ON CONFLICT(name) DO UPDATE SET
      area_label = excluded.area_label,
      description = excluded.description,
      group_kind = 'system',
      system_key = excluded.system_key,
      locked = 1,
      enabled = 1,
      updated_at = excluded.updated_at
  `);
  upsert.run('公区', '公区', '系统区域：按公区规则动态匹配', 'public', now, now);
  upsert.run('非公区', '非公区', '系统区域：按非公区规则动态匹配', 'non_public', now, now);
  const oldDefault = db.prepare("SELECT id, group_kind, system_key FROM monitor_groups WHERE name = '未开放楼层'").get();
  if (oldDefault && oldDefault.group_kind === 'system') {
    db.prepare(`
      UPDATE monitor_groups
      SET group_kind = 'custom', system_key = NULL, locked = 0,
          description = CASE WHEN description LIKE '由旧版%' THEN '自定义组：未开放楼层' ELSE description END,
          updated_at = ?
      WHERE id = ?
    `).run(now, oldDefault.id);
  }
  const staleDefault = db.prepare(`
    SELECT mg.id
    FROM monitor_groups mg
    WHERE mg.name = '未开放楼层'
      AND mg.group_kind = 'custom'
      AND mg.locked = 0
      AND NOT EXISTS (SELECT 1 FROM monitor_group_items i WHERE i.group_id = mg.id)
      AND NOT EXISTS (SELECT 1 FROM monitored_floors mf WHERE mf.enabled = 1)
  `).get();
  if (staleDefault) {
    db.prepare('DELETE FROM monitor_groups WHERE id = ?').run(staleDefault.id);
  }
}

function migrateMonitoredFloorsToGroup(db) {
  const group = db.prepare("SELECT id FROM monitor_groups WHERE name = '未开放楼层'").get();
  if (!group) return;
  const rows = db.prepare(`
    SELECT id, building, floor_label, floor_value, sub_area_text, note
    FROM monitored_floors
    WHERE enabled = 1
  `).all();
  const exists = db.prepare(`
    SELECT id FROM monitor_group_items
    WHERE group_id = ? AND target_type = 'floor' AND building = ?
      AND IFNULL(floor_label, '') = IFNULL(?, '')
      AND IFNULL(sub_area_text, '') = IFNULL(?, '')
      AND IFNULL(card_name, '') = ''
  `);
  const insert = db.prepare(`
    INSERT INTO monitor_group_items
      (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, note, created_at, updated_at)
    VALUES (?, 'floor', ?, ?, ?, ?, NULL, ?, ?, ?)
  `);
  const now = new Date().toISOString();
  const tx = db.transaction(() => {
    for (const r of rows) {
      if (exists.get(group.id, r.building, r.floor_label, r.sub_area_text || null)) continue;
      insert.run(group.id, r.building, r.floor_label, r.floor_value, r.sub_area_text || null, r.note || '', now, now);
    }
  });
  tx();
}

function loadMonitors(db, options = {}) {
  ensureMonitorSchema(db);
  const where = options.includeDisabled ? '' : 'WHERE enabled = 1';
  return db.prepare(`
    SELECT id, building, floor_label, floor_value, sub_area_text, expected_status,
           priority, enabled, note, created_at, updated_at
    FROM monitored_floors
    ${where}
    ORDER BY building, floor_value, floor_label, IFNULL(sub_area_text, '')
  `).all();
}

function loadMonitorGroups(db, options = {}) {
  ensureMonitorSchema(db);
  const where = options.includeDisabled ? '' : 'WHERE enabled = 1';
  return db.prepare(`
    SELECT id, name, area_label, description, priority, group_kind, system_key, locked,
           enabled, created_at, updated_at
    FROM monitor_groups
    ${where}
    ORDER BY enabled DESC, priority DESC, id
  `).all();
}

function saveMonitorGroup(db, input) {
  ensureMonitorSchema(db);
  const name = String(input.name || '').trim();
  if (!name) throw new Error('Group name is required');
  const areaLabel = String(input.area_label || input.areaLabel || '').trim();
  const description = String(input.description || '').trim();
  const priority = normalizePriority(input.priority);
  const enabled = input.enabled === false || input.enabled === 0 ? 0 : 1;
  const now = new Date().toISOString();
  if (input.id) {
    const id = Number(input.id);
    const current = db.prepare('SELECT * FROM monitor_groups WHERE id = ?').get(id);
    if (!current) throw new Error('Group not found: ' + id);
    if (current.locked) {
      db.prepare(`
        UPDATE monitor_groups
        SET area_label = ?, description = ?, enabled = ?, updated_at = ?
        WHERE id = ?
      `).run(areaLabel || current.area_label, description || current.description, enabled, now, id);
      return db.prepare('SELECT * FROM monitor_groups WHERE id = ?').get(id);
    }
    db.prepare(`
      UPDATE monitor_groups
      SET name = ?, area_label = ?, description = ?, priority = ?, enabled = ?, updated_at = ?
      WHERE id = ?
    `).run(name, areaLabel, description, priority, enabled, now, id);
    return db.prepare('SELECT * FROM monitor_groups WHERE id = ?').get(id);
  }
  const existing = db.prepare('SELECT id FROM monitor_groups WHERE name = ?').get(name);
  if (existing) {
    db.prepare(`
      UPDATE monitor_groups
      SET area_label = ?, description = ?, priority = ?, enabled = ?, updated_at = ?
      WHERE id = ?
    `).run(areaLabel, description, priority, enabled, now, existing.id);
    return db.prepare('SELECT * FROM monitor_groups WHERE id = ?').get(existing.id);
  }
  const res = db.prepare(`
    INSERT INTO monitor_groups (name, area_label, description, priority, enabled, created_at, updated_at)
    VALUES (?, ?, ?, ?, ?, ?, ?)
  `).run(name, areaLabel, description, priority, enabled, now, now);
  return db.prepare('SELECT * FROM monitor_groups WHERE id = ?').get(res.lastInsertRowid);
}

function deleteMonitorGroup(db, id) {
  ensureMonitorSchema(db);
  const group = db.prepare('SELECT name, locked FROM monitor_groups WHERE id = ?').get(id);
  if (group && group.locked) throw new Error('系统区域不能删除');
  db.prepare('DELETE FROM monitor_group_items WHERE group_id = ?').run(id);
  return db.prepare('DELETE FROM monitor_groups WHERE id = ?').run(id).changes;
}

function loadMonitorGroupItems(db, groupId = null) {
  ensureMonitorSchema(db);
  const params = [];
  const where = groupId ? 'WHERE i.group_id = ?' : '';
  if (groupId) params.push(Number(groupId));
  return db.prepare(`
    SELECT i.id, i.group_id, g.name AS group_name, g.area_label, i.target_type,
           i.building, i.floor_label, i.floor_value, i.sub_area_text, i.card_name,
           i.note, i.created_at, i.updated_at
    FROM monitor_group_items i
    JOIN monitor_groups g ON g.id = i.group_id
    ${where}
    ORDER BY g.id, i.building, i.floor_value, i.sub_area_text, i.card_name
  `).all(...params);
}

function saveMonitorGroupItem(db, input) {
  ensureMonitorSchema(db);
  const groupId = Number(input.group_id || input.groupId);
  const group = db.prepare('SELECT id, group_kind, locked FROM monitor_groups WHERE id = ?').get(groupId);
  if (!group) throw new Error('Area not found: ' + groupId);
  if (group.group_kind === 'system' || group.locked) throw new Error('系统区域不需要手动添加成员');
  const targetType = String(input.target_type || input.targetType || 'floor').trim();
  const building = String(input.building || '').trim();
  const floorLabel = input.floor_label || input.floor || input.floorLabel ? normalizeFloorLabel(input.floor_label || input.floor || input.floorLabel) : null;
  const floorValue = floorLabel ? parseFloorValue(floorLabel) : null;
  const subAreaText = String(input.sub_area_text || input.subArea || '').trim() || null;
  const cardName = String(input.card_name || input.cardName || '').trim() || null;
  const note = String(input.note || '').trim();
  if (!BLDG_ORDER.includes(building)) throw new Error('Invalid building: ' + building);
  if (targetType === 'floor' && !floorLabel) throw new Error('floor target requires floor_label');
  if (targetType === 'sub_area' && (!floorLabel || !subAreaText)) throw new Error('sub_area target requires floor and sub_area');
  if (targetType === 'device' && !cardName) throw new Error('device target requires card_name');
  const now = new Date().toISOString();
  const existing = db.prepare(`
    SELECT id FROM monitor_group_items
    WHERE group_id = ? AND target_type = ? AND building = ?
      AND IFNULL(floor_label, '') = IFNULL(?, '')
      AND IFNULL(sub_area_text, '') = IFNULL(?, '')
      AND IFNULL(card_name, '') = IFNULL(?, '')
  `).get(groupId, targetType, building, floorLabel, subAreaText, cardName);
  if (existing) {
    db.prepare(`
      UPDATE monitor_group_items
      SET floor_value = ?, note = ?, updated_at = ?
      WHERE id = ?
    `).run(floorValue, note, now, existing.id);
    return db.prepare('SELECT * FROM monitor_group_items WHERE id = ?').get(existing.id);
  }
  const res = db.prepare(`
    INSERT INTO monitor_group_items
      (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, note, created_at, updated_at)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(groupId, targetType, building, floorLabel, floorValue, subAreaText, cardName, note, now, now);
  return db.prepare('SELECT * FROM monitor_group_items WHERE id = ?').get(res.lastInsertRowid);
}

function deleteMonitorGroupItem(db, id) {
  ensureMonitorSchema(db);
  return db.prepare('DELETE FROM monitor_group_items WHERE id = ?').run(Number(id)).changes;
}

function groupFilterClause(groupIds, alias = { sa: 'sa', c: 'c' }) {
  const ids = (Array.isArray(groupIds) ? groupIds : String(groupIds || '').split(','))
    .map(v => Number(v))
    .filter(v => Number.isInteger(v) && v > 0);
  if (!ids.length) return { clause: '', params: [] };
  const placeholders = ids.map(() => '?').join(',');
  return {
    clause: `
      EXISTS (
        SELECT 1
        FROM monitor_groups mg
        LEFT JOIN monitor_group_items mgi ON mgi.group_id = mg.id
        WHERE mg.enabled = 1
          AND mg.id IN (${placeholders})
          AND (
            (mg.group_kind = 'system' AND mg.system_key = 'public' AND ${publicSql(alias.c, 'p')})
            OR (mg.group_kind = 'system' AND mg.system_key = 'non_public' AND NOT (${publicSql(alias.c, 'p')}))
            OR (
              mg.group_kind <> 'system'
              AND mgi.building = ${alias.sa}.building
              AND (
                (mgi.target_type = 'device' AND mgi.card_name = ${alias.c}.name)
                OR (
                  mgi.target_type = 'sub_area'
                  AND ABS(COALESCE(${alias.sa}.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                  AND IFNULL(mgi.sub_area_text, '') = IFNULL(${alias.sa}.text, '')
                )
                OR (
                  mgi.target_type = 'floor'
                  AND ABS(COALESCE(${alias.sa}.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                )
              )
            )
          )
      )
    `,
    params: ids,
  };
}

function publicSql(cardAlias = 'c', pageAlias = 'p') {
  return `(
    ${pageAlias}.layout = 'group'
    OR (
      ${cardAlias}.name NOT GLOB 'QL-[0-9]*'
      AND (
        ${cardAlias}.name LIKE '%GQ%'
        OR ${cardAlias}.name LIKE '%WSJ%'
        OR ${cardAlias}.name LIKE '%DTT%'
        OR ${cardAlias}.name LIKE '%FDT%'
        OR ${cardAlias}.name LIKE '%XFDT%'
        OR ${cardAlias}.name LIKE '%CSJ%'
        OR ${cardAlias}.name LIKE '%FWJ%'
        OR ${cardAlias}.name LIKE '%ZBS%'
        OR ${cardAlias}.name LIKE '%ZSG%'
        OR ${cardAlias}.name LIKE '%MD%'
        OR ${cardAlias}.name LIKE '%RDJHJF%'
      )
    )
  )`;
}

function computeMonitorGroupStats(db, options = {}) {
  ensureMonitorSchema(db);
  const runId = resolveRunId(db, options.runId || options.run_id);
  const source = sourceForRun(runId);
  const groups = loadMonitorGroups(db, { includeDisabled: true });
  const out = [];
  for (const group of groups) {
    const filter = groupFilterClause([group.id]);
    const where = [];
    const params = [];
    if (source.runWhere) {
      where.push(source.runWhere);
      params.push(...source.runParams);
    }
    where.push(filter.clause);
    params.push(...filter.params);
    const stats = db.prepare(`
      SELECT COUNT(*) AS total,
             SUM(c.comm = '开机') AS on_count,
             SUM(c.comm = '关机') AS off_count,
             SUM(c.comm = '离线') AS offline_count,
             SUM(COALESCE(c.comm, '') NOT IN ('开机', '关机', '离线')) AS unknown_count,
             COUNT(DISTINCT sa.building || ':' || COALESCE(sa.floor, '') || ':' || COALESCE(sa.text, '')) AS covered_areas
      FROM ${source.subAreas} sa
      JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
      JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
      WHERE ${where.join(' AND ')}
    `).get(...params);
    const itemCount = db.prepare('SELECT COUNT(*) AS c FROM monitor_group_items WHERE group_id = ?').get(group.id).c;
    out.push({
      ...group,
      item_count: itemCount,
      run_id: runId,
      total: stats.total || 0,
      on_count: stats.on_count || 0,
      off_count: stats.off_count || 0,
      offline_count: stats.offline_count || 0,
      unknown_count: stats.unknown_count || 0,
      covered_areas: stats.covered_areas || 0,
    });
  }
  return out;
}

function getMonitor(db, id) {
  ensureMonitorSchema(db);
  return db.prepare('SELECT * FROM monitored_floors WHERE id = ?').get(id);
}

function saveMonitor(db, input) {
  ensureMonitorSchema(db);
  const building = String(input.building || '').trim();
  const floorLabel = normalizeFloorLabel(input.floor_label || input.floor || input.floorLabel);
  const floorValue = parseFloorValue(floorLabel);
  const subAreaText = String(input.sub_area_text || input.subArea || '').trim() || null;
  const expectedStatus = String(input.expected_status || input.expectedStatus || '未开放').trim() || '未开放';
  const priority = normalizePriority(input.priority);
  const enabled = input.enabled === false || input.enabled === 0 ? 0 : 1;
  const note = String(input.note || '').trim();

  if (!BLDG_ORDER.includes(building)) throw new Error('Invalid building: ' + building);
  if (!floorLabel) throw new Error('floor_label is required');

  const now = new Date().toISOString();
  if (input.id) {
    const existing = getMonitor(db, Number(input.id));
    if (!existing) throw new Error('Monitor not found: ' + input.id);
    db.prepare(`
      UPDATE monitored_floors
      SET building = ?, floor_label = ?, floor_value = ?, sub_area_text = ?,
          expected_status = ?, priority = ?, enabled = ?, note = ?, updated_at = ?
      WHERE id = ?
    `).run(building, floorLabel, floorValue, subAreaText, expectedStatus, priority, enabled, note, now, Number(input.id));
    return getMonitor(db, Number(input.id));
  }

  const existing = db.prepare(`
    SELECT id FROM monitored_floors
    WHERE building = ? AND floor_label = ? AND IFNULL(sub_area_text, '') = IFNULL(?, '')
  `).get(building, floorLabel, subAreaText);
  if (existing) {
    db.prepare(`
      UPDATE monitored_floors
      SET floor_value = ?, expected_status = ?, priority = ?, enabled = ?, note = ?, updated_at = ?
      WHERE id = ?
    `).run(floorValue, expectedStatus, priority, enabled, note, now, existing.id);
    return getMonitor(db, existing.id);
  }

  const res = db.prepare(`
    INSERT INTO monitored_floors
      (building, floor_label, floor_value, sub_area_text, expected_status, priority, enabled, note, created_at, updated_at)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(building, floorLabel, floorValue, subAreaText, expectedStatus, priority, enabled, note, now, now);
  return getMonitor(db, res.lastInsertRowid);
}

function deleteMonitor(db, id) {
  ensureMonitorSchema(db);
  db.prepare('DELETE FROM floor_monitor_events WHERE monitored_floor_id = ?').run(id);
  db.prepare('DELETE FROM floor_monitor_snapshots WHERE monitored_floor_id = ?').run(id);
  return db.prepare('DELETE FROM monitored_floors WHERE id = ?').run(id).changes;
}

function buildTargetWhere(monitor, source) {
  const params = [monitor.building];
  const clauses = ['sa.building = ?'];
  if (source.runWhere) {
    clauses.unshift(source.runWhere);
    params.unshift(...source.runParams);
  }
  const floorValue = monitor.floor_value;
  if (floorValue === null || floorValue === undefined || !Number.isFinite(Number(floorValue))) {
    return { invalid: true, where: clauses.join(' AND '), params };
  }
  clauses.push('ABS(COALESCE(sa.floor, -999999) - ?) < 0.001');
  params.push(Number(floorValue));
  if (monitor.sub_area_text) {
    clauses.push('sa.text = ?');
    params.push(monitor.sub_area_text);
  }
  return { where: clauses.join(' AND '), params };
}

function computeMonitorStatus(db, monitor, options = {}) {
  ensureMonitorSchema(db);
  const runId = resolveRunId(db, options.runId || options.run_id);
  const source = sourceForRun(runId);
  const target = buildTargetWhere(monitor, source);
  if (target.invalid) {
    return makeStatus(monitor, 'invalid_target', {
      message: '楼层无法解析为数值，无法与采集楼层匹配',
      floor_label: monitor.floor_label,
    }, runId);
  }

  const stats = db.prepare(`
    SELECT
      COUNT(DISTINCT sa.id) AS sub_area_count,
      COUNT(DISTINCT p.id) AS page_count,
      COUNT(DISTINCT c.id) AS card_count,
      SUM(CASE WHEN c.id IS NOT NULL AND c.comm = '开机' THEN 1 ELSE 0 END) AS on_count,
      SUM(CASE WHEN c.id IS NOT NULL AND c.comm = '关机' THEN 1 ELSE 0 END) AS off_count,
      SUM(CASE WHEN c.id IS NOT NULL AND c.comm = '离线' THEN 1 ELSE 0 END) AS offline_count,
      SUM(CASE WHEN c.id IS NOT NULL AND COALESCE(c.comm, '') NOT IN ('开机', '关机', '离线') THEN 1 ELSE 0 END) AS unknown_count,
      SUM(CASE WHEN c.id IS NOT NULL AND CAST(NULLIF(c.indoor, '-') AS REAL) > 0 THEN 1 ELSE 0 END) AS real_temp_count,
      SUM(CASE WHEN p.id IS NOT NULL AND COALESCE(p.err, '') <> '' THEN 1 ELSE 0 END) AS err_pages
    FROM ${source.subAreas} sa
    LEFT JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    LEFT JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    WHERE ${target.where}
  `).get(...target.params);

  const subAreaCount = Number(stats.sub_area_count || 0);
  const pageCount = Number(stats.page_count || 0);
  const cardCount = Number(stats.card_count || 0);
  const onCount = Number(stats.on_count || 0);
  const offCount = Number(stats.off_count || 0);
  const offlineCount = Number(stats.offline_count || 0);
  const unknownCount = Number(stats.unknown_count || 0);
  const realTempCount = Number(stats.real_temp_count || 0);
  const errPages = Number(stats.err_pages || 0);

  let code = 'not_in_system';
  if (subAreaCount === 0) code = 'not_in_system';
  else if (cardCount === 0 && errPages > 0) code = 'collect_failed';
  else if (cardCount === 0) code = 'visible_no_cards';
  else if (onCount > 0) code = 'found_on';
  else if (offlineCount === cardCount) code = 'all_offline';
  else if (offCount > 0 && offlineCount === 0 && unknownCount === 0) code = 'all_off';
  else if (offCount > 0 || realTempCount > 0) code = 'mixed_idle';
  else code = 'data_incomplete';

  return makeStatus(monitor, code, {
    sub_area_count: subAreaCount,
    page_count: pageCount,
    card_count: cardCount,
    on_count: onCount,
    off_count: offCount,
    offline_count: offlineCount,
    unknown_count: unknownCount,
    real_temp_count: realTempCount,
    err_pages: errPages,
  }, runId);
}

function makeStatus(monitor, code, stats, runId = null) {
  const meta = STATUS_META[code] || STATUS_META.invalid_target;
  return {
    run_id: runId,
    monitor_id: monitor.id,
    building: monitor.building,
    floor_label: monitor.floor_label,
    floor_value: monitor.floor_value,
    sub_area_text: monitor.sub_area_text || '',
    expected_status: monitor.expected_status || '未开放',
    priority: monitor.priority || '重点',
    enabled: Number(monitor.enabled) !== 0,
    note: monitor.note || '',
    status_code: code,
    status_label: meta.label,
    severity: meta.severity,
    opened: meta.opened,
    sub_area_count: Number(stats.sub_area_count || 0),
    page_count: Number(stats.page_count || 0),
    card_count: Number(stats.card_count || 0),
    on_count: Number(stats.on_count || 0),
    off_count: Number(stats.off_count || 0),
    offline_count: Number(stats.offline_count || 0),
    unknown_count: Number(stats.unknown_count || 0),
    real_temp_count: Number(stats.real_temp_count || 0),
    detail: stats,
  };
}

function insertSnapshot(db, status) {
  const now = new Date().toISOString();
  const previous = db.prepare(`
    SELECT *
    FROM floor_monitor_snapshots
    WHERE monitored_floor_id = ? AND IFNULL(run_id, 0) = IFNULL(?, 0)
    ORDER BY id DESC
    LIMIT 1
  `).get(status.monitor_id, status.run_id || null);

  const res = db.prepare(`
    INSERT INTO floor_monitor_snapshots
      (monitored_floor_id, observed_at, status_code, status_label, severity, opened,
       sub_area_count, page_count, card_count, on_count, off_count, offline_count,
       unknown_count, real_temp_count, run_id, detail_json)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(
    status.monitor_id,
    now,
    status.status_code,
    status.status_label,
    status.severity,
    status.opened ? 1 : 0,
    status.sub_area_count,
    status.page_count,
    status.card_count,
    status.on_count,
    status.off_count,
    status.offline_count,
    status.unknown_count,
    status.real_temp_count,
    status.run_id || null,
    JSON.stringify(status.detail || {})
  );

  const changed = !previous ||
    previous.status_code !== status.status_code ||
    Number(previous.card_count || 0) !== status.card_count ||
    Number(previous.on_count || 0) !== status.on_count;

  if (changed) {
    const previousLabel = previous ? previous.status_label : null;
    const eventType = previous ? 'status_changed' : 'initial_snapshot';
    const message = previous
      ? `${status.building} ${status.floor_label} 从「${previousLabel}」变为「${status.status_label}」`
      : `${status.building} ${status.floor_label} 初始状态「${status.status_label}」`;
    db.prepare(`
      INSERT INTO floor_monitor_events
        (monitored_floor_id, event_at, event_type, severity, previous_status,
         current_status, message, snapshot_id, run_id, detail_json)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `).run(
      status.monitor_id,
      now,
      eventType,
      status.severity,
      previousLabel,
      status.status_label,
      message,
      res.lastInsertRowid,
      status.run_id || null,
      JSON.stringify(status.detail || {})
    );
  }

  return { ...status, snapshot_id: res.lastInsertRowid, observed_at: now, changed };
}

function computeMonitorStatuses(db, options = {}) {
  const monitors = loadMonitors(db, { includeDisabled: options.includeDisabled !== false });
  return monitors.map(m => computeMonitorStatus(db, m, options));
}

function previousRunId(db, runId) {
  const current = resolveRunId(db, runId);
  if (!current) {
    const rows = db.prepare('SELECT id FROM collection_runs ORDER BY datetime(completed_at) DESC, id DESC LIMIT 2').all();
    return rows.length >= 2 ? rows[1].id : null;
  }
  const run = db.prepare('SELECT completed_at, id FROM collection_runs WHERE id = ?').get(current);
  if (!run) return null;
  const prev = db.prepare(`
    SELECT id
    FROM collection_runs
    WHERE datetime(completed_at) < datetime(?)
       OR (datetime(completed_at) = datetime(?) AND id < ?)
    ORDER BY datetime(completed_at) DESC, id DESC
    LIMIT 1
  `).get(run.completed_at, run.completed_at, current);
  return prev ? prev.id : null;
}

function compareMonitorStatuses(db, options = {}) {
  ensureMonitorSchema(db);
  const currentRunId = resolveRunId(db, options.runId || options.run_id);
  const baseRunId = options.baseRunId || options.base_run_id
    ? resolveRunId(db, options.baseRunId || options.base_run_id)
    : previousRunId(db, currentRunId);
  const current = computeMonitorStatuses(db, { includeDisabled: true, run_id: currentRunId || '' });
  if (!baseRunId) {
    return {
      current_run_id: currentRunId,
      base_run_id: null,
      changes: current.map(s => ({
        monitor_id: s.monitor_id,
        building: s.building,
        floor_label: s.floor_label,
        sub_area_text: s.sub_area_text,
        severity: s.severity,
        change_type: 'no_baseline',
        message: `${s.building} ${s.floor_label} 暂无上一批次可对比`,
        current: s,
        previous: null,
      })),
    };
  }
  const previous = computeMonitorStatuses(db, { includeDisabled: true, run_id: baseRunId });
  const prevById = new Map(previous.map(s => [s.monitor_id, s]));
  const changes = current.map(s => {
    const p = prevById.get(s.monitor_id) || null;
    return monitorChange(s, p);
  }).filter(Boolean);
  return {
    current_run_id: currentRunId,
    base_run_id: baseRunId,
    changes,
  };
}

function monitorChange(current, previous) {
  if (!previous) {
    return {
      monitor_id: current.monitor_id,
      building: current.building,
      floor_label: current.floor_label,
      sub_area_text: current.sub_area_text,
      severity: current.severity,
      change_type: 'new_monitor',
      message: `${current.building} ${current.floor_label} 新增监控对象，当前「${current.status_label}」`,
      current,
      previous: null,
    };
  }

  const diffs = [];
  let severity = current.severity;
  let changeType = 'unchanged';

  if (previous.status_code !== current.status_code) {
    diffs.push(`状态 ${previous.status_label} -> ${current.status_label}`);
    changeType = 'status_changed';
    severity = maxSeverity(severity, current.severity);
  }
  if (previous.card_count !== current.card_count) {
    diffs.push(`卡片 ${previous.card_count} -> ${current.card_count}`);
    if (changeType === 'unchanged') changeType = previous.card_count === 0 && current.card_count > 0 ? 'first_seen_cards' : 'card_count_changed';
  }
  if (previous.on_count !== current.on_count) {
    diffs.push(`开机 ${previous.on_count} -> ${current.on_count}`);
    changeType = current.on_count > previous.on_count ? 'on_increased' : 'on_decreased';
    if (current.on_count > previous.on_count) severity = 'P1';
  }
  if (previous.offline_count !== current.offline_count) {
    diffs.push(`离线 ${previous.offline_count} -> ${current.offline_count}`);
    if (changeType === 'unchanged') changeType = 'offline_changed';
  }

  const becameVisible = previous.status_code === 'not_in_system' && current.sub_area_count > 0;
  if (becameVisible && !diffs.some(d => d.startsWith('状态'))) {
    diffs.push(`楼层从未出现在系统变为可见`);
    changeType = 'became_visible';
  }

  if (diffs.length === 0) return null;

  return {
    monitor_id: current.monitor_id,
    building: current.building,
    floor_label: current.floor_label,
    sub_area_text: current.sub_area_text,
    severity,
    change_type: changeType,
    message: `${current.building} ${current.floor_label}${current.sub_area_text ? ' ' + current.sub_area_text : ''}: ${diffs.join('，')}`,
    current,
    previous,
  };
}

function maxSeverity(a, b) {
  const rank = { P1: 4, P2: 3, P3: 2, INFO: 1, OK: 0 };
  return (rank[b] || 0) > (rank[a] || 0) ? b : a;
}

function refreshMonitorSnapshots(db, options = {}) {
  ensureMonitorSchema(db);
  const monitors = loadMonitors(db, { includeDisabled: false });
  const insertMany = db.transaction(() => monitors.map(m => insertSnapshot(db, computeMonitorStatus(db, m, options))));
  return insertMany();
}

function loadMonitorEvents(db, limit = 100, options = {}) {
  ensureMonitorSchema(db);
  const runId = resolveRunId(db, options.runId || options.run_id);
  const where = runId ? 'WHERE e.run_id = ?' : '';
  const params = runId ? [runId] : [];
  params.push(Math.max(1, Math.min(Number(limit) || 100, 500)));
  return db.prepare(`
    SELECT e.id, e.monitored_floor_id, m.building, m.floor_label, m.sub_area_text,
           e.event_at, e.event_type, e.severity, e.previous_status, e.current_status,
           e.message, e.acknowledged, e.run_id
    FROM floor_monitor_events e
    JOIN monitored_floors m ON m.id = e.monitored_floor_id
    ${where}
    ORDER BY e.id DESC
    LIMIT ?
  `).all(...params);
}

function loadAvailableFloors(db, building, options = {}) {
  const runId = resolveRunId(db, options.runId || options.run_id);
  const source = sourceForRun(runId);
  const params = [];
  const clauses = [];
  if (source.runWhere) {
    clauses.push(source.runWhere);
    params.push(...source.runParams);
  }
  if (building) {
    clauses.push('building = ?');
    params.push(building);
  }
  const where = clauses.length ? 'WHERE ' + clauses.join(' AND ') : '';
  const rows = db.prepare(`
    SELECT building, floor, text AS sub_area_text, COUNT(*) AS sub_area_count
    FROM ${source.subAreas} sa
    ${where}
    GROUP BY building, floor, text
    ORDER BY building, floor, text
  `).all(...params);
  return rows.map(r => ({
    building: r.building,
    floor_value: r.floor,
    floor_label: floorLabelFromValue(r.floor),
    sub_area_text: r.sub_area_text,
    sub_area_count: r.sub_area_count,
  }));
}

module.exports = {
  DB_PATH,
  STATUS_META,
  openDb,
  ensureMonitorSchema,
  parseFloorValue,
  normalizeFloorLabel,
  floorLabelFromValue,
  loadMonitors,
  loadMonitorGroups,
  saveMonitorGroup,
  deleteMonitorGroup,
  loadMonitorGroupItems,
  saveMonitorGroupItem,
  deleteMonitorGroupItem,
  groupFilterClause,
  computeMonitorGroupStats,
  getMonitor,
  saveMonitor,
  deleteMonitor,
  computeMonitorStatus,
  computeMonitorStatuses,
  compareMonitorStatuses,
  refreshMonitorSnapshots,
  loadMonitorEvents,
  loadAvailableFloors,
};
