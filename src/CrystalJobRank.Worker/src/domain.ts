export const RATING_RULES_VERSION = 4;
export const INITIAL_RATING = 1500;
export const RATING_PRIOR_MATCHES = 40;
export const RATING_SCALE = 1000;
export const PROVISIONAL_MATCHES = 10;
export const MINIMUM_RATING = 500;
export const MAXIMUM_RATING = 2500;

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
  matchKey: string;
  completedAtUtc: string;
  characterName: string;
  worldId: number;
  worldName: string;
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

export class InputError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "InputError";
  }
}

const CONTROL_CHARACTER_PATTERN = /\p{Cc}/u;
const CHARACTER_NAME_PATTERN = /^[\p{L}\p{M}'-]+ [\p{L}\p{M}'-]+$/u;
const UTC_TIMESTAMP_PATTERN = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?Z$/;
const VALID_JOB_IDS = new Set<number>(Object.values(JOBS));

// Canonical global-service Home Worlds from the game's World sheet. The API
// derives public presentation data from the numeric row ID and never trusts a
// caller-provided World label. Historical travel-only Worlds and separately
// operated regional editions are intentionally excluded.
const WORLD_NAMES_BY_ID: Readonly<Record<number, string>> = {
  // Materia
  21: "Ravana",
  22: "Bismarck",
  86: "Sephirot",
  87: "Sophia",
  88: "Zurvan",
  // Mana
  23: "Asura",
  28: "Pandaemonium",
  44: "Anima",
  47: "Hades",
  48: "Ixion",
  61: "Titan",
  70: "Chocobo",
  96: "Masamune",
  // Meteor
  24: "Belias",
  29: "Shinryu",
  30: "Unicorn",
  31: "Yojimbo",
  32: "Zeromus",
  52: "Valefor",
  60: "Ramuh",
  82: "Mandragora",
  // Light
  33: "Twintania",
  36: "Lich",
  42: "Zodiark",
  56: "Phoenix",
  66: "Odin",
  67: "Shiva",
  402: "Alpha",
  403: "Raiden",
  // Crystal
  34: "Brynhildr",
  37: "Mateus",
  41: "Zalera",
  62: "Diabolos",
  74: "Coeurl",
  75: "Malboro",
  81: "Goblin",
  91: "Balmung",
  // Primal
  35: "Famfrit",
  53: "Exodus",
  55: "Lamia",
  64: "Leviathan",
  77: "Ultros",
  78: "Behemoth",
  93: "Excalibur",
  95: "Hyperion",
  // Chaos
  39: "Omega",
  71: "Moogle",
  80: "Cerberus",
  83: "Louisoix",
  85: "Spriggan",
  97: "Ragnarok",
  400: "Sagittarius",
  401: "Phantom",
  // Aether
  40: "Jenova",
  54: "Faerie",
  57: "Siren",
  63: "Gilgamesh",
  65: "Midgardsormr",
  73: "Adamantoise",
  79: "Cactuar",
  99: "Sargatanas",
  // Gaia
  43: "Alexander",
  46: "Fenrir",
  51: "Ultima",
  59: "Ifrit",
  69: "Bahamut",
  76: "Tiamat",
  92: "Durandal",
  98: "Ridill",
  // Elemental
  45: "Carbuncle",
  49: "Kujata",
  50: "Typhon",
  58: "Garuda",
  68: "Atomos",
  72: "Tonberry",
  90: "Aegis",
  94: "Gungnir",
  // Dynamis
  404: "Marilith",
  405: "Seraph",
  406: "Halicarnassus",
  407: "Maduin",
  408: "Cuchulainn",
  409: "Kraken",
  410: "Rafflesia",
  411: "Golem",
};

export function normalizeCharacterName(value: unknown): string {
  if (typeof value !== "string") {
    throw new InputError("Character name must be a string.");
  }
  if (CONTROL_CHARACTER_PATTERN.test(value)) {
    throw new InputError("Character name must not contain control characters.");
  }

  const parts = value.trim().normalize("NFC").split(/ +/u);
  const normalized = parts.join(" ");
  if (
    parts.length !== 2 ||
    normalized.length < 3 ||
    normalized.length > 42 ||
    !CHARACTER_NAME_PATTERN.test(normalized)
  ) {
    throw new InputError(
      "Character name must contain exactly two name parts and 3-42 letters, apostrophes, or hyphens.",
    );
  }
  return normalized;
}

export function parseWorldId(value: unknown): number {
  return integerInRange(value, 1, 65_535, "World ID must be an integer between 1 and 65535.");
}

export function canonicalWorldName(worldId: number): string {
  const worldName = WORLD_NAMES_BY_ID[worldId];
  if (!worldName) {
    throw new InputError("Home World is not supported by this leaderboard version.");
  }
  return worldName;
}

export function characterIdentityKey(characterName: string, worldId: number): string {
  return `${characterName.normalize("NFC").toLocaleLowerCase("en-US")}|${worldId}`;
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
  const matchKey = requireString(
    input.matchKey,
    "Match key must contain exactly 64 lowercase hexadecimal characters.",
  );
  if (!/^[0-9a-f]{64}$/.test(matchKey)) {
    throw new InputError("Match key must contain exactly 64 lowercase hexadecimal characters.");
  }

  const { normalized: completedAtUtc, milliseconds } = normalizeUtcTimestamp(input.completedAtUtc);
  const oldest = now.getTime() - 90 * 24 * 60 * 60 * 1000;
  const newest = now.getTime() + 10 * 60 * 1000;
  if (milliseconds < oldest || milliseconds > newest) {
    throw new InputError("Match timestamp is outside the accepted window.");
  }

  const characterName = normalizeCharacterName(input.characterName);
  const worldId = parseWorldId(input.worldId);
  const worldName = canonicalWorldName(worldId);

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

  const territoryId = integerInRange(
    input.territoryId,
    0,
    65_535,
    "Territory ID is outside the accepted range.",
  );
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
    matchKey,
    completedAtUtc,
    characterName,
    worldId,
    worldName,
    job: job as CombatJob,
    outcome,
    queue,
    territoryId,
    durationSeconds,
    stats,
  };
}

/**
 * Rules v4 is a Beta(20,20) prior expressed as a symmetric linear score.
 * It depends only on the season record, so upload order cannot change it.
 */
export function ratingFromRecord(wins: number, losses: number): number {
  if (!Number.isInteger(wins) || !Number.isInteger(losses) || wins < 0 || losses < 0) {
    throw new InputError("Wins and losses must be non-negative integers.");
  }
  const matches = wins + losses;
  const offset = roundAwayFromZero((RATING_SCALE * (wins - losses)) / (matches + RATING_PRIOR_MATCHES));
  return Math.min(MAXIMUM_RATING, Math.max(MINIMUM_RATING, INITIAL_RATING + offset));
}

export function applyRating(state: RatingState, outcome: MatchOutcome): RatingState {
  if (!VALID_JOB_IDS.has(state.job)) throw new InputError("A rating cannot be calculated for an invalid job.");
  if (outcome !== 0 && outcome !== 1) throw new InputError("A valid match outcome is required.");
  if (state.matches !== state.wins + state.losses) {
    throw new InputError("Rating counters are inconsistent.");
  }

  const wins = state.wins + (outcome === 1 ? 1 : 0);
  const losses = state.losses + (outcome === 0 ? 1 : 0);
  return {
    job: state.job,
    rating: ratingFromRecord(wins, losses),
    matches: wins + losses,
    wins,
    losses,
  };
}

export function replayRating(job: CombatJob, outcomes: readonly MatchOutcome[]): RatingState {
  const wins = outcomes.reduce<number>((total, outcome) => total + (outcome === 1 ? 1 : 0), 0);
  const losses = outcomes.length - wins;
  return { job, rating: ratingFromRecord(wins, losses), matches: outcomes.length, wins, losses };
}

export function canonicalSubmission(submission: MatchSubmission): string {
  const stats = submission.stats;
  return JSON.stringify([
    submission.matchKey,
    submission.completedAtUtc,
    submission.characterName,
    submission.worldId,
    submission.worldName,
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
