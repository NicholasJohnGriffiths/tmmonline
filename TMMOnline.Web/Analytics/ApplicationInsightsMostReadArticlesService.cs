using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TMMOnline.Web.Analytics;

public sealed class MostReadAnalyticsOptions
{
    public bool Enabled { get; set; }
    public string? AppId { get; set; }
    public string? ApiKey { get; set; }
    public int LookbackDays { get; set; } = 7;
    public int CacheMinutes { get; set; } = 10;
}

public interface IMostReadArticlesService
{
    Task<IReadOnlyList<int>> GetMostReadArticleIdsAsync(IReadOnlyCollection<int> candidateArticleIds, int take, CancellationToken cancellationToken);
}

public sealed class ApplicationInsightsMostReadArticlesService : IMostReadArticlesService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptions<MostReadAnalyticsOptions> _options;
    private readonly ILogger<ApplicationInsightsMostReadArticlesService> _logger;

    public ApplicationInsightsMostReadArticlesService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<MostReadAnalyticsOptions> options,
        ILogger<ApplicationInsightsMostReadArticlesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<int>> GetMostReadArticleIdsAsync(
        IReadOnlyCollection<int> candidateArticleIds,
        int take,
        CancellationToken cancellationToken)
    {
        if (candidateArticleIds.Count == 0 || take <= 0)
        {
            return Array.Empty<int>();
        }

        MostReadAnalyticsOptions options = _options.Value;
        if (options.Enabled == false
            || string.IsNullOrWhiteSpace(options.AppId)
            || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return Array.Empty<int>();
        }

        int lookbackDays = Math.Clamp(options.LookbackDays, 1, 90);
        int cacheMinutes = Math.Clamp(options.CacheMinutes, 1, 60);

        string cacheKey = $"ai-mostread:{lookbackDays}";
        IReadOnlyList<int> rankedIds = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheMinutes);
            return await QueryMostReadIdsAsync(options.AppId!, options.ApiKey!, lookbackDays, cancellationToken);
        }) ?? Array.Empty<int>();

        if (rankedIds.Count == 0)
        {
            return rankedIds;
        }

        var candidateSet = new HashSet<int>(candidateArticleIds);
        return rankedIds
            .Where(candidateSet.Contains)
            .Take(take)
            .ToArray();
    }

    private async Task<IReadOnlyList<int>> QueryMostReadIdsAsync(
        string appId,
        string apiKey,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        string query = $@"
requests
| where timestamp > ago({lookbackDays}d)
| where success == true
| extend requestUrl = tostring(url)
| extend nodeId = toint(extract(@""[?&]node=(\\d+)"", 1, requestUrl))
| where isnotnull(nodeId)
| summarize views = count() by nodeId
| order by views desc
| take 200";

        string endpoint = $"https://api.applicationinsights.io/v1/apps/{Uri.EscapeDataString(appId)}/query?query={Uri.EscapeDataString(query)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("x-api-key", apiKey);

            using HttpClient client = _httpClientFactory.CreateClient();
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode == false)
            {
                _logger.LogWarning("Application Insights query failed with status code {StatusCode}.", response.StatusCode);
                return Array.Empty<int>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (json.RootElement.TryGetProperty("tables", out JsonElement tables) == false
                || tables.ValueKind != JsonValueKind.Array
                || tables.GetArrayLength() == 0)
            {
                return Array.Empty<int>();
            }

            JsonElement rows = tables[0].GetProperty("rows");
            var results = new List<int>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() == 0)
                {
                    continue;
                }

                JsonElement nodeIdElement = row[0];
                if (nodeIdElement.ValueKind == JsonValueKind.Number && nodeIdElement.TryGetInt32(out int nodeId))
                {
                    results.Add(nodeId);
                    continue;
                }

                if (nodeIdElement.ValueKind == JsonValueKind.String && int.TryParse(nodeIdElement.GetString(), out int parsedNodeId))
                {
                    results.Add(parsedNodeId);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Application Insights Most Read query failed.");
            return Array.Empty<int>();
        }
    }
}
