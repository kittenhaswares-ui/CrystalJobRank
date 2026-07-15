export const RATING_RULES_VERSION = 3;
export const INITIAL_RATING = 1500;
export const BASELINE_RATING = 1500;
export const RATING_SCALE = 2000;
export const PROVISIONAL_MATCHES = 10;
export const PROVISIONAL_K = 64;
export const ESTABLISHED_K = 32;
export const MINIMUM_RATING = 0;
export const MAXIMUM_RATING = 3000;

export const JOBS = {
  PLD: 1,
  WAR: 2,
  DRK: 3,
  GNB: 4,
  WHM: 5,
  SCH: 6,
  AST: 7,
  SGE: 8,
  MNK: 9,
  DRG: 10,
  NIN: 11,
  SAM: 12,
  RPR: 13,
  VPR: 14,
  BRD: 15,
  MCH: 16,
  DNC: 17,
  BLM: 18,
  SMN: 19,
  RDM: 20,
  PCT: 21,
} as const;

export type CombatJob = (typeof JOBS)[keyof typeof JOBS];
export type MatchOutcome = 0 | 1;
export type RatedQueue = 1 | 2;

export interface ScoreboardStats {
  kills: number;
  deaths: number;
  assists: number;
  damageDealt: number;
  damageTaken: number;
  hpRestored: number;
  timeOnCrystalSeconds: number;
}

export interface MatchSubmission {
  fingerprint: string;
  completedAtUtc: string;
  job: CombatJob;
  outcome: MatchOutcome;
  queue: RatedQueue;
  territoryId: number;
  durationSeconds: number;
  stats: ScoreboardStats;
}

export interface RatingState {
  job: CombatJob;
  rating: number;
  matches: number;
  wins: number;
  losses: number;
}

export interface RatingEvent {
  completedAtUtc: string;
  fingerprint: string;
  outcome: MatchOutcome;
}

export class InputError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "InputError";
  }
}

const DISPLAY_NAME_PATTERN = /^[\p{L}\p{N} _.'-]+$/u;
const UTC_TIMESTAMP_PATTERN = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?Z$/;
const VALID_JOB_IDS = new Set<number>(Object.values(JOBS));

export function normalizeDisplayName(value: unknown): string {
  if (typeof value !== "string") {
    throw new InputError("Display name must be a string.");
  }

  const normalized = value.trim().normalize("NFC");
  if (
    normalized.length < 2 ||
    normalized.length > 24 ||
    !DISPLAY_NAME_PATTERN.test(normalized)
  ) {
    throw new InputError("Display name must contain 2-24 letters, numbers, spaces, or ._'-.");
  }

  return normalized;
}

export function displayNameKey(displayName: string): string {
  return displayName.normalize("NFC").toLocaleLowerCase("en-US");
}

export function parseJobQuery(value: string | null): CombatJob {
  if (!value) throw new InputError("A valid combat job is required.");
  const job = JOBS[value.toUpperCase() as keyof typeof JOBS];
  if (!job) throw new InputError("A valid combat job is required.");
  return job;
}

export function parseLeaderboardLimit(value: string | null): number {
  if (value === null || value === "") return 50;
  if (!/^\d+$/.test(value)) throw new InputError("Leaderboard limit must be an integer.");
  return Math.min(100, Math.max(1, Number(value)));
}

export function parseMatchSubmission(value: unknown, now = new Date()): MatchSubmission {
  const input = requireObject(value, "Match submission is required.");
  const fingerprint = requireString(
    input.fingerprint,
    "Fingerprint must contain exactly 32 lowercase hexadecimal characters.",
  );
  if (!/^[0-9a-f]{32}$/.test(fingerprint)) {
    throw new InputError("Fingerprint must contain exactly 32 lowercase hexadecimal characters.");
  }

  const { normalized: completedAtUtc, milliseconds } = normalizeUtcTimestamp(input.completedAtUtc);
  const oldest = now.getTime() - 90 * 24 * 60 * 60 * 1000;
  const newest = now.getTime() + 10 * 60 * 1000;
  if (milliseconds < oldest || milliseconds > newest) {
    throw new InputError("Match timestamp is outside the accepted window.");
  }

  const job = requireInteger(input.job, "Job is required.");
  if (!VALID_JOB_IDS.has(job)) throw new InputError("Job is required.");

  const outcome = requireInteger(input.outcome, "A valid match outcome is required.");
  if (outcome !== 0 && outcome !== 1) {
    throw new InputError("A valid match outcome is required.");
  }

  const queue = requireInteger(input.queue, "A valid match queue is required.");
  if (queue !== 1 && queue !== 2) {
    throw new InputError("Only Casual and Ranked matches can affect the community leaderboard.");
  }

  const territoryId = integerInRange(input.territoryId, 0, 65_535, "Territory ID is outside the accepted range.");
  const durationSeconds = integerInRange(
    input.durationSeconds,
    10,
    1800,
    "Match duration is outside the accepted range.",
  );
  const statsInput = requireObject(input.stats, "Scoreboard values are outside the accepted range.");
  const stats: ScoreboardStats = {
    kills: integerInRange(statsInput.kills, 0, 100, "Scoreboard values are outside the accepted range."),
    deaths: integerInRange(statsInput.deaths, 0, 100, "Scoreboard values are outside the accepted range."),
    assists: integerInRange(statsInput.assists, 0, 100, "Scoreboard values are outside the accepted range."),
    damageDealt: integerInRange(
      statsInput.damageDealt,
      0,
      20_000_000,
      "Scoreboard values are outside the accepted range.",
    ),
    damageTaken: integerInRange(
      statsInput.damageTaken,
      0,
      20_000_000,
      "Scoreboard values are outside the accepted range.",
    ),
    hpRestored: integerInRange(
      statsInput.hpRestored,
      0,
      20_000_000,
      "Scoreboard values are outside the accepted range.",
    ),
    timeOnCrystalSeconds: integerInRange(
      statsInput.timeOnCrystalSeconds,
      0,
      1800,
      "Scoreboard values are outside the accepted range.",
    ),
  };

  return {
    fingerprint,
    completedAtUtc,
    job: job as CombatJob,
    outcome,
    queue,
    territoryId,
    durationSeconds,
    stats,
  };
}

export function applyRating(state: RatingState, outcome: MatchOutcome): RatingState {
  if (!VALID_JOB_IDS.has(state.job)) throw new InputError("A rating cannot be calculated for an invalid job.");
  if (outcome !== 0 && outcome !== 1) throw new InputError("A valid match outcome is required.");

  const expected = 1 / (1 + 10 ** ((BASELINE_RATING - state.rating) / RATING_SCALE));
  const k = state.matches < PROVISIONAL_MATCHES ? PROVISIONAL_K : ESTABLISHED_K;
  let delta = roundAwayFromZero(k * (outcome - expected));
  if (delta === 0) delta = outcome === 1 ? 1 : -1;

  const rating = Math.min(MAXIMUM_RATING, Math.max(MINIMUM_RATING, state.rating + delta));
  return {
    job: state.job,
    rating,
    matches: state.matches + 1,
    wins: state.wins + (outcome === 1 ? 1 : 0),
    losses: state.losses + (outcome === 0 ? 1 : 0),
  };
}

export function replayRating(job: CombatJob, outcomes: readonly MatchOutcome[]): RatingState {
  let state: RatingState = { job, rating: INITIAL_RATING, matches: 0, wins: 0, losses: 0 };
  for (const outcome of outcomes) state = applyRating(state, outcome);
  return state;
}

export function replayRatingEvents(job: CombatJob, events: readonly RatingEvent[]): RatingState {
  const ordered = [...events].sort(
    (left, right) =>
      left.completedAtUtc.localeCompare(right.completedAtUtc) ||
      compareOrdinal(left.fingerprint, right.fingerprint),
  );
  return replayRating(job, ordered.map((event) => event.outcome));
}

export function canonicalSubmission(submission: MatchSubmission): string {
  const stats = submission.stats;
  return JSON.stringify([
    submission.fingerprint,
    submission.completedAtUtc,
    submission.job,
    submission.outcome,
    submission.queue,
    submission.territoryId,
    submission.durationSeconds,
    stats.kills,
    stats.deaths,
    stats.assists,
    stats.damageDealt,
    stats.damageTaken,
    stats.hpRestored,
    stats.timeOnCrystalSeconds,
  ]);
}

function normalizeUtcTimestamp(value: unknown): { normalized: string; milliseconds: number } {
  if (typeof value !== "string") throw new InputError("CompletedAtUtc must be UTC.");
  const match = UTC_TIMESTAMP_PATTERN.exec(value);
  if (!match?.[1]) throw new InputError("CompletedAtUtc must be UTC.");

  const fraction = match[2] ?? "";
  const parseable = `${match[1]}.${fraction.slice(0, 3).padEnd(3, "0")}Z`;
  const milliseconds = Date.parse(parseable);
  if (!Number.isFinite(milliseconds) || new Date(milliseconds).toISOString().slice(0, 19) !== match[1]) {
    throw new InputError("CompletedAtUtc must be a valid UTC timestamp.");
  }

  return {
    normalized: `${match[1]}.${fraction.padEnd(7, "0")}Z`,
    milliseconds,
  };
}

function roundAwayFromZero(value: number): number {
  return value < 0 ? -Math.round(-value) : Math.round(value);
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function requireObject(value: unknown, message: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new InputError(message);
  return value as Record<string, unknown>;
}

function requireString(value: unknown, message: string): string {
  if (typeof value !== "string") throw new InputError(message);
  return value;
}

function requireInteger(value: unknown, message: string): number {
  if (typeof value !== "number" || !Number.isInteger(value)) throw new InputError(message);
  return value;
}

function integerInRange(value: unknown, minimum: number, maximum: number, message: string): number {
  const result = requireInteger(value, message);
  if (result < minimum || result > maximum) throw new InputError(message);
  return result;
}
