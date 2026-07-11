PRAGMA journal_mode = WAL;
PRAGMA user_version = 7;

CREATE TABLE buildings (
    building TEXT PRIMARY KEY,
    sub_area_count INTEGER
);

CREATE TABLE sub_areas (
    id INTEGER PRIMARY KEY,
    building TEXT NOT NULL,
    floor REAL
);

CREATE TABLE pages (
    id INTEGER PRIMARY KEY,
    sub_area_id INTEGER NOT NULL,
    page_name TEXT
);

CREATE TABLE cards (
    id INTEGER PRIMARY KEY,
    page_id INTEGER NOT NULL,
    name TEXT
);

CREATE INDEX idx_cards_name ON cards(name);
