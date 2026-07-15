import { applyD1Migrations, env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { JOBS } from "../src/domain";

describe("CrystalJobRank Worker with D1", () => {
  it("handles out-of-order matches and concurrent idempotent retries atomically", async () => {
    const health = await SELF.fetch("https://worker.test/health");
    expect(health.status).toBe(200);
    await expect(health.json()).resolves.toMatchObject({
      status: "ok",
      schemaVersion: 3,
      ratingRulesVersion: 3,
      season: 1,
    });

    const registration = await jsonRequest(
      "/v1/players/register",
      characterRegistration("Test Reaper", 21, "Ravana"),
    );
    expect(registration.status).toBe(200);
    const credentials = (await registration.json()) as { playerId: string; apiKey: string };
    expect(credentials.playerId).toMatch(/^[0-9a-f-]{36}$/);
    expect(credentials.apiKey).toMatch(/^[A-Za-z0-9_-]{43}$/);

    const base = Date.now() - 120_000;
    const laterLoss = submission("b".repeat(32), new Date(base + 60_000), 0);
    const earlierWin = submission("a".repeat(32), new Date(base), 1);

    const lossResponse = await submit(credentials.apiKey, laterLoss);
    expect(lossResponse.status).toBe(200);
    await expect(lossResponse.json()).resolves.toMatchObject({ rating: 1468, matches: 1, wins: 0, losses: 1 });

    const outOfOrderResponse = await submit(credentials.apiKey, earlierWin);
    expect(outOfOrderResponse.status).toBe(200);
    await expect(outOfOrderResponse.json()).resolves.toMatchObject({
      job: JOBS.DRK,
      rating: 1499,
      matches: 2,
      wins: 1,
      losses: 1,
      winRate: 0.5,
    });

    const concurrentWin = submission("c".repeat(32), new Date(base + 120_000), 1);
    const [retryOne, retryTwo] = await Promise.all([
      submit(credentials.apiKey, concurrentWin),
      submit(credentials.apiKey, concurrentWin),
    ]);
    expect([retryOne.status, retryTwo.status]).toEqual([200, 200]);
    const retryStates = await Promise.all([retryOne.json(), retryTwo.json()]);
    for (const state of retryStates) {
      expect(state).toMatchObject({ rating: 1531, matches: 3, wins: 2, losses: 1 });
    }

    const conflict = await submit(credentials.apiKey, { ...concurrentWin, outcome: 0 });
    expect(conflict.status).toBe(409);

    const stored = await env.DB.prepare("SELECT COUNT(*) AS count FROM matches").first<{ count: number }>();
    expect(stored?.count).toBe(3);

    const leaderboard = await SELF.fetch("https://worker.test/v1/leaderboard?job=DRK&limit=50", {
      headers: { "CF-Connecting-IP": "192.0.2.10" },
    });
    expect(leaderboard.status).toBe(200);
    await expect(leaderboard.json()).resolves.toEqual([
      {
        rank: 1,
        characterName: "Test Reaper",
        worldName: "Ravana",
        job: JOBS.DRK,
        rating: 1531,
        matches: 3,
        wins: 2,
        losses: 1,
        winRate: 2 / 3,
      },
    ]);

    const custom = await submit(credentials.apiKey, {
      ...submission("d".repeat(32), new Date(base + 180_000), 1),
      queue: 3,
    });
    expect(custom.status).toBe(400);

    const oversized = await SELF.fetch("https://worker.test/v1/matches", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Api-Key": credentials.apiKey,
        "CF-Connecting-IP": "192.0.2.10",
      },
      body: new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(new Uint8Array(9_000));
          controller.enqueue(new Uint8Array(9_000));
          controller.close();
        },
      }),
    });
    expect(oversized.status).toBe(413);

    const deleted = await SELF.fetch("https://worker.test/v1/players/me", {
      method: "DELETE",
      headers: { "X-Api-Key": credentials.apiKey },
    });
    expect(deleted.status).toBe(204);

    const counts = await env.DB
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM players) AS players,
           (SELECT COUNT(*) FROM matches) AS matches,
           (SELECT COUNT(*) FROM ratings) AS ratings`,
      )
      .first<{ players: number; matches: number; ratings: number }>();
    expect(counts).toEqual({ players: 0, matches: 0, ratings: 0 });

    const oldKey = await submit(
      credentials.apiKey,
      submission("e".repeat(32), new Date(base + 180_000), 1),
    );
    expect(oldKey.status).toBe(401);

    const invalidKeyBeforeBody = await SELF.fetch("https://worker.test/v1/matches", {
      method: "POST",
      headers: {
        "Content-Type": "text/plain",
        "X-Api-Key": "x".repeat(43),
        "CF-Connecting-IP": "192.0.2.11",
      },
      body: "this is deliberately not JSON",
    });
    expect(invalidKeyBeforeBody.status).toBe(401);

    const concurrentRegistration = await jsonRequest(
      "/v1/players/register",
      characterRegistration("Concurrency Ninja", 22, "Bismarck"),
    );
    expect(concurrentRegistration.status).toBe(200);
    const concurrentCredentials = (await concurrentRegistration.json()) as {
      playerId: string;
      apiKey: string;
    };

    const concurrencyBase = Date.now() - 240_000;
    const laterDrkLoss = submission("f".repeat(32), new Date(concurrencyBase + 60_000), 0);
    const earlierDrkWin = submission("e".repeat(32), new Date(concurrencyBase), 1);
    const drkConcurrent = await Promise.all([
      submit(concurrentCredentials.apiKey, laterDrkLoss),
      submit(concurrentCredentials.apiKey, earlierDrkWin),
    ]);
    expect(drkConcurrent.map((response) => response.status)).toEqual([200, 200]);

    const laterNinWin = {
      ...submission("1".repeat(32), new Date(concurrencyBase + 180_000), 1),
      job: JOBS.NIN,
    };
    const earlierNinLoss = {
      ...submission("0".repeat(32), new Date(concurrencyBase + 120_000), 0),
      job: JOBS.NIN,
    };
    const ninConcurrent = await Promise.all([
      submit(concurrentCredentials.apiKey, laterNinWin),
      submit(concurrentCredentials.apiKey, earlierNinLoss),
    ]);
    expect(ninConcurrent.map((response) => response.status)).toEqual([200, 200]);

    const concurrentRatings = await env.DB
      .prepare(
        `SELECT job, rating, matches, wins, losses
         FROM ratings WHERE player_id = ?1 ORDER BY job`,
      )
      .bind(concurrentCredentials.playerId)
      .all<{ job: number; rating: number; matches: number; wins: number; losses: number }>();
    expect(concurrentRatings.results).toEqual([
      { job: JOBS.DRK, rating: 1499, matches: 2, wins: 1, losses: 1 },
      { job: JOBS.NIN, rating: 1501, matches: 2, wins: 1, losses: 1 },
    ]);

    const deletedConcurrent = await SELF.fetch("https://worker.test/v1/players/me", {
      method: "DELETE",
      headers: {
        "X-Api-Key": concurrentCredentials.apiKey,
        "CF-Connecting-IP": "192.0.2.10",
      },
    });
    expect(deletedConcurrent.status).toBe(204);
    const finalPlayers = await env.DB.prepare("SELECT COUNT(*) AS count FROM players").first<{ count: number }>();
    expect(finalPlayers?.count).toBe(0);
  });

  it("registers by normalized character and Home World without accepting aliases", async () => {
    const first = await jsonRequest(
      "/v1/players/register",
      characterRegistration("  Test   Reaper  ", 21, "  Ravana  "),
      "192.0.2.70",
    );
    expect(first.status).toBe(200);
    const firstCredentials = (await first.json()) as { playerId: string; apiKey: string };
    expect(firstCredentials).toEqual({
      playerId: expect.stringMatching(/^[0-9a-f-]{36}$/),
      apiKey: expect.stringMatching(/^[A-Za-z0-9_-]{43}$/),
    });

    const stored = await env.DB
      .prepare(
        `SELECT display_name, display_name_key, world_id, world_name
         FROM players WHERE id = ?1`,
      )
      .bind(firstCredentials.playerId)
      .first<{
        display_name: string;
        display_name_key: string;
        world_id: number;
        world_name: string;
      }>();
    expect(stored).toEqual({
      display_name: "Test Reaper",
      display_name_key: "test reaper|21",
      world_id: 21,
      world_name: "Ravana",
    });

    const duplicate = await jsonRequest(
      "/v1/players/register",
      characterRegistration("test reaper", 21, "Ravana"),
      "192.0.2.71",
    );
    expect(duplicate.status).toBe(409);

    const otherWorld = await jsonRequest(
      "/v1/players/register",
      characterRegistration("Test Reaper", 22, "Bismarck"),
      "192.0.2.72",
    );
    expect(otherWorld.status).toBe(200);

    const legacyAlias = await jsonRequest(
      "/v1/players/register",
      { displayName: "Legacy Alias" },
      "192.0.2.73",
    );
    expect(legacyAlias.status).toBe(400);

    const invalidWorldId = await jsonRequest(
      "/v1/players/register",
      characterRegistration("Worldless Hero", 0, "Ravana"),
      "192.0.2.74",
    );
    expect(invalidWorldId.status).toBe(400);

    const invalidWorldName = await jsonRequest(
      "/v1/players/register",
      characterRegistration("Worldless Hero", 21, "Bad\nWorld"),
      "192.0.2.75",
    );
    expect(invalidWorldName.status).toBe(400);

    const otherCredentials = (await otherWorld.json()) as { playerId: string };
    await env.DB
      .prepare("DELETE FROM players WHERE id IN (?1, ?2)")
      .bind(firstCredentials.playerId, otherCredentials.playerId)
      .run();
  });

  it("isolates jobs, applies deterministic tie-breakers, and follows the current season", async () => {
    const timestamp = "2000-01-01T00:00:00.000Z";
    await env.DB
      .prepare("INSERT INTO seasons (id, name, started_at_utc) VALUES (2, 'Season 2', ?1)")
      .bind(timestamp)
      .run();

    const seededRows = [
      { characterName: "Zulu Knight", worldId: 21, worldName: "Ravana", season: 1, job: JOBS.DRK, rating: 1700, matches: 20, wins: 12 },
      { characterName: "beta Knight", worldId: 22, worldName: "Bismarck", season: 1, job: JOBS.DRK, rating: 1600, matches: 12, wins: 7 },
      { characterName: "Alpha Knight", worldId: 23, worldName: "Asura", season: 1, job: JOBS.DRK, rating: 1600, matches: 12, wins: 6 },
      { characterName: "Match Tiebreak", worldId: 24, worldName: "Belias", season: 1, job: JOBS.DRK, rating: 1600, matches: 5, wins: 3 },
      { characterName: "Zero Matches", worldId: 25, worldName: "Chaos", season: 1, job: JOBS.DRK, rating: 3000, matches: 0, wins: 0 },
      { characterName: "Other Job", worldId: 26, worldName: "Hecatoncheir", season: 1, job: JOBS.NIN, rating: 2500, matches: 10, wins: 10 },
      { characterName: "Old Champion", worldId: 28, worldName: "Pandaemonium", season: 2, job: JOBS.DRK, rating: 2800, matches: 30, wins: 25 },
    ];

    const statements = [];
    for (const [index, row] of seededRows.entries()) {
      const suffix = String(index + 1).padStart(12, "0");
      const playerId = `50000000-0000-0000-0000-${suffix}`;
      statements.push(
        env.DB
          .prepare(
            `INSERT INTO players (
               id, display_name, display_name_key, world_id, world_name, api_key_hash, created_at_utc
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)`,
          )
          .bind(
            playerId,
            row.characterName,
            `${row.characterName.toLocaleLowerCase("en-US")}|${row.worldId}`,
            row.worldId,
            row.worldName,
            String(index + 1).padStart(64, "0"),
            timestamp,
          ),
        env.DB
          .prepare(
            `INSERT INTO ratings (
               season_id, player_id, job, rating, matches, wins, losses, updated_at_utc
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`,
          )
          .bind(
            row.season,
            playerId,
            row.job,
            row.rating,
            row.matches,
            row.wins,
            row.matches - row.wins,
            timestamp,
          ),
      );
    }
    await env.DB.batch(statements);

    const current = await SELF.fetch("https://worker.test/v1/leaderboard?job=drk", {
      headers: { "CF-Connecting-IP": "192.0.2.60" },
    });
    expect(current.status).toBe(200);
    const currentRows = (await current.json()) as Array<{
      rank: number;
      characterName: string;
      worldName: string;
      job: number;
      rating: number;
      matches: number;
    }>;
    expect(currentRows.map(({ rank, characterName, worldName }) => [rank, characterName, worldName])).toEqual([
      [1, "Zulu Knight", "Ravana"],
      [2, "Alpha Knight", "Asura"],
      [3, "beta Knight", "Bismarck"],
      [4, "Match Tiebreak", "Belias"],
    ]);
    expect(currentRows.every((row) => row.job === JOBS.DRK)).toBe(true);

    const limited = await SELF.fetch("https://worker.test/v1/leaderboard?job=DRK&limit=2", {
      headers: { "CF-Connecting-IP": "192.0.2.61" },
    });
    expect(limited.status).toBe(200);
    const limitedRows = (await limited.json()) as Array<{ characterName: string }>;
    expect(limitedRows.map((row) => row.characterName)).toEqual(["Zulu Knight", "Alpha Knight"]);

    await env.DB.prepare("UPDATE app_settings SET current_season = 2 WHERE id = 1").run();
    const nextSeason = await SELF.fetch("https://worker.test/v1/leaderboard?job=DRK", {
      headers: { "CF-Connecting-IP": "192.0.2.62" },
    });
    const nextSeasonRows = await nextSeason.json();

    await env.DB.prepare("UPDATE app_settings SET current_season = 1 WHERE id = 1").run();
    await env.DB.prepare("DELETE FROM players WHERE id LIKE '50000000-%'").run();
    await env.DB.prepare("DELETE FROM seasons WHERE id = 2").run();
    await env.DB.prepare("DELETE FROM daily_registration_counts WHERE day_utc = '2000-01-01'").run();

    expect(nextSeason.status).toBe(200);
    expect(nextSeasonRows).toMatchObject([
      {
        rank: 1,
        characterName: "Old Champion",
        worldName: "Pandaemonium",
        job: JOBS.DRK,
        rating: 2800,
        matches: 30,
      },
    ]);
  });

  it("rejects invalid leaderboard query parameters", async () => {
    for (const path of [
      "/v1/leaderboard",
      "/v1/leaderboard?job=BLU",
      "/v1/leaderboard?job=DRK&limit=1.5",
    ]) {
      const response = await SELF.fetch(`https://worker.test${path}`, {
        headers: { "CF-Connecting-IP": "192.0.2.63" },
      });
      expect(response.status).toBe(400);
      expect(response.headers.get("Cache-Control")).toBe("no-store");
    }
  });

  it("bounds late-match replay after 128 job matches while preserving exact retry idempotency", async () => {
    const registration = await jsonRequest(
      "/v1/players/register",
      characterRegistration("Replay Sentinel", 29, "Shinryu"),
      "192.0.2.30",
    );
    expect(registration.status).toBe(200);
    const credentials = (await registration.json()) as { playerId: string; apiKey: string };

    const base = Date.now() - 60 * 60 * 1000;
    const firstMatch = submission("f".repeat(32), new Date(base), 1);
    const firstResponse = await submit(credentials.apiKey, firstMatch, "192.0.2.30");
    expect(firstResponse.status).toBe(200);

    await env.DB
      .prepare(
        `WITH RECURSIVE sequence(n) AS (
           SELECT 1
           UNION ALL
           SELECT n + 1 FROM sequence WHERE n < 127
         )
         INSERT INTO matches (
           id, season_id, player_id, fingerprint, payload_hash,
           completed_at_utc, received_at_utc, job, outcome, queue
         )
         SELECT
           printf('20000000-0000-0000-0000-%012d', n),
           1,
           ?1,
           printf('%032x', n),
           printf('%064x', n + 1000),
           strftime('%Y-%m-%dT%H:%M:%fZ', ?2, printf('+%d seconds', n)),
           strftime('%Y-%m-%dT%H:%M:%fZ', ?2, printf('-%d days', n)),
           ?3,
           n % 2,
           1
         FROM sequence
         ORDER BY n`,
      )
      .bind(credentials.playerId, new Date(base).toISOString(), JOBS.DRK)
      .run();
    await env.DB
      .prepare(
        `UPDATE ratings
         SET rating = 1500, matches = 128, wins = 65, losses = 63
         WHERE season_id = 1 AND player_id = ?1 AND job = ?2`,
      )
      .bind(credentials.playerId, JOBS.DRK)
      .run();

    const exactRetry = await submit(credentials.apiKey, firstMatch, "192.0.2.30");
    expect(exactRetry.status).toBe(200);
    await expect(exactRetry.json()).resolves.toMatchObject({ rating: 1500, matches: 128 });

    const lateMatch = submission("e".repeat(32), new Date(base - 1000), 0);
    const rejectedLateMatch = await submit(credentials.apiKey, lateMatch, "192.0.2.30");
    expect(rejectedLateMatch.status).toBe(409);
    await expect(rejectedLateMatch.json()).resolves.toEqual({
      error: "Late matches are accepted only before this job reaches 128 season matches.",
    });

    await expect(
      env.DB
        .prepare(
          `INSERT INTO matches (
             id, season_id, player_id, fingerprint, payload_hash,
             completed_at_utc, received_at_utc, job, outcome, queue
           ) VALUES (?1, 1, ?2, ?3, ?4, ?5, ?6, ?7, 0, 1)`,
        )
        .bind(
          crypto.randomUUID(),
          credentials.playerId,
          "d".repeat(32),
          "0".repeat(64),
          new Date(base - 2000).toISOString(),
          new Date().toISOString(),
          JOBS.DRK,
        )
        .run(),
    ).rejects.toThrow(/late_match_replay_limit/);

    const stored = await env.DB
      .prepare("SELECT COUNT(*) AS count FROM matches WHERE player_id = ?1 AND job = ?2")
      .bind(credentials.playerId, JOBS.DRK)
      .first<{ count: number }>();
    expect(stored?.count).toBe(128);

    await env.DB.prepare("DELETE FROM players WHERE id = ?1").bind(credentials.playerId).run();
  });

  it("keeps the global registration cap after account deletion and maps it to HTTP 429", async () => {
    await env.DB.prepare("DELETE FROM players").run();
    await env.DB.prepare("DELETE FROM daily_registration_counts").run();
    const now = new Date().toISOString();
    await env.DB
      .prepare(
        `WITH RECURSIVE sequence(n) AS (
           SELECT 1
           UNION ALL
           SELECT n + 1 FROM sequence WHERE n < 100
         )
         INSERT INTO players (
           id, display_name, display_name_key, world_id, world_name, api_key_hash, created_at_utc
         )
         SELECT
           printf('30000000-0000-0000-0000-%012d', n),
           'Seed ' || n,
           'seed ' || n || '|' || (1000 + n),
           1000 + n,
           'World ' || n,
           printf('%064x', n),
           ?1
         FROM sequence
         ORDER BY n`,
      )
      .bind(now)
      .run();

    await env.DB.prepare("DELETE FROM players").run();
    const persisted = await env.DB
      .prepare("SELECT registration_count FROM daily_registration_counts WHERE day_utc = ?1")
      .bind(now.slice(0, 10))
      .first<{ registration_count: number }>();
    expect(persisted?.registration_count).toBe(100);

    const friendly = await jsonRequest(
      "/v1/players/register",
      characterRegistration("One Too-many", 30, "Twintania"),
      "192.0.2.40",
    );
    expect(friendly.status).toBe(429);
    expect(Number(friendly.headers.get("Retry-After"))).toBeGreaterThan(0);
    await expect(friendly.json()).resolves.toEqual({
      error: "The leaderboard's daily registration capacity has been reached.",
    });

    await expect(
      env.DB
        .prepare(
          `INSERT INTO players (
             id, display_name, display_name_key, world_id, world_name, api_key_hash, created_at_utc
           ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)`,
        )
        .bind(
          "40000000-0000-0000-0000-000000000001",
          "Direct Attempt",
          "direct attempt|31",
          31,
          "Brynhildr",
          "f".repeat(64),
          now,
        )
        .run(),
    ).rejects.toThrow(/global_daily_registration_limit/);
  });

  it("migrates v2 aliases by invalidating unverifiable identity data", async () => {
    expect(env.TEST_MIGRATIONS).toHaveLength(3);
    await applyD1Migrations(env.MIGRATION_DB, env.TEST_MIGRATIONS.slice(0, 2));

    const timestamp = "2000-01-01T00:00:00.000Z";
    const legacyPlayerId = "60000000-0000-0000-0000-000000000001";
    await env.MIGRATION_DB.batch([
      env.MIGRATION_DB
        .prepare(
          `INSERT INTO players (
             id, display_name, display_name_key, api_key_hash, created_at_utc
           ) VALUES (?1, ?2, ?3, ?4, ?5)`,
        )
        .bind(legacyPlayerId, "Legacy Alias", "legacy alias", "a".repeat(64), timestamp),
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
          JOBS.DRK,
        ),
      env.MIGRATION_DB
        .prepare(
          `INSERT INTO ratings (
             season_id, player_id, job, rating, matches, wins, losses, updated_at_utc
           ) VALUES (1, ?1, ?2, 1532, 1, 1, 0, ?3)`,
        )
        .bind(legacyPlayerId, JOBS.DRK, timestamp),
    ]);

    const legacyCounts = await env.MIGRATION_DB
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM players) AS players,
           (SELECT COUNT(*) FROM matches) AS matches,
           (SELECT COUNT(*) FROM ratings) AS ratings`,
      )
      .first<{ players: number; matches: number; ratings: number }>();
    expect(legacyCounts).toEqual({ players: 1, matches: 1, ratings: 1 });

    await applyD1Migrations(env.MIGRATION_DB, env.TEST_MIGRATIONS);

    const settings = await env.MIGRATION_DB
      .prepare("SELECT schema_version FROM app_settings WHERE id = 1")
      .first<{ schema_version: number }>();
    expect(settings?.schema_version).toBe(3);

    const migratedCounts = await env.MIGRATION_DB
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM players) AS players,
           (SELECT COUNT(*) FROM matches) AS matches,
           (SELECT COUNT(*) FROM ratings) AS ratings`,
      )
      .first<{ players: number; matches: number; ratings: number }>();
    expect(migratedCounts).toEqual({ players: 0, matches: 0, ratings: 0 });

    const columns = await env.MIGRATION_DB
      .prepare("PRAGMA table_info(players)")
      .all<{ name: string; notnull: number }>();
    expect(
      Object.fromEntries(
        columns.results
          .filter((column) => column.name === "world_id" || column.name === "world_name")
          .map((column) => [column.name, column.notnull]),
      ),
    ).toEqual({ world_id: 1, world_name: 1 });

    const triggers = await env.MIGRATION_DB
      .prepare("SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'players' ORDER BY name")
      .all<{ name: string }>();
    expect(triggers.results.map((row) => row.name)).toEqual([
      "players_count_daily_registration",
      "players_global_daily_limit",
    ]);

    await expect(
      env.MIGRATION_DB
        .prepare(
          `INSERT INTO players (
             id, display_name, display_name_key, api_key_hash, created_at_utc
           ) VALUES (?1, ?2, ?3, ?4, ?5)`,
        )
        .bind(
          "62000000-0000-0000-0000-000000000001",
          "Still Alias",
          "still alias",
          "c".repeat(64),
          timestamp,
        )
        .run(),
    ).rejects.toThrow(/world_id|world_name|NOT NULL/i);

    const officialPlayerId = "63000000-0000-0000-0000-000000000001";
    await env.MIGRATION_DB
      .prepare(
        `INSERT INTO players (
           id, display_name, display_name_key, world_id, world_name, api_key_hash, created_at_utc
         ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)`,
      )
      .bind(
        officialPlayerId,
        "Official Hero",
        "official hero|21",
        21,
        "Ravana",
        "d".repeat(64),
        timestamp,
      )
      .run();
    await env.MIGRATION_DB
      .prepare(
        `INSERT INTO matches (
           id, season_id, player_id, fingerprint, payload_hash,
           completed_at_utc, received_at_utc, job, outcome, queue
         ) VALUES (?1, 1, ?2, ?3, ?4, ?5, ?5, ?6, 1, 1)`,
      )
      .bind(
        "64000000-0000-0000-0000-000000000001",
        officialPlayerId,
        "e".repeat(32),
        "f".repeat(64),
        timestamp,
        JOBS.DRK,
      )
      .run();
    await env.MIGRATION_DB.prepare("DELETE FROM players WHERE id = ?1").bind(officialPlayerId).run();

    const remainingMatch = await env.MIGRATION_DB
      .prepare("SELECT COUNT(*) AS count FROM matches WHERE player_id = ?1")
      .bind(officialPlayerId)
      .first<{ count: number }>();
    expect(remainingMatch?.count).toBe(0);

    const retainedLimit = await env.MIGRATION_DB
      .prepare("SELECT registration_count FROM daily_registration_counts WHERE day_utc = '2000-01-01'")
      .first<{ registration_count: number }>();
    expect(retainedLimit?.registration_count).toBe(2);
  });

  it("requires schema v3 and a real current season without caching a 503", async () => {
    await env.DB.prepare("UPDATE app_settings SET schema_version = 1 WHERE id = 1").run();
    const wrongSchema = await SELF.fetch("https://schema.worker.test/health?probe=one", {
      headers: { "CF-Connecting-IP": "192.0.2.50" },
    });
    expect(wrongSchema.status).toBe(503);
    expect(wrongSchema.headers.get("Cache-Control")).toBe("no-store");

    await env.DB.prepare("UPDATE app_settings SET schema_version = 3 WHERE id = 1").run();
    const recovered = await SELF.fetch("https://schema.worker.test/health?probe=two", {
      headers: { "CF-Connecting-IP": "192.0.2.50" },
    });
    expect(recovered.status).toBe(200);

    await env.DB.prepare("UPDATE app_settings SET current_season = 999 WHERE id = 1").run();
    const missingSeason = await SELF.fetch("https://season.worker.test/health", {
      headers: { "CF-Connecting-IP": "192.0.2.51" },
    });
    expect(missingSeason.status).toBe(503);
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

async function submit(apiKey: string, body: unknown, address = "192.0.2.10"): Promise<Response> {
  return SELF.fetch("https://worker.test/v1/matches", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Api-Key": apiKey,
      "CF-Connecting-IP": address,
    },
    body: JSON.stringify(body),
  });
}

function submission(fingerprint: string, completedAt: Date, outcome: 0 | 1) {
  return {
    fingerprint,
    completedAtUtc: completedAt.toISOString(),
    job: JOBS.DRK,
    outcome,
    queue: 1,
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

function characterRegistration(characterName: string, worldId: number, worldName: string) {
  return { characterName, worldId, worldName };
}
