using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Strings;
using System.Linq;

namespace TMMOnline.Web.DoctypeProvisioning;

public sealed class DoctypeProvisioningComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Components().Append<DoctypeProvisioningComponent>();
        builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentSavedNotification, ArticlePrimaryImageMediaRoutingHandler>();
    }
}

public sealed class DoctypeProvisioningComponent : IComponent
{
    private const string SkipProvisioningEnvironmentVariable = "TMMONLINE_SKIP_DOCTYPE_PROVISIONING";
    private const string ForceRunEnvironmentVariable = "TMMONLINE_DOCTYPE_PROVISIONING_FORCE";
    private const string DefaultTopAdvertLinkUrl = "https://tella.co.nz/";

    private readonly IContentTypeService _contentTypeService;
    private readonly IMediaTypeService _mediaTypeService;
    private readonly IDataTypeService _dataTypeService;
    private readonly IContentService _contentService;
    private readonly IFileService _fileService;
    private readonly IMediaService _mediaService;
    private readonly PropertyEditorCollection _propertyEditors;
    private readonly IConfigurationEditorJsonSerializer _configurationEditorJsonSerializer;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly ILogger<DoctypeProvisioningComponent> _logger;

    public DoctypeProvisioningComponent(
        IContentTypeService contentTypeService,
        IMediaTypeService mediaTypeService,
        IDataTypeService dataTypeService,
        IContentService contentService,
        IFileService fileService,
        IMediaService mediaService,
        PropertyEditorCollection propertyEditors,
        IConfigurationEditorJsonSerializer configurationEditorJsonSerializer,
        IShortStringHelper shortStringHelper,
        ILogger<DoctypeProvisioningComponent> logger)
    {
        _contentTypeService = contentTypeService;
        _mediaTypeService = mediaTypeService;
        _dataTypeService = dataTypeService;
        _contentService = contentService;
        _fileService = fileService;
        _mediaService = mediaService;
        _propertyEditors = propertyEditors;
        _configurationEditorJsonSerializer = configurationEditorJsonSerializer;
        _shortStringHelper = shortStringHelper;
        _logger = logger;
    }

    public void Initialize()
    {
        bool skipProvisioning = string.Equals(Environment.GetEnvironmentVariable(SkipProvisioningEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);
        if (skipProvisioning)
        {
            Console.WriteLine("[DoctypeProvisioning] Skipping due to TMMONLINE_SKIP_DOCTYPE_PROVISIONING=true.");
            _logger.LogInformation("Document type provisioning skipped due to environment variable {EnvironmentVariable}.", SkipProvisioningEnvironmentVariable);
            return;
        }

        bool forceRun = string.Equals(Environment.GetEnvironmentVariable(ForceRunEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);
        if (!forceRun && IsProvisioningAlreadyComplete())
        {
            EnsureAdvertMediaType();
            EnsureTopAdvertLinkSeed();
            Console.WriteLine("[DoctypeProvisioning] Skipping provisioning because target doctypes are already complete.");
            _logger.LogInformation("Document type provisioning skipped because target doctypes are already complete.");
            return;
        }

        Console.WriteLine("[DoctypeProvisioning] Starting provisioning run.");

        try
        {
            EnsureAdvertMediaType();
            EnsureMediaFolders();
            EnsureTopAdvertLinkSeed();

            IContentType pageSeo = EnsurePageSeoComposition();
            IContentType pageContent = EnsurePageContentComposition();

            IContentType home = EnsureDocType("homePage", "Home Page", true, "HomePage", pageSeo, pageContent);
            IContentType section = EnsureDocType("sectionPage", "Section Page", false, "SectionPage", pageSeo, pageContent);
            IContentType article = EnsureDocType("articlePage", "Article Page", false, "ArticlePage", pageSeo, pageContent);
            EnsureProperty(home, "content", "Content", "headerBannerImage", "Header Banner Image", Constants.DataTypes.Guids.MediaPicker3SingleImageGuid, Constants.PropertyEditors.Aliases.MediaPicker3);
            EnsureProperty(section, "content", "Content", "sectionTags", "Section Tags", Constants.DataTypes.Guids.TextareaGuid, Constants.PropertyEditors.Aliases.TextArea);
            EnsureProperty(article, "content", "Content", "primaryImage", "Primary Image", Constants.DataTypes.Guids.MediaPicker3SingleImageGuid, Constants.PropertyEditors.Aliases.MediaPicker3);
            EnsureProperty(article, "content", "Content", "articleTags", "Article Tags", Constants.DataTypes.Guids.TextareaGuid, Constants.PropertyEditors.Aliases.TextArea);
            EnsureProperty(article, "content", "Content", "legacySourceUrl", "Legacy Source URL", Constants.DataTypes.Guids.TextstringGuid, Constants.PropertyEditors.Aliases.TextBox);

            // Allowed children mirror the migration plan and are safe to re-apply.
            home.AllowedContentTypes =
            [
                new ContentTypeSort(section.Key, 0, section.Alias),
                new ContentTypeSort(article.Key, 1, article.Alias)
            ];

            section.AllowedContentTypes =
            [
                new ContentTypeSort(section.Key, 0, section.Alias),
                new ContentTypeSort(article.Key, 1, article.Alias)
            ];

            article.AllowedContentTypes = [];

            _contentTypeService.Save(home);
            _contentTypeService.Save(section);
            _contentTypeService.Save(article);

            EnsureMinimumContentTree();

            Console.WriteLine("[DoctypeProvisioning] Provisioning completed.");
            _logger.LogInformation("Document type provisioning completed.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DoctypeProvisioning] Provisioning failed: {ex}");
            _logger.LogError(ex, "Document type provisioning failed.");
        }
    }

    public void Terminate()
    {
    }

    private bool IsProvisioningAlreadyComplete()
    {
        IContentType? pageSeo = _contentTypeService.Get("pageSeo");
        IContentType? pageContent = _contentTypeService.Get("pageContent");
        IContentType? home = _contentTypeService.Get("homePage");
        IContentType? section = _contentTypeService.Get("sectionPage");
        IContentType? article = _contentTypeService.Get("articlePage");

        if (pageSeo == null || pageContent == null || home == null || section == null || article == null)
        {
            return false;
        }

        IPropertyType? contentBlocks = pageContent.PropertyTypes.FirstOrDefault(x => x.Alias == "contentBlocks");
        if (contentBlocks == null)
        {
            return false;
        }

        IPropertyType? headerBannerImage = home.PropertyTypes.FirstOrDefault(x => x.Alias == "headerBannerImage");
        if (headerBannerImage == null)
        {
            return false;
        }

        IPropertyType? primaryImage = article.PropertyTypes.FirstOrDefault(x => x.Alias == "primaryImage");
        IPropertyType? articleTags = article.PropertyTypes.FirstOrDefault(x => x.Alias == "articleTags");
        IPropertyType? legacySourceUrl = article.PropertyTypes.FirstOrDefault(x => x.Alias == "legacySourceUrl");
        IPropertyType? sectionTags = section.PropertyTypes.FirstOrDefault(x => x.Alias == "sectionTags");
        if (primaryImage == null || articleTags == null || legacySourceUrl == null || sectionTags == null)
        {
            return false;
        }

        IDataType? contentBlocksDataType = _dataTypeService.GetDataType(contentBlocks.DataTypeId);
        IDataType? headerBannerImageDataType = _dataTypeService.GetDataType(headerBannerImage.DataTypeId);
        IDataType? primaryImageDataType = _dataTypeService.GetDataType(primaryImage.DataTypeId);
        IDataType? articleTagsDataType = _dataTypeService.GetDataType(articleTags.DataTypeId);
        IDataType? sectionTagsDataType = _dataTypeService.GetDataType(sectionTags.DataTypeId);
        bool contentBlocksEditorAvailable = contentBlocksDataType != null
            && (IsRegisteredPropertyEditor(contentBlocksDataType.EditorAlias)
                || IsRegisteredPropertyEditor(contentBlocksDataType.EditorUiAlias));
        bool templatesExist = _fileService.GetTemplate("HomePage") != null
            && _fileService.GetTemplate("SectionPage") != null
            && _fileService.GetTemplate("ArticlePage") != null;

        bool templateBindingsAreCorrect = string.Equals(home.DefaultTemplate?.Alias, "HomePage", StringComparison.Ordinal)
            && string.Equals(section.DefaultTemplate?.Alias, "SectionPage", StringComparison.Ordinal)
            && string.Equals(article.DefaultTemplate?.Alias, "ArticlePage", StringComparison.Ordinal);

        ITemplate? homeTemplate = _fileService.GetTemplate("HomePage");
        IContent? homeContent = _contentService
            .GetRootContent()
            .FirstOrDefault(x => x.ContentType.Alias == "homePage");

        bool homeContentTemplateIsCorrect = homeTemplate != null
            && homeContent != null
            && homeContent.TemplateId == homeTemplate.Id;

        bool hasPublishedHome = _contentService
            .GetRootContent()
            .Any(x => x.ContentType.Alias == "homePage" && x.Published);

        return contentBlocksDataType != null
            && string.Equals(contentBlocksDataType.EditorAlias, Constants.PropertyEditors.Aliases.BlockList, StringComparison.OrdinalIgnoreCase)
            && contentBlocksEditorAvailable
            && headerBannerImageDataType != null
            && headerBannerImageDataType.Key == Constants.DataTypes.Guids.MediaPicker3SingleImageGuid
            && primaryImageDataType != null
            && primaryImageDataType.Key == Constants.DataTypes.Guids.MediaPicker3SingleImageGuid
            && articleTagsDataType != null
            && articleTagsDataType.Key == Constants.DataTypes.Guids.TextareaGuid
            && sectionTagsDataType != null
            && sectionTagsDataType.Key == Constants.DataTypes.Guids.TextareaGuid
            && templatesExist
            && templateBindingsAreCorrect
            && homeContentTemplateIsCorrect
            && hasPublishedHome
            && AreMediaFoldersProvisioned();
    }

    private void EnsureMinimumContentTree()
    {
        IContent? home = _contentService
            .GetRootContent()
            .FirstOrDefault(x => x.ContentType.Alias == "homePage");

        if (home == null)
        {
            home = _contentService.Create("TMM Online", -1, "homePage");
        }

        EnsureContentTemplate(home);
        SetValueIfExists(home, "heroHeading", "TMM Online");
        SetValueIfExists(home, "introText", "Daily news and analysis for New Zealand mortgage advisers.");
        _contentService.Save(home);
        _contentService.Publish(home, Array.Empty<string>());

        var defaultSections = new (string Name, string Tags)[]
        {
            ("News", "news"),
            ("Rates", "rates"),
            ("People", "people"),
            ("Conference", "conference"),
            ("Property News", "property-news"),
            ("News Bites", "news-bites"),
            ("Video", "video"),
            ("Podcast", "podcast")
        };

        IEnumerable<IContent> existingSections = _contentService.GetPagedChildren(home.Id, 0, 200, out _)
            .Where(x => x.ContentType.Alias == "sectionPage");

        foreach ((string name, string tags) in defaultSections)
        {
            IContent section = existingSections.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? _contentService.Create(name, home.Id, "sectionPage");

            EnsureContentTemplate(section);
            SetValueIfExists(section, "heroHeading", name);
            SetValueIfExists(section, "sectionTags", tags);
            _contentService.Save(section);
            _contentService.Publish(section, Array.Empty<string>());
        }
    }

    private static void EnsureContentTemplate(IContent content)
    {
        int defaultTemplateId = content.ContentType.DefaultTemplate?.Id ?? 0;
        if (defaultTemplateId > 0 && content.TemplateId != defaultTemplateId)
        {
            content.TemplateId = defaultTemplateId;
        }
    }

    private static void SetValueIfExists(IContent content, string alias, object value)
    {
        if (content.Properties.Any(x => x.Alias == alias))
        {
            content.SetValue(alias, value);
        }
    }

    private bool AreMediaFoldersProvisioned()
    {
        IMedia? root = FindRootMediaFolder("Content Type Media");
        if (root == null)
        {
            return false;
        }

        return FindChildMediaFolder(root.Id, "homePage") != null
            && FindChildMediaFolder(root.Id, "sectionPage") != null
            && FindChildMediaFolder(root.Id, "articlePage") != null;
    }

    private void EnsureMediaFolders()
    {
        IMedia root = EnsureRootMediaFolder("Content Type Media");
        EnsureChildMediaFolder(root.Id, "homePage");
        EnsureChildMediaFolder(root.Id, "sectionPage");
        EnsureChildMediaFolder(root.Id, "articlePage");
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

    private void EnsureChildMediaFolder(int parentId, string name)
    {
        if (FindChildMediaFolder(parentId, name) != null)
        {
            return;
        }

        IMedia created = _mediaService.CreateMedia(name, parentId, Constants.Conventions.MediaTypes.Folder);
        _mediaService.Save(created);
    }

    private IMedia? FindRootMediaFolder(string name)
        => _mediaService.GetRootMedia()
            .FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
                && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private IMedia? FindChildMediaFolder(int parentId, string name)
    {
        IEnumerable<IMedia> children = _mediaService.GetPagedChildren(parentId, 0, 200, out _);
        return children.FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
            && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureTopAdvertLinkSeed()
    {
        IMedia? advertsFolder = FindRootMediaFolder("adverts");
        if (advertsFolder == null)
        {
            return;
        }

        IMedia? topFolder = FindChildMediaFolder(advertsFolder.Id, "top");
        if (topFolder == null)
        {
            return;
        }

        IEnumerable<IMedia> topChildren = _mediaService.GetPagedChildren(topFolder.Id, 0, 50, out _);
        IMedia? firstAdvert = topChildren.FirstOrDefault(x => x.ContentType.Alias != Constants.Conventions.MediaTypes.Folder);
        if (firstAdvert == null)
        {
            return;
        }

        string[] linkAliases =
        {
            "advertLink",
            "link",
            "externalLink",
            "destinationUrl",
            "targetUrl",
            "url"
        };

        string? linkAlias = linkAliases.FirstOrDefault(alias =>
            firstAdvert.Properties.Any(x => x.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)));

        if (string.IsNullOrWhiteSpace(linkAlias))
        {
            _logger.LogInformation("Top advert seed skipped because no link alias exists on media type '{MediaTypeAlias}'.", firstAdvert.ContentType.Alias);
            return;
        }

        string? existingLink = firstAdvert.GetValue(linkAlias)?.ToString();
        if (!string.IsNullOrWhiteSpace(existingLink))
        {
            return;
        }

        firstAdvert.SetValue(linkAlias, DefaultTopAdvertLinkUrl);
        _mediaService.Save(firstAdvert);
        _logger.LogInformation("Seeded top advert link on media '{MediaName}' via alias '{LinkAlias}'.", firstAdvert.Name, linkAlias);
    }

    private void EnsureAdvertMediaType()
    {
        IMediaType? advertMediaType = _mediaTypeService.Get("advertMedia");
        if (advertMediaType == null)
        {
            advertMediaType = new MediaType(_shortStringHelper, -1)
            {
                Alias = "advertMedia",
                Name = "Advert"
            };

            IMediaType? imageMediaType = _mediaTypeService.Get(Constants.Conventions.MediaTypes.Image);
            if (imageMediaType != null && !advertMediaType.ContentTypeCompositionExists(imageMediaType.Alias))
            {
                advertMediaType.AddContentType(imageMediaType);
            }

            _mediaTypeService.Save(advertMediaType);
            _logger.LogInformation("Created media type 'Advert' with alias 'advertMedia'.");
        }
        else
        {
            _logger.LogInformation("Media type 'Advert' with alias 'advertMedia' already exists.");
        }

        EnsureMediaTypeProperty(
            advertMediaType,
            "content",
            "Content",
            "advertLink",
            "Advert Link",
            Constants.DataTypes.Guids.TextstringGuid,
            Constants.PropertyEditors.Aliases.TextBox);

        _mediaTypeService.Save(advertMediaType);
        _logger.LogInformation("Ensured media type property '{PropertyAlias}' on alias '{MediaAlias}'.", "advertLink", "advertMedia");

        EnsureFolderAllowsAdvertMediaType(advertMediaType);
    }

    private void EnsureFolderAllowsAdvertMediaType(IMediaType advertMediaType)
    {
        IMediaType? folderMediaType = _mediaTypeService.Get(Constants.Conventions.MediaTypes.Folder);
        if (folderMediaType == null)
        {
            return;
        }

        bool alreadyAllowed = folderMediaType.AllowedContentTypes
            .Any(x => x.Alias.Equals(advertMediaType.Alias, StringComparison.OrdinalIgnoreCase));

        if (alreadyAllowed)
        {
            return;
        }

        var updatedAllowedTypes = folderMediaType.AllowedContentTypes
            .Concat(new[]
            {
                new ContentTypeSort(advertMediaType.Key, folderMediaType.AllowedContentTypes.Count(), advertMediaType.Alias)
            })
            .ToArray();

        folderMediaType.AllowedContentTypes = updatedAllowedTypes;
        _mediaTypeService.Save(folderMediaType);
        _logger.LogInformation("Allowed media type '{ChildAlias}' under folder media type.", advertMediaType.Alias);
    }

    private IContentType EnsurePageSeoComposition()
    {
        IContentType? composition = _contentTypeService.Get("pageSeo");
        if (composition != null)
        {
            return composition;
        }

        composition = new ContentType(_shortStringHelper, -1)
        {
            Alias = "pageSeo",
            Name = "Page SEO",
            IsElement = true
        };

        composition.AddPropertyGroup("seo", "SEO");
        composition.AddPropertyType(MakeProperty("metaDescription", "Meta Description", Constants.DataTypes.Guids.TextstringGuid, Constants.PropertyEditors.Aliases.TextBox), "seo");
        composition.AddPropertyType(MakeProperty("metaTitle", "Meta Title", Constants.DataTypes.Guids.TextstringGuid, Constants.PropertyEditors.Aliases.TextBox), "seo");
        composition.AddPropertyType(MakeProperty("hideInNavigation", "Hide in Navigation", Constants.DataTypes.Guids.CheckboxGuid, Constants.PropertyEditors.Aliases.Boolean), "seo");

        _contentTypeService.Save(composition);
        return composition;
    }

    private IContentType EnsurePageContentComposition()
    {
        IContentType? composition = _contentTypeService.Get("pageContent");
        IDataType blockListDataType = EnsureBlockListDataType();
        if (composition != null)
        {
            // Upgrade existing field mapping from fallback textarea to Block List.
            IPropertyType? contentBlocks = composition.PropertyTypes.FirstOrDefault(x => x.Alias == "contentBlocks");
            if (contentBlocks != null)
            {
                contentBlocks.DataTypeId = blockListDataType.Id;
                contentBlocks.DataTypeKey = blockListDataType.Key;
                _contentTypeService.Save(composition);
            }

            return composition;
        }

        composition = new ContentType(_shortStringHelper, -1)
        {
            Alias = "pageContent",
            Name = "Page Content",
            IsElement = true
        };

        composition.AddPropertyGroup("content", "Content");
        composition.AddPropertyType(MakeProperty("heroHeading", "Hero Heading", Constants.DataTypes.Guids.TextstringGuid, Constants.PropertyEditors.Aliases.TextBox), "content");
        composition.AddPropertyType(MakeProperty("introText", "Intro Text", Constants.DataTypes.Guids.TextareaGuid, Constants.PropertyEditors.Aliases.TextArea), "content");
        composition.AddPropertyType(MakeProperty("leadText", "Lead Text", Constants.DataTypes.Guids.TextstringGuid, Constants.PropertyEditors.Aliases.TextBox), "content");
        composition.AddPropertyType(MakeProperty("bodyText", "Body Text", Constants.DataTypes.Guids.RichtextEditorGuid, Constants.PropertyEditors.Aliases.RichText), "content");
        composition.AddPropertyType(MakeProperty("contentBlocks", "Content Blocks", blockListDataType.Key, Constants.PropertyEditors.Aliases.BlockList), "content");
        composition.AddPropertyType(MakeProperty("publishedOn", "Published On", Constants.DataTypes.Guids.DatePickerWithTimeGuid, Constants.PropertyEditors.Aliases.DateTime), "content");

        _contentTypeService.Save(composition);
        return composition;
    }

    private IDataType EnsureBlockListDataType()
    {
        IDataType[] allDataTypes = _dataTypeService.GetAll(Array.Empty<int>()).ToArray();

        IDataType? existing = allDataTypes.FirstOrDefault(x =>
            x.Name.Equals("Content Blocks (Auto)", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.EditorAlias, Constants.PropertyEditors.Aliases.BlockList, StringComparison.OrdinalIgnoreCase)
            && (IsRegisteredPropertyEditor(x.EditorAlias) || IsRegisteredPropertyEditor(x.EditorUiAlias)));

        if (existing != null)
        {
            return existing;
        }

        IDataEditor? blockListEditor = _propertyEditors.FirstOrDefault(x => x.Alias == Constants.PropertyEditors.Aliases.BlockList);
        if (blockListEditor == null)
        {
            throw new InvalidOperationException("Block List editor is not available in this Umbraco installation.");
        }

        var dataType = new DataType(blockListEditor, _configurationEditorJsonSerializer, -1)
        {
            Name = allDataTypes.Any(x => x.Name.Equals("Content Blocks (Auto)", StringComparison.OrdinalIgnoreCase))
                ? "Content Blocks (Auto) (Repaired)"
                : "Content Blocks (Auto)"
        };

        _dataTypeService.Save(dataType);
        return dataType;
    }

    private bool IsRegisteredPropertyEditor(string? alias)
    {
        return string.IsNullOrWhiteSpace(alias) == false
            && _propertyEditors.Any(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase));
    }

    private IContentType EnsureDocType(
        string alias,
        string name,
        bool allowAsRoot,
        string templateAlias,
        params IContentType[] compositions)
    {
        IContentType? contentType = _contentTypeService.Get(alias);
        if (contentType == null)
        {
            contentType = new ContentType(_shortStringHelper, -1)
            {
                Alias = alias,
                Name = name
            };
        }

        contentType.AllowedAsRoot = allowAsRoot;

        foreach (IContentType composition in compositions)
        {
            if (contentType.ContentTypeCompositionExists(composition.Alias) == false)
            {
                contentType.AddContentType(composition);
            }
        }

        EnsureTemplateExistsForContentType(templateAlias, contentType.Alias, contentType.Name);

        ITemplate? template = _fileService.GetTemplate(templateAlias);
        template ??= _fileService.GetTemplate(contentType.Alias);
        if (template != null)
        {
            contentType.AllowedTemplates = [template];
            contentType.SetDefaultTemplate(template);
        }

        _contentTypeService.Save(contentType);
        return contentType;
    }

    private void EnsureTemplateExistsForContentType(string templateAlias, string contentTypeAlias, string contentTypeName)
    {
        if (_fileService.GetTemplate(templateAlias) != null || _fileService.GetTemplate(contentTypeAlias) != null)
        {
            return;
        }

        _fileService.CreateTemplateForContentType(templateAlias, contentTypeName);
    }

    private void EnsureProperty(
        IContentType contentType,
        string groupAlias,
        string groupName,
        string propertyAlias,
        string propertyName,
        Guid? dataTypeKey,
        string editorAlias)
    {
        if (contentType.PropertyGroups.Any(x => x.Alias == groupAlias) == false)
        {
            contentType.AddPropertyGroup(groupAlias, groupName);
        }

        IPropertyType? existing = contentType.PropertyTypes.FirstOrDefault(x => x.Alias == propertyAlias);
        if (existing != null)
        {
            IDataType? dataType = dataTypeKey.HasValue
                ? _dataTypeService.GetAll(Array.Empty<int>()).FirstOrDefault(x => x.Key == dataTypeKey.Value)
                : null;
            if (dataType != null)
            {
                existing.DataTypeId = dataType.Id;
                existing.DataTypeKey = dataType.Key;
            }

            return;
        }

        contentType.AddPropertyType(MakeProperty(propertyAlias, propertyName, dataTypeKey, editorAlias), groupAlias);
    }

    private void EnsureMediaTypeProperty(
        IMediaType mediaType,
        string groupAlias,
        string groupName,
        string propertyAlias,
        string propertyName,
        Guid? dataTypeKey,
        string editorAlias)
    {
        if (mediaType.PropertyGroups.Any(x => x.Alias == groupAlias) == false)
        {
            mediaType.AddPropertyGroup(groupAlias, groupName);
        }

        IPropertyType? existing = mediaType.PropertyTypes.FirstOrDefault(x => x.Alias == propertyAlias);
        if (existing != null)
        {
            IDataType? dataType = dataTypeKey.HasValue
                ? _dataTypeService.GetAll(Array.Empty<int>()).FirstOrDefault(x => x.Key == dataTypeKey.Value)
                : null;
            if (dataType != null)
            {
                existing.DataTypeId = dataType.Id;
                existing.DataTypeKey = dataType.Key;
            }

            return;
        }

        mediaType.AddPropertyType(MakeProperty(propertyAlias, propertyName, dataTypeKey, editorAlias), groupAlias);
    }

    private PropertyType MakeProperty(string alias, string name, Guid? dataTypeKey, string editorAlias)
    {
        IDataType[] allDataTypes = _dataTypeService.GetAll(Array.Empty<int>()).ToArray();

        IDataType? dataType = dataTypeKey.HasValue
            ? allDataTypes.FirstOrDefault(x => x.Key == dataTypeKey.Value)
            : null;

        dataType ??= allDataTypes.FirstOrDefault(x => x.EditorAlias == editorAlias || x.EditorUiAlias == editorAlias);

        if (dataType == null)
        {
            throw new InvalidOperationException($"Could not find Umbraco data type for alias '{alias}' using key '{dataTypeKey}' or editor '{editorAlias}'.");
        }

        return new PropertyType(_shortStringHelper, dataType, alias)
        {
            Name = name
        };
    }
}