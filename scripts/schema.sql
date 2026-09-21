-- EMS schema.sql — 数据库建表语句
-- AC Energy Management - SQLite schema

-- This file is an additive initializer. Production imports use idempotent migrations;
-- never execute destructive DROP statements against the working database.

CREATE TABLE IF NOT EXISTS buildings (
    building TEXT PRIMARY KEY,
    sub_area_count INT,
    menu_clicked TEXT,
    updated_at TEXT
);

CREATE TABLE IF NOT EXISTS sub_areas (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    building TEXT NOT NULL,
    sub_idx INT,
    floor REAL,
    text TEXT,
    x INT,
    y INT,
    FOREIGN KEY(building) REFERENCES buildings(building)
);
CREATE INDEX IF NOT EXISTS idx_sa_building ON sub_areas(building);
CREATE INDEX IF NOT EXISTS idx_sa_floor ON sub_areas(building, floor);

CREATE TABLE IF NOT EXISTS pages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sub_area_id INT NOT NULL,
    page_name TEXT,
    count INT,
    raw_count INT,
    unique_count INT,
    duplicate_names TEXT,
    on_href TEXT,
    off_href TEXT,
    layout TEXT,
    quality_reason TEXT,
    collected_at TEXT,
    err TEXT,
    FOREIGN KEY(sub_area_id) REFERENCES sub_areas(id)
);
CREATE INDEX IF NOT EXISTS idx_pg_sa ON pages(sub_area_id);

CREATE TABLE IF NOT EXISTS cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    page_id INT NOT NULL,
    name TEXT,
    switch TEXT,
    mode TEXT,
    indoor TEXT,
    set_temp TEXT,
    fan TEXT,
    indicator TEXT,
    comm TEXT,
    FOREIGN KEY(page_id) REFERENCES pages(id)
);
CREATE INDEX IF NOT EXISTS idx_cd_pg ON cards(page_id);
CREATE INDEX IF NOT EXISTS idx_cd_sw ON cards(switch);
CREATE INDEX IF NOT EXISTS idx_cd_name ON cards(name);

CREATE TABLE IF NOT EXISTS run_area_group_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    group_id INTEGER NOT NULL,
    group_key TEXT NOT NULL DEFAULT '',
    group_name TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    rule_order INTEGER NOT NULL,
    building TEXT NOT NULL,
    zuo TEXT NOT NULL DEFAULT '-',
    floor_label TEXT NOT NULL DEFAULT '',
    floor_value REAL,
    match_mode TEXT NOT NULL,
    keywords TEXT NOT NULL DEFAULT '',
    note TEXT NOT NULL DEFAULT '',
    FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_run_area_group_rules_run_group
    ON run_area_group_rules(run_id, group_id, rule_order, id);

-- Native area groups. Membership is defined only by area_group_rules.
CREATE TABLE IF NOT EXISTS monitor_groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    area_label TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    priority TEXT NOT NULL DEFAULT '重点',
    enabled INTEGER NOT NULL DEFAULT 1,
    group_key TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60))
);

CREATE TABLE IF NOT EXISTS area_group_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    rule_order INTEGER NOT NULL DEFAULT 0,
    building TEXT NOT NULL,
    zuo TEXT NOT NULL DEFAULT '-',
    floor_label TEXT NOT NULL DEFAULT '',
    floor_value REAL,
    match_mode TEXT NOT NULL,
    keywords TEXT NOT NULL DEFAULT '',
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(group_id) REFERENCES monitor_groups(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_area_group_rules_group_order
    ON area_group_rules(group_id, rule_order, id);

CREATE TABLE IF NOT EXISTS collection_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_no INTEGER,
    run_key TEXT UNIQUE,
    started_at TEXT,
    completed_at TEXT NOT NULL,
    imported_at TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'completed',
    scope TEXT NOT NULL DEFAULT 'full',
    buildings TEXT NOT NULL DEFAULT '[]',
    json_path TEXT,
    db_snapshot_path TEXT,
    card_count INTEGER NOT NULL DEFAULT 0,
    on_count INTEGER NOT NULL DEFAULT 0,
    off_count INTEGER NOT NULL DEFAULT 0,
    offline_count INTEGER NOT NULL DEFAULT 0,
    unknown_count INTEGER NOT NULL DEFAULT 0,
    quality_summary TEXT NOT NULL DEFAULT '{}',
    is_anomaly INTEGER NOT NULL DEFAULT 0,
    note TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT '采集导入',
    data_version TEXT NOT NULL DEFAULT 'v1.0.0',
    operator_name TEXT NOT NULL DEFAULT '本机',
    restored_from_run_id INTEGER,
    batch_uid TEXT NOT NULL DEFAULT '',
    lifecycle_state TEXT NOT NULL DEFAULT 'completed',
    current_revision_uid TEXT,
    restored_from_batch_uid TEXT
);
CREATE INDEX IF NOT EXISTS idx_collection_runs_completed
    ON collection_runs(completed_at DESC);

CREATE TABLE IF NOT EXISTS run_id_registry (
    technical_id INTEGER PRIMARY KEY,
    allocated_at TEXT NOT NULL,
    allocation_kind TEXT NOT NULL DEFAULT 'collection_run'
);
CREATE INDEX IF NOT EXISTS idx_run_id_registry_allocated
    ON run_id_registry(allocated_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_run_no
    ON collection_runs(run_no) WHERE run_no IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_batch_uid
    ON collection_runs(batch_uid) WHERE batch_uid <> '';

CREATE TABLE IF NOT EXISTS run_key_registry (
    run_key TEXT PRIMARY KEY,
    batch_uid TEXT NOT NULL DEFAULT '',
    first_seen_at TEXT NOT NULL,
    deleted_at TEXT,
    last_run_id INTEGER
);
CREATE INDEX IF NOT EXISTS idx_run_key_registry_deleted ON run_key_registry(deleted_at);

CREATE TABLE IF NOT EXISTS run_operations (
    operation_id TEXT PRIMARY KEY,
    operation_type TEXT NOT NULL,
    run_id INTEGER,
    batch_uid TEXT,
    run_key TEXT,
    occurred_at TEXT NOT NULL,
    result TEXT NOT NULL,
    summary TEXT NOT NULL DEFAULT '',
    deleted_cards INTEGER NOT NULL DEFAULT 0,
    deleted_pages INTEGER NOT NULL DEFAULT 0,
    deleted_sub_areas INTEGER NOT NULL DEFAULT 0,
    deleted_buildings INTEGER NOT NULL DEFAULT 0,
    pending_artifacts INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS current_data_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    revision_uid TEXT NOT NULL UNIQUE,
    updated_at TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT '本机 SQLite'
);

CREATE TABLE IF NOT EXISTS current_data_sources (
    building TEXT PRIMARY KEY,
    revision_uid TEXT NOT NULL,
    run_id INTEGER,
    batch_uid TEXT,
    source_updated_at TEXT,
    card_count INTEGER NOT NULL DEFAULT 0,
    state TEXT NOT NULL DEFAULT 'bound',
    reason TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS run_buildings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    building TEXT NOT NULL,
    sub_area_count INTEGER,
    menu_clicked TEXT,
    updated_at TEXT,
    FOREIGN KEY(run_id) REFERENCES collection_runs(id)
);
CREATE INDEX IF NOT EXISTS idx_run_buildings_run ON run_buildings(run_id, building);

CREATE TABLE IF NOT EXISTS run_sub_areas (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    source_sub_area_id INTEGER,
    building TEXT NOT NULL,
    sub_idx INTEGER,
    floor REAL,
    floor_label TEXT,
    text TEXT,
    x INTEGER,
    y INTEGER,
    FOREIGN KEY(run_id) REFERENCES collection_runs(id)
);
CREATE INDEX IF NOT EXISTS idx_run_sa_run_building ON run_sub_areas(run_id, building);
CREATE INDEX IF NOT EXISTS idx_run_sa_run_floor ON run_sub_areas(run_id, building, floor);

CREATE TABLE IF NOT EXISTS run_pages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    run_sub_area_id INTEGER NOT NULL,
    source_page_id INTEGER,
    page_name TEXT,
    count INTEGER,
    raw_count INTEGER,
    unique_count INTEGER,
    duplicate_names TEXT,
    on_href TEXT,
    off_href TEXT,
    layout TEXT,
    quality_reason TEXT,
    collected_at TEXT,
    err TEXT,
    FOREIGN KEY(run_id) REFERENCES collection_runs(id),
    FOREIGN KEY(run_sub_area_id) REFERENCES run_sub_areas(id)
);
CREATE INDEX IF NOT EXISTS idx_run_pages_sa ON run_pages(run_sub_area_id);

CREATE TABLE IF NOT EXISTS run_cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    run_page_id INTEGER NOT NULL,
    source_card_id INTEGER,
    name TEXT,
    switch TEXT,
    mode TEXT,
    indoor TEXT,
    set_temp TEXT,
    fan TEXT,
    indicator TEXT,
    comm TEXT,
    FOREIGN KEY(run_id) REFERENCES collection_runs(id),
    FOREIGN KEY(run_page_id) REFERENCES run_pages(id)
);
CREATE INDEX IF NOT EXISTS idx_run_cards_run ON run_cards(run_id);
CREATE INDEX IF NOT EXISTS idx_run_cards_page ON run_cards(run_page_id);
CREATE INDEX IF NOT EXISTS idx_run_cards_name ON run_cards(name);
CREATE INDEX IF NOT EXISTS idx_run_cards_switch ON run_cards(switch);

CREATE TABLE IF NOT EXISTS run_realtime_details (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    batch_uid TEXT,
    source_row_id TEXT NOT NULL,
    building TEXT NOT NULL,
    floor REAL,
    sub_area TEXT,
    page_name TEXT,
    name TEXT,
    source_file TEXT,
    source_updated_at TEXT,
    payload_json TEXT NOT NULL,
    FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_run_realtime_details_row
    ON run_realtime_details(run_id, source_row_id);
CREATE INDEX IF NOT EXISTS idx_run_realtime_details_run_building
    ON run_realtime_details(run_id, building);

CREATE TABLE IF NOT EXISTS floor_catalog (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    building TEXT NOT NULL,
    floor_label TEXT NOT NULL,
    floor_value REAL,
    source TEXT NOT NULL DEFAULT 'manual',
    enabled INTEGER NOT NULL DEFAULT 1,
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_floor_catalog_key
    ON floor_catalog(building, floor_label);

CREATE TABLE IF NOT EXISTS device_tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    card_name TEXT NOT NULL,
    building TEXT,
    tag TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
    UNIQUE(card_name, building, tag)
);

CREATE TABLE IF NOT EXISTS device_notes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    card_name TEXT NOT NULL,
    building TEXT,
    note TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
    UNIQUE(card_name, building)
);

CREATE TABLE IF NOT EXISTS manual_overrides (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  card_name TEXT NOT NULL,
  building TEXT,
  field TEXT NOT NULL,
    value TEXT NOT NULL,
    reason TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
  updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
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
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
  updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60))
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
