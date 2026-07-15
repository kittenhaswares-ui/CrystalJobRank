using System.Text.Json;
using CrystalJobRank.Core;
using CrystalJobRank.Server;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

const int publicSchemaVersion = 4;
const int maximumJsonBodyBytes = 16 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddSingleton<LeaderboardStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("read", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("write", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/health", (LeaderboardStore store) => Results.Ok(new
{
    status = "ok",
    schemaVersion = publicSchemaVersion,
    ratingRulesVersion = RatingEngine.RulesVersion,
    season = store.CurrentSeason,
    seasonStartedAtUtc = store.CurrentSeasonStartedAtUtc,
})).RequireRateLimiting("read");

app.MapPost("/v2/matches", async (HttpRequest request, LeaderboardStore store) =>
{
    if (!TryGetInstallationKey(request, out _))
    {
        return Results.Json(new { error = "A valid installation key is required." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!request.HasJsonContentType())
    {
        return Results.Json(new { error = "Content-Type must be application/json." }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    if (request.ContentLength is > maximumJsonBodyBytes)
    {
        return Results.Json(new { error = "JSON request body is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    AutomaticMatchSubmission? submission;
    try
    {
        submission = await ReadBoundedJsonAsync<AutomaticMatchSubmission>(request, maximumJsonBodyBytes);
    }
    catch (BodyTooLargeException)
    {
        return Results.Json(new { error = "JSON request body is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Request body must contain valid JSON." });
    }
    catch (NotSupportedException)
    {
        return Results.BadRequest(new { error = "Request body must contain valid JSON." });
    }

    if (submission is null)
    {
        return Results.BadRequest(new { error = "Match submission is required." });
    }

    try
    {
        return Results.Ok(store.Submit(submission));
    }
    catch (DailyMatchLimitException exception)
    {
        request.HttpContext.Response.Headers.RetryAfter = exception.RetryAfterSeconds.ToString();
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (DuplicateMatchException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (SeasonMatchLimitException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (SeasonBoundaryException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireRateLimiting("write");

app.MapGet("/v1/leaderboard", (string job, int? limit, HttpResponse response, LeaderboardStore store) =>
{
    if (!CombatJobs.TryParseAbbreviation(job, out var parsedJob))
    {
        return Results.BadRequest(new { error = "A valid combat job is required." });
    }

    response.Headers.CacheControl = "public, max-age=15";
    return Results.Ok(store.Leaderboard(parsedJob, Math.Clamp(limit ?? 50, 1, 100)));
}).RequireRateLimiting("read");

app.Run();

static bool TryGetInstallationKey(HttpRequest request, out string installationKey)
{
    installationKey = request.Headers["X-Installation-Key"].ToString();
    return InstallationKeys.IsValid(installationKey);
}

static async Task<T?> ReadBoundedJsonAsync<T>(HttpRequest request, int maximumBytes)
{
    using var buffer = new MemoryStream(Math.Min(maximumBytes, 4 * 1024));
    var chunk = new byte[4 * 1024];
    while (true)
    {
        var read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted);
        if (read == 0) break;
        if (buffer.Length + read > maximumBytes) throw new BodyTooLargeException();
        buffer.Write(chunk, 0, read);
    }

    return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonSerializerOptions.Web);
}

public sealed class BodyTooLargeException : Exception;
public partial class Program;
