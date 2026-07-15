import {
  canonicalSubmission,
  characterIdentityKey,
  type CombatJob,
  InputError,
  type MatchSubmission,
  parseJobQuery,
  parseLeaderboardLimit,
  parseMatchSubmission,
  PROVISIONAL_MATCHES,
  RATING_RULES_VERSION,
} from "./domain";

interface SettingsRow {
  schema_version: number;
  rating_rules_version: number;
  current_season: number;
  season_started_at_utc: string;
  season_ended_at_utc: string | null;
}

interface ExistingMatchRow {
  payload_hash: string;
}

interface RatingDbRow {
  rating: number;
  matches: number;
  wins: number;
  losses: number;
}

interface LeaderboardDbRow extends RatingDbRow {
  character_name: string;
  world_name: string;
}

interface MatchLimitRow {
  season_count: number;
  daily_count: number;
}

const MAX_JSON_BODY_BYTES = 16 * 1024;
const INSTALLATION_KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/;
const EXPECTED_SCHEMA_VERSION = 4;
const DAILY_CHARACTER_JOB_MATCH_LIMIT = 100;
const SEASON_CHARACTER_JOB_MATCH_LIMIT = 5_000;
const HEALTH_CACHE_TTL_SECONDS = 15;
const HEALTH_CACHE_NAME = "crystal-job-rank-health-v4";

const UPSERT_CHARACTER_SQL = `
  INSERT INTO characters (
    identity_key, character_name, world_id, world_name,
    created_at_utc, updated_at_utc
  ) VALUES (?1, ?2, ?3, ?4, ?5, ?5)
  ON CONFLICT(identity_key) DO UPDATE SET
    character_name = excluded.character_name,
    world_name = excluded.world_name,
    updated_at_utc = excluded.updated_at_utc
`;

const INSERT_MATCH_SQL = `
  INSERT INTO matches (
    season_id, identity_key, match_key, payload_hash,
    completed_at_utc, received_at_utc, job, outcome, queue
  ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
`;

// Rules v4 is based only on aggregate wins and losses. Each accepted event is
// therefore an O(1) counter update and concurrent/out-of-order delivery cannot
// change the result.
const UPSERT_RATING_SQL = `
  INSERT INTO ratings (
    season_id, identity_key, job, rating,
    matches, wins, losses, updated_at_utc
  ) VALUES (
    ?1, ?2, ?3,
    1500 + CAST(round(1000.0 * (?4 - ?5) / 41.0, 0) AS INTEGER),
    1, ?4, ?5, ?6
  )
  ON CONFLICT(season_id, identity_key, job) DO UPDATE SET
    rating = 1500 + CAST(round(
      1000.0 * (
        (ratings.wins + excluded.wins) -
        (ratings.losses + excluded.losses)
      ) / (ratings.matches + 41.0),
      0
    ) AS INTEGER),
    matches = ratings.matches + 1,
    wins = ratings.wins + excluded.wins,
    losses = ratings.losses + excluded.losses,
    updated_at_utc = excluded.updated_at_utc
`;

const SELECT_RATING_SQL = `
  SELECT rating, matches, wins, losses
  FROM ratings
  WHERE season_id = ?1 AND identity_key = ?2 AND job = ?3
`;

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const requestId = crypto.randomUUID();
    try {
      return await route(request, env, ctx);
    } catch (error) {
      if (error instanceof HttpError) return errorResponse(error.status, error.message, error.headers);
      if (error instanceof InputError) return errorResponse(400, error.message);

      console.error(JSON.stringify({
        event: "leaderboard_request_failed",
        requestId,
        method: request.method,
        path: new URL(request.url).pathname,
        error: error instanceof Error ? error.name : "UnknownError",
      }));
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

  if (path === "/v2/matches") {
    if (request.method !== "POST") return methodNotAllowed("POST");
    return submitMatch(request, env);
  }

  if (path === "/v1/leaderboard") {
    if (request.method !== "GET") return methodNotAllowed("GET");
    return leaderboard(request, url, env);
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
    const settings = await readSettings(env.DB);
    if (!settingsAreCurrent(settings)) {
      return errorResponse(503, "Leaderboard database configuration is invalid.");
    }

    const response = jsonResponse(
      {
        status: "ok",
        schemaVersion: settings.schema_version,
        ratingRulesVersion: settings.rating_rules_version,
        season: settings.current_season,
      },
      200,
      { "Cache-Control": `public, max-age=${HEALTH_CACHE_TTL_SECONDS}` },
    );
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

async function submitMatch(request: Request, env: Env): Promise<Response> {
  await enforceRateLimit(env.AUTH_RATE_LIMITER, await anonymousRateKey(request, "write"));
  const installationKey = requireInstallationKey(request);
  const installationHash = await sha256Hex(installationKey);
  await enforceRateLimit(env.WRITE_RATE_LIMITER, `installation:${installationHash}`);

  const submission = parseMatchSubmission(await readJson(request));
  const identityKey = characterIdentityKey(submission.characterName, submission.worldId);
  const payloadHash = await sha256Hex(canonicalSubmission(submission));
  const settings = await requireCurrentSettings(env.DB);
  if (Date.parse(submission.completedAtUtc) < Date.parse(settings.season_started_at_utc)) {
    throw new HttpError(409, "This match was completed before the current community season started.");
  }

  const duplicate = await findDuplicate(
    env.DB,
    settings.current_season,
    identityKey,
    submission.matchKey,
  );
  if (duplicate) {
    return duplicateMatchResponse(
      env.DB,
      settings.current_season,
      identityKey,
      submission,
      payloadHash,
      duplicate,
    );
  }

  const now = new Date().toISOString();
  const wins = submission.outcome === 1 ? 1 : 0;
  const losses = submission.outcome === 0 ? 1 : 0;
  try {
    const batch = await env.DB.batch([
      env.DB
        .prepare(UPSERT_CHARACTER_SQL)
        .bind(
          identityKey,
          submission.characterName,
          submission.worldId,
          submission.worldName,
          now,
        ),
      env.DB
        .prepare(INSERT_MATCH_SQL)
        .bind(
          settings.current_season,
          identityKey,
          submission.matchKey,
          payloadHash,
          submission.completedAtUtc,
          now,
          submission.job,
          submission.outcome,
          submission.queue,
        ),
      env.DB
        .prepare(UPSERT_RATING_SQL)
        .bind(
          settings.current_season,
          identityKey,
          submission.job,
          wins,
          losses,
          now,
        ),
      env.DB
        .prepare(SELECT_RATING_SQL)
        .bind(settings.current_season, identityKey, submission.job),
    ]);

    const rating = resultRow<RatingDbRow>(batch[3]);
    if (!rating) throw new Error("Rating materialization returned no row.");
    return jsonResponse(toRatingState(submission.job, rating));
  } catch (error) {
    // The unique key is authoritative. Concurrent exact retries return the
    // winner's current rating, while a reused key with different data is a
    // permanent conflict.
    const raced = await findDuplicate(
      env.DB,
      settings.current_season,
      identityKey,
      submission.matchKey,
    );
    if (raced) {
      return duplicateMatchResponse(
        env.DB,
        settings.current_season,
        identityKey,
        submission,
        payloadHash,
        raced,
      );
    }

    if (databaseErrorContains(error, "match_outside_current_season")) {
      throw new HttpError(409, "The community season changed before this match could be stored.");
    }

    const limit = await matchLimitStatus(
      env.DB,
      settings.current_season,
      identityKey,
      submission.job,
      now,
    );
    if (limit.daily_count >= DAILY_CHARACTER_JOB_MATCH_LIMIT) {
      throw new HttpError(429, "Daily match limit for this character and job has been reached.", {
        "Retry-After": secondsUntilNextUtcDay(now).toString(),
      });
    }
    if (limit.season_count >= SEASON_CHARACTER_JOB_MATCH_LIMIT) {
      throw new HttpError(409, "Season match limit for this character and job has been reached.");
    }
    throw error;
  }
}

async function duplicateMatchResponse(
  db: D1Database,
  season: number,
  identityKey: string,
  submission: MatchSubmission,
  payloadHash: string,
  duplicate: ExistingMatchRow,
): Promise<Response> {
  if (duplicate.payload_hash !== payloadHash) {
    throw new HttpError(409, "This match key was already submitted with different match data.");
  }

  const rating = await db
    .prepare(SELECT_RATING_SQL)
    .bind(season, identityKey, submission.job)
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
      `SELECT
         c.character_name, c.world_name,
         r.rating, r.matches, r.wins, r.losses
       FROM ratings r
       JOIN characters c ON c.identity_key = r.identity_key
       JOIN app_settings settings ON settings.id = 1 AND settings.current_season = r.season_id
       WHERE r.job = ?1 AND r.matches > 0
       ORDER BY
         CASE WHEN r.matches >= ?2 THEN 0 ELSE 1 END,
         CASE WHEN r.matches >= ?2 THEN r.rating END DESC,
         r.matches DESC,
         r.rating DESC,
         c.character_name COLLATE NOCASE,
         c.world_name COLLATE NOCASE,
         c.world_id,
         c.identity_key COLLATE BINARY
       LIMIT ?3`,
    )
    .bind(job, PROVISIONAL_MATCHES, limit)
    .all<LeaderboardDbRow>();

  let establishedRank = 0;
  const rows = result.results.map((row) => {
    const isProvisional = row.matches < PROVISIONAL_MATCHES;
    if (!isProvisional) establishedRank += 1;
    return {
      rank: isProvisional ? 0 : establishedRank,
      characterName: row.character_name,
      worldName: row.world_name,
      job,
      rating: row.rating,
      matches: row.matches,
      wins: row.wins,
      losses: row.losses,
      winRate: row.matches === 0 ? 0 : row.wins / row.matches,
      isProvisional,
    };
  });
  return jsonResponse(rows, 200, { "Cache-Control": "public, max-age=15" });
}

async function readSettings(db: D1Database): Promise<SettingsRow | null> {
  return db
    .prepare(
       `SELECT settings.schema_version, settings.rating_rules_version, settings.current_season
             , active_season.started_at_utc AS season_started_at_utc
             , active_season.ended_at_utc AS season_ended_at_utc
        FROM app_settings settings
       JOIN seasons active_season ON active_season.id = settings.current_season
       WHERE settings.id = 1`,
    )
    .first<SettingsRow>();
}

function settingsAreCurrent(settings: SettingsRow | null): settings is SettingsRow {
  return Boolean(
    settings &&
    settings.schema_version === EXPECTED_SCHEMA_VERSION &&
    settings.rating_rules_version === RATING_RULES_VERSION &&
    settings.current_season >= 2 &&
    settings.season_ended_at_utc === null &&
    Number.isFinite(Date.parse(settings.season_started_at_utc)),
  );
}

async function requireCurrentSettings(db: D1Database): Promise<SettingsRow> {
  const settings = await readSettings(db);
  if (!settingsAreCurrent(settings)) {
    throw new HttpError(503, "Leaderboard database configuration is invalid.");
  }
  return settings;
}

async function findDuplicate(
  db: D1Database,
  season: number,
  identityKey: string,
  matchKey: string,
): Promise<ExistingMatchRow | null> {
  return db
    .prepare(
      `SELECT payload_hash FROM matches
       WHERE season_id = ?1 AND identity_key = ?2 AND match_key = ?3`,
    )
    .bind(season, identityKey, matchKey)
    .first<ExistingMatchRow>();
}

async function matchLimitStatus(
  db: D1Database,
  season: number,
  identityKey: string,
  job: CombatJob,
  receivedAtUtc: string,
): Promise<MatchLimitRow> {
  const day = receivedAtUtc.slice(0, 10);
  return (
    (await db
      .prepare(
        `SELECT
           (SELECT COUNT(*) FROM matches
            WHERE season_id = ?1 AND identity_key = ?2 AND job = ?3) AS season_count,
           (SELECT COUNT(*) FROM matches
            WHERE identity_key = ?2 AND job = ?3
              AND received_at_utc >= (?4 || 'T00:00:00.000Z')
              AND received_at_utc < (date(?4, '+1 day') || 'T00:00:00.000Z')) AS daily_count`,
      )
      .bind(season, identityKey, job, day)
      .first<MatchLimitRow>()) ?? { season_count: 0, daily_count: 0 }
  );
}

function toRatingState(job: CombatJob, rating: RatingDbRow) {
  return {
    job,
    rating: rating.rating,
    matches: rating.matches,
    wins: rating.wins,
    losses: rating.losses,
    winRate: rating.matches === 0 ? 0 : rating.wins / rating.matches,
    isProvisional: rating.matches < PROVISIONAL_MATCHES,
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

function requireInstallationKey(request: Request): string {
  const installationKey = request.headers.get("X-Installation-Key") ?? "";
  if (!INSTALLATION_KEY_PATTERN.test(installationKey)) {
    throw new HttpError(401, "A valid installation key is required.");
  }
  return installationKey;
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

function resultRow<T>(result: D1Result<unknown> | undefined): T | null {
  return (result?.results[0] as T | undefined) ?? null;
}

function databaseErrorContains(error: unknown, marker: string): boolean {
  return error instanceof Error && error.message.includes(marker);
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
