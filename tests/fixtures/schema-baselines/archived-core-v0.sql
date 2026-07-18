PRAGMA journal_mode = WAL;
PRAGMA user_version = 0;

CREATE TABLE buildings (
    building TEXT PRIMARY KEY,
    sub_area_count INTEGER,
    menu_clicked TEXT
);

CREATE TABLE sub_areas (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    building TEXT NOT NULL,
    sub_idx INTEGER,
    floor INTEGER,
    text TEXT,
    x INTEGER,
    y INTEGER
);

CREATE TABLE pages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sub_area_id INTEGER NOT NULL,
    page_name TEXT,
    count INTEGER,
    on_href TEXT,
    off_href TEXT,
    layout TEXT,
    err TEXT
);

CREATE TABLE cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    page_id INTEGER NOT NULL,
    name TEXT,
    switch TEXT,
    mode TEXT,
    indoor TEXT,
    set_temp TEXT,
    fan TEXT,
    comm TEXT
);

INSERT INTO buildings(building, sub_area_count, menu_clicked)
VALUES ('1号', 1, 'true');

INSERT INTO sub_areas(id, building, sub_idx, floor, text, x, y)
VALUES (10, '1号', NULL, 1, '1F', 10, 20);

INSERT INTO pages(id, sub_area_id, page_name, count, layout)
VALUES (20, 10, '一页', 1, 'grid');

INSERT INTO cards(id, page_id, name, switch, mode, indoor, set_temp, fan, comm)
VALUES (30, 20, '1F-101-KT', 'OFF', '制冷', '25', '24', '中', '关机');
