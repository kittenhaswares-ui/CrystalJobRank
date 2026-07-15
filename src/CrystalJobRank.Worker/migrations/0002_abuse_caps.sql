CREATE TABLE daily_registration_counts (
    day_utc TEXT PRIMARY KEY CHECK (length(day_utc) = 10),
    registration_count INTEGER NOT NULL CHECK (registration_count >= 0)
);

INSERT INTO daily_registration_counts (day_utc, registration_count)
SELECT substr(created_at_utc, 1, 10), COUNT(*)
FROM players
GROUP BY substr(created_at_utc, 1, 10);

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

CREATE TRIGGER matches_late_replay_limit
BEFORE INSERT ON matches
WHEN (
    SELECT COUNT(*)
    FROM (
        SELECT 1
        FROM matches history
        WHERE history.season_id = NEW.season_id
          AND history.player_id = NEW.player_id
          AND history.job = NEW.job
        LIMIT 128
    )
) >= 128
AND EXISTS (
    SELECT 1
    FROM matches later
    WHERE later.season_id = NEW.season_id
      AND later.player_id = NEW.player_id
      AND later.job = NEW.job
      AND (
          later.completed_at_utc > NEW.completed_at_utc COLLATE BINARY OR
          (later.completed_at_utc = NEW.completed_at_utc COLLATE BINARY AND
           later.fingerprint > NEW.fingerprint COLLATE BINARY)
      )
    LIMIT 1
)
BEGIN
    SELECT RAISE(ABORT, 'late_match_replay_limit');
END;

UPDATE app_settings
SET schema_version = 2
WHERE id = 1;
