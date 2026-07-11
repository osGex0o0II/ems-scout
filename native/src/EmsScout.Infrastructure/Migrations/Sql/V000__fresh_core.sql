-- Core capture schema used only when creating a brand-new EMS database.
-- Existing databases remain subject to the additive migration eligibility checks.

CREATE TABLE buildings (
    building TEXT PRIMARY KEY,
    sub_area_count INTEGER,
    menu_clicked TEXT
);

CREATE TABLE sub_areas (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    building TEXT NOT NULL,
    sub_idx INTEGER,
    floor REAL,
    text TEXT,
    x INTEGER,
    y INTEGER,
    FOREIGN KEY(building) REFERENCES buildings(building)
);

CREATE TABLE pages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sub_area_id INTEGER NOT NULL,
    page_name TEXT,
    count INTEGER,
    on_href TEXT,
    off_href TEXT,
    layout TEXT,
    err TEXT,
    FOREIGN KEY(sub_area_id) REFERENCES sub_areas(id)
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
    comm TEXT,
    FOREIGN KEY(page_id) REFERENCES pages(id)
);
