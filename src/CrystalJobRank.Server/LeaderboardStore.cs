using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrystalJobRank.Core;

namespace CrystalJobRank.Server;

/// <summary>
/// A small, file-backed development implementation of the public Worker API.
/// The JSON file is the durable event log; all hot-path lookups and rating
/// totals are indexed in memory.
/// </summary>
public sealed class LeaderboardStore
{
    // This is an internal JSON-file version. The public API/database schema is
    // version 4; version 6 removes installation correlations and persists the
    // authoritative start of the current community season.
    private const int CurrentStoreVersion = 6;
    private const int FirstAutomaticSeason = 2;
    private const int SeasonMatchLimit = 5_000;
    private const int DailyMatchLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly string filePath;
    private readonly ILogger<LeaderboardStore> log;

    private ServerData data;
    private Dictionary<string, CommunityProfile> profilesByIdentity = new(StringComparer.Ordinal);
    private Dictionary<Guid, CommunityProfile> profilesById = [];
    private Dictionary<(Guid ProfileId, int Season, CombatJob Job), Aggregate> aggregates = [];
    private Dictionary<(Guid ProfileId, int Season, string MatchKey), StoredAutomaticMatch> matchesByKey = [];
    private Dictionary<(Guid ProfileId, int Season, CombatJob Job, DateOnly Day), int> dailyCounts = [];

    public LeaderboardStore(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LeaderboardStore> log)
    {
        this.log = log;
        filePath = configuration["Leaderboard:DataPath"]
            ?? Path.Combine(environment.ContentRootPath, "server-data.json");

        data = Load();
        var migrated = data.Version < CurrentStoreVersion ||
                       data.RatingRulesVersion != RatingEngine.RulesVersion ||
                       data.CurrentSeason < FirstAutomaticSeason ||
                       data.CurrentSeasonStartedAtUtc.Kind != DateTimeKind.Utc;
        if (migrated)
        {
            // Store v6 removes installation records and establishes an authoritative,
            // persisted season boundary. Resetting avoids retaining the correlation
            // that earlier development schemas stored between installations and players.
            data = new ServerData { CurrentSeasonStartedAtUtc = DateTime.UtcNow };
            SaveLocked();
            log.LogInformation(
                "Started automatic leaderboard season {Season} at {StartedAtUtc}; legacy development data was reset.",
                data.CurrentSeason,
                data.CurrentSeasonStartedAtUtc);
        }

        RebuildIndexesLocked();
    }

    public int CurrentSeason
    {
        get
        {
            lock (gate) return data.CurrentSeason;
        }
    }

    public DateTime CurrentSeasonStartedAtUtc
    {
        get
        {
            lock (gate) return data.CurrentSeasonStartedAtUtc;
        }
    }

    public RatingState Submit(AutomaticMatchSubmission submission, DateTime? receivedAtUtc = null)
    {
        var normalized = NormalizeSubmission(submission);
        var received = receivedAtUtc ?? DateTime.UtcNow;
        if (received.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("ReceivedAtUtc must be UTC.", nameof(receivedAtUtc));
        }

        lock (gate)
        {
            if (normalized.CompletedAtUtc < data.CurrentSeasonStartedAtUtc)
            {
                throw new SeasonBoundaryException(
                    "This match was completed before the current community season started.");
            }

            var identityKey = CharacterIdentityKey(normalized.CharacterName, normalized.WorldId);
            profilesByIdentity.TryGetValue(identityKey, out var profile);

            if (profile is not null &&
                matchesByKey.TryGetValue((profile.Id, data.CurrentSeason, normalized.MatchKey), out var duplicate))
            {
                var payloadHash = HashPayload(normalized);
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(payloadHash),
                        Convert.FromHexString(duplicate.PayloadHash)))
                {
                    throw new DuplicateMatchException("This match key was already submitted with different match data.");
                }

                return RatingForLocked(profile.Id, normalized.Job);
            }

            var aggregateKey = profile is null
                ? ((Guid ProfileId, int Season, CombatJob Job)?)null
                : (profile.Id, data.CurrentSeason, normalized.Job);
            var existingAggregate = aggregateKey is not null && aggregates.TryGetValue(aggregateKey.Value, out var found)
                ? found
                : null;

            if (existingAggregate?.Matches >= SeasonMatchLimit)
            {
                throw new SeasonMatchLimitException("Season match limit for this character and job has been reached.");
            }

            var day = DateOnly.FromDateTime(received);
            if (profile is not null &&
                dailyCounts.TryGetValue((profile.Id, data.CurrentSeason, normalized.Job, day), out var dailyCount) &&
                dailyCount >= DailyMatchLimit)
            {
                throw new DailyMatchLimitException(
                    "Daily match limit for this character and job has been reached.",
                    SecondsUntilNextUtcDay(received));
            }

            var newProfile = profile is null;
            profile ??= new CommunityProfile
            {
                Id = Guid.NewGuid(),
                CharacterName = normalized.CharacterName,
                WorldId = normalized.WorldId,
                WorldName = normalized.WorldName,
                CreatedAtUtc = received,
                UpdatedAtUtc = received,
            };

            var previousCharacterName = profile.CharacterName;
            var previousWorldName = profile.WorldName;
            var previousProfileUpdatedAt = profile.UpdatedAtUtc;

            profile.CharacterName = normalized.CharacterName;
            profile.WorldName = normalized.WorldName;
            profile.UpdatedAtUtc = received;

            var stored = new StoredAutomaticMatch
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                Season = data.CurrentSeason,
                MatchKey = normalized.MatchKey,
                PayloadHash = HashPayload(normalized),
                CompletedAtUtc = normalized.CompletedAtUtc,
                ReceivedAtUtc = received,
                Job = normalized.Job,
                Outcome = normalized.Outcome,
            };

            var actualAggregateKey = (profile.Id, data.CurrentSeason, normalized.Job);
            var newAggregate = !aggregates.TryGetValue(actualAggregateKey, out var aggregate);
            aggregate ??= new Aggregate();

            if (normalized.Outcome == MatchOutcome.Win) aggregate.Wins++;
            else aggregate.Losses++;

            if (newProfile)
            {
                data.Profiles.Add(profile);
                profilesByIdentity.Add(identityKey, profile);
                profilesById.Add(profile.Id, profile);
            }

            if (newAggregate) aggregates.Add(actualAggregateKey, aggregate);
            data.AcceptedMatches.Add(stored);
            matchesByKey.Add((profile.Id, data.CurrentSeason, normalized.MatchKey), stored);
            dailyCounts[(profile.Id, data.CurrentSeason, normalized.Job, day)] =
                dailyCounts.GetValueOrDefault((profile.Id, data.CurrentSeason, normalized.Job, day)) + 1;

            try
            {
                SaveLocked();
            }
            catch
            {
                data.AcceptedMatches.Remove(stored);
                matchesByKey.Remove((profile.Id, data.CurrentSeason, normalized.MatchKey));
                DecrementDailyCountLocked(profile.Id, data.CurrentSeason, normalized.Job, day);

                if (normalized.Outcome == MatchOutcome.Win) aggregate.Wins--;
                else aggregate.Losses--;
                if (newAggregate) aggregates.Remove(actualAggregateKey);

                profile.CharacterName = previousCharacterName;
                profile.WorldName = previousWorldName;
                profile.UpdatedAtUtc = previousProfileUpdatedAt;

                if (newProfile)
                {
                    data.Profiles.Remove(profile);
                    profilesByIdentity.Remove(identityKey);
                    profilesById.Remove(profile.Id);
                }

                throw;
            }

            return RatingEngine.FromResults(normalized.Job, aggregate.Wins, aggregate.Losses);
        }
    }

    public IReadOnlyList<LeaderboardRow> Leaderboard(CombatJob job, int limit)
    {
        if (!CombatJobs.All.Contains(job)) throw new ArgumentException("A valid combat job is required.", nameof(job));
        limit = Math.Clamp(limit, 1, 100);

        lock (gate)
        {
            var candidates = aggregates
                .Where(x => x.Key.Season == data.CurrentSeason && x.Key.Job == job && x.Value.Matches > 0)
                .Select(x =>
                {
                    var profile = profilesById[x.Key.ProfileId];
                    var rating = RatingEngine.FromResults(job, x.Value.Wins, x.Value.Losses);
                    return new Candidate(profile, rating);
                })
                .OrderBy(x => x.Rating.IsProvisional ? 1 : 0)
                .ThenByDescending(x => x.Rating.Rating)
                .ThenByDescending(x => x.Rating.Matches)
                .ThenBy(x => x.Profile.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Profile.WorldId)
                .ToArray();

            var establishedRank = 0;
            var rows = new List<LeaderboardRow>(Math.Min(limit, candidates.Length));
            foreach (var candidate in candidates)
            {
                if (rows.Count >= limit) break;
                var rank = candidate.Rating.IsProvisional ? 0 : ++establishedRank;
                rows.Add(new LeaderboardRow(
                    rank,
                    candidate.Profile.CharacterName,
                    candidate.Profile.WorldName,
                    job,
                    candidate.Rating.Rating,
                    candidate.Rating.Matches,
                    candidate.Rating.Wins,
                    candidate.Rating.Losses,
                    candidate.Rating.WinRate));
            }

            return rows;
        }
    }

    private RatingState RatingForLocked(Guid profileId, CombatJob job)
    {
        var total = aggregates.GetValueOrDefault((profileId, data.CurrentSeason, job));
        return RatingEngine.FromResults(job, total?.Wins ?? 0, total?.Losses ?? 0);
    }

    private void RebuildIndexesLocked()
    {
        profilesByIdentity = new Dictionary<string, CommunityProfile>(StringComparer.Ordinal);
        profilesById = [];
        aggregates = [];
        matchesByKey = [];
        dailyCounts = [];

        foreach (var profile in data.Profiles)
        {
            if (profile.Id == Guid.Empty) throw new InvalidOperationException("Leaderboard data contains an invalid profile.");
            var identity = NormalizeIdentity(profile.CharacterName, profile.WorldId);
            profile.CharacterName = identity.CharacterName;
            profile.WorldId = identity.WorldId;
            profile.WorldName = identity.WorldName;
            var identityKey = CharacterIdentityKey(profile.CharacterName, profile.WorldId);
            if (!profilesById.TryAdd(profile.Id, profile) || !profilesByIdentity.TryAdd(identityKey, profile))
            {
                throw new InvalidOperationException("Leaderboard data contains a duplicate character identity.");
            }
        }

        foreach (var match in data.AcceptedMatches)
        {
            if (match.Id == Guid.Empty || match.Season < 1 || !profilesById.ContainsKey(match.ProfileId) ||
                !MatchKeys.IsValid(match.MatchKey) || !PayloadHashes.IsValid(match.PayloadHash) ||
                !CombatJobs.All.Contains(match.Job) || !Enum.IsDefined(match.Outcome) ||
                match.CompletedAtUtc.Kind != DateTimeKind.Utc || match.ReceivedAtUtc.Kind != DateTimeKind.Utc ||
                (match.Season == data.CurrentSeason && match.CompletedAtUtc < data.CurrentSeasonStartedAtUtc))
            {
                throw new InvalidOperationException("Leaderboard data contains an invalid accepted match.");
            }

            if (!matchesByKey.TryAdd((match.ProfileId, match.Season, match.MatchKey), match))
            {
                throw new InvalidOperationException("Leaderboard data contains a duplicate match key for one character and season.");
            }

            var aggregate = aggregates.GetValueOrDefault((match.ProfileId, match.Season, match.Job));
            if (aggregate is null)
            {
                aggregate = new Aggregate();
                aggregates.Add((match.ProfileId, match.Season, match.Job), aggregate);
            }
            if (match.Outcome == MatchOutcome.Win) aggregate.Wins++;
            else aggregate.Losses++;

            var day = DateOnly.FromDateTime(match.ReceivedAtUtc);
            var dailyKey = (match.ProfileId, match.Season, match.Job, day);
            dailyCounts[dailyKey] = dailyCounts.GetValueOrDefault(dailyKey) + 1;
        }
    }

    private ServerData Load()
    {
        if (!File.Exists(filePath)) return new ServerData();
        try
        {
            var loaded = JsonSerializer.Deserialize<ServerData>(File.ReadAllText(filePath), JsonOptions)
                ?? throw new InvalidOperationException("Leaderboard data is empty.");
            if (loaded.Version > CurrentStoreVersion)
            {
                throw new InvalidOperationException(
                    $"Leaderboard store {loaded.Version} is newer than supported version {CurrentStoreVersion}.");
            }
            if (loaded.Profiles is null || loaded.AcceptedMatches is null)
            {
                throw new InvalidOperationException("Leaderboard data contains missing required collections.");
            }
            if (loaded.Version >= CurrentStoreVersion &&
                (loaded.CurrentSeason < 1 || loaded.CurrentSeasonStartedAtUtc.Kind != DateTimeKind.Utc))
            {
                throw new InvalidOperationException("Leaderboard current season and its UTC start time are invalid.");
            }
            return loaded;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Leaderboard data at '{filePath}' could not be read. Refusing to overwrite it.",
                exception);
        }
    }

    private void SaveLocked()
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, data, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static AutomaticMatchSubmission NormalizeSubmission(AutomaticMatchSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!MatchKeys.IsValid(submission.MatchKey))
        {
            throw new ArgumentException("Match key must contain exactly 64 lowercase hexadecimal characters.");
        }

        var identity = NormalizeIdentity(submission.CharacterName, submission.WorldId);
        var coreSubmission = new MatchSubmission(
            submission.MatchKey,
            submission.CompletedAtUtc,
            submission.Job,
            submission.Outcome,
            submission.Queue,
            submission.TerritoryId,
            submission.DurationSeconds,
            submission.Stats);
        Validation.ValidateSubmission(coreSubmission);
        if (!RatingEngine.IsRatedQueue(submission.Queue))
        {
            throw new ArgumentException("Only Casual and Ranked matches can affect the community leaderboard.");
        }

        return submission with
        {
            CharacterName = identity.CharacterName,
            WorldId = identity.WorldId,
            WorldName = identity.WorldName,
        };
    }

    private static CharacterIdentity NormalizeIdentity(string characterName, uint worldId)
    {
        var normalizedName = Validation.NormalizeCharacterName(characterName);
        var normalizedWorldId = Validation.ValidateWorldId(worldId);
        return new CharacterIdentity(
            normalizedName,
            normalizedWorldId,
            HomeWorlds.CanonicalName(normalizedWorldId));
    }

    private static string HashPayload(AutomaticMatchSubmission submission)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(submission, CanonicalJsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string CharacterIdentityKey(string characterName, uint worldId) =>
        $"{characterName.Normalize(NormalizationForm.FormC).ToLowerInvariant()}|{worldId}";

    private static int SecondsUntilNextUtcDay(DateTime now)
    {
        var next = now.Date.AddDays(1);
        return Math.Clamp((int)Math.Ceiling((next - now).TotalSeconds), 1, 86_400);
    }

    private void DecrementDailyCountLocked(Guid profileId, int season, CombatJob job, DateOnly day)
    {
        var key = (profileId, season, job, day);
        var count = dailyCounts.GetValueOrDefault(key);
        if (count <= 1) dailyCounts.Remove(key);
        else dailyCounts[key] = count - 1;
    }

    private sealed class Aggregate
    {
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Matches => checked(Wins + Losses);
    }

    private sealed record Candidate(CommunityProfile Profile, RatingState Rating);
}

public static partial class InstallationKeys
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static bool IsValid(string? value) => value is not null && KeyPattern().IsMatch(value);
}

public static partial class MatchKeys
{
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
}

public static partial class PayloadHashes
{
    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
}

public static class HomeWorlds
{
    private static readonly IReadOnlyDictionary<uint, string> Names = new Dictionary<uint, string>
    {
        [21] = "Ravana",
        [22] = "Bismarck",
        [23] = "Asura",
        [24] = "Belias",
        [28] = "Pandaemonium",
        [29] = "Shinryu",
        [30] = "Unicorn",
        [31] = "Yojimbo",
        [32] = "Zeromus",
        [33] = "Twintania",
        [34] = "Brynhildr",
        [35] = "Famfrit",
        [36] = "Lich",
        [37] = "Mateus",
        [39] = "Omega",
        [40] = "Jenova",
        [41] = "Zalera",
        [42] = "Zodiark",
        [43] = "Alexander",
        [44] = "Anima",
        [45] = "Carbuncle",
        [46] = "Fenrir",
        [47] = "Hades",
        [48] = "Ixion",
        [49] = "Kujata",
        [50] = "Typhon",
        [51] = "Ultima",
        [52] = "Valefor",
        [53] = "Exodus",
        [54] = "Faerie",
        [55] = "Lamia",
        [56] = "Phoenix",
        [57] = "Siren",
        [58] = "Garuda",
        [59] = "Ifrit",
        [60] = "Ramuh",
        [61] = "Titan",
        [62] = "Diabolos",
        [63] = "Gilgamesh",
        [64] = "Leviathan",
        [65] = "Midgardsormr",
        [66] = "Odin",
        [67] = "Shiva",
        [68] = "Atomos",
        [69] = "Bahamut",
        [70] = "Chocobo",
        [71] = "Moogle",
        [72] = "Tonberry",
        [73] = "Adamantoise",
        [74] = "Coeurl",
        [75] = "Malboro",
        [76] = "Tiamat",
        [77] = "Ultros",
        [78] = "Behemoth",
        [79] = "Cactuar",
        [80] = "Cerberus",
        [81] = "Goblin",
        [82] = "Mandragora",
        [83] = "Louisoix",
        [85] = "Spriggan",
        [86] = "Sephirot",
        [87] = "Sophia",
        [88] = "Zurvan",
        [90] = "Aegis",
        [91] = "Balmung",
        [92] = "Durandal",
        [93] = "Excalibur",
        [94] = "Gungnir",
        [95] = "Hyperion",
        [96] = "Masamune",
        [97] = "Ragnarok",
        [98] = "Ridill",
        [99] = "Sargatanas",
        [400] = "Sagittarius",
        [401] = "Phantom",
        [402] = "Alpha",
        [403] = "Raiden",
        [404] = "Marilith",
        [405] = "Seraph",
        [406] = "Halicarnassus",
        [407] = "Maduin",
        [408] = "Cuchulainn",
        [409] = "Kraken",
        [410] = "Rafflesia",
        [411] = "Golem",
    };

    public static int Count => Names.Count;

    public static string CanonicalName(uint worldId) => Names.TryGetValue(worldId, out var name)
        ? name
        : throw new ArgumentException(
            "Home World is not supported by this leaderboard version.",
            nameof(worldId));
}

public sealed class DuplicateMatchException(string message) : Exception(message);
public sealed class SeasonMatchLimitException(string message) : Exception(message);
public sealed class SeasonBoundaryException(string message) : Exception(message);
public sealed class DailyMatchLimitException(string message, int retryAfterSeconds) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed record AutomaticMatchSubmission(
    string MatchKey,
    DateTime CompletedAtUtc,
    string CharacterName,
    uint WorldId,
    string WorldName,
    CombatJob Job,
    MatchOutcome Outcome,
    MatchQueue Queue,
    ushort TerritoryId,
    ushort DurationSeconds,
    ScoreboardStats Stats);

public sealed class ServerData
{
    public int Version { get; set; } = 6;
    public int RatingRulesVersion { get; set; } = RatingEngine.RulesVersion;
    public int CurrentSeason { get; set; } = 2;
    public DateTime CurrentSeasonStartedAtUtc { get; set; } = DateTime.UtcNow;
    public List<CommunityProfile> Profiles { get; set; } = [];
    public List<StoredAutomaticMatch> AcceptedMatches { get; set; } = [];
}

public sealed class CommunityProfile
{
    public Guid Id { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class StoredAutomaticMatch
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public int Season { get; set; }
    public string MatchKey { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public DateTime CompletedAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public CombatJob Job { get; set; }
    public MatchOutcome Outcome { get; set; }
}
