using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

internal static class TestScheduleSchema
{
    public static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS collection_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                status TEXT NOT NULL DEFAULT 'completed',
                completed_at TEXT
            );
            CREATE TABLE schedule_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                area_group_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE schedule_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                schedule_group_id INTEGER NOT NULL,
                calendar_date TEXT NOT NULL,
                expected_status TEXT NOT NULL DEFAULT 'enabled',
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(schedule_group_id, calendar_date)
            );
            CREATE TABLE schedule_intervals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rule_id INTEGER NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE schedule_group_members (
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
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX idx_schedule_groups_area ON schedule_groups(area_group_id);
            CREATE INDEX idx_schedule_rules_group_date ON schedule_rules(schedule_group_id, calendar_date);
            CREATE INDEX idx_schedule_intervals_rule ON schedule_intervals(rule_id);
            CREATE INDEX idx_schedule_members_group ON schedule_group_members(schedule_group_id);
            CREATE INDEX idx_schedule_members_target ON schedule_group_members(building, floor_value, sub_area_text, card_name);
            CREATE UNIQUE INDEX ux_schedule_groups_area_name ON schedule_groups(area_group_id, name COLLATE NOCASE);
            CREATE UNIQUE INDEX ux_schedule_members_area_item ON schedule_group_members(schedule_group_id, area_group_item_id) WHERE area_group_item_id IS NOT NULL;
            CREATE INDEX idx_schedule_members_area_item ON schedule_group_members(area_group_item_id);
            CREATE UNIQUE INDEX ux_schedule_intervals_window ON schedule_intervals(rule_id, start_time, end_time);
            PRAGMA user_version = 6;
            """;
        command.ExecuteNonQuery();
    }
}
