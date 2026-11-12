CREATE TABLE IF NOT EXISTS skip_segment (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    episode_path    TEXT NOT NULL,
    region_type     TEXT NOT NULL,
    start_seconds   REAL NOT NULL,
    end_seconds     REAL NOT NULL,
    confidence      REAL,
    computed_at     TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_skip_segment_episode
    ON skip_segment (episode_path, region_type);
