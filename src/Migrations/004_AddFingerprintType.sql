-- Add fingerprint_type column to support both INTRO and CREDITS fingerprints.
-- Changes PK from (relative_path) to (relative_path, fingerprint_type).

CREATE TABLE fingerprint_cache_new (
    relative_path     TEXT    NOT NULL,
    fingerprint_type  TEXT    NOT NULL DEFAULT 'INTRO',
    file_size         INTEGER NOT NULL,
    last_modified     TEXT    NOT NULL,
    fingerprint       BLOB    NOT NULL,
    duration_seconds  REAL    NOT NULL,
    fpcalc_version    TEXT,
    created_at        TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (relative_path, fingerprint_type)
);

INSERT INTO fingerprint_cache_new
    (relative_path, fingerprint_type, file_size, last_modified, fingerprint, duration_seconds, fpcalc_version, created_at)
SELECT relative_path, 'INTRO', file_size, last_modified, fingerprint, duration_seconds, fpcalc_version, created_at
FROM fingerprint_cache;

DROP TABLE fingerprint_cache;
ALTER TABLE fingerprint_cache_new RENAME TO fingerprint_cache;
