PRAGMA foreign_keys = ON;

-- Rules v4 intentionally starts a clean community season. Registration-era
-- identities, credentials, matches, and ratings are not carried forward.
DROP TABLE ratings;
DROP TABLE matches;
DROP TABLE players;
DROP TABLE daily_registration_counts;

UPDATE seasons
SET ended_at_utc = COALESCE(ended_at_utc, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
WHERE id = 1;

INSERT INTO seasons (id, name, started_at_utc, ended_at_utc)
VALUES (2, 'Season 2', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), NULL);

CREATE TABLE characters (
    identity_key TEXT PRIMARY KEY
        CHECK (length(identity_key) BETWEEN 5 AND 256),
    character_name TEXT NOT NULL
        CHECK (length(character_name) BETWEEN 3 AND 42),
    world_id INTEGER NOT NULL
        CHECK (world_id BETWEEN 1 AND 65535),
    world_name TEXT NOT NULL
        CHECK (length(world_name) BETWEEN 1 AND 32),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE matches (
    season_id INTEGER NOT NULL REFERENCES seasons(id) ON DELETE RESTRICT,
    identity_key TEXT NOT NULL REFERENCES characters(identity_key) ON DELETE CASCADE,
    match_key TEXT NOT NULL CHECK (
        length(match_key) = 64 AND
        match_key NOT GLOB '*[^0-9a-f]*'
    ),
    payload_hash TEXT NOT NULL CHECK (
        length(payload_hash) = 64 AND
        payload_hash NOT GLOB '*[^0-9a-f]*'
    ),
    completed_at_utc TEXT NOT NULL,
    received_at_utc TEXT NOT NULL,
    job INTEGER NOT NULL CHECK (job BETWEEN 1 AND 21),
    outcome INTEGER NOT NULL CHECK (outcome IN (0, 1)),
    queue INTEGER NOT NULL CHECK (queue IN (1, 2)),
    PRIMARY KEY (season_id, identity_key, match_key)
);

CREATE INDEX idx_matches_character_job
    ON matches (season_id, identity_key, job);

CREATE INDEX idx_matches_daily_limit
    ON matches (identity_key, job, received_at_utc);

CREATE TABLE ratings (
    season_id INTEGER NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    identity_key TEXT NOT NULL REFERENCES characters(identity_key) ON DELETE CASCADE,
    job INTEGER NOT NULL CHECK (job BETWEEN 1 AND 21),
    rating INTEGER NOT NULL CHECK (rating BETWEEN 500 AND 2500),
    matches INTEGER NOT NULL CHECK (matches > 0),
    wins INTEGER NOT NULL CHECK (wins >= 0),
    losses INTEGER NOT NULL CHECK (losses >= 0),
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (season_id, identity_key, job),
    CHECK (wins + losses = matches),
    CHECK (
        rating = 1500 + CAST(round(
            1000.0 * (wins - losses) / (matches + 40.0),
            0
        ) AS INTEGER)
    )
);

CREATE INDEX idx_ratings_leaderboard
    ON ratings (season_id, job, matches, rating DESC);

-- This is the authoritative season boundary. It protects against an upload
-- that passed application validation immediately before an administrator
-- atomically closed or changed the active season.
CREATE TRIGGER matches_current_open_season
BEFORE INSERT ON matches
WHEN NOT EXISTS (
    SELECT 1
    FROM app_settings settings
    JOIN seasons active ON active.id = settings.current_season
    WHERE settings.id = 1
      AND settings.current_season = NEW.season_id
      AND active.ended_at_utc IS NULL
      AND julianday(NEW.completed_at_utc) >= julianday(active.started_at_utc)
)
BEGIN
    SELECT RAISE(ABORT, 'match_outside_current_season');
END;

CREATE TRIGGER matches_daily_character_job_limit
BEFORE INSERT ON matches
WHEN (
    SELECT COUNT(*)
    FROM matches
    WHERE identity_key = NEW.identity_key
      AND job = NEW.job
      AND received_at_utc >= substr(NEW.received_at_utc, 1, 10) || 'T00:00:00.000Z'
      AND received_at_utc < date(substr(NEW.received_at_utc, 1, 10), '+1 day') || 'T00:00:00.000Z'
) >= 100
BEGIN
    SELECT RAISE(ABORT, 'daily_character_job_match_limit');
END;

CREATE TRIGGER matches_season_character_job_limit
BEFORE INSERT ON matches
WHEN (
    SELECT COUNT(*)
    FROM matches
    WHERE season_id = NEW.season_id
      AND identity_key = NEW.identity_key
      AND job = NEW.job
) >= 5000
BEGIN
    SELECT RAISE(ABORT, 'season_character_job_match_limit');
END;

UPDATE app_settings
SET schema_version = 4,
    rating_rules_version = 4,
    current_season = 2
WHERE id = 1;
