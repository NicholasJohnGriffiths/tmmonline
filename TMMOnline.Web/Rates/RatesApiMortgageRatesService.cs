using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TMMOnline.Web.Rates;

public sealed class RatesApiOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://ratesapi.nz";
    public string EndpointPath { get; set; } = "/api/v1/mortgage-rates";
    public string TimeSeriesEndpointPath { get; set; } = "/api/v1/mortgage-rates/time-series";
    public int? TermInMonths { get; set; }
    public string? ApiKey { get; set; }
    public string ApiKeyHeaderName { get; set; } = "x-api-key";
    public bool UseBearerToken { get; set; }
    public int CacheMinutes { get; set; } = 10;
}

public sealed record MortgageRateQuote(
    string Lender,
    string Product,
    string Term,
    decimal Rate,
    DateTimeOffset? UpdatedAt,
    string? RateId = null);

public interface IMortgageRatesService
{
    Task<IReadOnlyList<MortgageRateQuote>> GetRatesAsync(int take, int? termInMonths, CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> GetChangedRateKeysAsync(string changeWindow, int? termInMonths, CancellationToken cancellationToken);
}

public sealed class RatesApiMortgageRatesService : IMortgageRatesService
{
    private static readonly string[] LenderKeys = ["lender", "provider", "bank", "institution", "name"];
    private static readonly string[] ProductKeys = ["product", "productName", "offer", "title", "description", "loanType"];
    private static readonly string[] TermKeys = ["term", "fixedTerm", "duration", "termLabel", "period"];
    private static readonly string[] RateKeys = ["rate", "interestRate", "value", "apr", "percentage"];
    private static readonly string[] UpdatedAtKeys = ["updatedAt", "updated", "lastUpdated", "timestamp", "asAt"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptions<RatesApiOptions> _options;
    private readonly ILogger<RatesApiMortgageRatesService> _logger;

    public RatesApiMortgageRatesService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        IOptions<RatesApiOptions> options,
        ILogger<RatesApiMortgageRatesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MortgageRateQuote>> GetRatesAsync(int take, int? termInMonths, CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return Array.Empty<MortgageRateQuote>();
        }

        RatesApiOptions options = _options.Value;
        if (options.Enabled == false)
        {
            return Array.Empty<MortgageRateQuote>();
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.EndpointPath))
        {
            return Array.Empty<MortgageRateQuote>();
        }

        int cacheMinutes = Math.Clamp(options.CacheMinutes, 1, 60);
        int? effectiveTermInMonths = termInMonths ?? options.TermInMonths;
        string cacheKey = $"ratesapi:mortgage:{options.BaseUrl}:{options.EndpointPath}:{effectiveTermInMonths}";

        IReadOnlyList<MortgageRateQuote> allRates = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheMinutes);
            return await QueryRatesApiAsync(options, effectiveTermInMonths, cancellationToken);
        }) ?? Array.Empty<MortgageRateQuote>();

        return allRates.Take(take).ToArray();
    }

    public async Task<IReadOnlySet<string>> GetChangedRateKeysAsync(string changeWindow, int? termInMonths, CancellationToken cancellationToken)
    {
        RatesApiOptions options = _options.Value;
        if (options.Enabled == false)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        int lookbackDays = changeWindow switch
        {
            "1day" => 1,
            "2week" => 14,
            "4week" => 28,
            "3month" => 90,
            "6month" => 180,
            _ => 7
        };

        int cacheMinutes = Math.Clamp(options.CacheMinutes, 1, 60);
        int? effectiveTermInMonths = termInMonths ?? options.TermInMonths;
        string cacheKey = $"ratesapi:timeseries:changed:{changeWindow}:{effectiveTermInMonths}";

        HashSet<string>? changedKeys = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheMinutes);
            return await QueryChangedRateKeysAsync(options, lookbackDays, effectiveTermInMonths, cancellationToken);
        });

        return changedKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<MortgageRateQuote>> QueryRatesApiAsync(
        RatesApiOptions options,
        int? termInMonths,
        CancellationToken cancellationToken)
    {
        try
        {
            string baseUrl = options.BaseUrl.TrimEnd('/');
            string endpointPath = options.EndpointPath.StartsWith('/') ? options.EndpointPath : $"/{options.EndpointPath}";
            string endpoint = $"{baseUrl}{endpointPath}";
            if (termInMonths.HasValue && termInMonths.Value > 0)
            {
                endpoint = $"{endpoint}?termInMonths={termInMonths.Value}";
            }

            using HttpClient client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            if (string.IsNullOrWhiteSpace(options.ApiKey) == false)
            {
                if (options.UseBearerToken)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, options.ApiKey);
                }
            }

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode == false)
            {
                _logger.LogWarning("Rates API request failed with status {StatusCode} for {Endpoint}", response.StatusCode, endpoint);
                return Array.Empty<MortgageRateQuote>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            IReadOnlyList<MortgageRateQuote> parsed = ParseCurrentRates(json.RootElement, termInMonths)
                .Where(x => x.Rate > 0)
                .OrderBy(x => x.Rate)
                .ThenBy(x => x.Lender)
                .ThenBy(x => x.Product)
                .ToArray();

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rates API query failed.");
            return Array.Empty<MortgageRateQuote>();
        }
    }

    private async Task<HashSet<string>> QueryChangedRateKeysAsync(
        RatesApiOptions options,
        int lookbackDays,
        int? termInMonths,
        CancellationToken cancellationToken)
    {
        try
        {
            DateOnly endDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            DateOnly startDate = endDate.AddDays(-lookbackDays);

            var queryParameters = new Dictionary<string, string>
            {
                ["startDate"] = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["endDate"] = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };

            if (termInMonths.HasValue && termInMonths.Value > 0)
            {
                queryParameters["termInMonths"] = termInMonths.Value.ToString(CultureInfo.InvariantCulture);
            }

            string endpoint = BuildEndpoint(options.BaseUrl, options.TimeSeriesEndpointPath, queryParameters);

            using HttpClient client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            if (string.IsNullOrWhiteSpace(options.ApiKey) == false)
            {
                if (options.UseBearerToken)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, options.ApiKey);
                }
            }

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode == false)
            {
                _logger.LogWarning("Rates API time-series request failed with status {StatusCode} for {Endpoint}", response.StatusCode, endpoint);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var valuesByKeyAndDate = new Dictionary<string, SortedDictionary<DateOnly, decimal>>(StringComparer.OrdinalIgnoreCase);

            foreach ((DateOnly date, MortgageRateQuote quote) in ParseTimeSeriesRates(json.RootElement))
            {
                string key = BuildRateKey(quote);
                if (valuesByKeyAndDate.TryGetValue(key, out SortedDictionary<DateOnly, decimal>? valuesByDate) == false)
                {
                    valuesByDate = new SortedDictionary<DateOnly, decimal>();
                    valuesByKeyAndDate[key] = valuesByDate;
                }

                valuesByDate[date] = quote.Rate;
            }

            var changedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, SortedDictionary<DateOnly, decimal>> pair in valuesByKeyAndDate)
            {
                if (pair.Value.Count < 2)
                {
                    continue;
                }

                decimal first = pair.Value.First().Value;
                decimal last = pair.Value.Last().Value;
                if (first != last)
                {
                    changedKeys.Add(pair.Key);
                }
            }

            return changedKeys;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rates API time-series query failed.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string BuildEndpoint(string baseUrl, string endpointPath, IDictionary<string, string>? query)
    {
        string normalizedBase = baseUrl.TrimEnd('/');
        string normalizedPath = endpointPath.StartsWith('/') ? endpointPath : $"/{endpointPath}";
        string endpoint = $"{normalizedBase}{normalizedPath}";

        if (query == null || query.Count == 0)
        {
            return endpoint;
        }

        string queryString = string.Join("&", query
            .Where(x => string.IsNullOrWhiteSpace(x.Value) == false)
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

        return string.IsNullOrWhiteSpace(queryString)
            ? endpoint
            : $"{endpoint}?{queryString}";
    }

    private static string BuildRateKey(MortgageRateQuote quote)
    {
        if (string.IsNullOrWhiteSpace(quote.RateId) == false)
        {
            return quote.RateId.Trim().ToLowerInvariant();
        }

        string lender = (quote.Lender ?? string.Empty).Trim().ToLowerInvariant();
        string product = (quote.Product ?? string.Empty).Trim().ToLowerInvariant();
        string term = (quote.Term ?? string.Empty).Trim().ToLowerInvariant();
        return $"{lender}|{product}|{term}";
    }

    private static IEnumerable<MortgageRateQuote> ParseCurrentRates(JsonElement root, int? requestedTermInMonths)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParseRates(root);
        }

        if (root.TryGetProperty("data", out JsonElement data) == false || data.ValueKind != JsonValueKind.Array)
        {
            return ParseRates(root);
        }

        DateTimeOffset? updatedAt = GetDateTimeOffset(root, UpdatedAtKeys);
        return FlattenInstitutionRates(data, updatedAt, requestedTermInMonths);
    }

    private static IEnumerable<MortgageRateQuote> FlattenInstitutionRates(
        JsonElement institutions,
        DateTimeOffset? updatedAt,
        int? requestedTermInMonths)
    {
        foreach (JsonElement institution in institutions.EnumerateArray())
        {
            if (institution.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string lenderName = GetString(institution, ["name"]) ?? "Unknown lender";
            if (institution.TryGetProperty("products", out JsonElement products) == false || products.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement product in products.EnumerateArray())
            {
                if (product.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string productName = GetString(product, ["name"]) ?? "Mortgage";
                if (product.TryGetProperty("rates", out JsonElement rates) == false || rates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement rate in rates.EnumerateArray())
                {
                    if (rate.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    decimal? value = GetDecimal(rate, RateKeys);
                    if (value.HasValue == false)
                    {
                        continue;
                    }

                    int? termInMonths = GetInt(rate, ["termInMonths"]);
                    if (requestedTermInMonths.HasValue
                        && termInMonths.HasValue
                        && requestedTermInMonths.Value != termInMonths.Value)
                    {
                        continue;
                    }

                    decimal normalizedRate = value.Value > 0m && value.Value < 1m
                        ? value.Value * 100m
                        : value.Value;

                    yield return new MortgageRateQuote(
                        lenderName,
                        productName,
                        GetString(rate, ["term"]) ?? string.Empty,
                        normalizedRate,
                        updatedAt,
                        GetString(rate, ["id"]));
                }
            }
        }
    }

    private static IEnumerable<(DateOnly Date, MortgageRateQuote Quote)> ParseTimeSeriesRates(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("timeSeries", out JsonElement timeSeries)
            && timeSeries.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty dateNode in timeSeries.EnumerateObject())
            {
                if (DateOnly.TryParse(dateNode.Name, out DateOnly date) == false)
                {
                    continue;
                }

                if (dateNode.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (dateNode.Value.TryGetProperty("data", out JsonElement data) == false
                    || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (MortgageRateQuote quote in FlattenInstitutionRates(data, null, null))
                {
                    yield return (date, quote);
                }
            }

            yield break;
        }

        // Fallback for non-standard shapes.
        foreach (JsonElement snapshot in ExtractRows(root))
        {
            DateOnly? snapshotDate = GetDateOnly(snapshot, ["date", "asAt", "timestamp", "updatedAt"]);
            if (snapshotDate.HasValue == false)
            {
                continue;
            }

            foreach (MortgageRateQuote quote in ParseRates(snapshot))
            {
                yield return (snapshotDate.Value, quote);
            }
        }
    }

    private static IEnumerable<MortgageRateQuote> ParseRates(JsonElement root)
    {
        IEnumerable<JsonElement> rows = ExtractRows(root);

        foreach (JsonElement row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string lender = GetString(row, LenderKeys) ?? "Unknown lender";
            string product = GetString(row, ProductKeys) ?? "Mortgage";
            string term = GetString(row, TermKeys) ?? string.Empty;
            decimal? rate = GetDecimal(row, RateKeys);
            DateTimeOffset? updatedAt = GetDateTimeOffset(row, UpdatedAtKeys);

            if (rate.HasValue == false)
            {
                continue;
            }

            decimal normalizedRate = rate.Value > 0m && rate.Value < 1m
                ? rate.Value * 100m
                : rate.Value;

            yield return new MortgageRateQuote(lender, product, term, normalizedRate, updatedAt);
        }
    }

    private static IEnumerable<JsonElement> ExtractRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<JsonElement>();
        }

        foreach (string key in new[] { "data", "rates", "items", "results" })
        {
            if (root.TryGetProperty(key, out JsonElement property) && property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray().ToArray();
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static string? GetString(JsonElement row, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (row.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text) == false)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static decimal? GetDecimal(JsonElement row, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (row.TryGetProperty(key, out JsonElement value) == false)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? string.Empty;
                text = text.Replace("%", string.Empty, StringComparison.Ordinal).Trim();
                if (decimal.TryParse(text, out decimal parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement row, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (row.TryGetProperty(key, out JsonElement value) == false)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateOnly? GetDateOnly(JsonElement row, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (row.TryGetProperty(key, out JsonElement value) == false || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string raw = value.GetString() ?? string.Empty;
            if (DateOnly.TryParse(raw, out DateOnly dateOnly))
            {
                return dateOnly;
            }

            if (DateTimeOffset.TryParse(raw, out DateTimeOffset dto))
            {
                return DateOnly.FromDateTime(dto.UtcDateTime);
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement row, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (row.TryGetProperty(key, out JsonElement value) == false)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
