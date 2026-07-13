CREATE TABLE IF NOT EXISTS attention_issues (
    issue_id TEXT PRIMARY KEY,
    source_key TEXT NOT NULL,
    issue_type TEXT NOT NULL,
    severity TEXT NOT NULL,
    run_id INTEGER,
    title TEXT NOT NULL,
    detail TEXT NOT NULL,
    scope TEXT NOT NULL,
    issue_count INTEGER NOT NULL DEFAULT 0,
    navigation_json TEXT NOT NULL DEFAULT '{}',
    status TEXT NOT NULL DEFAULT 'unprocessed'
        CHECK (status IN ('unprocessed', 'acknowledged', 'ignored', 'resolved')),
    ignore_reason TEXT NOT NULL DEFAULT '',
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    resolved_at TEXT
);

CREATE TABLE IF NOT EXISTS attention_issue_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    issue_id TEXT NOT NULL,
    changed_at TEXT NOT NULL,
    previous_status TEXT NOT NULL,
    current_status TEXT NOT NULL,
    reason TEXT NOT NULL DEFAULT '',
    FOREIGN KEY (issue_id) REFERENCES attention_issues(issue_id)
);

CREATE INDEX IF NOT EXISTS idx_attention_issues_source
    ON attention_issues(source_key);

CREATE INDEX IF NOT EXISTS idx_attention_issues_status
    ON attention_issues(status, last_seen_at DESC);

CREATE INDEX IF NOT EXISTS idx_attention_history_issue
    ON attention_issue_history(issue_id, changed_at DESC);
