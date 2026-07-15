import { describe, expect, it } from "vitest";
import {
  applyRating,
  canonicalSubmission,
  displayNameKey,
  JOBS,
  normalizeDisplayName,
  parseJobQuery,
  parseLeaderboardLimit,
  parseMatchSubmission,
  replayRating,
  replayRatingEvents,
} from "../src/domain";

describe("Rules v3 rating", () => {
  it("matches the C# golden values and provisional boundary", () => {
    expect(replayRating(JOBS.DRK, [1, 0])).toMatchObject({
      rating: 1499,
      matches: 2,
      wins: 1,
      losses: 1,
    });
    expect(replayRating(JOBS.DRK, [0, 1]).rating).toBe(1501);

    const tenth = applyRating({ job: JOBS.SGE, rating: 1500, matches: 9, wins: 5, losses: 4 }, 1);
    const eleventh = applyRating({ job: JOBS.SGE, rating: 1500, matches: 10, wins: 5, losses: 5 }, 1);
    expect(tenth.rating).toBe(1532);
    expect(eleventh.rating).toBe(1516);
  });

  it("replays out-of-order events by UTC timestamp and ordinal fingerprint", () => {
    const replayed = replayRatingEvents(JOBS.DRK, [
      { completedAtUtc: "2026-07-15T10:01:00.0000000Z", fingerprint: "b".repeat(32), outcome: 0 },
      { completedAtUtc: "2026-07-15T10:00:00.0000000Z", fingerprint: "a".repeat(32), outcome: 1 },
    ]);
    expect(replayed.rating).toBe(1499);

    const tied = replayRatingEvents(JOBS.DRK, [
      { completedAtUtc: "2026-07-15T10:00:00.0000000Z", fingerprint: "b".repeat(32), outcome: 1 },
      { completedAtUtc: "2026-07-15T10:00:00.0000000Z", fingerprint: "a".repeat(32), outcome: 0 },
    ]);
    expect(tied.rating).toBe(1501);
  });
});

describe("API input contract", () => {
  it("validates and bounds leaderboard query parameters", () => {
    expect(parseJobQuery("drk")).toBe(JOBS.DRK);
    expect(() => parseJobQuery(null)).toThrow(/valid combat job/);
    expect(() => parseJobQuery("BLU")).toThrow(/valid combat job/);

    expect(parseLeaderboardLimit(null)).toBe(50);
    expect(parseLeaderboardLimit("")).toBe(50);
    expect(parseLeaderboardLimit("0")).toBe(1);
    expect(parseLeaderboardLimit("25")).toBe(25);
    expect(parseLeaderboardLimit("101")).toBe(100);
    expect(() => parseLeaderboardLimit("1.5")).toThrow(/integer/);
    expect(() => parseLeaderboardLimit("-1")).toThrow(/integer/);
  });

  it("normalizes Unicode display names into a stable unique key", () => {
    const composed = normalizeDisplayName("  E\u0301owyn  ");
    expect(composed).toBe("Éowyn");
    expect(displayNameKey(composed)).toBe(displayNameKey("ÉOWYN"));
  });

  it("accepts the plugin JSON shape and canonicalizes UTC precision", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    const submission = parseMatchSubmission(sampleSubmission(), now);
    expect(submission.completedAtUtc).toBe("2026-07-15T10:00:00.1230000Z");
    expect(submission.job).toBe(JOBS.DRK);
  });

  it("rejects custom queues, malformed fingerprints, and implausible stats", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    expect(() => parseMatchSubmission({ ...sampleSubmission(), queue: 3 }, now)).toThrow(/Casual and Ranked/);
    expect(() => parseMatchSubmission({ ...sampleSubmission(), fingerprint: "A".repeat(32) }, now)).toThrow(
      /32 lowercase hexadecimal/,
    );
    expect(() =>
      parseMatchSubmission(
        { ...sampleSubmission(), stats: { ...sampleSubmission().stats, damageDealt: 20_000_001 } },
        now,
      ),
    ).toThrow(/Scoreboard/);
  });

  it("uses every validated field for idempotency conflict detection", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    const first = parseMatchSubmission(sampleSubmission(), now);
    const retry = parseMatchSubmission({ ...sampleSubmission() }, now);
    const conflict = parseMatchSubmission({ ...sampleSubmission(), outcome: 0 }, now);
    expect(canonicalSubmission(retry)).toBe(canonicalSubmission(first));
    expect(canonicalSubmission(conflict)).not.toBe(canonicalSubmission(first));
  });
});

function sampleSubmission() {
  return {
    fingerprint: "a".repeat(32),
    completedAtUtc: "2026-07-15T10:00:00.123Z",
    job: JOBS.DRK,
    outcome: 1,
    queue: 1,
    territoryId: 1032,
    durationSeconds: 300,
    stats: {
      kills: 3,
      deaths: 1,
      assists: 5,
      damageDealt: 750_000,
      damageTaken: 500_000,
      hpRestored: 25_000,
      timeOnCrystalSeconds: 45,
    },
  };
}
