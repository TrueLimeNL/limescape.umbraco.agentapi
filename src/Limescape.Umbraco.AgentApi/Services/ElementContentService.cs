using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>The bytes of one element, with what the response headers need.</summary>
/// <param name="Stream">File content; the caller disposes it.</param>
/// <param name="FileName">Name of the element (for <c>Content-Type</c> and <c>Content-Disposition</c>).</param>
/// <param name="ContentType">Content type derived from the extension, as for the detail-page downloads.</param>
/// <param name="Sha256">Stored SHA-256 (64 lowercase hex characters), or null when none is stored.</param>
public sealed record ElementContent(Stream Stream, string FileName, string ContentType, string? Sha256);

/// <summary>
/// Element bytes for a trusted server-side integration (the index sync of the platform): the API user of the tenant
/// acting with a validated acting-user token. Unlike <see cref="SignedDownloadService"/> there is no person, so there is no
/// person visibility: drafts are included, and an element belongs to a material when it is in the elements of the working
/// version or of the published version. Confidential elements are never served; elements outside the material, elements of
/// another media type and anything in the recycle bin are not found. Successful reads are neither audited nor logged; every
/// refusal is logged once, without the file name.
/// </summary>
public sealed partial class ElementContentService(
    IContentService contentService,
    IContentTypeService contentTypeService,
    IMediaService mediaService,
    MediaFileManager files,
    IOptions<AgentApiOptions> options,
    ILogger<ElementContentService> logger)
{
    /// <summary>Problem code: unknown material or element, not an element of this material, wrong media type, or in the recycle bin.</summary>
    public const string NotFoundCode = "element_not_found";

    /// <summary>Problem code: the element is confidential.</summary>
    public const string ConfidentialCode = "element_confidential";

    /// <summary>Problem code: the request carries no validated acting-user token.</summary>
    public const string ActingUserRequiredCode = "acting_user_required";

    /// <summary>Problem code: the bearer is a backoffice user but not an API user.</summary>
    public const string ApiUserRequiredCode = "api_user_required";

    /// <summary>Media property that marks an element confidential (as in <see cref="SignedDownloadService"/>).</summary>
    public const string ConfidentialProperty = "confidential";

    private ContentModelOptions Model => options.Value.Content;

    /// <summary>Opens the file of <paramref name="elementKey"/> if it is a servable element of <paramref name="materialKey"/>.</summary>
    public AgentResult<ElementContent> Open(Guid materialKey, Guid elementKey)
    {
        var material = contentService.GetById(materialKey);
        if (material is null || material.Trashed || !IsMaterial(material))
        {
            return NotFound();
        }

        var belongs = LearningMaterialService.ParsePickedMedia(material.GetValue<string>(Model.ElementsProperty))
            .Concat(LearningMaterialService.ParsePickedMedia(material.GetValue<string>(Model.ElementsProperty, published: true)))
            .Contains(elementKey);
        var element = belongs ? mediaService.GetById(elementKey) : null;
        if (element is null || element.Trashed || element.ContentType.Alias != Model.ElementMediaTypeAlias)
        {
            return NotFound();
        }

        if (element.GetValue<bool>(ConfidentialProperty))
        {
            return AgentResult.Fail<ElementContent>(ConfidentialCode, "The element is confidential.");
        }

        var stream = files.GetFile(element, out var path);
        if (stream is null)
        {
            return NotFound();
        }

        var name = string.IsNullOrWhiteSpace(element.Name) ? Path.GetFileName(path) ?? "element" : element.Name;
        var sha256 = element.GetValue<string>(Model.Sha256Property)?.Trim().ToLowerInvariant();
        return AgentResult.Ok(new ElementContent(stream, name, SignedDownloadService.TypeOf(name),
            sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigitLower) ? sha256 : null));
    }

    /// <summary>One warning per refusal: material, element, acting user and correlation id; never a file name.</summary>
    public void LogRefused(int status, string code, Guid materialKey, Guid elementKey, string actingUserId, string? correlationId) =>
        LogRefusal(logger, status, code, materialKey, elementKey, actingUserId, correlationId);

    private bool IsMaterial(IContent content) =>
        content.ContentType.Alias != Model.MaterialCompositionAlias
        && contentTypeService.Get(content.ContentType.Key)?.ContentTypeCompositionExists(Model.MaterialCompositionAlias) == true;

    private static AgentResult<ElementContent> NotFound() => AgentResult.Fail<ElementContent>(NotFoundCode, "Element not found.");

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refused element content {Status} {Code}: material {MaterialKey}, element {ElementKey}, acting user {ActingUserId}, correlation id {CorrelationId}")]
    private static partial void LogRefusal(ILogger logger, int status, string code, Guid materialKey, Guid elementKey, string actingUserId, string? correlationId);
}
