using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace TMMOnline.Web.DoctypeProvisioning;

public sealed partial class ArticlePrimaryImageMediaRoutingHandler : INotificationHandler<ContentSavedNotification>
{
    private const string MediaRootFolderName = "Content Type Media";
    private const string ArticleMediaFolderName = "articlePage";
    private const string ArticleContentTypeAlias = "articlePage";
    private const string PrimaryImagePropertyAlias = "primaryImage";

    private readonly IMediaService _mediaService;
    private readonly ILogger<ArticlePrimaryImageMediaRoutingHandler> _logger;

    public ArticlePrimaryImageMediaRoutingHandler(
        IMediaService mediaService,
        ILogger<ArticlePrimaryImageMediaRoutingHandler> logger)
    {
        _mediaService = mediaService;
        _logger = logger;
    }

    public void Handle(ContentSavedNotification notification)
    {
        IMedia? articleFolder = GetArticleMediaFolder();
        if (articleFolder == null)
        {
            _logger.LogWarning("Article primary image routing skipped because the '{FolderName}' media folder is missing.", ArticleMediaFolderName);
            return;
        }

        foreach (IContent content in notification.SavedEntities.Where(x => x.ContentType.Alias == ArticleContentTypeAlias))
        {
            if (TryGetSelectedMediaKey(content, out Guid mediaKey) == false)
            {
                continue;
            }

            IMedia? media = _mediaService.GetById(mediaKey);
            if (media == null)
            {
                _logger.LogWarning("Article primary image routing could not find media {MediaKey} referenced by content {ContentId}.", mediaKey, content.Id);
                continue;
            }

            if (media.ContentType.Alias == Umbraco.Cms.Core.Constants.Conventions.MediaTypes.Folder)
            {
                continue;
            }

            if (media.ParentId == articleFolder.Id)
            {
                continue;
            }

            _mediaService.Move(media, articleFolder.Id);
            _logger.LogInformation(
                "Moved media {MediaId} into article media folder for content {ContentId}.",
                media.Id,
                content.Id);
        }
    }

    private IMedia? GetArticleMediaFolder()
    {
        IEnumerable<IMedia> rootMedia = _mediaService.GetRootMedia() ?? Enumerable.Empty<IMedia>();
        IMedia? root = rootMedia
            .FirstOrDefault(x => x.ContentType?.Alias == Umbraco.Cms.Core.Constants.Conventions.MediaTypes.Folder
                && string.Equals(x.Name, MediaRootFolderName, StringComparison.OrdinalIgnoreCase));

        if (root == null)
        {
            return null;
        }

        IEnumerable<IMedia> children = _mediaService.GetPagedChildren(root.Id, 0, 200, out _) ?? Enumerable.Empty<IMedia>();
        return children.FirstOrDefault(x => x.ContentType?.Alias == Umbraco.Cms.Core.Constants.Conventions.MediaTypes.Folder
            && string.Equals(x.Name, ArticleMediaFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetSelectedMediaKey(IContent content, out Guid mediaKey)
    {
        mediaKey = Guid.Empty;

        if (content.HasProperty(PrimaryImagePropertyAlias) == false)
        {
            return false;
        }

        object? value = content.GetValue(PrimaryImagePropertyAlias);
        if (value == null)
        {
            return false;
        }

        return TryGetSelectedMediaKey(value, out mediaKey);
    }

    private bool TryGetSelectedMediaKey(object value, out Guid mediaKey)
    {
        mediaKey = Guid.Empty;

        if (TryExtractGuid(value as string, out mediaKey))
        {
            return true;
        }

        if (TryExtractGuidFromIdsProperty(value, out mediaKey))
        {
            return true;
        }

        string serialized = JsonSerializer.Serialize(value);
        return TryExtractGuid(serialized, out mediaKey);
    }

    private static bool TryExtractGuidFromIdsProperty(object value, out Guid mediaKey)
    {
        mediaKey = Guid.Empty;

        var idsProperty = value.GetType().GetProperty("Ids");
        if (idsProperty?.GetValue(value) is not IEnumerable ids)
        {
            return false;
        }

        foreach (object? id in ids)
        {
            if (TryExtractGuid(id?.ToString(), out mediaKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractGuid(string? rawValue, out Guid mediaKey)
    {
        mediaKey = Guid.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        Match match = MediaUdiRegex().Match(rawValue);
        if (match.Success && Guid.TryParse(match.Groups[1].Value, out mediaKey))
        {
            return true;
        }

        return Guid.TryParse(rawValue, out mediaKey);
    }

    [GeneratedRegex("umb://media/([0-9a-fA-F-]{32,36})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaUdiRegex();
}
