namespace EmsScout.Infrastructure.Migrations;

internal sealed record AdditiveColumn(string Table, string Name, string SqlDefinition);

internal sealed record ExpectedIndex(string Table, bool IsUnique);

internal static class BaselineSchema
{
    public const int V1Version = 1;
    public const int V2Version = 2;
    public const int V3Version = 3;
    public const int V4Version = 4;
    public const int V5Version = 5;
    public const int LatestVersion = V5Version;

    public static readonly HashSet<string> CoreTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "buildings",
        "sub_areas",
        "pages",
        "cards",
    };

    public static readonly Dictionary<string, string[]> ExpectedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["buildings"] = ["building", "sub_area_count", "menu_clicked", "updated_at"],
        ["sub_areas"] = ["id", "building", "sub_idx", "floor", "text", "x", "y"],
        ["pages"] = ["id", "sub_area_id", "page_name", "count", "raw_count", "unique_count", "duplicate_names", "on_href", "off_href", "layout", "quality_reason", "err"],
        ["cards"] = ["id", "page_id", "name", "switch", "mode", "indoor", "set_temp", "fan", "indicator", "comm", "source_key", "device_uid"],
        ["collection_runs"] = ["id", "run_key", "started_at", "completed_at", "imported_at", "status", "scope", "buildings", "json_path", "db_snapshot_path", "card_count", "on_count", "off_count", "offline_count", "unknown_count", "quality_summary", "is_anomaly", "note"],
        ["run_buildings"] = ["id", "run_id", "building", "sub_area_count", "menu_clicked", "updated_at"],
        ["run_sub_areas"] = ["id", "run_id", "source_sub_area_id", "building", "sub_idx", "floor", "floor_label", "text", "x", "y"],
        ["run_pages"] = ["id", "run_id", "run_sub_area_id", "source_page_id", "page_name", "count", "raw_count", "unique_count", "duplicate_names", "on_href", "off_href", "layout", "quality_reason", "err"],
        ["run_cards"] = ["id", "run_id", "run_page_id", "source_card_id", "name", "switch", "mode", "indoor", "set_temp", "fan", "indicator", "comm", "source_key", "device_uid"],
        ["floor_catalog"] = ["id", "building", "floor_label", "floor_value", "source", "enabled", "note", "created_at", "updated_at"],
        ["monitor_groups"] = ["id", "name", "area_label", "description", "priority", "group_kind", "system_key", "locked", "enabled", "created_at", "updated_at"],
        ["monitor_group_items"] = ["id", "group_id", "target_type", "building", "floor_label", "floor_value", "sub_area_text", "card_name", "device_uid", "note", "created_at", "updated_at"],
        ["monitored_floors"] = ["id", "building", "floor_label", "floor_value", "sub_area_text", "expected_status", "priority", "enabled", "note", "created_at", "updated_at"],
        ["floor_monitor_snapshots"] = ["id", "monitored_floor_id", "observed_at", "status_code", "status_label", "severity", "opened", "sub_area_count", "page_count", "card_count", "on_count", "off_count", "offline_count", "unknown_count", "real_temp_count", "run_id", "detail_json"],
        ["floor_monitor_events"] = ["id", "monitored_floor_id", "event_at", "event_type", "severity", "previous_status", "current_status", "message", "snapshot_id", "run_id", "acknowledged", "detail_json"],
        ["device_tags"] = ["id", "card_name", "building", "device_uid", "tag", "created_at"],
        ["device_notes"] = ["id", "card_name", "building", "device_uid", "note", "created_at", "updated_at"],
        ["manual_overrides"] = ["id", "card_name", "building", "device_uid", "field", "value", "reason", "created_at", "updated_at"],
        ["realtime_match_overrides"] = ["id", "building", "dev_id", "floor_label", "sub_area", "page_name", "realtime_name", "action", "target_card_id", "device_uid", "zuo_override", "area_type_override", "note", "created_at", "updated_at"],
        ["device_watch_rules"] = ["id", "group_id", "name", "start_at", "end_at", "enabled", "note", "created_at", "updated_at"],
        ["ems_schema_migrations"] = ["version", "name", "applied_at", "source_shape", "backup_path", "tool_version"],
        ["device_registry"] = ["device_uid", "primary_source_key", "status", "created_at", "updated_at"],
        ["device_source_keys"] = ["source_key", "device_uid", "building", "sub_idx", "page_name", "device_name", "first_seen_run_id", "last_seen_run_id", "is_current", "created_at", "updated_at"],
        ["device_identity_ambiguities"] = ["id", "migration_version", "detected_at", "entity_table", "entity_key", "reason_code", "status", "source_key", "identity_json", "candidate_device_uids", "resolved_device_uid", "resolution_note", "resolved_at"],
        ["schedule_groups"] = ["id", "area_group_id", "name", "description", "enabled", "created_at", "updated_at"],
        ["schedule_rules"] = ["id", "schedule_group_id", "calendar_date", "expected_status", "note", "created_at", "updated_at"],
        ["schedule_intervals"] = ["id", "rule_id", "start_time", "end_time", "created_at"],
        ["schedule_group_members"] = ["id", "schedule_group_id", "area_group_item_id", "target_type", "building", "floor_label", "floor_value", "sub_area_text", "card_name", "device_uid", "expected_status", "note", "created_at", "updated_at"],
        ["attention_issues"] = ["issue_id", "source_key", "issue_type", "severity", "run_id", "title", "detail", "scope", "issue_count", "navigation_json", "status", "ignore_reason", "first_seen_at", "last_seen_at", "resolved_at"],
        ["attention_issue_history"] = ["id", "issue_id", "changed_at", "previous_status", "current_status", "reason"],
    };

    public static readonly AdditiveColumn[] V1AdditiveColumns =
    [
        new("buildings", "updated_at", "TEXT"),
        new("sub_areas", "sub_idx", "INTEGER"),
        new("pages", "raw_count", "INTEGER"),
        new("pages", "unique_count", "INTEGER"),
        new("pages", "duplicate_names", "TEXT"),
        new("pages", "quality_reason", "TEXT"),
        new("cards", "indicator", "TEXT"),
        new("collection_runs", "quality_summary", "TEXT NOT NULL DEFAULT '{}'"),
        new("collection_runs", "is_anomaly", "INTEGER NOT NULL DEFAULT 0"),
        new("run_pages", "quality_reason", "TEXT"),
        new("monitor_groups", "group_kind", "TEXT NOT NULL DEFAULT 'custom'"),
        new("monitor_groups", "system_key", "TEXT"),
        new("monitor_groups", "locked", "INTEGER NOT NULL DEFAULT 0"),
        new("floor_monitor_snapshots", "run_id", "INTEGER"),
        new("floor_monitor_events", "run_id", "INTEGER"),
    ];

    public static readonly AdditiveColumn[] V2AdditiveColumns =
    [
        new("cards", "source_key", "TEXT"),
        new("cards", "device_uid", "TEXT"),
        new("run_cards", "source_key", "TEXT"),
        new("run_cards", "device_uid", "TEXT"),
        new("device_notes", "device_uid", "TEXT"),
        new("device_tags", "device_uid", "TEXT"),
        new("manual_overrides", "device_uid", "TEXT"),
        new("monitor_group_items", "device_uid", "TEXT"),
        new("realtime_match_overrides", "device_uid", "TEXT"),
    ];

    public static readonly AdditiveColumn[] V4AdditiveColumns =
    [
        new("schedule_group_members", "area_group_item_id", "INTEGER"),
    ];

    public static readonly Dictionary<string, ExpectedIndex> ExpectedIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idx_sa_building"] = new("sub_areas", false),
        ["idx_sa_floor"] = new("sub_areas", false),
        ["idx_pg_sa"] = new("pages", false),
        ["idx_cd_pg"] = new("cards", false),
        ["idx_cd_sw"] = new("cards", false),
        ["idx_cd_name"] = new("cards", false),
        ["idx_collection_runs_completed"] = new("collection_runs", false),
        ["idx_run_buildings_run"] = new("run_buildings", false),
        ["idx_run_sa_run_building"] = new("run_sub_areas", false),
        ["idx_run_sa_run_floor"] = new("run_sub_areas", false),
        ["idx_run_pages_sa"] = new("run_pages", false),
        ["idx_run_cards_run"] = new("run_cards", false),
        ["idx_run_cards_page"] = new("run_cards", false),
        ["idx_run_cards_name"] = new("run_cards", false),
        ["idx_run_cards_switch"] = new("run_cards", false),
        ["idx_floor_catalog_key"] = new("floor_catalog", true),
        ["idx_monitor_group_items_group"] = new("monitor_group_items", false),
        ["idx_monitor_group_items_target"] = new("monitor_group_items", false),
        ["idx_monitored_floors_key"] = new("monitored_floors", true),
        ["idx_floor_monitor_snapshots_target"] = new("floor_monitor_snapshots", false),
        ["idx_floor_monitor_snapshots_run"] = new("floor_monitor_snapshots", false),
        ["idx_floor_monitor_events_target"] = new("floor_monitor_events", false),
        ["idx_floor_monitor_events_run"] = new("floor_monitor_events", false),
        ["idx_realtime_match_overrides_dev"] = new("realtime_match_overrides", false),
        ["idx_realtime_match_overrides_identity"] = new("realtime_match_overrides", false),
        ["ux_realtime_match_overrides_dev"] = new("realtime_match_overrides", true),
        ["ux_realtime_match_overrides_identity"] = new("realtime_match_overrides", true),
        ["idx_device_watch_rules_enabled"] = new("device_watch_rules", false),
        ["idx_cards_source_key"] = new("cards", false),
        ["idx_cards_device_uid"] = new("cards", false),
        ["idx_run_cards_source_key"] = new("run_cards", false),
        ["idx_run_cards_device_uid"] = new("run_cards", false),
        ["idx_device_notes_uid"] = new("device_notes", false),
        ["idx_device_tags_uid"] = new("device_tags", false),
        ["idx_manual_overrides_uid"] = new("manual_overrides", false),
        ["idx_monitor_group_items_uid"] = new("monitor_group_items", false),
        ["idx_realtime_match_overrides_uid"] = new("realtime_match_overrides", false),
        ["ux_device_registry_primary_source"] = new("device_registry", true),
        ["idx_device_source_keys_uid"] = new("device_source_keys", false),
        ["idx_device_source_keys_current"] = new("device_source_keys", false),
        ["idx_device_identity_ambiguities_status"] = new("device_identity_ambiguities", false),
        ["idx_device_identity_ambiguities_entity"] = new("device_identity_ambiguities", false),
        ["idx_schedule_groups_area"] = new("schedule_groups", false),
        ["idx_schedule_rules_group_date"] = new("schedule_rules", false),
        ["idx_schedule_intervals_rule"] = new("schedule_intervals", false),
        ["idx_schedule_members_group"] = new("schedule_group_members", false),
        ["idx_schedule_members_target"] = new("schedule_group_members", false),
        ["ux_schedule_groups_area_name"] = new("schedule_groups", true),
        ["ux_schedule_members_area_item"] = new("schedule_group_members", true),
        ["idx_schedule_members_area_item"] = new("schedule_group_members", false),
        ["ux_schedule_intervals_window"] = new("schedule_intervals", true),
        ["idx_attention_issues_source"] = new("attention_issues", false),
        ["idx_attention_issues_status"] = new("attention_issues", false),
        ["idx_attention_history_issue"] = new("attention_issue_history", false),
    };

    public static bool TryGetAdditiveColumn(string table, string column, out AdditiveColumn addition)
    {
        addition = V1AdditiveColumns.Concat(V2AdditiveColumns).Concat(V4AdditiveColumns).FirstOrDefault(candidate =>
            candidate.Table.Equals(table, StringComparison.OrdinalIgnoreCase) &&
            candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase))!;
        return addition is not null;
    }
}
