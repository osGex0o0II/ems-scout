CREATE UNIQUE INDEX IF NOT EXISTS ux_schedule_groups_area_name
    ON schedule_groups(area_group_id, name COLLATE NOCASE);

CREATE UNIQUE INDEX IF NOT EXISTS ux_schedule_members_area_item
    ON schedule_group_members(schedule_group_id, area_group_item_id)
    WHERE area_group_item_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_schedule_members_area_item
    ON schedule_group_members(area_group_item_id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_schedule_intervals_window
    ON schedule_intervals(rule_id, start_time, end_time);
