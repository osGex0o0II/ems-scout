-- Schedule groups are audit-only expectations. They never write EMS device state.
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
