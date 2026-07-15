PRAGMA foreign_keys = ON;

-- Alias-only accounts cannot be assigned a trustworthy home World. Version 3
-- therefore invalidates them and their dependent history instead of publishing
-- a fabricated character identity. Daily registration counters deliberately
-- remain intact so the migration cannot reset the global abuse cap.
DELETE FROM matches;
DELETE FROM ratings;
DELETE FROM players;

CREATE TABLE players_v3 (
    id TEXT PRIMARY KEY CHECK (length(id) = 36),
    -- Kept as internal column names for a minimal migration. They now contain
    -- the canonical character name and normalized name|world-id identity key.
    display_name TEXT NOT NULL
        CHECK (length(display_name) BETWEEN 3 AND 42),
    display_name_key TEXT NOT NULL UNIQUE
        CHECK (length(display_name_key) BETWEEN 5 AND 256),
    world_id INTEGER NOT NULL
        CHECK (world_id BETWEEN 1 AND 65535),
    world_name TEXT NOT NULL
        CHECK (length(world_name) BETWEEN 1 AND 32),
    api_key_hash TEXT NOT NULL UNIQUE
        CHECK (length(api_key_hash) = 64),
    created_at_utc TEXT NOT NULL
);

DROP TABLE players;
ALTER TABLE players_v3 RENAME TO players;

-- DROP TABLE removes triggers attached to the legacy players table.
CREATE TRIGGER players_global_daily_limit
BEFORE INSERT ON players
WHEN COALESCE((
    SELECT registration_count
    FROM daily_registration_counts
    WHERE day_utc = substr(NEW.created_at_utc, 1, 10)
), 0) >= 100
BEGIN
    SELECT RAISE(ABORT, 'global_daily_registration_limit');
END;

CREATE TRIGGER players_count_daily_registration
AFTER INSERT ON players
BEGIN
    INSERT OR REPLACE INTO daily_registration_counts (day_utc, registration_count)
    VALUES (
        substr(NEW.created_at_utc, 1, 10),
        COALESCE((
            SELECT registration_count
            FROM daily_registration_counts
            WHERE day_utc = substr(NEW.created_at_utc, 1, 10)
        ), 0) + 1
    );
END;

UPDATE app_settings
SET schema_version = 3
WHERE id = 1;
