
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string? blobConnectionString = builder.Configuration["Umbraco:Storage:AzureBlob:Media:ConnectionString"]
    ?? builder.Configuration["Umbraco:CMS:Storage:AzureBlob:Media:ConnectionString"];
string? blobContainerName = builder.Configuration["Umbraco:Storage:AzureBlob:Media:ContainerName"]
    ?? builder.Configuration["Umbraco:CMS:Storage:AzureBlob:Media:ContainerName"];
string? blobVirtualPath = builder.Configuration["Umbraco:Storage:AzureBlob:Media:VirtualPath"]
    ?? builder.Configuration["Umbraco:CMS:Storage:AzureBlob:Media:VirtualPath"];

// Some environments store Azure Blob settings under the CMS-prefixed path.
// Mirror them into the legacy path that the storage provider binds to.
if (string.IsNullOrWhiteSpace(blobConnectionString) == false
    && string.IsNullOrWhiteSpace(blobContainerName) == false)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Umbraco:Storage:AzureBlob:Media:ConnectionString"] = blobConnectionString,
        ["Umbraco:Storage:AzureBlob:Media:ContainerName"] = blobContainerName,
        ["Umbraco:Storage:AzureBlob:Media:VirtualPath"] = blobVirtualPath
    });
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

IUmbracoBuilder umbracoBuilder = builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers();

// Use Azure Blob for media only when explicitly configured with required values.
if (string.IsNullOrWhiteSpace(blobConnectionString) == false
    && string.IsNullOrWhiteSpace(blobContainerName) == false)
{
    umbracoBuilder.AddAzureBlobMediaFileSystem();
}

umbracoBuilder.Build();

WebApplication app = builder.Build();

app.UseForwardedHeaders();

await app.BootUmbracoAsync();

Dictionary<string, int> routedSlugToNodeId = new(StringComparer.OrdinalIgnoreCase);
DateTimeOffset routedSlugCacheExpiry = DateTimeOffset.MinValue;
object routedSlugCacheLock = new();

void EnsureRoutedSlugCache()
{
    if (DateTimeOffset.UtcNow < routedSlugCacheExpiry)
    {
        return;
    }

    lock (routedSlugCacheLock)
    {
        if (DateTimeOffset.UtcNow < routedSlugCacheExpiry)
        {
            return;
        }

        var contentTypeService = app.Services.GetRequiredService<IContentTypeService>();
        var contentService = app.Services.GetRequiredService<IContentService>();

        var sectionType = contentTypeService.Get("sectionPage");
        var articleType = contentTypeService.Get("articlePage");
        if (sectionType == null && articleType == null)
        {
            routedSlugToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            routedSlugCacheExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            return;
        }

        var slugs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (sectionType != null)
        {
            var sections = contentService.GetPagedOfType(sectionType.Id, 0, int.MaxValue, out _, null!, null);
            foreach (var section in sections)
            {
                if (section.Trashed)
                {
                    continue;
                }

                foreach (string slug in GetRouteSlugs(section.Name))
                {
                    if (slugs.ContainsKey(slug) == false)
                    {
                        slugs[slug] = section.Id;
                    }
                }
            }
        }

        if (articleType != null)
        {
            var articles = contentService.GetPagedOfType(articleType.Id, 0, int.MaxValue, out _, null!, null);
            foreach (var article in articles)
            {
                if (article.Trashed)
                {
                    continue;
                }

                foreach (string slug in GetRouteSlugs(article.Name))
                {
                    // Section routes win on collision (e.g. /rates should always hit section page).
                    if (slugs.ContainsKey(slug) == false)
                    {
                        slugs[slug] = article.Id;
                    }
                }
            }
        }

        routedSlugToNodeId = slugs;
        routedSlugCacheExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
    }
}

string ToSlug(string? value)
{
    string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
    if (normalized.Length == 0)
    {
        return string.Empty;
    }

    normalized = Regex.Replace(normalized, "[^a-z0-9]+", "-");
    normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
    return normalized;
}

IEnumerable<string> GetRouteSlugs(string? value)
{
    string baseValue = value ?? string.Empty;

    string primary = ToSlug(baseValue);
    if (string.IsNullOrWhiteSpace(primary) == false)
    {
        yield return primary;
    }

    // Legacy links often collapse apostrophes (e.g. basecorp's -> basecorps).
    string apostropheCollapsed = baseValue
        .Replace("'", string.Empty, StringComparison.Ordinal)
        .Replace("\u2019", string.Empty, StringComparison.Ordinal)
        .Replace("\u2018", string.Empty, StringComparison.Ordinal);

    string alternate = ToSlug(apostropheCollapsed);
    if (string.IsNullOrWhiteSpace(alternate) == false
        && string.Equals(alternate, primary, StringComparison.OrdinalIgnoreCase) == false)
    {
        yield return alternate;
    }
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Unhandled request exception. Method={Method} Path={Path} Query={QueryString} TraceId={TraceIdentifier}",
            context.Request.Method,
            context.Request.Path.Value,
            context.Request.QueryString.Value,
            context.TraceIdentifier);
        throw;
    }
});

app.Use(async (context, next) =>
{
    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.Request.Query.ContainsKey("node") == false)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string trimmed = path.Trim('/');
        bool hasSingleSegment = trimmed.Length > 0 && trimmed.Contains('/') == false;
        bool looksLikeFile = trimmed.Contains('.');

        if (hasSingleSegment && looksLikeFile == false)
        {
            EnsureRoutedSlugCache();
            if (routedSlugToNodeId.TryGetValue(trimmed.ToLowerInvariant(), out int nodeId))
            {
                context.Request.Path = "/";
                string existingQuery = context.Request.QueryString.HasValue
                    ? context.Request.QueryString.Value!.TrimStart('?')
                    : string.Empty;
                string nodeQuery = $"node={nodeId}";
                context.Request.QueryString = string.IsNullOrWhiteSpace(existingQuery)
                    ? new QueryString($"?{nodeQuery}")
                    : new QueryString($"?{nodeQuery}&{existingQuery}");
            }
        }
    }

    await next();
});


app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();
