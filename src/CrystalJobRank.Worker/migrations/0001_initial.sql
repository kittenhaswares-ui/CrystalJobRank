PRAGMA foreign_keys = ON;

CREATE TABLE app_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    schema_version INTEGER NOT NULL CHECK (schema_version >= 1),
    rating_rules_version INTEGER NOT NULL CHECK (rating_rules_version >= 1),
    current_season INTEGER NOT NULL CHECK (current_season >= 1)
);

INSERT INTO app_settings (id, schema_version, rating_rules_version, current_season)
VALUES (1, 1, 3, 1);

CREATE TABLE seasons (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    started_at_utc TEXT NOT NULL,
    ended_at_utc TEXT
);

INSERT INTO seasons (id, name, started_at_utc)
VALUES (1, 'Season 1', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE players (
    id TEXT PRIMARY KEY CHECK (length(id) = 36),
    display_name TEXT NOT NULL
        CHECK (length(display_name) BETWEEN 2 AND 24),
    display_name_key TEXT NOT NULL UNIQUE
        CHECK (length(display_name_key) BETWEEN 2 AND 64),
    api_key_hash TEXT NOT NULL UNIQUE
        CHECK (length(api_key_hash) = 64),
    created_at_utc TEXT NOT NULL
);

CREATE TABLE matches (
    id TEXT PRIMARY KEY CHECK (length(id) = 36),
    season_id INTEGER NOT NULL REFERENCES seasons(id) ON DELETE RESTRICT,
    player_id TEXT NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    fingerprint TEXT NOT NULL CHECK (
        length(fingerprint) = 32 AND
        fingerprint NOT GLOB '*[^0-9a-f]*'
    ),
    payload_hash TEXT NOT NULL CHECK (length(payload_hash) = 64),
    completed_at_utc TEXT NOT NULL,
    received_at_utc TEXT NOT NULL,
    job INTEGER NOT NULL CHECK (job BETWEEN 1 AND 21),
    outcome INTEGER NOT NULL CHECK (outcome IN (0, 1)),
    queue INTEGER NOT NULL CHECK (queue IN (1, 2)),
    UNIQUE (player_id, fingerprint)
);

CREATE INDEX idx_matches_rating_replay
    ON matches (season_id, player_id, job, completed_at_utc, fingerprint);

CREATE INDEX idx_matches_daily_limit
    ON matches (player_id, job, received_at_utc);

CREATE TRIGGER matches_daily_job_limit
BEFORE INSERT ON matches
WHEN (
    SELECT COUNT(*)
    FROM matches
    WHERE player_id = NEW.player_id
      AND job = NEW.job
      AND received_at_utc >= substr(NEW.received_at_utc, 1, 10) || 'T00:00:00.000Z'
      AND received_at_utc < date(substr(NEW.received_at_utc, 1, 10), '+1 day') || 'T00:00:00.000Z'
) >= 100
BEGIN
    SELECT RAISE(ABORT, 'daily_job_match_limit');
END;

CREATE TABLE ratings (
    season_id INTEGER NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    player_id TEXT NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    job INTEGER NOT NULL CHECK (job BETWEEN 1 AND 21),
    rating INTEGER NOT NULL CHECK (rating BETWEEN 0 AND 3000),
    matches INTEGER NOT NULL CHECK (matches >= 0),
    wins INTEGER NOT NULL CHECK (wins >= 0),
    losses INTEGER NOT NULL CHECK (losses >= 0),
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (season_id, player_id, job),
    CHECK (wins + losses = matches)
);

CREATE INDEX idx_ratings_leaderboard
    ON ratings (season_id, job, rating DESC, matches DESC);

CREATE TRIGGER matches_season_job_limit
BEFORE INSERT ON matches
WHEN COALESCE((
    SELECT matches
    FROM ratings
    WHERE season_id = NEW.season_id
      AND player_id = NEW.player_id
      AND job = NEW.job
), 0) >= 5000
BEGIN
    SELECT RAISE(ABORT, 'season_job_match_limit');
END;
