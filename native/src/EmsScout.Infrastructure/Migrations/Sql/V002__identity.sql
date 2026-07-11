-- v2-identity is additive. Legacy identity fields remain in place for dual-write.

CREATE TABLE IF NOT EXISTS device_registry (
    device_uid TEXT PRIMARY KEY,
    primary_source_key TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'active',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_source_keys (
    source_key TEXT PRIMARY KEY,
    device_uid TEXT NOT NULL,
    building TEXT NOT NULL,
    sub_idx INTEGER NOT NULL,
    page_name TEXT NOT NULL,
    device_name TEXT NOT NULL,
    first_seen_run_id INTEGER,
    last_seen_run_id INTEGER,
    is_current INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(device_uid) REFERENCES device_registry(device_uid)
);

CREATE TABLE IF NOT EXISTS device_identity_ambiguities (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    migration_version INTEGER NOT NULL DEFAULT 2,
    detected_at TEXT NOT NULL,
    entity_table TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'unresolved',
    source_key TEXT,
    identity_json TEXT NOT NULL DEFAULT '{}',
    candidate_device_uids TEXT NOT NULL DEFAULT '[]',
    resolved_device_uid TEXT,
    resolution_note TEXT NOT NULL DEFAULT '',
    resolved_at TEXT,
    UNIQUE(entity_table, entity_key, reason_code),
    FOREIGN KEY(resolved_device_uid) REFERENCES device_registry(device_uid)
);

CREATE INDEX IF NOT EXISTS idx_cards_source_key ON cards(source_key);
CREATE INDEX IF NOT EXISTS idx_cards_device_uid ON cards(device_uid);
CREATE INDEX IF NOT EXISTS idx_run_cards_source_key ON run_cards(source_key);
CREATE INDEX IF NOT EXISTS idx_run_cards_device_uid ON run_cards(device_uid);
CREATE INDEX IF NOT EXISTS idx_device_notes_uid ON device_notes(device_uid);
CREATE INDEX IF NOT EXISTS idx_device_tags_uid ON device_tags(device_uid);
CREATE INDEX IF NOT EXISTS idx_manual_overrides_uid ON manual_overrides(device_uid);
CREATE INDEX IF NOT EXISTS idx_monitor_group_items_uid ON monitor_group_items(device_uid);
CREATE INDEX IF NOT EXISTS idx_realtime_match_overrides_uid ON realtime_match_overrides(device_uid);
CREATE UNIQUE INDEX IF NOT EXISTS ux_device_registry_primary_source ON device_registry(primary_source_key);
CREATE INDEX IF NOT EXISTS idx_device_source_keys_uid ON device_source_keys(device_uid);
CREATE INDEX IF NOT EXISTS idx_device_source_keys_current ON device_source_keys(is_current, device_uid);
CREATE INDEX IF NOT EXISTS idx_device_identity_ambiguities_status ON device_identity_ambiguities(status, id);
CREATE INDEX IF NOT EXISTS idx_device_identity_ambiguities_entity ON device_identity_ambiguities(entity_table, entity_key);
