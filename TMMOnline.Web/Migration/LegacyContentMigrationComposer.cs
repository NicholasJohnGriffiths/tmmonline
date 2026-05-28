using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TMMOnline.Web.Tagging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace TMMOnline.Web.Migration;

public sealed class LegacyContentMigrationComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Components().Append<LegacyContentMigrationComponent>();
    }
}

public sealed class LegacyContentMigrationComponent : IComponent
{
    private static readonly string[] PreferredSectionOrder =
    [
        "news",
        "rates",
        "people",
        "conference",
        "property-news",
        "news-bites",
        "video",
        "podcast"
    ];

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly IMediaService _mediaService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly MediaUrlGeneratorCollection _mediaUrlGenerators;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly IContentTypeBaseServiceProvider _contentTypeBaseServiceProvider;
    private readonly ILogger<LegacyContentMigrationComponent> _logger;

    public LegacyContentMigrationComponent(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        IContentService contentService,
        IContentTypeService contentTypeService,
        IMediaService mediaService,
        MediaFileManager mediaFileManager,
        MediaUrlGeneratorCollection mediaUrlGenerators,
        IShortStringHelper shortStringHelper,
        IContentTypeBaseServiceProvider contentTypeBaseServiceProvider,
        ILogger<LegacyContentMigrationComponent> logger)
    {
        _configuration = configuration;
        _hostEnvironment = hostEnvironment;
        _contentService = contentService;
        _contentTypeService = contentTypeService;
        _mediaService = mediaService;
        _mediaFileManager = mediaFileManager;
        _mediaUrlGenerators = mediaUrlGenerators;
        _shortStringHelper = shortStringHelper;
        _contentTypeBaseServiceProvider = contentTypeBaseServiceProvider;
        _logger = logger;
    }

    public void Initialize()
    {
        bool migrationEnabled = _configuration.GetValue<bool>("LegacyMigration:Enabled");
        bool repairImagesEnabled = _configuration.GetValue<bool>("LegacyMigration:RepairImagesOnStartup");
        bool normalizeImportedArticlesEnabled = _configuration.GetValue<bool>("LegacyMigration:NormalizeImportedArticlesOnStartup");

        if (migrationEnabled == false && repairImagesEnabled == false && normalizeImportedArticlesEnabled == false)
        {
            ImportHeaderBannerImageIfConfigured();
            return;
        }

        try
        {
            if (migrationEnabled)
            {
                RunMigration();
            }

            if (repairImagesEnabled)
            {
                RepairImportedImages();
            }

            if (normalizeImportedArticlesEnabled)
            {
                NormalizeImportedArticles();
            }

            ImportHeaderBannerImageIfConfigured();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy migration failed.");
        }
    }

    public void Terminate()
    {
    }

    private void RunMigration()
    {
        string pagesCsvPath = ResolvePath(_configuration["LegacyMigration:PagesCsvPath"], "migration-output/pages.csv");
        if (System.IO.File.Exists(pagesCsvPath) == false)
        {
            _logger.LogWarning("Legacy migration skipped: pages file not found at {Path}", pagesCsvPath);
            return;
        }

        int maxArticles = _configuration.GetValue<int?>("LegacyMigration:MaxArticles") ?? 0;
        string baseUrl = _configuration["LegacyMigration:BaseUrl"] ?? "https://tmmonline.nz";
        string onlySectionSlug = ContentTagParser.NormalizeTag(_configuration["LegacyMigration:OnlySectionSlug"]);

        IContent? home = _contentService.GetRootContent().FirstOrDefault(x => x.ContentType.Alias == "homePage");
        if (home == null)
        {
            _logger.LogWarning("Legacy migration skipped: no homePage root node found.");
            return;
        }

        Dictionary<string, IContent> sectionBySlug = GetSections(home);
        IContent defaultSection = GetDefaultSection(home, sectionBySlug);

        IContentType? articleContentType = _contentTypeService.Get("articlePage");
        if (articleContentType == null)
        {
            _logger.LogWarning("Legacy migration skipped: articlePage content type missing.");
            return;
        }

        Dictionary<string, IContent> existingLegacyArticles = GetExistingLegacyArticles(articleContentType.Id);
        Dictionary<string, IContent> existingLegacyArticlesByRouteAlias = GetExistingLegacyArticlesByRouteAlias(articleContentType.Id);
        Dictionary<string, List<IContent>> existingArticlesByTitle = GetExistingArticlesByTitle(articleContentType.Id);

        List<PageRecord> articlePages = LoadPageRecords(pagesCsvPath)
            .Where(x => x.StatusCode == 200 && x.Url.Contains("/article/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        using var httpClient = CreateHttpClient();

        if (sectionBySlug.TryGetValue("conference", out IContent? conferenceSection))
        {
            string conferenceSeedUrl = _configuration["LegacyMigration:SectionSeeds:Conference"] ?? $"{baseUrl.TrimEnd('/')}/better-business";
            string? conferenceBodyHtml = TryLoadConferenceSectionBodyHtml(httpClient, conferenceSeedUrl);
            if (string.IsNullOrWhiteSpace(conferenceBodyHtml) == false)
            {
                SetValueIfExists(conferenceSection, "bodyText", conferenceBodyHtml);
                _contentService.Save(conferenceSection);
                _contentService.Publish(conferenceSection, Array.Empty<string>());
                _logger.LogInformation("Conference section body content refreshed from {SeedUrl}.", conferenceSeedUrl);
            }
        }

        var forcedSectionByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sectionSeeds = BuildSectionSeedMap(baseUrl);
        if (string.IsNullOrWhiteSpace(onlySectionSlug) == false)
        {
            sectionSeeds = sectionSeeds
                .Where(x => x.TargetSectionSlug.Equals(onlySectionSlug, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.LogInformation(
                "Legacy migration constrained to section '{SectionSlug}'. Seed count: {SeedCount}.",
                onlySectionSlug,
                sectionSeeds.Count);
        }

        foreach ((string targetSectionSlug, string sectionUrl) in sectionSeeds)
        {
            IReadOnlyList<string> sectionArticleUrls = DiscoverSectionArticleUrls(httpClient, sectionUrl);
            foreach (string articleUrl in sectionArticleUrls)
            {
                string normalizedKey = NormalizeLegacyUrlKey(articleUrl);
                if (forcedSectionByUrl.ContainsKey(normalizedKey) == false)
                {
                    forcedSectionByUrl[normalizedKey] = targetSectionSlug;
                }

                bool alreadyPresent = articlePages.Any(x => NormalizeLegacyUrlKey(x.Url).Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (alreadyPresent == false)
                {
                    articlePages.Add(new PageRecord(articleUrl, 200));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(onlySectionSlug) == false)
        {
            articlePages = articlePages
                .Where(x =>
                {
                    string key = NormalizeLegacyUrlKey(x.Url);
                    return forcedSectionByUrl.TryGetValue(key, out string? forced)
                        && forced.Equals(onlySectionSlug, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        if (maxArticles > 0)
        {
            articlePages = articlePages.Take(maxArticles).ToList();
        }

        if (articlePages.Count == 0)
        {
            _logger.LogInformation("Legacy migration found no article URLs to import.");
            return;
        }

        IMedia? articleMediaFolder = GetArticleMediaFolder();

        int createdCount = 0;
        int updatedCount = 0;
        int skippedCount = 0;

        foreach (PageRecord page in articlePages)
        {
            string legacyUrlKey = NormalizeLegacyUrlKey(page.Url);
            string? pageRouteAliasKey = ExtractLegacyArticleAliasKey(page.Url);

            LegacyArticleData? data = TryLoadArticleData(httpClient, page.Url, baseUrl);
            if (data == null)
            {
                skippedCount++;
                continue;
            }

            string normalizedUrlKey = NormalizeLegacyUrlKey(page.Url);
            if (forcedSectionByUrl.TryGetValue(normalizedUrlKey, out string? forcedSectionSlug)
                && string.IsNullOrWhiteSpace(forcedSectionSlug) == false)
            {
                data = new LegacyArticleData
                {
                    SourceUrl = data.SourceUrl,
                    Title = data.Title,
                    SectionSlug = forcedSectionSlug,
                    IntroText = data.IntroText,
                    LeadText = data.LeadText,
                    MetaDescription = data.MetaDescription,
                    BodyHtml = data.BodyHtml,
                    PublishedOn = data.PublishedOn,
                    MainImageUrl = data.MainImageUrl
                };
            }

            IContent parentSection = ResolveParentSection(sectionBySlug, defaultSection, data.SectionSlug);
            bool isExisting = existingLegacyArticles.TryGetValue(legacyUrlKey, out IContent? article);

            if (isExisting == false && string.IsNullOrWhiteSpace(pageRouteAliasKey) == false)
            {
                isExisting = existingLegacyArticlesByRouteAlias.TryGetValue(pageRouteAliasKey, out article);
            }

            string? canonicalRouteAliasKey = ExtractLegacyArticleAliasKey(data.CanonicalUrl);
            if (isExisting == false && string.IsNullOrWhiteSpace(canonicalRouteAliasKey) == false)
            {
                isExisting = existingLegacyArticlesByRouteAlias.TryGetValue(canonicalRouteAliasKey, out article);
            }

            if (isExisting == false)
            {
                article = TryFindExistingArticleByTitle(existingArticlesByTitle, data.Title, parentSection.Id);
                if (article != null)
                {
                    isExisting = true;
                    existingLegacyArticles[legacyUrlKey] = article;
                }
            }

            article ??= _contentService.Create(data.Title, parentSection.Id, "articlePage");

            if (isExisting && article.ParentId != parentSection.Id)
            {
                try
                {
                    _contentService.Move(article, parentSection.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not move article {ArticleId} to section {SectionId} during migration; keeping current parent {ParentId}.",
                        article.Id,
                        parentSection.Id,
                        article.ParentId);
                }
            }

            article.Name = data.Title;

            SetValueIfExists(article, "heroHeading", data.Title);
            SetValueIfExists(article, "leadText", data.LeadText);
            SetValueIfExists(article, "introText", data.IntroText);
            SetValueIfExists(article, "bodyText", data.BodyHtml);
            SetValueIfExists(article, "metaTitle", data.Title);
            SetValueIfExists(article, "metaDescription", data.MetaDescription);
            SetValueIfExists(article, "legacySourceUrl", data.CanonicalUrl ?? data.SourceUrl);

            if (data.PublishedOn.HasValue)
            {
                SetValueIfExists(article, "publishedOn", data.PublishedOn.Value);
            }

            string tagList = BuildArticleTags(data.SectionSlug, data.Title);
            SetValueIfExists(article, "articleTags", tagList);

            if (articleMediaFolder != null && string.IsNullOrWhiteSpace(data.MainImageUrl) == false)
            {
                string? mediaUdi = TryCreateArticleImageMedia(httpClient, articleMediaFolder.Id, data.MainImageUrl, data.Title);
                if (string.IsNullOrWhiteSpace(mediaUdi) == false)
                {
                    SetValueIfExists(article, "primaryImage", mediaUdi);
                }
            }

            _contentService.Save(article);
            _contentService.Publish(article, Array.Empty<string>());

            if (isExisting)
            {
                updatedCount++;
            }
            else
            {
                existingLegacyArticles[legacyUrlKey] = article;
                if (string.IsNullOrWhiteSpace(pageRouteAliasKey) == false)
                {
                    existingLegacyArticlesByRouteAlias[pageRouteAliasKey] = article;
                }

                if (string.IsNullOrWhiteSpace(canonicalRouteAliasKey) == false)
                {
                    existingLegacyArticlesByRouteAlias[canonicalRouteAliasKey] = article;
                }

                AddArticleToTitleLookup(existingArticlesByTitle, article);
                createdCount++;
            }
        }

        _logger.LogInformation(
            "Legacy migration completed. Created {CreatedCount}, updated {UpdatedCount}, skipped {SkippedCount}.",
            createdCount,
            updatedCount,
            skippedCount);
    }

    private List<(string TargetSectionSlug, string SectionUrl)> BuildSectionSeedMap(string baseUrl)
    {
        string peopleUrl = _configuration["LegacyMigration:SectionSeeds:People"] ?? $"{baseUrl.TrimEnd('/')}/people";
        string conferenceUrl = _configuration["LegacyMigration:SectionSeeds:Conference"] ?? $"{baseUrl.TrimEnd('/')}/better-business";
        string propertyNewsUrl = _configuration["LegacyMigration:SectionSeeds:PropertyNews"] ?? $"{baseUrl.TrimEnd('/')}/property-news";
        string newsBitesUrl = _configuration["LegacyMigration:SectionSeeds:NewsBites"] ?? $"{baseUrl.TrimEnd('/')}/news-bites";
        string videoUrl = _configuration["LegacyMigration:SectionSeeds:Video"] ?? $"{baseUrl.TrimEnd('/')}/video";
        string podcastUrl = _configuration["LegacyMigration:SectionSeeds:Podcast"] ?? $"{baseUrl.TrimEnd('/')}/podcast";

        return
        [
            ("people", peopleUrl),
            ("conference", conferenceUrl),
            ("property-news", propertyNewsUrl),
            ("news-bites", newsBitesUrl),
            ("video", videoUrl),
            ("podcast", podcastUrl)
        ];
    }

    private IReadOnlyList<string> DiscoverSectionArticleUrls(HttpClient httpClient, string sectionUrl)
    {
        if (string.IsNullOrWhiteSpace(sectionUrl))
        {
            return [];
        }

        try
        {
            Uri sectionUri = new(sectionUrl);
            using HttpResponseMessage response = httpClient.GetAsync(sectionUri).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode == false)
            {
                _logger.LogWarning("Section seed fetch failed for {SectionUrl} with status {StatusCode}.", sectionUrl, (int)response.StatusCode);
                return [];
            }

            string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var sectionUriHost = sectionUri.Host;
            MatchCollection hrefMatches = Regex.Matches(
                html,
                "<a[^>]*href\\s*=\\s*['\"](?<href>[^'\">]+)['\"]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Legacy list pages often include canonical /article/{id}/{slug} links; prefer these when present.
            MatchCollection articleRouteMatches = Regex.Matches(
                html,
                "(?<href>/article/\\d+/[a-z0-9\\-]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in articleRouteMatches)
            {
                string href = match.Groups["href"].Value;
                if (TryNormalizeSectionArticleHref(sectionUri, href, out string? articleUrl))
                {
                    urls.Add(articleUrl);
                }
            }

            foreach (Match match in hrefMatches)
            {
                string href = match.Groups["href"].Value;
                if (TryNormalizeSectionArticleHref(sectionUri, href, out string? articleUrl))
                {
                    urls.Add(articleUrl);
                }
            }

            _logger.LogInformation("Section seed {SectionUrl} discovered {Count} candidate article URLs.", sectionUrl, urls.Count);
            return urls.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Section seed fetch failed for {SectionUrl}.", sectionUrl);
            return [];
        }
    }

    private string? TryLoadConferenceSectionBodyHtml(HttpClient httpClient, string sectionUrl)
    {
        if (string.IsNullOrWhiteSpace(sectionUrl))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = httpClient.GetAsync(sectionUrl).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode == false)
            {
                _logger.LogWarning("Conference body fetch failed for {SectionUrl} with status {StatusCode}.", sectionUrl, (int)response.StatusCode);
                return null;
            }

            string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Match bodyStart = Regex.Match(
                html,
                "(?<value><h1[^>]*>\\s*Better\\s+Business\\s+Programme\\s*</h1>.*)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (bodyStart.Success)
            {
                html = bodyStart.Groups["value"].Value;
            }

            int trimIndex = html.IndexOf("About Us", StringComparison.OrdinalIgnoreCase);
            if (trimIndex >= 0)
            {
                int tagStart = html.LastIndexOf('<', trimIndex);
                html = (tagStart > 0 ? html[..tagStart] : html[..trimIndex]).Trim();
            }

            return html.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conference body fetch failed for {SectionUrl}.", sectionUrl);
            return null;
        }
    }

    private static bool TryNormalizeSectionArticleHref(Uri sectionUri, string href, out string? articleUrl)
    {
        articleUrl = null;
        if (string.IsNullOrWhiteSpace(href)
            || href.StartsWith("#", StringComparison.Ordinal)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Uri.TryCreate(sectionUri, href, out Uri? resolved) == false)
        {
            return false;
        }

        if (IsSameLegacyHost(resolved.Host, sectionUri.Host) == false)
        {
            return false;
        }

        string path = resolved.AbsolutePath.Trim('/');
        if (path.Length == 0)
        {
            return false;
        }

        string sectionPath = sectionUri.AbsolutePath.Trim('/');
        if (path.Equals(sectionPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Contains("search", StringComparison.OrdinalIgnoreCase)
            || path.Contains("newsletter", StringComparison.OrdinalIgnoreCase)
            || path.Contains("podcast", StringComparison.OrdinalIgnoreCase) && sectionPath.Contains("podcast", StringComparison.OrdinalIgnoreCase) == false
            || path.Contains("video", StringComparison.OrdinalIgnoreCase) && sectionPath.Contains("video", StringComparison.OrdinalIgnoreCase) == false)
        {
            return false;
        }

        bool isSectionChild = path.StartsWith(sectionPath + "/", StringComparison.OrdinalIgnoreCase);
        bool isArticlePath = path.StartsWith("article/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/article/", StringComparison.OrdinalIgnoreCase);

        // Accept section-child links and legacy article route links discovered on seeded section pages.
        if (isSectionChild == false && isArticlePath == false)
        {
            return false;
        }

        articleUrl = resolved.GetLeftPart(UriPartial.Path);
        return true;
    }

    private static bool IsSameLegacyHost(string leftHost, string rightHost)
    {
        if (leftHost.Equals(rightHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string NormalizeHost(string host)
        {
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                return host[4..];
            }

            return host;
        }

        return NormalizeHost(leftHost).Equals(NormalizeHost(rightHost), StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, IContent> GetSections(IContent home)
    {
        IEnumerable<IContent> children = _contentService.GetPagedChildren(home.Id, 0, 500, out _)
            .Where(x => x.ContentType.Alias == "sectionPage");

        var map = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);
        foreach (IContent section in children)
        {
            string slug = ContentTagParser.NormalizeTag(section.Name);
            if (string.IsNullOrWhiteSpace(slug) == false && map.ContainsKey(slug) == false)
            {
                map.Add(slug, section);
            }
        }

        return map;
    }

    private static IContent GetDefaultSection(IContent home, IReadOnlyDictionary<string, IContent> sectionBySlug)
    {
        foreach (string preferred in PreferredSectionOrder)
        {
            if (sectionBySlug.TryGetValue(preferred, out IContent? section))
            {
                return section;
            }
        }

        return sectionBySlug.Values.FirstOrDefault() ?? home;
    }

    private Dictionary<string, IContent> GetExistingLegacyArticles(int articleContentTypeId)
    {
        var existing = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<IContent> articles = _contentService.GetPagedOfType(articleContentTypeId, 0, int.MaxValue, out _, null!, null);
        foreach (IContent article in articles)
        {
            object? value = article.GetValue("legacySourceUrl");
            if (value is string url && string.IsNullOrWhiteSpace(url) == false)
            {
                existing[NormalizeLegacyUrlKey(url)] = article;
            }
        }

        return existing;
    }

    private Dictionary<string, IContent> GetExistingLegacyArticlesByRouteAlias(int articleContentTypeId)
    {
        var existing = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<IContent> articles = _contentService.GetPagedOfType(articleContentTypeId, 0, int.MaxValue, out _, null!, null);
        foreach (IContent article in articles)
        {
            object? value = article.GetValue("legacySourceUrl");
            string? routeAliasKey = value is string url ? ExtractLegacyArticleAliasKey(url) : null;
            if (string.IsNullOrWhiteSpace(routeAliasKey) == false && existing.ContainsKey(routeAliasKey) == false)
            {
                existing[routeAliasKey] = article;
            }
        }

        return existing;
    }

    private Dictionary<string, List<IContent>> GetExistingArticlesByTitle(int articleContentTypeId)
    {
        var existing = new Dictionary<string, List<IContent>>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<IContent> articles = _contentService.GetPagedOfType(articleContentTypeId, 0, int.MaxValue, out _, null!, null);
        foreach (IContent article in articles)
        {
            AddArticleToTitleLookup(existing, article);
        }

        return existing;
    }

    private static void AddArticleToTitleLookup(Dictionary<string, List<IContent>> existing, IContent article)
    {
        string titleKey = NormalizeTitleKey(article.Name);
        if (titleKey.Length == 0)
        {
            return;
        }

        if (existing.TryGetValue(titleKey, out List<IContent>? list) == false)
        {
            list = new List<IContent>();
            existing[titleKey] = list;
        }

        if (list.Any(x => x.Id == article.Id) == false)
        {
            list.Add(article);
        }
    }

    private static IContent? TryFindExistingArticleByTitle(
        IReadOnlyDictionary<string, List<IContent>> existingArticlesByTitle,
        string? title,
        int parentSectionId)
    {
        string titleKey = NormalizeTitleKey(title);
        if (titleKey.Length == 0 || existingArticlesByTitle.TryGetValue(titleKey, out List<IContent>? candidates) == false || candidates.Count == 0)
        {
            return null;
        }

        IContent? sameParent = candidates.FirstOrDefault(x => x.ParentId == parentSectionId);
        if (sameParent != null)
        {
            return sameParent;
        }

        return candidates
            .OrderByDescending(x => x.UpdateDate)
            .ThenByDescending(x => x.CreateDate)
            .FirstOrDefault();
    }

    private static string? ExtractLegacyArticleAliasKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string path = url.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absolute))
        {
            path = absolute.AbsolutePath;
        }

        Match articleIdMatch = Regex.Match(path, "(?:^|/)article/(?<id>\\d+)(?:/|$)", RegexOptions.IgnoreCase);
        if (articleIdMatch.Success)
        {
            return $"article-id:{articleIdMatch.Groups["id"].Value}";
        }

        return null;
    }

    private static string NormalizeTitleKey(string? title)
    {
        return (title ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeLegacyUrlKey(string url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absolute))
        {
            string path = absolute.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }

            return string.Concat(
                absolute.Scheme.ToLowerInvariant(),
                "://",
                absolute.Host.ToLowerInvariant(),
                absolute.IsDefaultPort ? string.Empty : $":{absolute.Port}",
                path.ToLowerInvariant());
        }

        return trimmed.TrimEnd('/').ToLowerInvariant();
    }

    private IContent ResolveParentSection(
        IReadOnlyDictionary<string, IContent> sectionBySlug,
        IContent defaultSection,
        string? sectionSlug)
    {
        if (string.IsNullOrWhiteSpace(sectionSlug) == false
            && sectionBySlug.TryGetValue(sectionSlug, out IContent? section))
        {
            return section;
        }

        return defaultSection;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TMMOnlineMigration", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://tmmonline.nz)"));
        return client;
    }

    private LegacyArticleData? TryLoadArticleData(HttpClient httpClient, string articleUrl, string baseUrl)
    {
        try
        {
            string html = httpClient.GetStringAsync(articleUrl).GetAwaiter().GetResult();
            string title = ExtractTitle(html);
            string sectionSlug = ExtractSectionSlug(html, baseUrl);
            string bodyHtml = ExtractBodyHtml(html);
            string leadText = ExtractLeadText(html, bodyHtml);
            string introText = leadText;
            string metaDescription = ExtractMetaTag(html, "description") ?? leadText;
            DateTime? publishedOn = ExtractPublishedOn(html);
            string? mainImageUrl = ExtractMainImageUrl(html, articleUrl);
            string? canonicalUrl = ExtractCanonicalUrl(html, articleUrl);

            bodyHtml = CleanBodyHtmlLeadingNoise(bodyHtml, title, out DateTime? extractedPublishedOn, out _, mainImageUrl);
            bool isPodcastArticle = title.Contains("podcast", StringComparison.OrdinalIgnoreCase)
                || sectionSlug.Equals("podcast", StringComparison.OrdinalIgnoreCase);
            if (isPodcastArticle)
            {
                bodyHtml = AppendLegacyPodcastEmbeds(bodyHtml, html, articleUrl);
                bodyHtml = AppendLegacyPodcastLinks(bodyHtml, html, articleUrl);
            }

            if (publishedOn.HasValue == false && extractedPublishedOn.HasValue)
            {
                publishedOn = extractedPublishedOn;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = articleUrl;
            }

            return new LegacyArticleData
            {
                SourceUrl = articleUrl,
                Title = title,
                SectionSlug = sectionSlug,
                IntroText = introText,
                LeadText = leadText,
                MetaDescription = metaDescription,
                BodyHtml = bodyHtml,
                PublishedOn = publishedOn,
                MainImageUrl = mainImageUrl,
                CanonicalUrl = canonicalUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping article {ArticleUrl} because its HTML could not be loaded.", articleUrl);
            return null;
        }
    }

    private string? TryCreateArticleImageMedia(HttpClient httpClient, int parentId, string imageUrl, string articleTitle)
    {
        try
        {
            using HttpResponseMessage response = httpClient.GetAsync(imageUrl).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode == false)
            {
                return null;
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == false)
            {
                return null;
            }

            using Stream source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;

            string extension = GetImageExtension(imageUrl, mediaType);
            string safeStem = Regex.Replace(articleTitle.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(safeStem))
            {
                safeStem = "article";
            }

            string fileName = $"{safeStem}-{Guid.NewGuid():N}{extension}";
            IMedia media = _mediaService.CreateMedia(articleTitle, parentId, Constants.Conventions.MediaTypes.Image);
            media.SetValue(
                _mediaFileManager,
                _mediaUrlGenerators,
                _shortStringHelper,
                _contentTypeBaseServiceProvider,
                Constants.Conventions.Media.File,
                fileName,
                memory);

            _mediaService.Save(media);
            return $"umb://media/{media.Key:D}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not import primary image {ImageUrl}", imageUrl);
            return null;
        }
    }

    private IMedia? GetArticleMediaFolder()
    {
        IMedia? root = _mediaService.GetRootMedia()
            .FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
                && x.Name.Equals("Content Type Media", StringComparison.OrdinalIgnoreCase));

        if (root == null)
        {
            return null;
        }

        return _mediaService.GetPagedChildren(root.Id, 0, 500, out _)
            .FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
                && x.Name.Equals("articlePage", StringComparison.OrdinalIgnoreCase));
    }

    private IMedia? FindRootMediaFolder(string name)
    {
        return _mediaService.GetRootMedia()
            .FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
                && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private IMedia? FindChildMediaFolder(int parentId, string name)
    {
        IEnumerable<IMedia> children = _mediaService.GetPagedChildren(parentId, 0, 200, out _);
        return children.FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
            && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildArticleTags(string? sectionSlug, string title)
    {
        var tags = new List<string>();

        if (string.IsNullOrWhiteSpace(sectionSlug) == false)
        {
            tags.Add(sectionSlug);
        }

        string lowerTitle = title.ToLowerInvariant();
        if (lowerTitle.Contains("podcast", StringComparison.Ordinal))
        {
            tags.Add("podcast");
        }

        if (lowerTitle.Contains("video", StringComparison.Ordinal)
            || lowerTitle.Contains("watch", StringComparison.Ordinal)
            || lowerTitle.Contains("grtv", StringComparison.Ordinal))
        {
            tags.Add("video");
        }

        return string.Join(",", tags
            .Select(ContentTagParser.NormalizeTag)
            .Where(x => string.IsNullOrWhiteSpace(x) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string GetImageExtension(string imageUrl, string mediaType)
    {
        string ext = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext) == false)
        {
            return ext.ToLowerInvariant();
        }

        return mediaType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => ".img"
        };
    }

    private static DateTime? ExtractPublishedOn(string html)
    {
        string? raw = ExtractMetaProperty(html, "article:published_time")
            ?? ExtractRegexGroup(html, "<time[^>]*datetime=\"(?<value>[^\"]+)\"");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string ExtractTitle(string html)
    {
        string? title = ExtractMetaProperty(html, "og:title")
            ?? ExtractRegexGroup(html, "<h1[^>]*>(?<value>.*?)</h1>")
            ?? ExtractRegexGroup(html, "<title>(?<value>.*?)</title>");

        title = HtmlDecodeAndStrip(title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Untitled Article";
        }

        return title.Replace(" - TMM Online", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static string ExtractSectionSlug(string html, string baseUrl)
    {
        string? section = ExtractMetaProperty(html, "article:section");
        if (string.IsNullOrWhiteSpace(section) == false)
        {
            return ContentTagParser.NormalizeTag(section);
        }

        Match articleTopicMatch = Regex.Match(
            html,
            "<div[^>]*class=\"[^\"]*article-topic[^\"]*\"[^>]*>.*?<a[^>]*href=\"(?<value>[^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (articleTopicMatch.Success == false)
        {
            articleTopicMatch = Regex.Match(
                html,
                "<div[^>]*class='[^']*article-topic[^']*'[^>]*>.*?<a[^>]*href='(?<value>[^']+)'",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        string? articleTopicHref = articleTopicMatch.Success
            ? articleTopicMatch.Groups["value"].Value
            : null;
        if (string.IsNullOrWhiteSpace(articleTopicHref) == false)
        {
            string articleTopicSlug = NormalizeHrefToSectionSlug(articleTopicHref, baseUrl);
            if (string.IsNullOrWhiteSpace(articleTopicSlug) == false)
            {
                return articleTopicSlug;
            }
        }

        MatchCollection hrefMatches = Regex.Matches(html, "<a[^>]*href=\"(?<href>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in hrefMatches)
        {
            string href = match.Groups["href"].Value;
            string normalized = NormalizeHrefToSectionSlug(href, baseUrl);
            if (string.IsNullOrWhiteSpace(normalized) == false)
            {
                return normalized;
            }
        }

        return "news";
    }

    private static string NormalizeHrefToSectionSlug(string href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return string.Empty;
        }

        if (href.StartsWith("/article/", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/article/", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        Uri baseUri = new(baseUrl);
        Uri absolute = Uri.TryCreate(baseUri, href, out Uri? resolved) ? resolved : baseUri;
        string[] segments = absolute.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        string slug = ContentTagParser.NormalizeTag(segments[0]);
        return PreferredSectionOrder.Contains(slug, StringComparer.OrdinalIgnoreCase) ? slug : string.Empty;
    }

    private static string ExtractBodyHtml(string html)
    {
        string? body = ExtractRegexGroup(html, "<article[^>]*>(?<value>.*?)</article>")
            ?? ExtractRegexGroup(html, "<main[^>]*>(?<value>.*?)</main>")
            ?? ExtractRegexGroup(html, "<body[^>]*>(?<value>.*?)</body>")
            ?? string.Empty;

        body = Regex.Replace(body, "<script(?<value>.*?)</script>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<style(?<value>.*?)</style>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return body.Trim();
    }

    private static string ExtractLeadText(string html, string bodyHtml)
    {
        string? lead = ExtractMetaTag(html, "description")
            ?? ExtractRegexGroup(bodyHtml, "<p[^>]*>(?<value>.*?)</p>")
            ?? ExtractRegexGroup(bodyHtml, "<h[1-4][^>]*>(?<value>.*?)</h[1-4]>")
            ?? string.Empty;

        return HtmlDecodeAndStrip(lead);
    }

    private static string? ExtractMainImageUrl(string html, string articleUrl)
    {
        string? image = ExtractMetaProperty(html, "og:image")
            ?? ExtractRegexGroup(html, "<img[^>]*src=\"(?<value>[^\"]+)\"");

        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        if (Uri.TryCreate(new Uri(articleUrl), image, out Uri? resolved))
        {
            return resolved.AbsoluteUri;
        }

        return image;
    }

    private static string? ExtractCanonicalUrl(string html, string articleUrl)
    {
        string? canonical = ExtractRegexGroup(html, "<link[^>]*rel=\"canonical\"[^>]*href=\"(?<value>[^\"]+)\"[^>]*>")
            ?? ExtractRegexGroup(html, "<link[^>]*href=\"(?<value>[^\"]+)\"[^>]*rel=\"canonical\"[^>]*>")
            ?? ExtractMetaProperty(html, "og:url");

        if (string.IsNullOrWhiteSpace(canonical))
        {
            return null;
        }

        if (Uri.TryCreate(new Uri(articleUrl), canonical, out Uri? resolved))
        {
            return resolved.GetLeftPart(UriPartial.Path);
        }

        return canonical;
    }

    private static string AppendLegacyPodcastLinks(string bodyHtml, string fullHtml, string articleUrl)
    {
        if (string.IsNullOrWhiteSpace(fullHtml))
        {
            return bodyHtml;
        }

        Uri articleUri = new(articleUrl);
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MatchCollection hrefMatches = Regex.Matches(
            fullHtml,
            "<a[^>]*href\\s*=\\s*['\"](?<href>[^'\">]+)['\"][^>]*>(?<label>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in hrefMatches)
        {
            string href = match.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href)
                || href.StartsWith("#", StringComparison.Ordinal)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(articleUri, href, out Uri? resolved) == false)
            {
                continue;
            }

            if (IsLikelyPodcastPlatformLink(resolved) == false)
            {
                continue;
            }

            links.Add(resolved.AbsoluteUri);
        }

        if (links.Count == 0)
        {
            return bodyHtml;
        }

        string existing = bodyHtml ?? string.Empty;
        List<string> missing = links
            .Where(x => existing.Contains(x, StringComparison.OrdinalIgnoreCase) == false)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return existing;
        }

        string podcastLinksHtml = "<section class=\"legacy-podcast-links\"><h3>Listen to this podcast</h3><ul>"
            + string.Join(string.Empty, missing.Select(x => $"<li><a href=\"{x}\" target=\"_blank\" rel=\"noopener\">{System.Net.WebUtility.HtmlEncode(GetPodcastLinkLabel(x))}</a></li>"))
            + "</ul></section>";

        return string.IsNullOrWhiteSpace(existing)
            ? podcastLinksHtml
            : $"{existing}\n{podcastLinksHtml}";
    }

    private static string AppendLegacyPodcastEmbeds(string bodyHtml, string fullHtml, string articleUrl)
    {
        if (string.IsNullOrWhiteSpace(fullHtml))
        {
            return bodyHtml;
        }

        string existing = bodyHtml ?? string.Empty;
        Uri articleUri = new(articleUrl);
        var embedSnippets = new List<string>();

        // Preserve trusted legacy podcast iframes that were stripped by generic script/style cleanup.
        MatchCollection iframeMatches = Regex.Matches(
            fullHtml,
            "<iframe(?<attrs>[^>]+)></iframe>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in iframeMatches)
        {
            string attrs = match.Groups["attrs"].Value;
            string? src = ExtractRegexGroup(attrs, "\\bsrc\\s*=\\s*['\"](?<value>[^'\"]+)['\"]");
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            if (Uri.TryCreate(articleUri, src, out Uri? resolved) == false)
            {
                continue;
            }

            if (IsLikelyPodcastPlatformLink(resolved) == false)
            {
                continue;
            }

            string resolvedSrc = resolved.AbsoluteUri;
            if (existing.Contains(resolvedSrc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            embedSnippets.Add($"<div class=\"legacy-podcast-embed\"><iframe src=\"{resolvedSrc}\" loading=\"lazy\" allow=\"autoplay; clipboard-write; encrypted-media; fullscreen; picture-in-picture\" referrerpolicy=\"no-referrer-when-downgrade\"></iframe></div>");
        }

        // Preserve trusted legacy podcast player scripts (e.g., Buzzsprout) and matching container div IDs.
        MatchCollection scriptMatches = Regex.Matches(
            fullHtml,
            "<script[^>]*\\bsrc\\s*=\\s*['\"](?<src>[^'\"]+)['\"][^>]*>\\s*</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match scriptMatch in scriptMatches)
        {
            string src = scriptMatch.Groups["src"].Value.Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            if (Uri.TryCreate(articleUri, src, out Uri? resolved) == false)
            {
                continue;
            }

            string host = resolved.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
            {
                host = host[4..];
            }

            bool trustedPodcastScriptHost = host.Contains("buzzsprout.com", StringComparison.Ordinal)
                || host.Contains("omny.fm", StringComparison.Ordinal)
                || host.Contains("podbean.com", StringComparison.Ordinal)
                || host.Contains("simplecast.com", StringComparison.Ordinal)
                || host.Contains("soundcloud.com", StringComparison.Ordinal)
                || host.Contains("spotify.com", StringComparison.Ordinal);

            if (trustedPodcastScriptHost == false)
            {
                continue;
            }

            string resolvedSrc = resolved.AbsoluteUri;
            if (existing.Contains(resolvedSrc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? containerId = ExtractRegexGroup(resolved.Query, "(?:container_id|containerId)=?(?<value>[a-zA-Z0-9_-]+)");
            if (string.IsNullOrWhiteSpace(containerId))
            {
                Match containerMatch = Regex.Match(fullHtml, "id=['\"](?<value>buzzsprout-player-[0-9]+)['\"]", RegexOptions.IgnoreCase);
                containerId = containerMatch.Success ? containerMatch.Groups["value"].Value : null;
            }

            string containerHtml = string.IsNullOrWhiteSpace(containerId)
                ? string.Empty
                : $"<div id=\"{containerId}\"></div>";

            embedSnippets.Add($"<div class=\"legacy-podcast-embed\">{containerHtml}<script src=\"{resolvedSrc}\"></script></div>");
        }

        if (embedSnippets.Count == 0)
        {
            return existing;
        }

        string embedHtml = "<section class=\"legacy-podcast-embeds\">"
            + string.Join(string.Empty, embedSnippets.Distinct(StringComparer.OrdinalIgnoreCase))
            + "</section>";

        return string.IsNullOrWhiteSpace(existing)
            ? embedHtml
            : $"{existing}\n{embedHtml}";
    }

    private static bool IsLikelyPodcastPlatformLink(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        if (host.EndsWith("tmmonline.nz", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return host.Contains("spotify.com", StringComparison.Ordinal)
            || host.Contains("soundcloud.com", StringComparison.Ordinal)
            || host.Contains("podbean.com", StringComparison.Ordinal)
            || host.Contains("buzzsprout.com", StringComparison.Ordinal)
            || host.Contains("anchor.fm", StringComparison.Ordinal)
            || host.Contains("omny.fm", StringComparison.Ordinal)
            || host.Contains("apple.com", StringComparison.Ordinal)
            || host.Contains("podcasts.apple.com", StringComparison.Ordinal)
            || host.Contains("iheart.com", StringComparison.Ordinal)
            || host.Contains("stitcher.com", StringComparison.Ordinal)
            || host.Contains("player.fm", StringComparison.Ordinal)
            || host.Contains("listennotes.com", StringComparison.Ordinal)
            || host.Contains("amazon.com", StringComparison.Ordinal)
            || host.Contains("music.amazon", StringComparison.Ordinal)
            || host.Contains("google.com", StringComparison.Ordinal)
            || host.Contains("youtube.com", StringComparison.Ordinal)
            || host.Contains("youtu.be", StringComparison.Ordinal);
    }

    private static string GetPodcastLinkLabel(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false)
        {
            return "Podcast link";
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        if (host.Contains("spotify", StringComparison.Ordinal)) return "Spotify";
        if (host.Contains("soundcloud", StringComparison.Ordinal)) return "SoundCloud";
        if (host.Contains("podbean", StringComparison.Ordinal)) return "Podbean";
        if (host.Contains("buzzsprout", StringComparison.Ordinal)) return "Buzzsprout";
        if (host.Contains("anchor.fm", StringComparison.Ordinal)) return "Anchor";
        if (host.Contains("omny.fm", StringComparison.Ordinal)) return "Omny";
        if (host.Contains("apple", StringComparison.Ordinal)) return "Apple Podcasts";
        if (host.Contains("iheart", StringComparison.Ordinal)) return "iHeart";
        if (host.Contains("stitcher", StringComparison.Ordinal)) return "Stitcher";
        if (host.Contains("player.fm", StringComparison.Ordinal)) return "Player FM";
        if (host.Contains("listennotes", StringComparison.Ordinal)) return "Listen Notes";
        if (host.Contains("amazon", StringComparison.Ordinal)) return "Amazon Music";
        if (host.Contains("youtube", StringComparison.Ordinal) || host.Contains("youtu.be", StringComparison.Ordinal)) return "YouTube";

        return uri.Host;
    }

    private static string? ExtractMetaTag(string html, string name)
    {
        string pattern = $"<meta[^>]*name=\"{Regex.Escape(name)}\"[^>]*content=\"(?<value>[^\"]*)\"[^>]*>";
        return HtmlDecodeAndStrip(ExtractRegexGroup(html, pattern));
    }

    private static string? ExtractMetaProperty(string html, string property)
    {
        string pattern = $"<meta[^>]*property=\"{Regex.Escape(property)}\"[^>]*content=\"(?<value>[^\"]*)\"[^>]*>";
        return HtmlDecodeAndStrip(ExtractRegexGroup(html, pattern));
    }

    private static string? ExtractRegexGroup(string input, string pattern)
    {
        Match match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success == false)
        {
            return null;
        }

        return match.Groups["value"].Value;
    }

    private static string HtmlDecodeAndStrip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decoded = System.Net.WebUtility.HtmlDecode(value);
        return Regex.Replace(decoded, "<[^>]+>", string.Empty).Trim();
    }

    private static void SetValueIfExists(IContent content, string alias, object value)
    {
        if (content.HasProperty(alias))
        {
            content.SetValue(alias, value);
        }
    }

    private static List<PageRecord> LoadPageRecords(string pagesCsvPath)
    {
        string[] lines = System.IO.File.ReadAllLines(pagesCsvPath, Encoding.UTF8);
        if (lines.Length <= 1)
        {
            return [];
        }

        string[] headers = ParseCsvLine(lines[0]);
        int urlIndex = Array.FindIndex(headers, x => x.Equals("Url", StringComparison.OrdinalIgnoreCase));
        int statusIndex = Array.FindIndex(headers, x => x.Equals("StatusCode", StringComparison.OrdinalIgnoreCase));

        if (urlIndex < 0 || statusIndex < 0)
        {
            return [];
        }

        var records = new List<PageRecord>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] cols = ParseCsvLine(line);
            if (cols.Length <= Math.Max(urlIndex, statusIndex))
            {
                continue;
            }

            string url = cols[urlIndex].Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            int.TryParse(cols[statusIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int statusCode);
            records.Add(new PageRecord(url, statusCode));
        }

        return records;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && inQuotes == false)
            {
                fields.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    private string ResolvePath(string? configuredPath, string defaultRelativePath)
    {
        string solutionRoot = Directory.GetParent(_hostEnvironment.ContentRootPath)?.FullName ?? _hostEnvironment.ContentRootPath;
        string candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(solutionRoot, defaultRelativePath)
            : configuredPath;

        if (Path.IsPathRooted(candidate))
        {
            return candidate;
        }

        return Path.GetFullPath(Path.Combine(solutionRoot, candidate));
    }

    private void RepairImportedImages()
    {
        string webRoot = Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot");
        int repaired = 0;
        int skipped = 0;

        foreach (IMedia root in _mediaService.GetRootMedia())
        {
            IEnumerable<IMedia> descendants = _mediaService.GetPagedDescendants(root.Id, 0, 10000, out _);
            foreach (IMedia media in descendants.Where(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Image))
            {
                string? src = ExtractMediaSrc(media.GetValue(Constants.Conventions.Media.File)?.ToString());
                if (string.IsNullOrWhiteSpace(src))
                {
                    skipped++;
                    continue;
                }

                string localPath = ResolveMediaLocalPath(webRoot, src);
                if (System.IO.File.Exists(localPath) == false)
                {
                    skipped++;
                    continue;
                }

                string fileName = Path.GetFileName(localPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    byte[] fileBytes = System.IO.File.ReadAllBytes(localPath);
                    using var stream = new MemoryStream(fileBytes, writable: false);
                    media.SetValue(
                        _mediaFileManager,
                        _mediaUrlGenerators,
                        _shortStringHelper,
                        _contentTypeBaseServiceProvider,
                        Constants.Conventions.Media.File,
                        fileName,
                        stream);

                    _mediaService.Save(media);
                    repaired++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Image repair skipped for media {MediaId}", media.Id);
                    skipped++;
                }
            }
        }

        _logger.LogInformation("Image media repair completed. Repaired {Repaired}, skipped {Skipped}.", repaired, skipped);
    }

    private void NormalizeImportedArticles()
    {
        IContentType? articleContentType = _contentTypeService.Get("articlePage");
        if (articleContentType == null)
        {
            _logger.LogWarning("Imported article normalization skipped: articlePage content type missing.");
            return;
        }

        int scanned = 0;
        int normalized = 0;
        int datesBackfilled = 0;
        int bodyCleaned = 0;
        int blocksReset = 0;
        int tagsBackfilled = 0;
        int primaryImagesBackfilled = 0;
        int duplicatesRemoved = 0;

        string baseUrl = _configuration["LegacyMigration:BaseUrl"] ?? "https://tmmonline.nz";
        IContent? home = _contentService.GetRootContent().FirstOrDefault(x => x.ContentType.Alias == "homePage");
        Dictionary<string, IContent> sections = home != null ? GetSections(home) : new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);
        IContent? defaultSection = home != null ? GetDefaultSection(home, sections) : null;
        IMedia? articleMediaFolder = GetArticleMediaFolder();
        using HttpClient? httpClient = articleMediaFolder != null ? CreateHttpClient() : null;
        var importedImageByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var legacyImageBySourceUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var legacySectionBySourceUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var forcedSectionByUrl = httpClient != null
            ? BuildForcedSectionByUrl(httpClient, baseUrl)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<IContent> articles = _contentService.GetPagedOfType(articleContentType.Id, 0, int.MaxValue, out _, null!, null);
        foreach (IContent article in articles)
        {
            scanned++;
            bool changed = false;

            string bodyHtml = article.GetValue("bodyText")?.ToString() ?? string.Empty;
            string cleanedBody = CleanBodyHtmlLeadingNoise(bodyHtml, article.Name, out DateTime? extractedPublishedOn, out _, null);
            if (extractedPublishedOn.HasValue == false)
            {
                extractedPublishedOn = TryExtractPublishedOnFromBodyHtml(bodyHtml);
            }

            if (string.Equals(cleanedBody, bodyHtml, StringComparison.Ordinal) == false)
            {
                SetValueIfExists(article, "bodyText", cleanedBody);
                changed = true;
                bodyCleaned++;
            }

            if (HasPublishedOnValue(article) == false && extractedPublishedOn.HasValue)
            {
                SetValueIfExists(article, "publishedOn", extractedPublishedOn.Value);
                changed = true;
                datesBackfilled++;
            }

            string existingTagList = article.GetValue("articleTags")?.ToString() ?? string.Empty;
            string? inferredSectionSlug = InferArticleSectionSlug(article, forcedSectionByUrl, legacySectionBySourceUrl, httpClient, baseUrl);
            string normalizedTagList = BuildArticleTags(inferredSectionSlug, article.Name);
            bool isTargetSectionTag = inferredSectionSlug != null
                && (inferredSectionSlug.Equals("people", StringComparison.OrdinalIgnoreCase)
                    || inferredSectionSlug.Equals("conference", StringComparison.OrdinalIgnoreCase)
                    || inferredSectionSlug.Equals("property-news", StringComparison.OrdinalIgnoreCase)
                    || inferredSectionSlug.Equals("news-bites", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(normalizedTagList) == false
                && string.Equals(existingTagList, normalizedTagList, StringComparison.OrdinalIgnoreCase) == false)
            {
                SetValueIfExists(article, "articleTags", normalizedTagList);
                changed = true;
                tagsBackfilled++;
            }
            else if (isTargetSectionTag)
            {
                // Re-set through Umbraco to rebuild tag relationships after any direct DB backfill.
                SetValueIfExists(article, "articleTags", normalizedTagList);
                changed = true;
            }

            if (defaultSection != null)
            {
                IContent targetSection = ResolveParentSection(sections, defaultSection, inferredSectionSlug);
                if (targetSection.Id != article.ParentId)
                {
                    try
                    {
                        _contentService.Move(article, targetSection.Id);
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Could not move article {ArticleId} to section {SectionId} during normalization; keeping current parent {ParentId}.",
                            article.Id,
                            targetSection.Id,
                            article.ParentId);
                    }
                }
            }

            if (HasPrimaryImageValue(article) == false && articleMediaFolder != null && httpClient != null)
            {
                string? fallbackImageUrl = ExtractFirstImageUrlFromBodyHtml(cleanedBody, baseUrl)
                    ?? ExtractFirstImageUrlFromBodyHtml(bodyHtml, baseUrl);

                if (string.IsNullOrWhiteSpace(fallbackImageUrl))
                {
                    string? legacySourceUrl = article.GetValue("legacySourceUrl")?.ToString();
                    if (string.IsNullOrWhiteSpace(legacySourceUrl) == false)
                    {
                        if (legacyImageBySourceUrl.TryGetValue(legacySourceUrl, out string? cachedImageUrl))
                        {
                            fallbackImageUrl = string.IsNullOrWhiteSpace(cachedImageUrl) ? null : cachedImageUrl;
                        }
                        else
                        {
                            fallbackImageUrl = TryFetchMainImageUrlFromLegacySource(httpClient, legacySourceUrl);
                            legacyImageBySourceUrl[legacySourceUrl] = fallbackImageUrl ?? string.Empty;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(fallbackImageUrl) == false)
                {
                    string? mediaUdi;
                    if (importedImageByUrl.TryGetValue(fallbackImageUrl, out string? existingUdi))
                    {
                        mediaUdi = existingUdi;
                    }
                    else
                    {
                        mediaUdi = TryCreateArticleImageMedia(httpClient, articleMediaFolder.Id, fallbackImageUrl, article.Name);
                        if (string.IsNullOrWhiteSpace(mediaUdi) == false)
                        {
                            importedImageByUrl[fallbackImageUrl] = mediaUdi;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(mediaUdi) == false)
                    {
                        SetValueIfExists(article, "primaryImage", mediaUdi);
                        changed = true;
                        primaryImagesBackfilled++;
                    }
                }
            }

            if (article.HasProperty("contentBlocks"))
            {
                string rawBlocks = article.GetValue("contentBlocks")?.ToString() ?? string.Empty;
                if (LooksLikeInvalidBlockListValue(rawBlocks))
                {
                    article.SetValue("contentBlocks", "[]");
                    changed = true;
                    blocksReset++;
                }
            }

            if (changed)
            {
                _contentService.Save(article);
                _contentService.Publish(article, Array.Empty<string>());
                normalized++;
            }
        }

        string? conferenceSeedUrl = BuildSectionSeedMap(baseUrl)
            .FirstOrDefault(x => x.TargetSectionSlug.Equals("conference", StringComparison.OrdinalIgnoreCase))
            .SectionUrl;

        EnsureSectionSeedArticle(
            articleContentType,
            "conference",
            "Conference Coverage Coming Soon",
            "Conference content is being migrated and will appear here shortly.",
            conferenceSeedUrl,
            baseUrl);
        EnsureSectionSeedArticle(articleContentType, "property-news", "Property News Coverage Coming Soon", "Property news content is being migrated and will appear here shortly.");

        duplicatesRemoved = DeduplicateImportedArticles(
            articleContentType.Id,
            sections,
            defaultSection,
            forcedSectionByUrl,
            legacySectionBySourceUrl,
            httpClient,
            baseUrl);

        _logger.LogInformation(
            "Imported article normalization completed. Scanned {Scanned}, normalized {Normalized}, dates backfilled {DatesBackfilled}, body cleaned {BodyCleaned}, tags backfilled {TagsBackfilled}, primary images backfilled {PrimaryImagesBackfilled}, contentBlocks reset {BlocksReset}, duplicates removed {DuplicatesRemoved}.",
            scanned,
            normalized,
            datesBackfilled,
            bodyCleaned,
            tagsBackfilled,
            primaryImagesBackfilled,
            blocksReset,
            duplicatesRemoved);
    }

    private int DeduplicateImportedArticles(
        int articleContentTypeId,
        IReadOnlyDictionary<string, IContent> sections,
        IContent? defaultSection,
        IReadOnlyDictionary<string, string> forcedSectionByUrl,
        IDictionary<string, string> legacySectionBySourceUrl,
        HttpClient? httpClient,
        string baseUrl)
    {
        IEnumerable<IContent> articles = _contentService.GetPagedOfType(articleContentTypeId, 0, int.MaxValue, out _, null!, null);
        var byKey = new Dictionary<string, List<IContent>>(StringComparer.OrdinalIgnoreCase);

        foreach (IContent article in articles)
        {
            string key = BuildDuplicateKey(article);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (byKey.TryGetValue(key, out List<IContent>? grouped) == false)
            {
                grouped = [];
                byKey[key] = grouped;
            }

            grouped.Add(article);
        }

        int removed = 0;
        foreach ((string _, List<IContent> grouped) in byKey.Where(x => x.Value.Count > 1))
        {
            string? inferredSectionSlug = InferArticleSectionSlug(grouped[0], forcedSectionByUrl, legacySectionBySourceUrl, httpClient, baseUrl);
            int? targetSectionId = defaultSection != null
                ? ResolveParentSection(sections, defaultSection, inferredSectionSlug).Id
                : null;

            IContent keeper = ChooseDuplicateKeeper(grouped, targetSectionId);

            if (targetSectionId.HasValue && keeper.ParentId != targetSectionId.Value)
            {
                try
                {
                    _contentService.Move(keeper, targetSectionId.Value);
                    _contentService.Save(keeper);
                    _contentService.Publish(keeper, Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not move dedupe keeper article {ArticleId} to section {SectionId}.", keeper.Id, targetSectionId.Value);
                }
            }

            foreach (IContent duplicate in grouped.Where(x => x.Id != keeper.Id))
            {
                try
                {
                    bool keeperChanged = MergeArticleValues(keeper, duplicate);
                    if (keeperChanged)
                    {
                        _contentService.Save(keeper);
                        _contentService.Publish(keeper, Array.Empty<string>());
                    }

                    _contentService.Delete(duplicate);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove duplicate article {DuplicateId}; keeping both records for manual review.", duplicate.Id);
                }
            }
        }

        return removed;
    }

    private static string BuildDuplicateKey(IContent article)
    {
        string legacySourceUrl = article.GetValue("legacySourceUrl")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(legacySourceUrl))
        {
            return string.Empty;
        }

        string? aliasKey = ExtractLegacyArticleAliasKey(legacySourceUrl);
        if (string.IsNullOrWhiteSpace(aliasKey) == false)
        {
            return aliasKey;
        }

        return $"legacy:{NormalizeLegacyUrlKey(legacySourceUrl)}";
    }

    private static IContent ChooseDuplicateKeeper(IEnumerable<IContent> grouped, int? targetSectionId)
    {
        return grouped
            .OrderByDescending(x => targetSectionId.HasValue && x.ParentId == targetSectionId.Value)
            .ThenByDescending(HasPrimaryImageValue)
            .ThenByDescending(HasPublishedOnValue)
            .ThenByDescending(x => (x.GetValue("bodyText")?.ToString() ?? string.Empty).Length)
            .ThenByDescending(x => x.UpdateDate)
            .ThenBy(x => x.Id)
            .First();
    }

    private static bool MergeArticleValues(IContent keeper, IContent duplicate)
    {
        bool changed = false;

        if (HasPrimaryImageValue(keeper) == false && HasPrimaryImageValue(duplicate))
        {
            SetValueIfExists(keeper, "primaryImage", duplicate.GetValue("primaryImage")?.ToString() ?? string.Empty);
            changed = true;
        }

        string keeperBody = keeper.GetValue("bodyText")?.ToString() ?? string.Empty;
        string duplicateBody = duplicate.GetValue("bodyText")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keeperBody) && string.IsNullOrWhiteSpace(duplicateBody) == false)
        {
            SetValueIfExists(keeper, "bodyText", duplicateBody);
            changed = true;
        }

        if (HasPublishedOnValue(keeper) == false && HasPublishedOnValue(duplicate))
        {
            SetValueIfExists(keeper, "publishedOn", duplicate.GetValue("publishedOn")!);
            changed = true;
        }

        string keeperTags = keeper.GetValue("articleTags")?.ToString() ?? string.Empty;
        string duplicateTags = duplicate.GetValue("articleTags")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keeperTags) && string.IsNullOrWhiteSpace(duplicateTags) == false)
        {
            SetValueIfExists(keeper, "articleTags", duplicateTags);
            changed = true;
        }

        return changed;
    }

    private void EnsureSectionSeedArticle(
        IContentType articleContentType,
        string sectionSlug,
        string seedTitle,
        string seedMessage,
        string? seedSourceUrl = null,
        string? baseUrl = null)
    {
        bool hasTaggedArticle = _contentService.GetPagedOfType(articleContentType.Id, 0, int.MaxValue, out _, null!, null)
            .Any(x => ContentTagParser.ParseTags(x.GetValue("articleTags")?.ToString()).Contains(sectionSlug, StringComparer.OrdinalIgnoreCase));
        // Conference uses a curated seed from the legacy better-business landing page,
        // so we allow refreshing it even when a single placeholder already exists.
        if (hasTaggedArticle && sectionSlug.Equals("conference", StringComparison.OrdinalIgnoreCase) == false)
        {
            return;
        }

        IContent? home = _contentService.GetRootContent().FirstOrDefault(x => x.ContentType.Alias == "homePage");
        if (home == null)
        {
            return;
        }

        Dictionary<string, IContent> sections = GetSections(home);
        if (sections.TryGetValue(sectionSlug, out IContent? sectionNode) == false)
        {
            return;
        }

        List<IContent> sectionArticles = _contentService.GetPagedChildren(sectionNode.Id, 0, 200, out _)
            .Where(x => x.ContentType.Alias == "articlePage")
            .ToList();

        IContent? existing = null;

        if (string.IsNullOrWhiteSpace(seedSourceUrl) == false)
        {
            existing = sectionArticles.FirstOrDefault(x =>
                string.Equals(x.GetValue("legacySourceUrl")?.ToString(), seedSourceUrl, StringComparison.OrdinalIgnoreCase));
        }

        existing ??= sectionArticles.FirstOrDefault(x =>
            x.Name.Equals(seedTitle, StringComparison.OrdinalIgnoreCase));

        if (existing == null && sectionSlug.Equals("conference", StringComparison.OrdinalIgnoreCase))
        {
            existing = _contentService.GetPagedOfType(articleContentType.Id, 0, int.MaxValue, out _, null!, null)
                .FirstOrDefault(x =>
                    string.Equals(x.GetValue("legacySourceUrl")?.ToString(), seedSourceUrl, StringComparison.OrdinalIgnoreCase))
                ?? _contentService.GetPagedOfType(articleContentType.Id, 0, int.MaxValue, out _, null!, null)
                    .FirstOrDefault(x => x.Name.Equals(seedTitle, StringComparison.OrdinalIgnoreCase))
                ?? _contentService.GetPagedOfType(articleContentType.Id, 0, int.MaxValue, out _, null!, null)
                    .FirstOrDefault(x =>
                        ContentTagParser.ParseTags(x.GetValue("articleTags")?.ToString()).Contains("conference", StringComparer.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = sectionArticles.FirstOrDefault(x =>
                    ContentTagParser.ParseTags(x.GetValue("articleTags")?.ToString()).Contains("conference", StringComparer.OrdinalIgnoreCase));
            }
        }

        if (existing != null && existing.ParentId != sectionNode.Id)
        {
            try
            {
                _contentService.Move(existing, sectionNode.Id);
                _contentService.Save(existing);
                _contentService.Publish(existing, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not move seed article {ArticleId} into section {SectionId}; keeping current parent {ParentId}.",
                    existing.Id,
                    sectionNode.Id,
                    existing.ParentId);
            }
        }

        if (existing == null)
        {
            existing = sectionArticles.FirstOrDefault(x =>
                ContentTagParser.ParseTags(x.GetValue("articleTags")?.ToString()).Contains("conference", StringComparer.OrdinalIgnoreCase));
        }

        string articleTitle = seedTitle;
        string leadText = seedMessage;
        string introText = seedMessage;
        string bodyText = $"<p>{seedMessage}</p>";
        string metaDescription = seedMessage;

        if (sectionSlug.Equals("conference", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(seedSourceUrl) == false
            && string.IsNullOrWhiteSpace(baseUrl) == false)
        {
            try
            {
                using HttpClient httpClient = CreateHttpClient();
                LegacyArticleData? landingData = TryLoadArticleData(httpClient, seedSourceUrl, baseUrl);
                if (landingData != null)
                {
                    articleTitle = string.IsNullOrWhiteSpace(landingData.Title) ? articleTitle : landingData.Title;
                    leadText = string.IsNullOrWhiteSpace(landingData.LeadText) ? leadText : landingData.LeadText;
                    introText = string.IsNullOrWhiteSpace(landingData.IntroText) ? introText : landingData.IntroText;
                    bodyText = string.IsNullOrWhiteSpace(landingData.BodyHtml) ? bodyText : landingData.BodyHtml;
                    metaDescription = string.IsNullOrWhiteSpace(landingData.MetaDescription) ? metaDescription : landingData.MetaDescription;

                    // The conference landing page can lack a concise meta description/lead paragraph.
                    // Fall back to the imported title so section cards don't retain placeholder copy.
                    const string conferenceSummary = "Better Business Programme schedule and speaker lineup.";

                    if (string.Equals(leadText, seedMessage, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(leadText))
                    {
                        leadText = conferenceSummary;
                    }

                    if (string.Equals(introText, seedMessage, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(introText))
                    {
                        introText = leadText;
                    }

                    if (string.Equals(metaDescription, seedMessage, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(metaDescription))
                    {
                        metaDescription = conferenceSummary;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to hydrate conference seed article from {SeedSourceUrl}.", seedSourceUrl);
            }
        }

        IContent article = existing ?? _contentService.Create(articleTitle, sectionNode.Id, "articlePage");
        article.Name = articleTitle;
        SetValueIfExists(article, "heroHeading", articleTitle);
        SetValueIfExists(article, "leadText", leadText);
        SetValueIfExists(article, "introText", introText);
        SetValueIfExists(article, "bodyText", bodyText);
        SetValueIfExists(article, "metaTitle", articleTitle);
        SetValueIfExists(article, "metaDescription", metaDescription);
        SetValueIfExists(article, "articleTags", sectionSlug);
        SetValueIfExists(article, "publishedOn", DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(seedSourceUrl) == false)
        {
            SetValueIfExists(article, "legacySourceUrl", seedSourceUrl);
        }

        _contentService.Save(article);
        _contentService.Publish(article, Array.Empty<string>());
    }

    private Dictionary<string, string> BuildForcedSectionByUrl(HttpClient httpClient, string baseUrl)
    {
        var forcedSectionByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string targetSectionSlug, string sectionUrl) in BuildSectionSeedMap(baseUrl))
        {
            IReadOnlyList<string> sectionArticleUrls = DiscoverSectionArticleUrls(httpClient, sectionUrl);
            foreach (string articleUrl in sectionArticleUrls)
            {
                string normalizedKey = NormalizeLegacyUrlKey(articleUrl);
                if (forcedSectionByUrl.ContainsKey(normalizedKey) == false)
                {
                    forcedSectionByUrl[normalizedKey] = targetSectionSlug;
                }
            }
        }

        return forcedSectionByUrl;
    }

    private string? InferArticleSectionSlug(
        IContent article,
        IReadOnlyDictionary<string, string> forcedSectionByUrl,
        IDictionary<string, string> legacySectionBySourceUrl,
        HttpClient? httpClient,
        string baseUrl)
    {
        string? legacySourceUrl = article.GetValue("legacySourceUrl")?.ToString();
        if (string.IsNullOrWhiteSpace(legacySourceUrl) == false)
        {
            string normalizedKey = NormalizeLegacyUrlKey(legacySourceUrl);
            if (forcedSectionByUrl.TryGetValue(normalizedKey, out string? forcedSectionSlug)
                && string.IsNullOrWhiteSpace(forcedSectionSlug) == false)
            {
                return forcedSectionSlug;
            }

            if (legacySectionBySourceUrl.TryGetValue(normalizedKey, out string? cachedSectionSlug)
                && string.IsNullOrWhiteSpace(cachedSectionSlug) == false)
            {
                return cachedSectionSlug;
            }

            if (httpClient != null)
            {
                LegacyArticleData? data = TryLoadArticleData(httpClient, legacySourceUrl, baseUrl);
                if (data != null && string.IsNullOrWhiteSpace(data.SectionSlug) == false)
                {
                    legacySectionBySourceUrl[normalizedKey] = data.SectionSlug;
                    return data.SectionSlug;
                }
            }
        }

        IContent? parent = _contentService.GetById(article.ParentId);
        if (parent != null && parent.ContentType.Alias.Equals("sectionPage", StringComparison.OrdinalIgnoreCase))
        {
            string parentSlug = ContentTagParser.NormalizeTag(parent.Name);
            if (string.IsNullOrWhiteSpace(parentSlug) == false)
            {
                return parentSlug;
            }
        }

        return null;
    }

    private void ImportHeaderBannerImageIfConfigured()
    {
        string? localPath = ResolvePath(_configuration["LegacyMigration:HeaderBannerImagePath"], string.Empty);
        if (string.IsNullOrWhiteSpace(localPath) || System.IO.File.Exists(localPath) == false)
        {
            return;
        }

        _logger.LogInformation("Attempting header banner import from {LocalPath}.", localPath);

        IContent? home = _contentService.GetRootContent().FirstOrDefault(x => x.ContentType.Alias == "homePage");
        if (home == null || home.HasProperty("headerBannerImage") == false)
        {
            _logger.LogWarning("Header banner import skipped: home page or headerBannerImage property not available.");
            return;
        }

        string fileName = Path.GetFileName(localPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        IMedia root = EnsureRootMediaFolder("Content Type Media");
        IMedia homeFolder = EnsureChildMediaFolder(root.Id, "homePage");

        string mediaName = Path.GetFileNameWithoutExtension(fileName);
        IMedia? existingMedia = _mediaService.GetPagedChildren(homeFolder.Id, 0, 200, out _)
            .FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Image
                && x.Name.Equals(mediaName, StringComparison.OrdinalIgnoreCase));

        IMedia bannerMedia = existingMedia ?? CreateMediaFromLocalFile(homeFolder.Id, localPath, mediaName);
        if (existingMedia == null)
        {
            _logger.LogInformation("Imported header banner image {FileName} into media item {MediaId}.", fileName, bannerMedia.Id);
        }

        string bannerUdi = $"umb://media/{bannerMedia.Key:D}";
        string? currentValue = home.GetValue("headerBannerImage")?.ToString();
        if (string.Equals(currentValue, bannerUdi, StringComparison.OrdinalIgnoreCase) == false)
        {
            home.SetValue("headerBannerImage", bannerUdi);
            _contentService.Save(home);
            _contentService.Publish(home, Array.Empty<string>());
            _logger.LogInformation("Assigned header banner image {BannerUdi} to home page.", bannerUdi);
        }

        string? finalValue = home.GetValue("headerBannerImage")?.ToString();
        _logger.LogInformation(
            "Header banner state after sync: media id {MediaId}, media key {MediaKey}, home value {HomeValue}.",
            bannerMedia.Id,
            bannerMedia.Key,
            finalValue ?? "<null>");
    }

    private IMedia CreateMediaFromLocalFile(int parentId, string localPath, string mediaName)
    {
        byte[] fileBytes = System.IO.File.ReadAllBytes(localPath);
        using var stream = new MemoryStream(fileBytes, writable: false);

        string extension = Path.GetExtension(localPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        string safeName = string.IsNullOrWhiteSpace(mediaName) ? "Header Banner" : mediaName;
        IMedia media = _mediaService.CreateMedia(safeName, parentId, Constants.Conventions.MediaTypes.Image);
        media.SetValue(
            _mediaFileManager,
            _mediaUrlGenerators,
            _shortStringHelper,
            _contentTypeBaseServiceProvider,
            Constants.Conventions.Media.File,
            $"{safeName}{extension}",
            stream);

        _mediaService.Save(media);
        return media;
    }

    private IMedia EnsureRootMediaFolder(string name)
    {
        IMedia? existing = FindRootMediaFolder(name);
        if (existing != null)
        {
            return existing;
        }

        IMedia created = _mediaService.CreateMedia(name, -1, Constants.Conventions.MediaTypes.Folder);
        _mediaService.Save(created);
        return created;
    }

    private IMedia EnsureChildMediaFolder(int parentId, string name)
    {
        IMedia? existing = FindChildMediaFolder(parentId, name);
        if (existing != null)
        {
            return existing;
        }

        IMedia created = _mediaService.CreateMedia(name, parentId, Constants.Conventions.MediaTypes.Folder);
        _mediaService.Save(created);
        return created;
    }

    private static bool HasPublishedOnValue(IContent article)
    {
        if (article.HasProperty("publishedOn") == false)
        {
            return false;
        }

        object? value = article.GetValue("publishedOn");
        if (value is DateTime dateTime)
        {
            return dateTime > DateTime.MinValue;
        }

        if (value is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
        {
            return parsed > DateTime.MinValue;
        }

        return false;
    }

    private static bool HasPrimaryImageValue(IContent article)
    {
        if (article.HasProperty("primaryImage") == false)
        {
            return false;
        }

        string value = article.GetValue("primaryImage")?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) == false;
    }

    private static string? ExtractFirstImageUrlFromBodyHtml(string bodyHtml, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml))
        {
            return null;
        }

        Match match = Regex.Match(
            bodyHtml,
            "<img[^>]*\\bsrc\\s*=\\s*[\"'](?<value>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (match.Success == false)
        {
            return null;
        }

        string src = match.Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(src)
            || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (src.StartsWith("//", StringComparison.Ordinal))
        {
            src = $"https:{src}";
        }

        if (Uri.TryCreate(src, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(new Uri(baseUrl), src, out Uri? resolved))
        {
            return resolved.AbsoluteUri;
        }

        return null;
    }

    private string? TryFetchMainImageUrlFromLegacySource(HttpClient httpClient, string sourceUrl)
    {
        try
        {
            if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? sourceUri) == false)
            {
                return null;
            }

            using HttpResponseMessage response = httpClient.GetAsync(sourceUri).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode == false)
            {
                return null;
            }

            string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ExtractMainImageUrl(html, sourceUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch fallback image from legacy source URL {SourceUrl}", sourceUrl);
            return null;
        }
    }

    private static string CleanBodyHtmlLeadingNoise(
        string bodyHtml,
        string articleTitle,
        out DateTime? extractedPublishedOn,
        out int removedLeadingLines,
        string? expectedMainImageUrl = null)
    {
        extractedPublishedOn = null;
        removedLeadingLines = 0;

        if (string.IsNullOrWhiteSpace(bodyHtml))
        {
            return string.Empty;
        }

        string working = bodyHtml;
        working = TrimLeadingNoiseCharacters(working);
        working = Regex.Replace(working, "^\\s*<header[^>]*>.*?</header>\\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove leading empty wrappers and line-break-only blocks before semantic cleanup.
        for (int i = 0; i < 6; i++)
        {
            string withoutEmptyBlocks = Regex.Replace(
                working,
                "^\\s*<(p|div|span)[^>]*>(?:\\s|&nbsp;|&#160;|<br\\s*/?>|<p[^>]*>(?:\\s|&nbsp;|&#160;|<br\\s*/?>)*</p>)*</\\1>\\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            string withoutLeadingBreaks = Regex.Replace(
                withoutEmptyBlocks,
                "^(?:\\s|&nbsp;|&#160;|<br\\s*/?>)+",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            withoutLeadingBreaks = TrimLeadingNoiseCharacters(withoutLeadingBreaks);

            if (string.Equals(withoutLeadingBreaks, working, StringComparison.Ordinal))
            {
                break;
            }

            working = withoutLeadingBreaks;
        }

        // Remove leading empty bullet/list markup that appears as blank bullets in the editor.
        for (int i = 0; i < 4; i++)
        {
            string withoutEmptyLists = Regex.Replace(
                working,
                "^\\s*<(ul|ol)[^>]*>(?:\\s*<li[^>]*>(?:\\s|&nbsp;|&#160;|<br\\s*/?>|<p[^>]*>(?:\\s|&nbsp;|&#160;|<br\\s*/?>)*</p>)*</li>)+\\s*</\\1>\\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            string withoutEmptyItems = Regex.Replace(
                withoutEmptyLists,
                "^\\s*<li[^>]*>(?:\\s|&nbsp;|&#160;|<br\\s*/?>|<p[^>]*>(?:\\s|&nbsp;|&#160;|<br\\s*/?>)*</p>)*</li>\\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (string.Equals(withoutEmptyItems, working, StringComparison.Ordinal))
            {
                break;
            }

            working = withoutEmptyItems;
        }

        working = RemoveLeadingHeroImageBlock(working, articleTitle, expectedMainImageUrl);
        working = RemoveDuplicateLeadingListBlocks(working);

        for (int i = 0; i < 6; i++)
        {
            working = Regex.Replace(working, "^\\s*<!--.*?-->\\s*", string.Empty, RegexOptions.Singleline);

            Match htmlBlock = Regex.Match(
                working,
            "^\\s*<(p|h1|h2|h3|h4|h5|h6|div|span|strong|em|time)[^>]*>(?<value>.*?)</\\1>\\s*",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            string? tokenText = null;
            int tokenLength = 0;

            if (htmlBlock.Success)
            {
                tokenText = HtmlDecodeAndStrip(htmlBlock.Groups["value"].Value);
                tokenLength = htmlBlock.Length;
            }
            else
            {
                Match plainLine = Regex.Match(working, "^\\s*(?<value>[^<\\r\\n][^\\r\\n]*)(?:\\r?\\n)+", RegexOptions.Singleline);
                if (plainLine.Success)
                {
                    tokenText = HtmlDecodeAndStrip(plainLine.Groups["value"].Value);
                    tokenLength = plainLine.Length;
                }
            }

            if (string.IsNullOrWhiteSpace(tokenText) || tokenLength <= 0)
            {
                break;
            }

            bool dropAsTitle = IsDuplicateTitleLine(tokenText, articleTitle);
            bool dropAsDate = TryParsePublishedDate(tokenText, out DateTime parsedDate);

            if (dropAsTitle == false && dropAsDate == false)
            {
                break;
            }

            if (dropAsDate && extractedPublishedOn.HasValue == false)
            {
                extractedPublishedOn = parsedDate;
            }

            working = working[tokenLength..];
            removedLeadingLines++;
        }

        return working.Trim();
    }

    private static string TrimLeadingNoiseCharacters(string value)
    {
        return value.TrimStart('\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060', '\u00A0', ' ', '\t', '\r', '\n');
    }

    private static bool IsDuplicateTitleLine(string candidate, string articleTitle)
    {
        string a = NormalizePlainText(candidate);
        string b = NormalizePlainText(articleTitle);

        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Regex.Replace(a, "\\s*\\(\\d+\\)$", string.Empty), b, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Trim('"', '\'', '“', '”'), b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePlainText(string value)
    {
        string normalized = Regex.Replace(value, "\\s+", " ").Trim();
        normalized = normalized.Replace(" - TMM Online", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return normalized;
    }

    private static string RemoveLeadingHeroImageBlock(string html, string articleTitle, string? expectedMainImageUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        Match figureBlock = Regex.Match(
            html,
            "^\\s*<figure[^>]*>\\s*<img[^>]*>\\s*(?:<figcaption[^>]*>(?<caption>.*?)</figcaption>\\s*)?</figure>\\s*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        string trimmed = html;

        if (figureBlock.Success)
        {
            string caption = HtmlDecodeAndStrip(figureBlock.Groups["caption"].Value);
            bool captionLooksLikeTitle = IsDuplicateTitleLine(caption, articleTitle);
            string figureImageSrc = ExtractRegexGroup(figureBlock.Value, "<img[^>]*\\bsrc\\s*=\\s*[\"'](?<value>[^\"']+)[\"']") ?? string.Empty;
            bool matchesExpected = IsMatchingImageSource(figureImageSrc, expectedMainImageUrl);

            if (matchesExpected || captionLooksLikeTitle || string.IsNullOrWhiteSpace(caption))
            {
                trimmed = html[figureBlock.Length..];
            }
        }

        Match wrappedImageBlock = Regex.Match(
            trimmed,
            "^\\s*<(p|div)[^>]*>\\s*(?:<a[^>]*>\\s*)?<img[^>]*\\bsrc\\s*=\\s*[\"'](?<src>[^\"']+)[\"'][^>]*>\\s*(?:</a>\\s*)?</\\1>\\s*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (wrappedImageBlock.Success)
        {
            string imageSrc = wrappedImageBlock.Groups["src"].Value;
            if (IsMatchingImageSource(imageSrc, expectedMainImageUrl) || string.IsNullOrWhiteSpace(expectedMainImageUrl))
            {
                trimmed = trimmed[wrappedImageBlock.Length..];
            }
        }

        Match titleEcho = Regex.Match(
            trimmed,
            "^\\s*<(p|div|span|strong|em)[^>]*>(?<value>.*?)</\\1>\\s*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (titleEcho.Success)
        {
            string tokenText = HtmlDecodeAndStrip(titleEcho.Groups["value"].Value);
            if (IsDuplicateTitleLine(tokenText, articleTitle))
            {
                trimmed = trimmed[titleEcho.Length..];
            }
        }

        return trimmed;
    }

    private static string RemoveDuplicateLeadingListBlocks(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        string working = html;
        for (int i = 0; i < 4; i++)
        {
            Match match = Regex.Match(
                working,
                "^\\s*(?<first><(?<tag1>ul|ol)[^>]*>.*?</\\k<tag1>>)\\s*(?<second><(?<tag2>ul|ol)[^>]*>.*?</\\k<tag2>>)\\s*",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success == false)
            {
                break;
            }

            string first = NormalizePlainText(HtmlDecodeAndStrip(match.Groups["first"].Value));
            string second = NormalizePlainText(HtmlDecodeAndStrip(match.Groups["second"].Value));

            if (string.IsNullOrWhiteSpace(first) || string.Equals(first, second, StringComparison.OrdinalIgnoreCase) == false)
            {
                break;
            }

            int secondStart = match.Groups["second"].Index;
            working = working[..secondStart] + working[(secondStart + match.Groups["second"].Length)..];
        }

        return working;
    }

    private static bool IsMatchingImageSource(string? source, string? expectedMainImageUrl)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(expectedMainImageUrl))
        {
            return false;
        }

        string normalizedSource = NormalizeImageSourceForComparison(source);
        string normalizedExpected = NormalizeImageSourceForComparison(expectedMainImageUrl);
        return string.Equals(normalizedSource, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageSourceForComparison(string source)
    {
        string cleaned = source.Trim();
        if (cleaned.StartsWith("//", StringComparison.Ordinal))
        {
            cleaned = $"https:{cleaned}";
        }

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        }

        return cleaned.Split('?', '#')[0].TrimEnd('/').ToLowerInvariant();
    }

    private static bool TryParsePublishedDate(string input, out DateTime publishedOn)
    {
        publishedOn = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string cleaned = Regex.Replace(input, "\\b(\\d{1,2})(st|nd|rd|th)\\b", "$1", RegexOptions.IgnoreCase).Trim();
        Match dateSegment = Regex.Match(
            cleaned,
            "(?<value>(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)?\\s*,?\\s*(?:january|february|march|april|may|june|july|august|september|october|november|december)\\s+\\d{1,2}\\s*,?\\s*\\d{4})",
            RegexOptions.IgnoreCase);

        if (dateSegment.Success)
        {
            cleaned = dateSegment.Groups["value"].Value;
        }

        if (Regex.IsMatch(cleaned, "\\b(january|february|march|april|may|june|july|august|september|october|november|december)\\b", RegexOptions.IgnoreCase) == false)
        {
            return false;
        }

        if (Regex.IsMatch(cleaned, "\\b\\d{4}\\b") == false)
        {
            return false;
        }

        string[] formats =
        [
            "dddd, MMMM d yyyy",
            "dddd, MMMM d, yyyy",
            "ddd, MMMM d yyyy",
            "ddd, MMMM d, yyyy",
            "MMMM d yyyy",
            "MMMM d, yyyy",
            "d MMMM yyyy",
            "d MMM yyyy",
            "yyyy-MM-dd"
        ];

        if (DateTime.TryParseExact(cleaned, formats, CultureInfo.GetCultureInfo("en-NZ"), DateTimeStyles.AllowWhiteSpaces, out DateTime parsed)
            || DateTime.TryParse(cleaned, CultureInfo.GetCultureInfo("en-NZ"), DateTimeStyles.AllowWhiteSpaces, out parsed)
            || DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            publishedOn = parsed;
            return true;
        }

        return false;
    }

    private static DateTime? TryExtractPublishedOnFromBodyHtml(string bodyHtml)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml))
        {
            return null;
        }

        string text = HtmlDecodeAndStrip(
            Regex.Replace(
                Regex.Replace(bodyHtml, "<br\\s*/?>", "\\n", RegexOptions.IgnoreCase),
                "</p>|</div>|</h1>|</h2>|</h3>|</h4>|</h5>|</h6>|</time>",
                "\\n",
                RegexOptions.IgnoreCase));

        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string line in lines.Take(12))
        {
            if (TryParsePublishedDate(line, out DateTime parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool LooksLikeInvalidBlockListValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed == "[]")
        {
            return false;
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) == false
            && trimmed.StartsWith("{", StringComparison.Ordinal) == false)
        {
            return true;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(trimmed);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string ResolveMediaLocalPath(string webRoot, string src)
    {
        string cleaned = src.Split('?', '#')[0];
        if (Uri.TryCreate(cleaned, UriKind.Absolute, out Uri? absolute))
        {
            cleaned = absolute.AbsolutePath;
        }

        cleaned = cleaned.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(webRoot, cleaned);
    }

    private static string? ExtractMediaSrc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("{") == false)
        {
            return trimmed;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("src", out JsonElement src)
                && src.ValueKind == JsonValueKind.String)
            {
                return src.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private sealed record PageRecord(string Url, int StatusCode);

    private sealed class LegacyArticleData
    {
        public string SourceUrl { get; init; } = string.Empty;
        public string? CanonicalUrl { get; init; }
        public string Title { get; init; } = string.Empty;
        public string SectionSlug { get; init; } = string.Empty;
        public string IntroText { get; init; } = string.Empty;
        public string LeadText { get; init; } = string.Empty;
        public string MetaDescription { get; init; } = string.Empty;
        public string BodyHtml { get; init; } = string.Empty;
        public DateTime? PublishedOn { get; init; }
        public string? MainImageUrl { get; init; }
    }
}
