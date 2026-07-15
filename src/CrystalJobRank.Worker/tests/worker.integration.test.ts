import { applyD1Migrations, env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { JOBS, ratingFromRecord } from "../src/domain";

const INSTALLATION_A = "a".repeat(43);
const INSTALLATION_B = "b".repeat(43);

describe("CrystalJobRank Worker v4 with D1", () => {
  it("automatically creates characters and counts Casual, Ranked, and group matches", async () => {
    const health = await SELF.fetch("https://worker.test/health");
    expect(health.status).toBe(200);
    await expect(health.json()).resolves.toMatchObject({
      status: "ok",
      schemaVersion: 4,
      ratingRulesVersion: 4,
      season: 2,
    });

    const removedRegistration = await jsonRequest(
      "/v1/players/register",
      { characterName: "Test Dancer", worldId: 21, worldName: "Ravana" },
    );
    expect(removedRegistration.status).toBe(404);

    const noInstallation = await jsonRequest(
      "/v2/matches",
      submission("1".repeat(64), new Date(Date.now() - 120_000), 1, 1),
    );
    expect(noInstallation.status).toBe(401);

    const base = Date.parse(await currentSeasonStart()) + 1_000;
    const casualGroupWin = {
      ...submission("1".repeat(64), new Date(base), 1, 1),
      worldName: "Injected World Label",
      partySize: 2,
    };
    const first = await submit(INSTALLATION_A, casualGroupWin);
    expect(first.status).toBe(200);
    await expect(first.json()).resolves.toMatchObject({
      job: JOBS.DNC,
      rating: 1524,
      matches: 1,
      wins: 1,
      losses: 0,
      isProvisional: true,
    });

    const rankedLoss = submission("2".repeat(64), new Date(base + 60_000), 0, 2);
    const second = await submit(INSTALLATION_B, rankedLoss);
    expect(second.status).toBe(200);
    await expect(second.json()).resolves.toMatchObject({
      rating: 1500,
      matches: 2,
      wins: 1,
      losses: 1,
      isProvisional: true,
    });

    const exactRetry = await submit(INSTALLATION_B, rankedLoss);
    expect(exactRetry.status).toBe(200);
    await expect(exactRetry.json()).resolves.toMatchObject({ rating: 1500, matches: 2 });

    const conflictingRetry = await submit(INSTALLATION_B, { ...rankedLoss, outcome: 1 });
    expect(conflictingRetry.status).toBe(409);
    await expect(conflictingRetry.json()).resolves.toEqual({
      error: "This match key was already submitted with different match data.",
    });

    const counts = await env.DB
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM characters) AS characters,
           (SELECT COUNT(*) FROM matches) AS matches,
           (SELECT COUNT(*) FROM ratings) AS ratings`,
      )
      .first<{ characters: number; matches: number; ratings: number }>();
    expect(counts).toEqual({ characters: 1, matches: 2, ratings: 1 });

    const leaderboard = await SELF.fetch("https://worker.test/v1/leaderboard?job=DNC&limit=50", {
      headers: { "CF-Connecting-IP": "192.0.2.10" },
    });
    expect(leaderboard.status).toBe(200);
    await expect(leaderboard.json()).resolves.toEqual([
      {
        rank: 0,
        characterName: "Test Dancer",
        worldName: "Ravana",
        job: JOBS.DNC,
        rating: 1500,
        matches: 2,
        wins: 1,
        losses: 1,
        winRate: 0.5,
        isProvisional: true,
      },
    ]);

    const custom = await submit(
      INSTALLATION_A,
      { ...submission("3".repeat(64), new Date(base + 90_000), 1, 1), queue: 3 },
    );
    expect(custom.status).toBe(400);

    const resetAttempt = await SELF.fetch("https://worker.test/v2/installations/me", {
      method: "DELETE",
      headers: { "X-Installation-Key": INSTALLATION_A },
    });
    expect(resetAttempt.status).toBe(404);

    await cleanIdentity("test dancer|21");
  });

  it("rejects prior-season offline uploads and atomically blocks writes to a closed season", async () => {
    const seasonStart = Date.parse(await currentSeasonStart());
    const stale = await submit(
      INSTALLATION_A,
      submission("4".repeat(64), new Date(seasonStart - 1), 1, 1),
      "192.0.2.70",
    );
    expect(stale.status).toBe(409);
    await expect(stale.json()).resolves.toEqual({
      error: "This match was completed before the current community season started.",
    });

    const identityKey = "season guard|21";
    const timestamp = new Date(Math.max(Date.now(), seasonStart + 1_000)).toISOString();
    await env.DB
      .prepare(
        `INSERT INTO characters (
           identity_key, character_name, world_id, world_name, created_at_utc, updated_at_utc
         ) VALUES (?1, 'Season Guard', 21, 'Ravana', ?2, ?2)`,
      )
      .bind(identityKey, timestamp)
      .run();

    await expect(
      env.DB
        .prepare(
          `INSERT INTO matches (
             season_id, identity_key, match_key, payload_hash,
             completed_at_utc, received_at_utc, job, outcome, queue
           ) VALUES (1, ?1, ?2, ?3, ?4, ?4, ?5, 1, 1)`,
        )
        .bind(identityKey, "5".repeat(64), "6".repeat(64), timestamp, JOBS.DNC)
        .run(),
    ).rejects.toThrow(/match_outside_current_season/);

    await cleanIdentity(identityKey);
  });

  it("numbers established players first and keeps provisional players visible with rank zero", async () => {
    const timestamp = "2000-01-01T00:00:00.000Z";
    const seededRows = [
      { characterName: "Established Leader", worldId: 31, worldName: "Ravana", matches: 10, wins: 10 },
      { characterName: "Established Lower", worldId: 32, worldName: "Bismarck", matches: 10, wins: 5 },
      { characterName: "Provisional Star", worldId: 33, worldName: "Asura", matches: 9, wins: 9 },
    ];

    const statements = [];
    for (const row of seededRows) {
      const identityKey = `${row.characterName.toLocaleLowerCase("en-US")}|${row.worldId}`;
      const losses = row.matches - row.wins;
      statements.push(
        env.DB
          .prepare(
            `INSERT INTO characters (
               identity_key, character_name, world_id, world_name, created_at_utc, updated_at_utc
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?5)`,
          )
          .bind(identityKey, row.characterName, row.worldId, row.worldName, timestamp),
        env.DB
          .prepare(
            `INSERT INTO ratings (
               season_id, identity_key, job, rating, matches, wins, losses, updated_at_utc
             ) VALUES (2, ?1, ?2, ?3, ?4, ?5, ?6, ?7)`,
          )
          .bind(
            identityKey,
            JOBS.DNC,
            ratingFromRecord(row.wins, losses),
            row.matches,
            row.wins,
            losses,
            timestamp,
          ),
      );
    }
    await env.DB.batch(statements);

    const response = await SELF.fetch("https://ranking.worker.test/v1/leaderboard?job=dnc", {
      headers: { "CF-Connecting-IP": "192.0.2.60" },
    });
    expect(response.status).toBe(200);
    const rows = (await response.json()) as Array<{
      rank: number;
      characterName: string;
      rating: number;
      isProvisional: boolean;
    }>;
    expect(rows.map(({ rank, characterName, rating, isProvisional }) => [rank, characterName, rating, isProvisional]))
      .toEqual([
        [1, "Established Leader", 1700, false],
        [2, "Established Lower", 1500, false],
        [0, "Provisional Star", 1684, true],
      ]);

    for (const row of seededRows) {
      await cleanIdentity(`${row.characterName.toLocaleLowerCase("en-US")}|${row.worldId}`);
    }
  });

  it("enforces the database daily character-job cap", async () => {
    const timestamp = new Date().toISOString();
    const identityKey = "limit dancer|44";
    await env.DB.batch([
      env.DB
        .prepare(
          `INSERT INTO characters (
             identity_key, character_name, world_id, world_name, created_at_utc, updated_at_utc
           ) VALUES (?1, 'Limit Dancer', 44, 'Ravana', ?2, ?2)`,
        )
        .bind(identityKey, timestamp),
    ]);
    await env.DB
      .prepare(
        `WITH RECURSIVE sequence(n) AS (
           SELECT 1
           UNION ALL
           SELECT n + 1 FROM sequence WHERE n < 100
         )
         INSERT INTO matches (
           season_id, identity_key, match_key, payload_hash,
           completed_at_utc, received_at_utc, job, outcome, queue
         )
         SELECT
           2, ?1, printf('%064x', n), printf('%064x', n + 1000),
           ?2, ?2, ?3, n % 2, 1
         FROM sequence`,
      )
      .bind(identityKey, timestamp, JOBS.DNC)
      .run();

    await expect(
      env.DB
        .prepare(
          `INSERT INTO matches (
             season_id, identity_key, match_key, payload_hash,
             completed_at_utc, received_at_utc, job, outcome, queue
           ) VALUES (2, ?1, ?2, ?3, ?4, ?4, ?5, 1, 1)`,
        )
        .bind(identityKey, "e".repeat(64), "d".repeat(64), timestamp, JOBS.DNC)
        .run(),
    ).rejects.toThrow(/daily_character_job_match_limit/);

    await cleanIdentity(identityKey);
  });

  it("migrates v1 through v4 and performs the one-time clean Season 2 reset", async () => {
    expect(env.TEST_MIGRATIONS).toHaveLength(4);
    await applyD1Migrations(env.MIGRATION_DB, env.TEST_MIGRATIONS.slice(0, 3));

    const timestamp = "2000-01-01T00:00:00.000Z";
    const legacyPlayerId = "60000000-0000-0000-0000-000000000001";
    await env.MIGRATION_DB.batch([
      env.MIGRATION_DB
        .prepare(
          `INSERT INTO players (
             id, display_name, display_name_key, world_id, world_name, api_key_hash, created_at_utc
           ) VALUES (?1, 'Legacy Hero', 'legacy hero|21', 21, 'Ravana', ?2, ?3)`,
        )
        .bind(legacyPlayerId, "a".repeat(64), timestamp),
      env.MIGRATION_DB
        .prepare(
          `INSERT INTO matches (
             id, season_id, player_id, fingerprint, payload_hash,
             completed_at_utc, received_at_utc, job, outcome, queue
           ) VALUES (?1, 1, ?2, ?3, ?4, ?5, ?5, ?6, 1, 1)`,
        )
        .bind(
          "61000000-0000-0000-0000-000000000001",
          legacyPlayerId,
          "a".repeat(32),
          "b".repeat(64),
          timestamp,
          JOBS.DNC,
        ),
      env.MIGRATION_DB
        .prepare(
          `INSERT INTO ratings (
             season_id, player_id, job, rating, matches, wins, losses, updated_at_utc
           ) VALUES (1, ?1, ?2, 1532, 1, 1, 0, ?3)`,
        )
        .bind(legacyPlayerId, JOBS.DNC, timestamp),
    ]);

    await applyD1Migrations(env.MIGRATION_DB, env.TEST_MIGRATIONS);

    const settings = await env.MIGRATION_DB
      .prepare("SELECT schema_version, rating_rules_version, current_season FROM app_settings WHERE id = 1")
      .first<{ schema_version: number; rating_rules_version: number; current_season: number }>();
    expect(settings).toEqual({ schema_version: 4, rating_rules_version: 4, current_season: 2 });

    const counts = await env.MIGRATION_DB
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM characters) AS characters,
           (SELECT COUNT(*) FROM matches) AS matches,
           (SELECT COUNT(*) FROM ratings) AS ratings`,
      )
      .first<{ characters: number; matches: number; ratings: number }>();
    expect(counts).toEqual({ characters: 0, matches: 0, ratings: 0 });

    const seasonRows = await env.MIGRATION_DB
      .prepare("SELECT id, ended_at_utc FROM seasons ORDER BY id")
      .all<{ id: number; ended_at_utc: string | null }>();
    expect(seasonRows.results).toHaveLength(2);
    expect(seasonRows.results[0]).toMatchObject({ id: 1 });
    expect(seasonRows.results[0]?.ended_at_utc).not.toBeNull();
    expect(seasonRows.results[1]).toEqual({ id: 2, ended_at_utc: null });

    const legacyTable = await env.MIGRATION_DB
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'players'")
      .first<{ name: string }>();
    expect(legacyTable).toBeNull();

    const installationTable = await env.MIGRATION_DB
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'installations'")
      .first<{ name: string }>();
    expect(installationTable).toBeNull();
  });

  it("requires schema v4 and Season 2 without caching a 503", async () => {
    await env.DB.prepare("UPDATE app_settings SET schema_version = 3 WHERE id = 1").run();
    const wrongSchema = await SELF.fetch("https://schema.worker.test/health", {
      headers: { "CF-Connecting-IP": "192.0.2.50" },
    });
    expect(wrongSchema.status).toBe(503);
    expect(wrongSchema.headers.get("Cache-Control")).toBe("no-store");

    await env.DB.prepare("UPDATE app_settings SET schema_version = 4 WHERE id = 1").run();
    const recovered = await SELF.fetch("https://schema.worker.test/health", {
      headers: { "CF-Connecting-IP": "192.0.2.50" },
    });
    expect(recovered.status).toBe(200);
  });
});

async function jsonRequest(path: string, body: unknown, address = "192.0.2.10"): Promise<Response> {
  return SELF.fetch(`https://worker.test${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "CF-Connecting-IP": address,
    },
    body: JSON.stringify(body),
  });
}

async function submit(installationKey: string, body: unknown, address = "192.0.2.10"): Promise<Response> {
  return SELF.fetch("https://worker.test/v2/matches", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Installation-Key": installationKey,
      "CF-Connecting-IP": address,
    },
    body: JSON.stringify(body),
  });
}

function submission(matchKey: string, completedAt: Date, outcome: 0 | 1, queue: 1 | 2) {
  return {
    matchKey,
    completedAtUtc: completedAt.toISOString(),
    characterName: "Test Dancer",
    worldId: 21,
    worldName: "Ravana",
    job: JOBS.DNC,
    outcome,
    queue,
    territoryId: 1032,
    durationSeconds: 300,
    stats: {
      kills: 3,
      deaths: outcome === 1 ? 0 : 1,
      assists: 5,
      damageDealt: 750_000,
      damageTaken: 500_000,
      hpRestored: 25_000,
      timeOnCrystalSeconds: 45,
    },
  };
}

async function cleanIdentity(identityKey: string): Promise<void> {
  await env.DB.prepare("DELETE FROM matches WHERE identity_key = ?1").bind(identityKey).run();
  await env.DB.prepare("DELETE FROM ratings WHERE identity_key = ?1").bind(identityKey).run();
  await env.DB.prepare("DELETE FROM characters WHERE identity_key = ?1").bind(identityKey).run();
}

async function currentSeasonStart(): Promise<string> {
  const row = await env.DB
    .prepare(
      `SELECT seasons.started_at_utc
       FROM app_settings
       JOIN seasons ON seasons.id = app_settings.current_season
       WHERE app_settings.id = 1`,
    )
    .first<{ started_at_utc: string }>();
  if (!row) throw new Error("Current test season is missing.");
  return row.started_at_utc;
}
