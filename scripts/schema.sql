-- EMS schema.sql — 数据库建表语句
-- AC Energy Management - SQLite schema

DROP TABLE IF EXISTS cards;
DROP TABLE IF EXISTS pages;
DROP TABLE IF EXISTS sub_areas;
DROP TABLE IF EXISTS buildings;

CREATE TABLE buildings (
    building TEXT PRIMARY KEY,
    sub_area_count INT,
    menu_clicked TEXT
);

CREATE TABLE sub_areas (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    building TEXT NOT NULL,
    sub_idx INT,
    floor REAL,
    text TEXT,
    x INT,
    y INT,
    FOREIGN KEY(building) REFERENCES buildings(building)
);
CREATE INDEX idx_sa_building ON sub_areas(building);
CREATE INDEX idx_sa_floor ON sub_areas(building, floor);

CREATE TABLE pages (
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
    err TEXT,
    FOREIGN KEY(sub_area_id) REFERENCES sub_areas(id)
);
CREATE INDEX idx_pg_sa ON pages(sub_area_id);

CREATE TABLE cards (
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
CREATE INDEX idx_cd_pg ON cards(page_id);
CREATE INDEX idx_cd_sw ON cards(switch);
CREATE INDEX idx_cd_name ON cards(name);

-- Panel extension tables. These are intentionally not dropped above because
-- they store manual monitoring/classification choices across imports.
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

CREATE TABLE IF NOT EXISTS schedule_groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    area_group_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(area_group_id) REFERENCES monitor_groups(id)
);
CREATE TABLE IF NOT EXISTS schedule_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    schedule_group_id INTEGER NOT NULL,
    calendar_date TEXT NOT NULL,
    expected_status TEXT NOT NULL DEFAULT 'enabled',
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(schedule_group_id, calendar_date),
    FOREIGN KEY(schedule_group_id) REFERENCES schedule_groups(id)
);
CREATE TABLE IF NOT EXISTS schedule_intervals (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id INTEGER NOT NULL,
    start_time TEXT NOT NULL,
    end_time TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(rule_id) REFERENCES schedule_rules(id)
);
CREATE TABLE IF NOT EXISTS schedule_group_members (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    schedule_group_id INTEGER NOT NULL,
    area_group_item_id INTEGER,
    target_type TEXT NOT NULL DEFAULT 'floor',
    building TEXT NOT NULL,
    floor_label TEXT,
    floor_value REAL,
    sub_area_text TEXT,
    card_name TEXT,
    device_uid TEXT,
    expected_status TEXT NOT NULL DEFAULT 'normal',
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(schedule_group_id) REFERENCES schedule_groups(id)
);
CREATE INDEX IF NOT EXISTS idx_schedule_groups_area ON schedule_groups(area_group_id);
CREATE INDEX IF NOT EXISTS idx_schedule_rules_group_date ON schedule_rules(schedule_group_id, calendar_date);
CREATE INDEX IF NOT EXISTS idx_schedule_intervals_rule ON schedule_intervals(rule_id);
CREATE INDEX IF NOT EXISTS idx_schedule_members_group ON schedule_group_members(schedule_group_id);
CREATE INDEX IF NOT EXISTS idx_schedule_members_target ON schedule_group_members(building, floor_value, sub_area_text, card_name);
CREATE UNIQUE INDEX IF NOT EXISTS ux_schedule_groups_area_name ON schedule_groups(area_group_id, name COLLATE NOCASE);
CREATE UNIQUE INDEX IF NOT EXISTS ux_schedule_members_area_item ON schedule_group_members(schedule_group_id, area_group_item_id) WHERE area_group_item_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_schedule_members_area_item ON schedule_group_members(area_group_item_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_schedule_intervals_window ON schedule_intervals(rule_id, start_time, end_time);

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

CREATE TABLE IF NOT EXISTS collection_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
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
    note TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_collection_runs_completed
    ON collection_runs(completed_at DESC);

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

CREATE TABLE IF NOT EXISTS floor_catalog (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    building TEXT NOT NULL,
    floor_label TEXT NOT NULL,
    floor_value REAL,
    source TEXT NOT NULL DEFAULT 'manual',
    enabled INTEGER NOT NULL DEFAULT 1,
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_floor_catalog_key
    ON floor_catalog(building, floor_label);

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
