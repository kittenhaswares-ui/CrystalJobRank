import { describe, expect, it } from "vitest";
import {
  applyRating,
  canonicalWorldName,
  canonicalSubmission,
  characterIdentityKey,
  JOBS,
  normalizeCharacterName,
  parseJobQuery,
  parseLeaderboardLimit,
  parseMatchSubmission,
  parseWorldId,
  ratingFromRecord,
  replayRating,
} from "../src/domain";

describe("Rules v4 rating", () => {
  it("uses the Beta(20,20) season record and matches the approved examples", () => {
    expect(ratingFromRecord(0, 0)).toBe(1500);
    expect(ratingFromRecord(1, 0)).toBe(1524);
    expect(ratingFromRecord(6, 4)).toBe(1540);
    expect(ratingFromRecord(9, 1)).toBe(1660);
    expect(ratingFromRecord(10, 0)).toBe(1700);
    expect(ratingFromRecord(5, 5)).toBe(1500);
    expect(ratingFromRecord(0, 10)).toBe(1300);
  });

  it("is order-independent and every result moves the score in its direction", () => {
    const winLoss = replayRating(JOBS.DNC, [1, 0, 1, 0, 1]);
    const reordered = replayRating(JOBS.DNC, [0, 1, 0, 1, 1]);
    expect(reordered).toEqual(winLoss);

    const before = { job: JOBS.DNC, rating: 1500, matches: 10, wins: 5, losses: 5 };
    expect(applyRating(before, 1).rating).toBeGreaterThan(before.rating);
    expect(applyRating(before, 0).rating).toBeLessThan(before.rating);
  });
});

describe("v2 API input contract", () => {
  it("validates and bounds leaderboard query parameters", () => {
    expect(parseJobQuery("dnc")).toBe(JOBS.DNC);
    expect(() => parseJobQuery(null)).toThrow(/valid combat job/);
    expect(() => parseJobQuery("BLU")).toThrow(/valid combat job/);

    expect(parseLeaderboardLimit(null)).toBe(50);
    expect(parseLeaderboardLimit("")).toBe(50);
    expect(parseLeaderboardLimit("0")).toBe(1);
    expect(parseLeaderboardLimit("25")).toBe(25);
    expect(parseLeaderboardLimit("101")).toBe(100);
    expect(() => parseLeaderboardLimit("1.5")).toThrow(/integer/);
  });

  it("normalizes automatic character identity into a stable name-and-World key", () => {
    const characterName = normalizeCharacterName("  E\u0301owyn   Night  ");
    expect(characterName).toBe("Éowyn Night");
    expect(characterIdentityKey(characterName, 21)).toBe(characterIdentityKey("ÉOWYN NIGHT", 21));
    expect(characterIdentityKey(characterName, 21)).not.toBe(characterIdentityKey(characterName, 22));
    expect(canonicalWorldName(21)).toBe("Ravana");
    expect(canonicalWorldName(56)).toBe("Phoenix");
    expect(parseWorldId(21)).toBe(21);
  });

  it("accepts the v2 match shape and canonicalizes UTC precision", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    const submission = parseMatchSubmission(sampleSubmission(), now);
    expect(submission.completedAtUtc).toBe("2026-07-15T10:00:00.1230000Z");
    expect(submission.characterName).toBe("Test Dancer");
    expect(submission.worldName).toBe("Ravana");
    expect(submission.job).toBe(JOBS.DNC);
  });

  it("derives the public World name from World ID and rejects unknown Worlds", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    const spoofed = parseMatchSubmission({ ...sampleSubmission(), worldName: "Definitely Not Ravana" }, now);
    expect(spoofed.worldName).toBe("Ravana");
    expect(() => parseMatchSubmission({ ...sampleSubmission(), worldId: 65_535 }, now)).toThrow(
      /Home World is not supported/,
    );
  });

  it("rejects custom queues, aliases, malformed keys, and implausible stats", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    expect(() => parseMatchSubmission({ ...sampleSubmission(), queue: 3 }, now)).toThrow(/Casual and Ranked/);
    expect(() => parseMatchSubmission({ ...sampleSubmission(), characterName: "Alias" }, now)).toThrow(/two name parts/);
    expect(() => parseMatchSubmission({ ...sampleSubmission(), matchKey: "A".repeat(64) }, now)).toThrow(
      /64 lowercase hexadecimal/,
    );
    expect(() =>
      parseMatchSubmission(
        { ...sampleSubmission(), stats: { ...sampleSubmission().stats, damageDealt: 20_000_001 } },
        now,
      ),
    ).toThrow(/Scoreboard/);
  });

  it("uses identity and every match field for idempotency conflict detection", () => {
    const now = new Date("2026-07-15T10:05:00.000Z");
    const first = parseMatchSubmission(sampleSubmission(), now);
    const retry = parseMatchSubmission({ ...sampleSubmission() }, now);
    const otherCharacter = parseMatchSubmission({ ...sampleSubmission(), characterName: "Other Dancer" }, now);
    const conflict = parseMatchSubmission({ ...sampleSubmission(), outcome: 0 }, now);
    expect(canonicalSubmission(retry)).toBe(canonicalSubmission(first));
    expect(canonicalSubmission(otherCharacter)).not.toBe(canonicalSubmission(first));
    expect(canonicalSubmission(conflict)).not.toBe(canonicalSubmission(first));
  });
});

function sampleSubmission() {
  return {
    matchKey: "a".repeat(64),
    completedAtUtc: "2026-07-15T10:00:00.123Z",
    characterName: "Test Dancer",
    worldId: 21,
    worldName: "Ravana",
    job: JOBS.DNC,
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
