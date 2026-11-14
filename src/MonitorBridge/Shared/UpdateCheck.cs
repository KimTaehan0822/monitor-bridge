using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MonitorBridge.Shared;

/// <summary>Asks GitHub for the latest release. Any failure (offline, rate limit, no release) gives null.</summary>
internal static class UpdateCheck
{
    public const string Repo = "KimTaehan0822/monitor-bridge";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorBridge"); // GitHub refuses requests without one
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, [property: JsonPropertyName("html_url")] string? Url);

    public static async Task<(string Tag, string Url)?> LatestAsync(CancellationToken ct)
    {
        try
        {
            var r = await Http.GetFromJsonAsync<Release>($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            return r is { Tag: { } tag, Url: { } url } ? (tag, url) : null;
        }
        catch
        {
            return null;
        }
    }
}
