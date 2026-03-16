-- Metadata table for storing app-level key-value pairs.
-- 'invalidate_skip_before' forces reprocessing of all .skip.json files
-- written before this timestamp (e.g., when a new analysis type is added).

CREATE TABLE IF NOT EXISTS metadata (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT INTO metadata (key, value)
VALUES ('invalidate_skip_before', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
