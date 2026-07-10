-- EMS views.sql — SQLite 视图
-- Convenient views joining cards with parent context

DROP VIEW IF EXISTS v_cards;
CREATE VIEW v_cards AS
SELECT
  sa.building,
  sa.sub_idx,
  sa.floor,
  sa.x,
  sa.y,
  p.page_name,
  p.layout AS recorded_layout,
  CASE
    WHEN c.name GLOB '[0-9]*F*' THEN 'grid-inferred'
    WHEN c.name GLOB '[0-9]-*' THEN 'group-inferred'
    ELSE 'unknown-inferred'
  END AS inferred_layout,
  p.id AS page_id,
  c.id AS card_id,
  c.name,
  c.switch,
  c.mode,
  c.indoor,
  c.set_temp,
  c.fan
FROM sub_areas sa
JOIN pages p ON p.sub_area_id = sa.id
JOIN cards c ON c.page_id = p.id;

DROP VIEW IF EXISTS v_layout_summary;
CREATE VIEW v_layout_summary AS
SELECT
  CASE
    WHEN c.name GLOB '[0-9]*F*' THEN 'grid-inferred'
    WHEN c.name GLOB '[0-9]-*' THEN 'group-inferred'
    ELSE 'unknown-inferred'
  END AS inferred_layout,
  COUNT(*) cards,
  SUM(c.switch='ON') on_cnt,
  SUM(c.switch='OFF') off_cnt,
  SUM(c.switch NOT IN ('ON','OFF') OR c.switch='' OR c.switch IS NULL) unk
FROM cards c
GROUP BY inferred_layout;
