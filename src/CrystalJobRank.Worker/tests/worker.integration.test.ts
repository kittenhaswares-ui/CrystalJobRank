import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { JOBS } from "../src/domain";

describe("CrystalJobRank Worker with D1", () => {
  it("handles out-of-order matches and concurrent idempotent retries atomically", async () => {
    const health = await SELF.fetch("https://worker.test/health");
    expect(health.status).toBe(200);
    await expect(health.json()).resolves.toMatchObject({
      status: "ok",
      schemaVersion: 2,
      ratingRulesVersion: 3,
      season: 1,
    });

    const registration = await jsonRequest("/v1/players/register", {
      displayName: "Test Reaper",
    });
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
        displayName: "Test Reaper",
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

    const concurrentRegistration = await jsonRequest("/v1/players/register", {
      displayName: "Concurrency Ninja",
    });
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

  it("bounds late-match replay after 128 job matches while preserving exact retry idempotency", async () => {
    const registration = await jsonRequest(
      "/v1/players/register",
      { displayName: "Replay Sentinel" },
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
           id, display_name, display_name_key, api_key_hash, created_at_utc
         )
         SELECT
           printf('30000000-0000-0000-0000-%012d', n),
           'Seed ' || n,
           'seed-' || n,
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
      { displayName: "One Too Many" },
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
             id, display_name, display_name_key, api_key_hash, created_at_utc
           ) VALUES (?1, ?2, ?3, ?4, ?5)`,
        )
        .bind(
          "40000000-0000-0000-0000-000000000001",
          "Direct Attempt",
          "direct-attempt",
          "f".repeat(64),
          now,
        )
        .run(),
    ).rejects.toThrow(/global_daily_registration_limit/);
  });

  it("requires schema v2 and a real current season without caching a 503", async () => {
    await env.DB.prepare("UPDATE app_settings SET schema_version = 1 WHERE id = 1").run();
    const wrongSchema = await SELF.fetch("https://schema.worker.test/health?probe=one", {
      headers: { "CF-Connecting-IP": "192.0.2.50" },
    });
    expect(wrongSchema.status).toBe(503);
    expect(wrongSchema.headers.get("Cache-Control")).toBe("no-store");

    await env.DB.prepare("UPDATE app_settings SET schema_version = 2 WHERE id = 1").run();
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
