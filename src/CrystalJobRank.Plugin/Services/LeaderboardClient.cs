using System.Net.Http.Json;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;

namespace CrystalJobRank.Plugin.Services;

internal sealed class LeaderboardClient : IDisposable
{
    private readonly HttpClient httpClient;

    public LeaderboardClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 64 * 1024,
        };
    }

    public void Dispose() => httpClient.Dispose();

    public async Task SubmitAsync(
        string baseUrl,
        string installationKey,
        LeaderboardMatchSubmission submission,
        CancellationToken cancellationToken = default)
    {
        if (!RatingEngine.IsRatedQueue(submission.Queue)) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "v2/matches"))
        {
            Content = JsonContent.Create(submission),
        };
        request.Headers.Add("X-Installation-Key", installationKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LeaderboardRow>> GetLeaderboardAsync(
        string baseUrl,
        CombatJob job,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildUri(baseUrl, $"v1/leaderboard?job={Uri.EscapeDataString(job.ToString())}&limit=50");
        using var response = await httpClient.GetAsync(endpoint, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LeaderboardRow>>(cancellationToken) ?? [];
    }

    private static Uri BuildUri(string baseUrl, string relative)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root))
        {
            throw new ArgumentException("The leaderboard URL is invalid.");
        }

        var secureRemote = root.Scheme == Uri.UriSchemeHttps;
        var localDevelopment = root.Scheme == Uri.UriSchemeHttp && root.IsLoopback;
        if (!secureRemote && !localDevelopment)
        {
            throw new ArgumentException("Leaderboard connections must use HTTPS (HTTP is allowed only for localhost development)." );
        }

        if (!string.IsNullOrEmpty(root.UserInfo))
        {
            throw new ArgumentException("The leaderboard URL must not contain credentials.");
        }

        return new Uri(root, relative);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        details = details.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (details.Length > 512) details = details[..512] + "...";
        throw new HttpRequestException(
            $"Leaderboard server returned {(int)response.StatusCode}: {details}",
            null,
            response.StatusCode);
    }
}
