import {
  canonicalSubmission,
  type CombatJob,
  displayNameKey,
  InputError,
  type MatchSubmission,
  normalizeDisplayName,
  parseJobQuery,
  parseLeaderboardLimit,
  parseMatchSubmission,
  RATING_RULES_VERSION,
} from "./domain";

interface SettingsRow {
  schema_version: number;
  rating_rules_version: number;
  current_season: number;
}

interface AuthenticatedPlayerRow {
  id: string;
  current_season: number;
}

interface ExistingMatchRow {
  payload_hash: string;
  season_id: number;
}

interface RatingDbRow {
  rating: number;
  matches: number;
  wins: number;
  losses: number;
}

interface LeaderboardDbRow extends RatingDbRow {
  display_name: string;
}

interface MatchLimitRow {
  season_count: number;
  daily_count: number;
}

interface ReplayLimitRow {
  history_count: number;
  has_later_match: number;
}

interface RegistrationLimitRow {
  daily_count: number;
}

const MAX_JSON_BODY_BYTES = 16 * 1024;
const API_KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/;
const EXPECTED_SCHEMA_VERSION = 2;
const DAILY_JOB_MATCH_LIMIT = 100;
const SEASON_JOB_MATCH_LIMIT = 5_000;
const LATE_MATCH_REPLAY_LIMIT = 128;
const GLOBAL_DAILY_REGISTRATION_LIMIT = 100;
const HEALTH_CACHE_TTL_SECONDS = 15;
const HEALTH_CACHE_NAME = "crystal-job-rank-health-v2";

const INSERT_MATCH_SQL = `
  INSERT INTO matches (
    id, season_id, player_id, fingerprint, payload_hash,
    completed_at_utc, received_at_utc, job, outcome, queue
  ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
`;

// The common append-only case advances the materialized rating in constant
// time. A complete canonical replay happens only for a genuinely out-of-order
// event or a missing/stale cache. The decision is made after INSERT_MATCH_SQL,
// inside the same D1 transaction, so concurrent different fingerprints cannot
// race the fast-path check.
const REFRESH_RATING_SQL = `
  WITH RECURSIVE
  args (
    season_id, player_id, job, new_completed_at_utc,
    new_fingerprint, new_outcome, updated_at_utc
  ) AS (
    VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
  ),
  rating_context AS (
    SELECT
      args.*,
      COALESCE(cached.rating, 1500) AS cached_rating,
      COALESCE(cached.matches, 0) AS cached_matches,
      COALESCE(cached.wins, 0) AS cached_wins,
      COALESCE(cached.losses, 0) AS cached_losses,
      CASE WHEN cached.player_id IS NULL THEN 0 ELSE 1 END AS cached_present,
      CASE WHEN EXISTS (
        SELECT 1
        FROM matches other
        WHERE other.season_id = args.season_id
          AND other.player_id = args.player_id
          AND other.job = args.job
          AND other.fingerprint <> args.new_fingerprint COLLATE BINARY
        LIMIT 1
      ) THEN 1 ELSE 0 END AS has_other_match,
      CASE WHEN NOT EXISTS (
        SELECT 1
        FROM matches later
        WHERE later.season_id = args.season_id
          AND later.player_id = args.player_id
          AND later.job = args.job
          AND (
            later.completed_at_utc > args.new_completed_at_utc COLLATE BINARY OR
            (later.completed_at_utc = args.new_completed_at_utc COLLATE BINARY AND
             later.fingerprint > args.new_fingerprint COLLATE BINARY)
          )
      ) THEN 1 ELSE 0 END AS new_is_latest
    FROM args
    LEFT JOIN ratings cached
      ON cached.season_id = args.season_id
     AND cached.player_id = args.player_id
     AND cached.job = args.job
  ),
  mode AS (
    SELECT *,
      CASE WHEN new_is_latest = 1 AND (cached_present = 1 OR has_other_match = 0)
        THEN 1 ELSE 0 END AS can_fast_path
    FROM rating_context
  ),
  fast_delta AS (
    SELECT *,
      CAST(round(
        (CASE WHEN cached_matches < 10 THEN 64.0 ELSE 32.0 END) *
        ((CASE WHEN new_outcome = 1 THEN 1.0 ELSE 0.0 END) -
          (1.0 / (1.0 + pow(10.0, (1500.0 - cached_rating) / 2000.0)))),
        0
      ) AS INTEGER) AS raw_delta
    FROM mode
    WHERE can_fast_path = 1
  ),
  fast_result (rating, matches, wins, losses) AS (
    SELECT
      max(0, min(3000,
        cached_rating + CASE WHEN raw_delta = 0
          THEN CASE WHEN new_outcome = 1 THEN 1 ELSE -1 END
          ELSE raw_delta END
      )),
      cached_matches + 1,
      cached_wins + CASE WHEN new_outcome = 1 THEN 1 ELSE 0 END,
      cached_losses + CASE WHEN new_outcome = 0 THEN 1 ELSE 0 END
    FROM fast_delta
  ),
  ordered (sequence, outcome) AS (
    SELECT
      ROW_NUMBER() OVER (
        ORDER BY event.completed_at_utc COLLATE BINARY, event.fingerprint COLLATE BINARY
      ),
      event.outcome
    FROM matches event
    JOIN mode
      ON mode.can_fast_path = 0
     AND event.season_id = mode.season_id
     AND event.player_id = mode.player_id
     AND event.job = mode.job
  ),
  replay (sequence, rating, matches, wins, losses) AS (
    SELECT 0, 1500, 0, 0, 0
    UNION ALL
    SELECT
      ordered.sequence,
      max(0, min(3000,
        replay.rating +
        CASE
          WHEN CAST(round(
            (CASE WHEN replay.matches < 10 THEN 64.0 ELSE 32.0 END) *
            ((CASE WHEN ordered.outcome = 1 THEN 1.0 ELSE 0.0 END) -
              (1.0 / (1.0 + pow(10.0, (1500.0 - replay.rating) / 2000.0)))),
            0
          ) AS INTEGER) = 0
          THEN CASE WHEN ordered.outcome = 1 THEN 1 ELSE -1 END
          ELSE CAST(round(
            (CASE WHEN replay.matches < 10 THEN 64.0 ELSE 32.0 END) *
            ((CASE WHEN ordered.outcome = 1 THEN 1.0 ELSE 0.0 END) -
              (1.0 / (1.0 + pow(10.0, (1500.0 - replay.rating) / 2000.0)))),
            0
          ) AS INTEGER)
        END
      )),
      replay.matches + 1,
      replay.wins + CASE WHEN ordered.outcome = 1 THEN 1 ELSE 0 END,
      replay.losses + CASE WHEN ordered.outcome = 0 THEN 1 ELSE 0 END
    FROM replay
    JOIN ordered ON ordered.sequence = replay.sequence + 1
  ),
  full_result (rating, matches, wins, losses) AS (
    SELECT replay.rating, replay.matches, replay.wins, replay.losses
    FROM replay
    JOIN mode
      ON mode.can_fast_path = 0
    ORDER BY replay.sequence DESC
    LIMIT 1
  ),
  final_result (rating, matches, wins, losses) AS (
    SELECT rating, matches, wins, losses FROM fast_result
    UNION ALL
    SELECT rating, matches, wins, losses FROM full_result
  )
  INSERT OR REPLACE INTO ratings (
    season_id, player_id, job, rating, matches, wins, losses, updated_at_utc
  )
  SELECT
    mode.season_id, mode.player_id, mode.job,
    final_result.rating, final_result.matches,
    final_result.wins, final_result.losses,
    mode.updated_at_utc
  FROM mode
  JOIN final_result
`;

const SELECT_RATING_SQL = `
  SELECT rating, matches, wins, losses
  FROM ratings
  WHERE season_id = ?1 AND player_id = ?2 AND job = ?3
`;

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const requestId = crypto.randomUUID();
    try {
      return await route(request, env, ctx);
    } catch (error) {
      if (error instanceof HttpError) return errorResponse(error.status, error.message, error.headers);
      if (error instanceof InputError) return errorResponse(400, error.message);

      console.error("Unhandled leaderboard request failure", { requestId });
      return errorResponse(500, "The leaderboard service could not complete the request.");
    }
  },
} satisfies ExportedHandler<Env>;

async function route(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  const url = new URL(request.url);
  const path = normalizePath(url.pathname);

  if (path === "/health") {
    if (request.method !== "GET") return methodNotAllowed("GET");
    return health(request, env, ctx);
  }

  if (path === "/v1/players/register") {
    if (request.method !== "POST") return methodNotAllowed("POST");
    return register(request, env);
  }

  if (path === "/v1/matches") {
    if (request.method !== "POST") return methodNotAllowed("POST");
    return submitMatch(request, env);
  }

  if (path === "/v1/leaderboard") {
    if (request.method !== "GET") return methodNotAllowed("GET");
    return leaderboard(request, url, env);
  }

  if (path === "/v1/players/me") {
    if (request.method !== "DELETE") return methodNotAllowed("DELETE");
    return deletePlayer(request, env);
  }

  return errorResponse(404, "Route not found.");
}

async function health(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  await enforceRateLimit(env.READ_RATE_LIMITER, await anonymousRateKey(request, "health"));

  const cacheKey = canonicalHealthCacheKey(request);
  let healthCache: Cache | null = null;
  try {
    healthCache = await caches.open(HEALTH_CACHE_NAME);
    const cached = await healthCache.match(cacheKey);
    if (cached) return cached;
  } catch {
    // Cache availability must not decide service health.
  }

  try {
    const settings = await env.DB
      .prepare(
        `SELECT settings.schema_version, settings.rating_rules_version, settings.current_season
         FROM app_settings settings
         JOIN seasons active_season ON active_season.id = settings.current_season
         WHERE settings.id = 1`,
      )
      .first<SettingsRow>();
    if (
      !settings ||
      settings.schema_version !== EXPECTED_SCHEMA_VERSION ||
      settings.rating_rules_version !== RATING_RULES_VERSION ||
      settings.current_season < 1
    ) {
      return errorResponse(503, "Leaderboard database configuration is invalid.");
    }

    const response = jsonResponse({
      status: "ok",
      schemaVersion: settings.schema_version,
      ratingRulesVersion: settings.rating_rules_version,
      season: settings.current_season,
    }, 200, {
      "Cache-Control": `public, max-age=${HEALTH_CACHE_TTL_SECONDS}`,
    });
    if (healthCache) {
      ctx.waitUntil(healthCache.put(cacheKey, response.clone()).catch(() => undefined));
    }
    return response;
  } catch {
    return errorResponse(503, "Leaderboard database is unavailable.");
  }
}

function canonicalHealthCacheKey(request: Request): Request {
  const url = new URL(request.url);
  url.pathname = "/health";
  url.search = "";
  url.hash = "";
  return new Request(url.toString(), { method: "GET" });
}

async function register(request: Request, env: Env): Promise<Response> {
  await enforceRateLimit(env.REGISTRATION_RATE_LIMITER, await anonymousRateKey(request, "register"));
  const body = requireObject(await readJson(request));
  const displayName = normalizeDisplayName(body.displayName);
  const nameKey = displayNameKey(displayName);

  const existing = await env.DB
    .prepare("SELECT id FROM players WHERE display_name_key = ?1")
    .bind(nameKey)
    .first<{ id: string }>();
  if (existing) throw new HttpError(409, "That display name is already registered.");

  const now = new Date().toISOString();
  const registrationLimit = await registrationLimitStatus(env.DB, now);
  if (registrationLimit.daily_count >= GLOBAL_DAILY_REGISTRATION_LIMIT) {
    throw registrationLimitError(now);
  }

  const playerId = crypto.randomUUID();
  const apiKey = generateApiKey();
  const apiKeyHash = await sha256Hex(apiKey);
  try {
    await env.DB
      .prepare(
        `INSERT INTO players (id, display_name, display_name_key, api_key_hash, created_at_utc)
         VALUES (?1, ?2, ?3, ?4, ?5)`,
      )
      .bind(playerId, displayName, nameKey, apiKeyHash, now)
      .run();
  } catch (error) {
    const raced = await env.DB
      .prepare("SELECT id FROM players WHERE display_name_key = ?1")
      .bind(nameKey)
      .first<{ id: string }>();
    if (raced) throw new HttpError(409, "That display name is already registered.");

    const racedLimit = await registrationLimitStatus(env.DB, now);
    if (racedLimit.daily_count >= GLOBAL_DAILY_REGISTRATION_LIMIT) {
      throw registrationLimitError(now);
    }
    throw error;
  }

  return jsonResponse({ playerId, apiKey });
}

async function submitMatch(request: Request, env: Env): Promise<Response> {
  await enforceRateLimit(env.AUTH_RATE_LIMITER, await anonymousRateKey(request, "auth"));
  const apiKey = requireApiKey(request);
  const apiKeyHash = await sha256Hex(apiKey);
  const player = await authenticate(env.DB, apiKeyHash);
  if (!player) throw new HttpError(401, "A valid API key is required.");
  await enforceRateLimit(env.WRITE_RATE_LIMITER, `account:${apiKeyHash}`);

  const submission = parseMatchSubmission(await readJson(request));
  const payloadHash = await sha256Hex(canonicalSubmission(submission));

  const duplicate = await findDuplicate(
    env.DB,
    player.id,
    submission.fingerprint,
  );
  if (duplicate) {
    return duplicateMatchResponse(env.DB, player, submission, payloadHash, duplicate);
  }

  const replayLimit = await replayLimitStatus(
    env.DB,
    player.current_season,
    player.id,
    submission.job,
    submission.completedAtUtc,
    submission.fingerprint,
  );
  if (
    replayLimit.history_count >= LATE_MATCH_REPLAY_LIMIT &&
    replayLimit.has_later_match === 1
  ) {
    throw lateMatchReplayLimitError();
  }

  const now = new Date().toISOString();
  try {
    const batch = await env.DB.batch([
      env.DB
        .prepare(INSERT_MATCH_SQL)
        .bind(
          crypto.randomUUID(),
          player.current_season,
          player.id,
          submission.fingerprint,
          payloadHash,
          submission.completedAtUtc,
          now,
          submission.job,
          submission.outcome,
          submission.queue,
        ),
      env.DB
        .prepare(REFRESH_RATING_SQL)
        .bind(
          player.current_season,
          player.id,
          submission.job,
          submission.completedAtUtc,
          submission.fingerprint,
          submission.outcome,
          now,
        ),
      env.DB
        .prepare(SELECT_RATING_SQL)
        .bind(player.current_season, player.id, submission.job),
    ]);

    const rating = resultRow<RatingDbRow>(batch[2]);
    if (!rating) throw new Error("Rating materialization returned no row.");
    return jsonResponse(toRatingState(submission.job, rating));
  } catch (error) {
    // The UNIQUE constraint is authoritative. If two identical retries race,
    // the loser of the insert reads the winner and returns the same 200 result.
    const raced = await findDuplicate(
      env.DB,
      player.id,
      submission.fingerprint,
    );
    if (raced) return duplicateMatchResponse(env.DB, player, submission, payloadHash, raced);

    const racedReplayLimit = await replayLimitStatus(
      env.DB,
      player.current_season,
      player.id,
      submission.job,
      submission.completedAtUtc,
      submission.fingerprint,
    );
    if (
      racedReplayLimit.history_count >= LATE_MATCH_REPLAY_LIMIT &&
      racedReplayLimit.has_later_match === 1
    ) {
      throw lateMatchReplayLimitError();
    }

    const limit = await matchLimitStatus(
      env.DB,
      player.current_season,
      player.id,
      submission.job,
      now,
    );
    if (limit.daily_count >= DAILY_JOB_MATCH_LIMIT) {
      throw new HttpError(429, "Daily match limit for this job has been reached.", {
        "Retry-After": secondsUntilNextUtcDay(now).toString(),
      });
    }
    if (limit.season_count >= SEASON_JOB_MATCH_LIMIT) {
      // This limit cannot clear until the next season. Treat it as a permanent
      // conflict so ordered plugin outboxes can discard this submission and
      // continue with later matches instead of retrying the head forever.
      throw new HttpError(409, "Season match limit for this job has been reached.");
    }
    throw error;
  }
}

async function duplicateMatchResponse(
  db: D1Database,
  player: AuthenticatedPlayerRow,
  submission: MatchSubmission,
  payloadHash: string,
  duplicate: ExistingMatchRow,
): Promise<Response> {
  if (duplicate.payload_hash !== payloadHash) {
    throw new HttpError(409, "This fingerprint was already submitted with different match data.");
  }
  if (duplicate.season_id !== player.current_season) {
    throw new HttpError(409, "This fingerprint was already submitted in another leaderboard season.");
  }

  const rating = await db
    .prepare(SELECT_RATING_SQL)
    .bind(player.current_season, player.id, submission.job)
    .first<RatingDbRow>();
  if (!rating) throw new Error("An accepted match has no materialized rating.");
  return jsonResponse(toRatingState(submission.job, rating));
}

async function leaderboard(request: Request, url: URL, env: Env): Promise<Response> {
  await enforceRateLimit(env.READ_RATE_LIMITER, await anonymousRateKey(request, "leaderboard"));
  const job = parseJobQuery(url.searchParams.get("job"));
  const limit = parseLeaderboardLimit(url.searchParams.get("limit"));
  const result = await env.DB
    .prepare(
      `SELECT p.display_name, r.rating, r.matches, r.wins, r.losses
       FROM ratings r
       JOIN players p ON p.id = r.player_id
       JOIN app_settings settings ON settings.id = 1 AND settings.current_season = r.season_id
       WHERE r.job = ?1 AND r.matches > 0
       ORDER BY r.rating DESC, r.matches DESC, p.display_name COLLATE NOCASE, p.id
       LIMIT ?2`,
    )
    .bind(job, limit)
    .all<LeaderboardDbRow>();

  const rows = result.results.map((row, index) => ({
    rank: index + 1,
    displayName: row.display_name,
    job,
    rating: row.rating,
    matches: row.matches,
    wins: row.wins,
    losses: row.losses,
    winRate: row.matches === 0 ? 0 : row.wins / row.matches,
  }));
  return jsonResponse(rows, 200, { "Cache-Control": "public, max-age=15" });
}

async function deletePlayer(request: Request, env: Env): Promise<Response> {
  await enforceRateLimit(env.AUTH_RATE_LIMITER, await anonymousRateKey(request, "auth"));
  const apiKey = requireApiKey(request);
  const apiKeyHash = await sha256Hex(apiKey);
  const player = await authenticate(env.DB, apiKeyHash);
  if (!player) throw new HttpError(401, "A valid API key is required.");
  await enforceRateLimit(env.WRITE_RATE_LIMITER, `account:${apiKeyHash}`);

  await env.DB
    .prepare("DELETE FROM players WHERE id = ?1 AND api_key_hash = ?2")
    .bind(player.id, apiKeyHash)
    .run();
  return emptyResponse(204);
}

async function authenticate(db: D1Database, apiKeyHash: string): Promise<AuthenticatedPlayerRow | null> {
  return db
    .prepare(
      `SELECT players.id, app_settings.current_season
       FROM players CROSS JOIN app_settings
       WHERE players.api_key_hash = ?1 AND app_settings.id = 1`,
    )
    .bind(apiKeyHash)
    .first<AuthenticatedPlayerRow>();
}

async function findDuplicate(
  db: D1Database,
  playerId: string,
  fingerprint: string,
): Promise<ExistingMatchRow | null> {
  return db
    .prepare(
      `SELECT payload_hash, season_id FROM matches
       WHERE player_id = ?1 AND fingerprint = ?2`,
    )
    .bind(playerId, fingerprint)
    .first<ExistingMatchRow>();
}

async function matchLimitStatus(
  db: D1Database,
  season: number,
  playerId: string,
  job: CombatJob,
  receivedAtUtc: string,
): Promise<MatchLimitRow> {
  const day = receivedAtUtc.slice(0, 10);
  return (
    (await db
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM matches
            WHERE season_id = ?1 AND player_id = ?2 AND job = ?3) AS season_count,
           (SELECT COUNT(*) FROM matches
            WHERE player_id = ?2 AND job = ?3
              AND received_at_utc >= (?4 || 'T00:00:00.000Z')
              AND received_at_utc < (date(?4, '+1 day') || 'T00:00:00.000Z')) AS daily_count`,
      )
      .bind(season, playerId, job, day)
      .first<MatchLimitRow>()) ?? { season_count: 0, daily_count: 0 }
  );
}

async function replayLimitStatus(
  db: D1Database,
  season: number,
  playerId: string,
  job: CombatJob,
  completedAtUtc: string,
  fingerprint: string,
): Promise<ReplayLimitRow> {
  return (
    (await db
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM (
              SELECT 1
              FROM matches history
              WHERE history.season_id = ?1
                AND history.player_id = ?2
                AND history.job = ?3
              LIMIT ?6
            )) AS history_count,
           EXISTS(
             SELECT 1
             FROM matches later
             WHERE later.season_id = ?1
               AND later.player_id = ?2
               AND later.job = ?3
               AND (
                 later.completed_at_utc > ?4 COLLATE BINARY OR
                 (later.completed_at_utc = ?4 COLLATE BINARY AND
                  later.fingerprint > ?5 COLLATE BINARY)
               )
             LIMIT 1
           ) AS has_later_match`,
      )
      .bind(season, playerId, job, completedAtUtc, fingerprint, LATE_MATCH_REPLAY_LIMIT)
      .first<ReplayLimitRow>()) ?? { history_count: 0, has_later_match: 0 }
  );
}

async function registrationLimitStatus(
  db: D1Database,
  createdAtUtc: string,
): Promise<RegistrationLimitRow> {
  const day = createdAtUtc.slice(0, 10);
  return (
    (await db
      .prepare(
        `SELECT registration_count AS daily_count
         FROM daily_registration_counts
         WHERE day_utc = ?1`,
      )
      .bind(day)
      .first<RegistrationLimitRow>()) ?? { daily_count: 0 }
  );
}

function lateMatchReplayLimitError(): HttpError {
  return new HttpError(
    409,
    `Late matches are accepted only before this job reaches ${LATE_MATCH_REPLAY_LIMIT} season matches.`,
  );
}

function registrationLimitError(now: string): HttpError {
  return new HttpError(429, "The leaderboard's daily registration capacity has been reached.", {
    "Retry-After": secondsUntilNextUtcDay(now).toString(),
  });
}

function toRatingState(job: CombatJob, rating: RatingDbRow) {
  return {
    job,
    rating: rating.rating,
    matches: rating.matches,
    wins: rating.wins,
    losses: rating.losses,
    winRate: rating.matches === 0 ? 0 : rating.wins / rating.matches,
  };
}

async function readJson(request: Request): Promise<unknown> {
  const contentType = request.headers.get("content-type")?.toLowerCase() ?? "";
  if (!contentType.startsWith("application/json")) {
    throw new HttpError(415, "Content-Type must be application/json.");
  }

  const contentLength = request.headers.get("content-length");
  const declaredLength = contentLength === null ? null : Number(contentLength);
  if (declaredLength !== null && Number.isFinite(declaredLength) && declaredLength > MAX_JSON_BODY_BYTES) {
    throw new HttpError(413, "JSON request body is too large.");
  }

  if (!request.body) throw new InputError("Request body must contain valid JSON.");
  const reader = request.body.getReader();
  const decoder = new TextDecoder("utf-8", { fatal: true });
  let body = "";
  let receivedBytes = 0;
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      receivedBytes += chunk.value.byteLength;
      if (receivedBytes > MAX_JSON_BODY_BYTES) {
        await reader.cancel("JSON request body is too large.");
        throw new HttpError(413, "JSON request body is too large.");
      }
      body += decoder.decode(chunk.value, { stream: true });
    }
    body += decoder.decode();
  } catch (error) {
    if (error instanceof HttpError) throw error;
    throw new InputError("Request body must contain valid UTF-8 JSON.");
  }

  try {
    return JSON.parse(body) as unknown;
  } catch {
    throw new InputError("Request body must contain valid JSON.");
  }
}

function requireObject(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new InputError("Request body must contain a JSON object.");
  }
  return value as Record<string, unknown>;
}

function requireApiKey(request: Request): string {
  const apiKey = request.headers.get("X-Api-Key") ?? "";
  if (!API_KEY_PATTERN.test(apiKey)) {
    throw new HttpError(401, "A valid API key is required.");
  }
  return apiKey;
}

async function enforceRateLimit(limiter: RateLimit, key: string): Promise<void> {
  const result = await limiter.limit({ key });
  if (!result.success) throw new HttpError(429, "Too many requests.", { "Retry-After": "60" });
}

async function anonymousRateKey(request: Request, scope: string): Promise<string> {
  const address = request.headers.get("CF-Connecting-IP") ?? "unknown";
  return `${scope}:${await sha256Hex(address)}`;
}

function secondsUntilNextUtcDay(utcTimestamp: string): number {
  const current = new Date(utcTimestamp);
  const nextDay = Date.UTC(
    current.getUTCFullYear(),
    current.getUTCMonth(),
    current.getUTCDate() + 1,
  );
  return Math.max(1, Math.ceil((nextDay - current.getTime()) / 1000));
}

export async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

function generateApiKey(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function resultRow<T>(result: D1Result<unknown> | undefined): T | null {
  return (result?.results[0] as T | undefined) ?? null;
}

function normalizePath(path: string): string {
  if (path.length > 1 && path.endsWith("/")) return path.slice(0, -1);
  return path;
}

function methodNotAllowed(allowed: string): Response {
  return errorResponse(405, "Method not allowed.", { Allow: allowed });
}

function jsonResponse(
  body: unknown,
  status = 200,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: responseHeaders({ "Content-Type": "application/json; charset=utf-8", ...headers }),
  });
}

function errorResponse(status: number, message: string, headers: Record<string, string> = {}): Response {
  return jsonResponse({ error: message }, status, { "Cache-Control": "no-store", ...headers });
}

function emptyResponse(status: number): Response {
  return new Response(null, { status, headers: responseHeaders({ "Cache-Control": "no-store" }) });
}

function responseHeaders(extra: Record<string, string>): Headers {
  return new Headers({
    "Cache-Control": "no-store",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
    ...extra,
  });
}

class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly headers: Record<string, string> = {},
  ) {
    super(message);
    this.name = "HttpError";
  }
}
