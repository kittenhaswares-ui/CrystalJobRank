using System.Net.Http.Json;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;

namespace CrystalJobRank.Plugin.Services;

internal sealed class LeaderboardClient : IDisposable
{
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public void Dispose() => httpClient.Dispose();

    public async Task<RegistrationResponse> RegisterAsync(
        string baseUrl,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildUri(baseUrl, "v1/players/register");
        var response = await httpClient.PostAsJsonAsync(
            endpoint,
            new RegisterRequest(Validation.NormalizeDisplayName(displayName)),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RegistrationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty registration response.");
    }

    public async Task SubmitAsync(
        string baseUrl,
        string apiKey,
        MatchRecord match,
        CancellationToken cancellationToken = default)
    {
        if (match.Queue == MatchQueue.Custom) return;

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "v1/matches"))
        {
            Content = JsonContent.Create(match.ToSubmission()),
        };
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LeaderboardRow>> GetLeaderboardAsync(
        string baseUrl,
        CombatJob job,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildUri(baseUrl, $"v1/leaderboard?job={Uri.EscapeDataString(job.ToString())}&limit=50");
        return await httpClient.GetFromJsonAsync<List<LeaderboardRow>>(endpoint, cancellationToken) ?? [];
    }

    public async Task DeleteAccountAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(baseUrl, "v1/players/me"));
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static Uri BuildUri(string baseUrl, string relative)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root))
        {
            throw new ArgumentException("The leaderboard URL is invalid.");
        }

        if (root.Scheme != Uri.UriSchemeHttps && !root.IsLoopback)
        {
            throw new ArgumentException("Leaderboard connections must use HTTPS (HTTP is allowed only for localhost development)." );
        }

        return new Uri(root, relative);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Leaderboard server returned {(int)response.StatusCode}: {details}");
    }
}
