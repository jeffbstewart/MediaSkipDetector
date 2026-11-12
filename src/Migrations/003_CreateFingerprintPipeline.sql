CREATE TABLE IF NOT EXISTS fingerprint_bundle (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    directory_path  TEXT NOT NULL UNIQUE,
    total_files     INTEGER NOT NULL,
    status          TEXT NOT NULL DEFAULT 'FINGERPRINTING',
    created_at      TEXT NOT NULL,
    completed_at    TEXT
);

CREATE TABLE IF NOT EXISTS fingerprint_work_item (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    bundle_id       INTEGER NOT NULL REFERENCES fingerprint_bundle(id) ON DELETE CASCADE,
    file_name       TEXT NOT NULL,
    relative_path   TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'PENDING',
    attempt_count   INTEGER NOT NULL DEFAULT 0,
    error_message   TEXT,
    created_at      TEXT NOT NULL,
    completed_at    TEXT
);

CREATE INDEX IF NOT EXISTS idx_work_item_bundle
    ON fingerprint_work_item (bundle_id, status);
