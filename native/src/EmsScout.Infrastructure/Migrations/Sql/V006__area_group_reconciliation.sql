CREATE TABLE IF NOT EXISTS area_group_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    rule_type TEXT NOT NULL CHECK (rule_type IN ('area_public', 'area_non_public', 'floor', 'name_exact', 'name_keyword', 'legacy_sub_area')),
    building TEXT NOT NULL DEFAULT '',
    floor_label TEXT NOT NULL DEFAULT '',
    floor_value REAL,
    match_value TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (group_id) REFERENCES monitor_groups(id)
);

CREATE TABLE IF NOT EXISTS area_group_members (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    rule_id INTEGER,
    member_origin TEXT NOT NULL CHECK (member_origin IN ('rule', 'manual', 'legacy')),
    identity_key TEXT NOT NULL,
    device_uid TEXT NOT NULL DEFAULT '',
    building TEXT NOT NULL,
    floor_label TEXT NOT NULL DEFAULT '',
    floor_value REAL,
    sub_area_text TEXT NOT NULL DEFAULT '',
    page_name TEXT NOT NULL DEFAULT '',
    card_name TEXT NOT NULL,
    source_key TEXT NOT NULL DEFAULT '',
    occurrence INTEGER NOT NULL DEFAULT 1 CHECK (occurrence >= 1),
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (group_id) REFERENCES monitor_groups(id),
    FOREIGN KEY (rule_id) REFERENCES area_group_rules(id)
);

CREATE TABLE IF NOT EXISTS area_group_exceptions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    exception_type TEXT NOT NULL CHECK (exception_type IN ('blocked', 'retained')),
    identity_key TEXT NOT NULL,
    device_uid TEXT NOT NULL DEFAULT '',
    building TEXT NOT NULL,
    floor_label TEXT NOT NULL DEFAULT '',
    sub_area_text TEXT NOT NULL DEFAULT '',
    page_name TEXT NOT NULL DEFAULT '',
    card_name TEXT NOT NULL,
    source_key TEXT NOT NULL DEFAULT '',
    occurrence INTEGER NOT NULL DEFAULT 1 CHECK (occurrence >= 1),
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (group_id) REFERENCES monitor_groups(id)
);

CREATE TABLE IF NOT EXISTS area_group_change_requests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    rule_id INTEGER,
    run_id INTEGER,
    action TEXT NOT NULL CHECK (action IN ('add', 'remove')),
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'rejected', 'superseded')),
    identity_key TEXT NOT NULL,
    device_uid TEXT NOT NULL DEFAULT '',
    building TEXT NOT NULL,
    floor_label TEXT NOT NULL DEFAULT '',
    sub_area_text TEXT NOT NULL DEFAULT '',
    page_name TEXT NOT NULL DEFAULT '',
    card_name TEXT NOT NULL,
    source_key TEXT NOT NULL DEFAULT '',
    occurrence INTEGER NOT NULL DEFAULT 1 CHECK (occurrence >= 1),
    match_reason TEXT NOT NULL DEFAULT '',
    decision_note TEXT NOT NULL DEFAULT '',
    detected_at TEXT NOT NULL,
    decided_at TEXT,
    FOREIGN KEY (group_id) REFERENCES monitor_groups(id),
    FOREIGN KEY (rule_id) REFERENCES area_group_rules(id),
    FOREIGN KEY (run_id) REFERENCES collection_runs(id)
);

CREATE INDEX IF NOT EXISTS idx_area_group_rules_group ON area_group_rules(group_id, enabled, rule_type);
CREATE UNIQUE INDEX IF NOT EXISTS ux_area_group_rules_identity ON area_group_rules(group_id, rule_type, building, floor_label, match_value);
CREATE INDEX IF NOT EXISTS idx_area_group_members_group ON area_group_members(group_id, member_origin, building, floor_value);
CREATE UNIQUE INDEX IF NOT EXISTS ux_area_group_members_identity ON area_group_members(group_id, identity_key);
CREATE INDEX IF NOT EXISTS idx_area_group_exceptions_group ON area_group_exceptions(group_id, exception_type, building);
CREATE UNIQUE INDEX IF NOT EXISTS ux_area_group_exceptions_identity ON area_group_exceptions(group_id, identity_key);
CREATE INDEX IF NOT EXISTS idx_area_group_changes_status ON area_group_change_requests(status, action, group_id, detected_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_area_group_change_pending ON area_group_change_requests(group_id, identity_key, action) WHERE status = 'pending';

INSERT OR IGNORE INTO area_group_rules
    (group_id, rule_type, building, floor_label, floor_value, match_value, enabled, note, created_at, updated_at)
SELECT group_id,
       CASE target_type WHEN 'floor' THEN 'floor' ELSE 'legacy_sub_area' END,
       building,
       COALESCE(floor_label, ''),
       floor_value,
       CASE target_type WHEN 'sub_area' THEN COALESCE(sub_area_text, '') ELSE '' END,
       1,
       note,
       created_at,
       updated_at
FROM monitor_group_items
WHERE target_type IN ('floor', 'sub_area')
  AND NOT EXISTS (SELECT 1 FROM ems_schema_migrations WHERE version = 6);

INSERT OR IGNORE INTO area_group_members
    (group_id, rule_id, member_origin, identity_key, device_uid, building, floor_label, floor_value,
     sub_area_text, page_name, card_name, source_key, occurrence, note, created_at, updated_at)
SELECT group_id,
       NULL,
       'legacy',
       'uid:' || UPPER(TRIM(device_uid)),
       COALESCE(device_uid, ''), building, COALESCE(floor_label, ''), floor_value,
       COALESCE(sub_area_text, ''), '', COALESCE(card_name, ''), '', 1, note, created_at, updated_at
FROM monitor_group_items
WHERE target_type = 'device'
  AND COALESCE(card_name, '') <> ''
  AND COALESCE(TRIM(device_uid), '') <> ''
  AND NOT EXISTS (SELECT 1 FROM ems_schema_migrations WHERE version = 6);

WITH ranked_legacy_devices AS (
    SELECT mgi.id AS item_id,
           mgi.group_id,
           mgi.building,
           COALESCE(mgi.floor_label, '') AS floor_label,
           mgi.floor_value,
           CASE
               WHEN s.floor IS NULL THEN COALESCE(mgi.floor_label, '')
               WHEN s.floor < 0 THEN 'B' || printf('%g', ABS(s.floor)) || 'F'
               ELSE printf('%g', s.floor) || 'F'
           END AS identity_floor_label,
           COALESCE(mgi.sub_area_text, '') AS sub_area_text,
           COALESCE(p.page_name, '') AS page_name,
           COALESCE(mgi.card_name, '') AS card_name,
           COALESCE(c.source_key, '') AS source_key,
           COALESCE(c.device_uid, '') AS current_device_uid,
           CASE
               WHEN COALESCE(TRIM(c.device_uid), '') <> '' THEN 1
               ELSE ROW_NUMBER() OVER (
                   PARTITION BY mgi.id, s.building, s.floor, COALESCE(s.text, ''), COALESCE(p.page_name, ''),
                                COALESCE(c.name, ''),
                                CASE WHEN COALESCE(TRIM(c.device_uid), '') = '' THEN 0 ELSE 1 END
                   ORDER BY COALESCE(c.source_key, ''), c.id
               )
           END AS occurrence,
           ROW_NUMBER() OVER (
               PARTITION BY mgi.id
               ORDER BY CASE WHEN COALESCE(TRIM(c.device_uid), '') = '' THEN 0 ELSE 1 END,
                        COALESCE(c.source_key, ''), c.id
           ) AS selection_rank,
           mgi.note,
           mgi.created_at,
           mgi.updated_at
    FROM monitor_group_items mgi
    JOIN sub_areas s
      ON s.building = mgi.building
     AND (mgi.floor_value IS NULL OR ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001)
     AND (COALESCE(mgi.sub_area_text, '') = '' OR COALESCE(s.text, '') = COALESCE(mgi.sub_area_text, ''))
    JOIN pages p ON p.sub_area_id = s.id
    JOIN cards c ON c.page_id = p.id AND c.name = mgi.card_name
    WHERE mgi.target_type = 'device'
      AND COALESCE(mgi.card_name, '') <> ''
      AND COALESCE(TRIM(mgi.device_uid), '') = ''
      AND NOT EXISTS (SELECT 1 FROM ems_schema_migrations WHERE version = 6)
)
INSERT OR IGNORE INTO area_group_members
    (group_id, rule_id, member_origin, identity_key, device_uid, building, floor_label, floor_value,
     sub_area_text, page_name, card_name, source_key, occurrence, note, created_at, updated_at)
SELECT group_id,
       NULL,
       'legacy',
       CASE
           WHEN COALESCE(TRIM(current_device_uid), '') <> ''
               THEN 'uid:' || UPPER(TRIM(current_device_uid))
           ELSE 'legacy:' || UPPER(
               TRIM(building) || '|' || TRIM(identity_floor_label) || '|' || TRIM(sub_area_text) || '|' ||
               TRIM(page_name) || '|' || TRIM(card_name) || '|' || TRIM(source_key) || '|' || occurrence)
       END,
       COALESCE(current_device_uid, ''), building, floor_label, floor_value, sub_area_text, page_name, card_name, source_key,
       occurrence, note, created_at, updated_at
FROM ranked_legacy_devices
WHERE selection_rank = 1;

INSERT OR IGNORE INTO area_group_members
    (group_id, rule_id, member_origin, identity_key, device_uid, building, floor_label, floor_value,
     sub_area_text, page_name, card_name, source_key, occurrence, note, created_at, updated_at)
SELECT mgi.group_id,
       NULL,
       'legacy',
       'legacy:' || UPPER(
           TRIM(mgi.building) || '|' || TRIM(CASE
               WHEN mgi.floor_value IS NULL THEN COALESCE(mgi.floor_label, '')
               WHEN mgi.floor_value < 0 THEN 'B' || printf('%g', ABS(mgi.floor_value)) || 'F'
               ELSE printf('%g', mgi.floor_value) || 'F'
           END) || '|' ||
           TRIM(COALESCE(mgi.sub_area_text, '')) || '||' || TRIM(COALESCE(mgi.card_name, '')) || '||1'),
       '', mgi.building, COALESCE(mgi.floor_label, ''), mgi.floor_value,
       COALESCE(mgi.sub_area_text, ''), '', COALESCE(mgi.card_name, ''), '', 1,
       mgi.note, mgi.created_at, mgi.updated_at
FROM monitor_group_items mgi
WHERE mgi.target_type = 'device'
  AND COALESCE(mgi.card_name, '') <> ''
  AND COALESCE(TRIM(mgi.device_uid), '') = ''
  AND NOT EXISTS (SELECT 1 FROM ems_schema_migrations WHERE version = 6)
  AND NOT EXISTS (
      SELECT 1
      FROM sub_areas s
      JOIN pages p ON p.sub_area_id = s.id
      JOIN cards c ON c.page_id = p.id AND c.name = mgi.card_name
      WHERE s.building = mgi.building
        AND (mgi.floor_value IS NULL OR ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001)
        AND (COALESCE(mgi.sub_area_text, '') = '' OR COALESCE(s.text, '') = COALESCE(mgi.sub_area_text, ''))
  );
