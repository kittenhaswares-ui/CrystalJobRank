using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrystalJobRank.Core;

namespace CrystalJobRank.Server;

public sealed class LeaderboardStore
{
    private const int CurrentSchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object gate = new();
    private readonly string filePath;
    private readonly ILogger<LeaderboardStore> log;
    private ServerData data;

    public LeaderboardStore(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LeaderboardStore> log)
    {
        this.log = log;
        filePath = configuration["Leaderboard:DataPath"]
            ?? Path.Combine(environment.ContentRootPath, "server-data.json");
        data = Load();
        var changed = false;
        if (data.Version < CurrentSchemaVersion)
        {
            // Version 4 replaces unverified aliases with a character + Home World
            // identity. Legacy accounts cannot be authenticated as that identity,
            // so discard them and their submissions once instead of mislabeling them.
            var removedPlayers = data.Players.Count;
            var removedMatches = data.Matches.Count;
            data.Players.Clear();
            data.Matches.Clear();
            data.CurrentSeason = Math.Max(1, data.CurrentSeason);
            data.Version = CurrentSchemaVersion;
            changed = true;
            log.LogInformation(
                "Migrated leaderboard identity schema; removed {Players} legacy alias accounts and {Matches} submissions.",
                removedPlayers,
                removedMatches);
        }

        if (data.RatingRulesVersion != RatingEngine.RulesVersion)
        {
            data.RatingRulesVersion = RatingEngine.RulesVersion;
            changed = true;
        }

        if (changed) SaveLocked();
    }

    public RegistrationResponse Register(string characterName, uint worldId, string worldName)
    {
        var identity = Validation.NormalizeRegistration(characterName, worldId, worldName);
        lock (gate)
        {
            if (data.Players.Any(x =>
                    x.WorldId == identity.WorldId &&
                    string.Equals(x.CharacterName, identity.CharacterName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DuplicateCharacterException("That character and Home World are already registered.");
            }

            var apiKeyBytes = RandomNumberGenerator.GetBytes(32);
            var apiKey = Base64Url(apiKeyBytes);
            var player = new PlayerAccount
            {
                Id = Guid.NewGuid(),
                CharacterName = identity.CharacterName,
                WorldId = identity.WorldId,
                WorldName = identity.WorldName,
                ApiKeyHash = HashKey(apiKey),
                CreatedAtUtc = DateTime.UtcNow,
            };
            data.Players.Add(player);
            SaveLocked();
            log.LogInformation("Registered leaderboard player {PlayerId}.", player.Id);
            return new RegistrationResponse(player.Id, apiKey);
        }
    }

    public RatingState Submit(string apiKey, MatchSubmission submission)
    {
        Validation.ValidateSubmission(submission);
        if (!RatingEngine.IsRatedQueue(submission.Queue))
        {
            throw new ArgumentException("Only Casual and Ranked matches can affect the community leaderboard.");
        }

        lock (gate)
        {
            var player = AuthenticateLocked(apiKey) ?? throw new UnauthorizedAccessException();
            if (data.Matches.Any(x => x.PlayerId == player.Id && x.Submission.Fingerprint == submission.Fingerprint))
            {
                throw new DuplicateMatchException("This match was already submitted.");
            }

            var stored = new StoredMatch
            {
                Id = Guid.NewGuid(),
                PlayerId = player.Id,
                ReceivedAtUtc = DateTime.UtcNow,
                Season = data.CurrentSeason,
                Submission = submission,
            };
            data.Matches.Add(stored);
            try
            {
                SaveLocked();
            }
            catch
            {
                data.Matches.Remove(stored);
                throw;
            }
            return RatingForLocked(player.Id, submission.Job);
        }
    }

    public IReadOnlyList<LeaderboardRow> Leaderboard(CombatJob job, int limit)
    {
        lock (gate)
        {
            var rows = data.Players
                .Select(player => (Player: player, Rating: RatingForLocked(player.Id, job)))
                .Where(x => x.Rating.Matches > 0)
                .OrderByDescending(x => x.Rating.Rating)
                .ThenByDescending(x => x.Rating.Matches)
                .ThenBy(x => x.Player.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Player.WorldId)
                .Take(limit)
                .Select((x, index) => new LeaderboardRow(
                    index + 1,
                    x.Player.CharacterName,
                    x.Player.WorldName,
                    job,
                    x.Rating.Rating,
                    x.Rating.Matches,
                    x.Rating.Wins,
                    x.Rating.Losses,
                    x.Rating.WinRate))
                .ToArray();
            return rows;
        }
    }

    public bool Delete(string apiKey)
    {
        lock (gate)
        {
            var player = AuthenticateLocked(apiKey);
            if (player is null) return false;
            data.Matches.RemoveAll(x => x.PlayerId == player.Id);
            data.Players.Remove(player);
            SaveLocked();
            log.LogInformation("Deleted leaderboard player {PlayerId} and all submitted matches.", player.Id);
            return true;
        }
    }

    private RatingState RatingForLocked(Guid playerId, CombatJob job) => RatingEngine.Replay(
        job,
        data.Matches
            .Where(x => x.Season == data.CurrentSeason && x.PlayerId == playerId && x.Submission.Job == job)
            .Where(x => RatingEngine.IsRatedQueue(x.Submission.Queue))
            .OrderBy(x => x.Submission.CompletedAtUtc)
            .ThenBy(x => x.Submission.Fingerprint, StringComparer.Ordinal)
            .Select(x => x.Submission.Outcome));

    private PlayerAccount? AuthenticateLocked(string apiKey)
    {
        var candidate = Convert.FromHexString(HashKey(apiKey));
        foreach (var player in data.Players)
        {
            var expected = Convert.FromHexString(player.ApiKeyHash);
            if (candidate.Length == expected.Length && CryptographicOperations.FixedTimeEquals(candidate, expected))
            {
                return player;
            }
        }
        return null;
    }

    private ServerData Load()
    {
        if (!File.Exists(filePath)) return new ServerData();
        try
        {
            var loaded = JsonSerializer.Deserialize<ServerData>(File.ReadAllText(filePath), JsonOptions) ?? new ServerData();
            if (loaded.Version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Leaderboard schema {loaded.Version} is newer than supported schema {CurrentSchemaVersion}.");
            }
            if (loaded.Players is null || loaded.Matches is null || loaded.Matches.Any(x => x is null || x.Submission is null))
            {
                throw new InvalidOperationException("Leaderboard data contains missing required collections or matches.");
            }
            if (loaded.Version >= CurrentSchemaVersion && loaded.CurrentSeason < 1)
            {
                throw new InvalidOperationException("Leaderboard current season must be at least 1.");
            }
            return loaded;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Leaderboard data at '{filePath}' could not be read. Refusing to overwrite it.", exception);
        }
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporary, filePath, true);
    }

    private static string HashKey(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class DuplicateCharacterException(string message) : Exception(message);
public sealed class DuplicateMatchException(string message) : Exception(message);

public sealed class ServerData
{
    public int Version { get; set; } = 4;
    public int RatingRulesVersion { get; set; } = RatingEngine.RulesVersion;
    public int CurrentSeason { get; set; } = 1;
    public List<PlayerAccount> Players { get; set; } = [];
    public List<StoredMatch> Matches { get; set; } = [];
}

public sealed class PlayerAccount
{
    public Guid Id { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class StoredMatch
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public int Season { get; set; }
    public MatchSubmission Submission { get; set; } = null!;
}
