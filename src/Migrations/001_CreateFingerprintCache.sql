CREATE TABLE IF NOT EXISTS fingerprint_cache (
    relative_path     TEXT    NOT NULL PRIMARY KEY,
    file_size         INTEGER NOT NULL,
    last_modified     TEXT    NOT NULL,
    fingerprint       BLOB    NOT NULL,
    duration_seconds  REAL    NOT NULL,
    fpcalc_version    TEXT,
    created_at        TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
