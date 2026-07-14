using CrystalJobRank.Core;
using CrystalJobRank.Server;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

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
    options.AddPolicy("registration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromHours(1),
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/players/register", (RegisterRequest request, LeaderboardStore store) =>
{
    try
    {
        return Results.Ok(store.Register(request.DisplayName));
    }
    catch (DuplicateDisplayNameException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireRateLimiting("registration");

app.MapPost("/v1/matches", (HttpRequest request, MatchSubmission submission, LeaderboardStore store) =>
{
    if (!TryGetApiKey(request, out var apiKey)) return Results.Unauthorized();
    try
    {
        var result = store.Submit(apiKey, submission);
        return Results.Ok(result);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
    catch (DuplicateMatchException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireRateLimiting("write");

app.MapGet("/v1/leaderboard", (string job, int? limit, LeaderboardStore store) =>
{
    if (!Enum.TryParse<CombatJob>(job, true, out var parsedJob) || parsedJob == CombatJob.Unknown)
    {
        return Results.BadRequest(new { error = "A valid combat job is required." });
    }

    return Results.Ok(store.Leaderboard(parsedJob, Math.Clamp(limit ?? 50, 1, 100)));
});

app.MapDelete("/v1/players/me", (HttpRequest request, LeaderboardStore store) =>
{
    if (!TryGetApiKey(request, out var apiKey)) return Results.Unauthorized();
    return store.Delete(apiKey) ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("write");

app.Run();

static bool TryGetApiKey(HttpRequest request, out string apiKey)
{
    apiKey = request.Headers["X-Api-Key"].ToString();
    return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length <= 256;
}

public partial class Program;
