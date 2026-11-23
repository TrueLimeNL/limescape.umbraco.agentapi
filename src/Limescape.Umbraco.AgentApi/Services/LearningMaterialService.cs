using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.FieldState;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Submission;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>
/// The one service layer for learning-material operations. MCP tools and REST endpoints both call it,
/// so the same checks and the same audit apply to both.
/// </summary>
public interface ILearningMaterialService
{
    /// <summary>Collections the caller can see, with the material types allowed in each.</summary>
    AgentResult<IReadOnlyList<CollectionInfo>> ListCollections(AgentCaller caller);

    /// <summary>Material types, fields, allowed values and collections, for a metadata proposal.</summary>
    /// <param name="caller">The person asking.</param>
    /// <param name="materialType">Only this material type (and the collections that allow it); null for every type. An unknown alias is <c>not_found</c>.</param>
    AgentResult<MetadataContext> GetMetadataContext(AgentCaller caller, string? materialType = null);

    /// <summary>Creates a material as draft, with the caller as owner. Never publishes.</summary>
    AgentResult<MaterialDetail> CreateMaterial(AgentCaller caller, CreateMaterialRequest request);

    /// <summary>Adds files to a draft or rejected material the caller owns.</summary>
    AgentResult<MaterialDetail> AddElements(AgentCaller caller, Guid materialKey, IReadOnlyList<ElementUpload> elements);

    /// <summary>
    /// Registers the file of a verified, platform-signed upload session as an element of a draft or rejected material
    /// the caller may change, with the rights of <see cref="AddElements"/>. The file stays where it is (no copy).
    /// Idempotent: a retry for a file that is already an element of this material changes nothing.
    /// </summary>
    AgentResult<MaterialDetail> AddElementFromSession(AgentCaller caller, Guid materialKey, UploadSessionRegistration session);

    /// <summary>
    /// Removes one element (a file) from a draft or rejected material, with the rights of <see cref="UpdateMaterial"/>
    /// and the same version check under one content write lock. The media item goes to the recycle bin of the media, never
    /// purged: removing it for good stays with the Compliance Pack's purge route.
    /// </summary>
    /// <param name="caller">The person removing the element.</param>
    /// <param name="materialKey">The material the element belongs to.</param>
    /// <param name="elementKey">Key of the element, as returned in <c>elements[].key</c>.</param>
    /// <param name="version">The version of the material as last read; required.</param>
    AgentResult<MaterialDetail> RemoveElement(AgentCaller caller, Guid materialKey, Guid elementKey, string? version);

    /// <summary>
    /// Changes the metadata of a draft or rejected material the caller owns, if it is still at the version the caller
    /// read. The version check and the save happen under one content write lock.
    /// </summary>
    AgentResult<MaterialDetail> UpdateMaterial(AgentCaller caller, Guid materialKey, UpdateMaterialRequest request);

    /// <summary>
    /// Submits a draft or rejected material for review (status submitted) after the Compliance Pack's required-field
    /// check, with the rights of <see cref="UpdateMaterial"/> and the same version check.
    /// </summary>
    AgentResult<MaterialDetail> SubmitMaterial(AgentCaller caller, Guid materialKey, SubmitMaterialRequest request);

    /// <summary>
    /// Replaces the owners of a material, or adds owners to it: an owner with the developer role or a functional manager, at
    /// the version the person confirmed. A published material only changes for a functional manager; an archived one never.
    /// </summary>
    AgentResult<MaterialDetail> SetOwners(AgentCaller caller, Guid materialKey, SetOwnersRequest request);

    /// <summary>
    /// Archives a published material through the Compliance Pack's archive route (status archived, archive date,
    /// unpublished): an owner with the developer role or a functional manager, at the version the person confirmed.
    /// </summary>
    AgentResult<MaterialDetail> ArchiveMaterial(AgentCaller caller, Guid materialKey, ArchiveMaterialRequest request);

    /// <summary>A material, if the caller may see it.</summary>
    AgentResult<MaterialDetail> GetMaterial(AgentCaller caller, Guid materialKey);

    /// <summary>The detail page link of a material, if the caller may see it.</summary>
    AgentResult<DetailLink> GetDetailLink(AgentCaller caller, Guid materialKey);

    /// <summary>Groups a material can be added to.</summary>
    AgentResult<IReadOnlyList<GroupSummary>> ListGroups(AgentCaller caller);

    /// <summary>
    /// Adds a material to the end of a group, with the rights of <see cref="UpdateMaterial"/> on the material.
    /// Idempotent: a member stays where it is.
    /// </summary>
    AgentResult<MaterialDetail> AddToGroup(AgentCaller caller, Guid materialKey, Guid groupKey);

    /// <summary>Removes a material from a group, with the rights of <see cref="UpdateMaterial"/> on the material. Idempotent.</summary>
    AgentResult<MaterialDetail> RemoveFromGroup(AgentCaller caller, Guid materialKey, Guid groupKey);

    /// <summary>
    /// Creates a typed link from a material the caller may change to a material the caller may see. Idempotent: an
    /// existing link (for a bidirectional type, in either direction) is left as it is.
    /// </summary>
    AgentResult<MaterialDetail> AddRelation(AgentCaller caller, Guid materialKey, RelationRequest request);

    /// <summary>Removes a typed link between a material the caller may change and a material the caller may see. Idempotent.</summary>
    AgentResult<MaterialDetail> RemoveRelation(AgentCaller caller, Guid materialKey, RelationRequest request);

    /// <summary>
    /// Submitted and in-review materials the caller may decide on: a reviewer, for materials they do not own, oldest
    /// first. Empty for every other role.
    /// </summary>
    AgentResult<IReadOnlyList<ReviewQueueItem>> ListReviewQueue(AgentCaller caller);

    /// <summary>
    /// Approves a submitted material and publishes it, the way Umbraco Workflow does: an approval record for this draft
    /// through the Compliance Pack, then the publish through the publication gate. A reviewer who is not an owner, at the
    /// version the person confirmed.
    /// </summary>
    AgentResult<ReviewDecision> ApproveMaterial(AgentCaller caller, Guid materialKey, ApproveMaterialRequest request);

    /// <summary>
    /// Rejects a submitted material with feedback for the owner, the way Umbraco Workflow does: a rejection record with the
    /// feedback as findings and status <c>rejected</c>. A reviewer who is not an owner, at the version the person confirmed.
    /// </summary>
    AgentResult<ReviewDecision> RejectMaterial(AgentCaller caller, Guid materialKey, RejectMaterialRequest request);

    /// <summary>Retention classes and the materials whose period runs, ends soon or has ended; for a functional manager.</summary>
    AgentResult<RetentionOverview> ListRetention(AgentCaller caller, RetentionListRequest request);

    /// <summary>
    /// Assigns a retention class to one material, in place of the class assigned to it before; for a functional manager,
    /// at the version the person confirmed. A shorter period does not shorten one that runs already (only an administrator
    /// confirms that).
    /// </summary>
    AgentResult<MaterialRetention> SetRetentionClass(AgentCaller caller, Guid materialKey, SetRetentionClassRequest request);

    /// <summary>Sets a legal hold on one material (deletion blocked whatever the period); for a functional manager. Idempotent.</summary>
    AgentResult<MaterialRetention> SetLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request);

    /// <summary>Lifts the legal hold of one material; for a functional manager. Idempotent.</summary>
    AgentResult<MaterialRetention> ClearLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request);

    /// <summary>
    /// Archives or restores a metadata field and sets its deprecated codes; for a functional manager with the
    /// person's own token. An unknown field, a system field or an unknown code is <c>invalid</c>. Existing values on
    /// materials are never touched; afterwards no write route accepts a new value in an archived field or a deprecated code
    /// that the material did not hold. Idempotent: an unchanged state writes no audit entry.
    /// </summary>
    AgentResult<MetadataFieldState> SetMetadataFieldState(AgentCaller caller, string fieldAlias, SetFieldStateRequest request);
}

/// <summary>
/// Default <see cref="ILearningMaterialService"/>. Writes are carried out as Umbraco's default (super) user and go through <see cref="IContentService"/> and
/// <see cref="IMediaService"/> inside one Umbraco scope, so Umbraco notifications fire and the Compliance Pack's rules
/// apply exactly as for every other route; the person comes from <see cref="AgentCallerAccessor"/>. Rights are checked
/// here, server-side, before anything is written.
/// </summary>
public sealed partial class LearningMaterialService(
    IContentService contentService,
    IContentTypeService contentTypeService,
    IMediaService mediaService,
    IMediaTypeService mediaTypeService,
    IDataTypeService dataTypeService,
    ICoreScopeProvider scopeProvider,
    MediaFileManager mediaFileManager,
    MediaUrlGeneratorCollection mediaUrlGenerators,
    IShortStringHelper shortStringHelper,
    IContentTypeBaseServiceProvider contentTypeBaseServiceProvider,
    IRelationService relationService,
    TimeProvider timeProvider,
    ISubmissionGuard submissionGuard,
    IArchiveService archiveService,
    IApprovalStore approvalStore,
    IContentVersionHasher versionHasher,
    IActingUserAccessor actingUserAccessor,
    IApprovalService approvalService,
    IRetentionService retentionService,
    IDeletionBlockService deletionBlockService,
    IFieldStateService fieldStates,
    IOptions<AgentApiOptions> options) : ILearningMaterialService
{
    /// <summary>Lifecycle status of a new material, and the statuses in which it can still be changed.</summary>
    private const string Draft = "draft";
    private static readonly string[] EditableStatuses = [Draft, "rejected"];
    private static readonly string[] ReviewStatuses = ["submitted", "inReview"];

    private const int MaxNameLength = 255;
    private const int MaxTitleLength = 300;
    private const int MaxLinkUrlLength = 2048;
    private const int MaxOwnerKeyLength = 256;

    /// <summary>Kinds of material: files (elements) or only a link.</summary>
    private const string KindFile = "file";
    private const string KindLink = "link";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ContentModelOptions Model => options.Value.Content;

    /// <inheritdoc />
    public AgentResult<IReadOnlyList<CollectionInfo>> ListCollections(AgentCaller caller)
    {
        if (caller.User.Roles.Count == 0)
        {
            return NoRole<IReadOnlyList<CollectionInfo>>();
        }

        return AgentResult.Ok<IReadOnlyList<CollectionInfo>>(Collections(caller, MaterialTypes()));
    }

    /// <inheritdoc />
    public AgentResult<MetadataContext> GetMetadataContext(AgentCaller caller, string? materialType = null)
    {
        if (caller.User.Roles.Count == 0)
        {
            return NoRole<MetadataContext>();
        }

        var types = MaterialTypes();
        if (!string.IsNullOrWhiteSpace(materialType))
        {
            var selected = types.Where(t => string.Equals(t.Alias, materialType.Trim(), StringComparison.Ordinal)).ToList();
            if (selected.Count == 0)
            {
                return AgentResult.Fail<MetadataContext>(AgentErrors.NotFound, $"Unknown material type '{materialType.Trim()}'. Call propose_metadata_context without a type for the available types.");
            }

            types = selected;
        }

        var categories = AllowedValues(ElementType()?.CompositionPropertyTypes.FirstOrDefault(p => p.Alias == Model.CategoryProperty)) ?? [];
        var collections = Collections(caller, types);
        if (!string.IsNullOrWhiteSpace(materialType))
        {
            collections = collections.Where(c => c.MaterialTypes.Count > 0).ToList();
        }

        return AgentResult.Ok(new MetadataContext(types.Select(ToTypeInfo).ToList(), collections, new ElementFieldInfo(categories, options.Value.MaxInlineElementBytes)));
    }

    private MaterialTypeInfo ToTypeInfo(IContentType type)
    {
        // Required at submission by the Compliance Pack's rule, so a form and the submit check agree.
        var submissionRequired = submissionGuard.RequiredFieldsFor(type.Alias).Select(f => f.Alias).ToHashSet(StringComparer.Ordinal);
        var stored = fieldStates.Stored();
        var fields = type.CompositionPropertyTypes.Select(p => ToFieldInfo(p, submissionRequired, fieldStates.StateOf(type, p.Alias, stored))).ToList();
        Model.MaterialTypes.TryGetValue(type.Alias, out var configured);
        return new MaterialTypeInfo(type.Alias, type.Name ?? type.Alias, type.Description, fields, Labels(type.Alias, configured?.Labels));
    }

    private FieldInfo ToFieldInfo(IPropertyType property, HashSet<string> submissionRequired, FieldStateInfo state)
    {
        var allowed = AllowedValues(property);
        var target = Target(property.Alias);
        Model.Fields.TryGetValue(property.Alias, out var configured);
        var values = allowed?.Select(code =>
        {
            Dictionary<string, string>? codeLabels = null;
            configured?.Values.TryGetValue(code, out codeLabels);
            return new ValueInfo(code, Labels(code, codeLabels), state.DeprecatedValues.Contains(code, StringComparer.OrdinalIgnoreCase));
        }).ToList();

        return new FieldInfo(
            property.Alias,
            property.Name,
            property.PropertyEditorAlias,
            property.Mandatory,
            values?.Where(v => !v.Deprecated).Select(v => v.Value).ToList(),
            target,
            FieldType(property, target, allowed is not null, configured?.Type),
            !state.Archived && (property.Mandatory || submissionRequired.Contains(property.Alias)),
            Labels(property.Alias, configured?.Labels),
            values,
            state.Archived,
            target != FieldTargets.System && !ImportMetadataRules.IsImportField(property.Alias));
    }

    private static readonly string[] FieldTypeNames = ["string", "text", "list", "number", "date", "url", "boolean", "other"];

    /// <summary>
    /// Type of a field for a form. A field the content layer sets itself is <c>system</c>; otherwise the tenant's configured
    /// type (when it is one of the known types), otherwise derived from the property editor.
    /// </summary>
    private string FieldType(IPropertyType property, string target, bool isList, string? configured)
    {
        if (target == FieldTargets.System)
        {
            return FieldTargets.System;
        }

        if (configured is not null && FieldTypeNames.Contains(configured.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return configured.Trim().ToLowerInvariant();
        }

        if (property.Alias == ImportMetadataRules.OriginalDate) { return "date"; }

        if (isList)
        {
            return "list";
        }

        if (property.Alias == Model.LinkUrlProperty)
        {
            return "url";
        }

        return property.PropertyEditorAlias switch
        {
            Constants.PropertyEditors.Aliases.TextBox => "string",
            Constants.PropertyEditors.Aliases.TextArea => "text",
            Constants.PropertyEditors.Aliases.Integer or Constants.PropertyEditors.Aliases.Decimal => "number",
            Constants.PropertyEditors.Aliases.DateTime => "date",
            Constants.PropertyEditors.Aliases.Boolean => "boolean",
            _ => "other",
        };
    }

    /// <summary>
    /// A label per language: <c>nl</c> and <c>en</c> always, plus every language the tenant configured. A language without a
    /// label falls back on <paramref name="fallback"/> (the alias or the code), so a consumer never has to guess.
    /// </summary>
    private static Dictionary<string, string> Labels(string fallback, IEnumerable<KeyValuePair<string, string>>? configured)
    {
        var set = (configured ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Value))
            .GroupBy(l => l.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.Ordinal);
        foreach (var language in new[] { "nl", "en" })
        {
            set.TryAdd(language, fallback);
        }

        return set.OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> CreateMaterial(AgentCaller caller, CreateMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        if (!caller.User.IsInRole(AgentRoles.Developer))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Forbidden, "Only developers can create learning materials.");
        }

        var type = MaterialTypes().FirstOrDefault(t => string.Equals(t.Alias, request.MaterialType, StringComparison.Ordinal));
        if (type is null)
        {
            return Invalid<MaterialDetail>($"Unknown material type '{request.MaterialType}'. Use propose_metadata_context for the available types.");
        }

        var collection = contentService.GetById(request.CollectionKey);
        if (collection is null || collection.Trashed || !Model.CollectionDocumentTypes.Contains(collection.ContentType.Alias, StringComparer.Ordinal))
        {
            return Invalid<MaterialDetail>("Unknown collection. Use list_collections for the available collections.");
        }

        if (!AllowedIn(collection.ContentType.Alias).Contains(type.Alias, StringComparer.Ordinal))
        {
            return Invalid<MaterialDetail>($"Material type '{type.Alias}' is not allowed in collection '{collection.Name}'.");
        }

        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > MaxTitleLength)
        {
            return Invalid<MaterialDetail>($"Title is required and at most {MaxTitleLength} characters.");
        }

        var linkUrl = request.LinkUrl?.Trim();
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? (string.IsNullOrEmpty(linkUrl) ? null : KindLink) : request.Kind.Trim().ToLowerInvariant();
        if (LinkRuleViolation(kind, linkUrl, request.Kind) is { } linkError)
        {
            return Invalid<MaterialDetail>(linkError);
        }

        if (ImportMetadataRules.Validate(request.OriginalDate ?? request.Metadata?.GetValueOrDefault(ImportMetadataRules.OriginalDate), request.Provenance ?? request.Metadata?.GetValueOrDefault(ImportMetadataRules.Provenance), timeProvider) is { } importError)
        {
            return AgentResult.Fail<MaterialDetail>(importError.Split(':')[0], importError);
        }

        // Core fields first, then the type's own metadata. The service sets status, owners, elements and archive date itself.
        var values = new List<(string Alias, string? Value)>
        {
            (Model.TitleProperty, title),
            (Model.DescriptionProperty, request.Description),
            (Model.LicenseProperty, request.License),
            (Model.SharingLevelProperty, request.SharingLevel),
            (Model.SourceProperty, request.Source),
            (ImportMetadataRules.OriginalDate, request.OriginalDate),
            (ImportMetadataRules.Provenance, request.Provenance),
            (Model.KindProperty, kind),
            (Model.LinkUrlProperty, linkUrl),
        };
        foreach (var (alias, value) in request.Metadata ?? new Dictionary<string, string>())
        {
            if ((alias == ImportMetadataRules.OriginalDate && request.OriginalDate is not null)
                || (alias == ImportMetadataRules.Provenance && request.Provenance is not null))
            {
                return Invalid<MaterialDetail>($"Send '{alias}' either as a named argument or through metadata, not both.");
            }
            if (Target(alias) != FieldTargets.Metadata)
            {
                return Invalid<MaterialDetail>($"Field '{alias}' cannot be set through metadata.");
            }

            values.Add((alias, value));
        }

        var content = contentService.Create(Truncate(title, MaxNameLength), collection.Key, type.Alias);
        var states = fieldStates.Stored();
        foreach (var (alias, value) in values.Where(v => !string.IsNullOrWhiteSpace(v.Value)))
        {
            var propertyType = type.CompositionPropertyTypes.FirstOrDefault(p => p.Alias == alias);
            if (propertyType is null)
            {
                return Invalid<MaterialDetail>($"Material type '{type.Alias}' has no field '{alias}'.");
            }

            if (Convert(propertyType, value!.Trim()) is not { } stored)
            {
                return Invalid<MaterialDetail>($"Value '{value}' is not allowed for field '{alias}'. Allowed: {AllowedText(propertyType)}.");
            }

            // A new material holds nothing yet, so an archived field or a deprecated code is refused outright.
            if (fieldStates.CheckChange(type, alias, null, stored, states) is { } violation)
            {
                return StateRefused<MaterialDetail>(violation);
            }

            content.SetValue(alias, stored);
        }

        SetIfExists(content, Model.StatusProperty, Draft);
        SetIfExists(content, Model.OwnersProperty, JsonSerializer.Serialize(new[] { caller.User.PersonId }));

        using var scope = scopeProvider.CreateCoreScope();
        var saved = contentService.Save(content);
        if (!saved.Success)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.EventMessages, "The content layer refused to save the material."));
        }

        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> AddElements(AgentCaller caller, Guid materialKey, IReadOnlyList<ElementUpload> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, "add elements") is { } notAllowed)
        {
            return notAllowed;
        }

        if (KindOf(content) == KindLink)
        {
            return Invalid<MaterialDetail>("A link material has no elements; it only has a link.");
        }

        if (elements.Count == 0)
        {
            return Invalid<MaterialDetail>("At least one element is required.");
        }

        var elementType = ElementType();
        if (elementType is null || !content.HasProperty(Model.ElementsProperty))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotConfigured, "Elements are not configured in this content layer.");
        }

        var categoryType = elementType.CompositionPropertyTypes.FirstOrDefault(p => p.Alias == Model.CategoryProperty);
        var files = new List<(ElementUpload Upload, string FileName, string? Category, byte[] Bytes)>();
        foreach (var element in elements)
        {
            if (ElementError(element.FileName, element.Category, categoryType, out var fileName, out var category) is { } elementError)
            {
                return Invalid<MaterialDetail>(elementError);
            }

            var bytes = Decode(element.ContentBase64);
            if (bytes is null || bytes.Length == 0)
            {
                return Invalid<MaterialDetail>($"The content of '{fileName}' is not valid base64 or is empty.");
            }

            if (bytes.LongLength > options.Value.MaxInlineElementBytes)
            {
                return Invalid<MaterialDetail>($"'{fileName}' is larger than {options.Value.MaxInlineElementBytes} bytes; use the upload screen for large files.");
            }

            files.Add((element, fileName, category, bytes));
        }

        using var scope = scopeProvider.CreateCoreScope();
        var folder = ElementsFolder(content);
        var picked = PickedMedia(content);
        foreach (var (_, fileName, category, bytes) in files)
        {
            var media = mediaService.CreateMedia(fileName, folder.Key, elementType.Alias);
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                media.SetValue(mediaFileManager, mediaUrlGenerators, shortStringHelper, contentTypeBaseServiceProvider, Constants.Conventions.Media.File, fileName, stream);
            }

            if (SaveElement(media, fileName, bytes.LongLength, System.Convert.ToHexStringLower(SHA256.HashData(bytes)), category) is { } notSaved)
            {
                return notSaved;
            }

            picked.Add(media.Key);
        }

        if (SavePicked(content, picked) is { } materialNotSaved)
        {
            return materialNotSaved;
        }

        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> AddElementFromSession(AgentCaller caller, Guid materialKey, UploadSessionRegistration session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        var settings = options.Value.UploadSessions;
        if (string.IsNullOrEmpty(settings.SigningSecret))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotConfigured, "Registration of upload sessions is not configured in this content layer.");
        }

        // Only the platform can sign: a session from anywhere else, or one changed on the way, never gets further.
        if (!UploadSessionSignature.IsValid(settings.SigningSecret, materialKey, session))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.UploadSessionInvalid, "The upload session is not signed by the platform for this material.");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (session.ExpiresAt < now || session.ExpiresAt > now + settings.MaxSignatureLifetimeSeconds)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.UploadSessionInvalid, "The signature of the upload session has expired or is valid for too long; ask the platform for a new one.");
        }

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, "add elements") is { } notAllowed)
        {
            return notAllowed;
        }

        if (KindOf(content) == KindLink)
        {
            return Invalid<MaterialDetail>("A link material has no elements; it only has a link.");
        }

        var elementType = ElementType();
        if (elementType is null || !content.HasProperty(Model.ElementsProperty))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotConfigured, "Elements are not configured in this content layer.");
        }

        var categoryType = elementType.CompositionPropertyTypes.FirstOrDefault(p => p.Alias == Model.CategoryProperty);
        if (ElementError(session.FileName, session.Category, categoryType, out var fileName, out var category) is { } elementError)
        {
            return Invalid<MaterialDetail>(elementError);
        }

        if (session.Sha256 is not { Length: 64 } sha256 || !sha256.All(char.IsAsciiHexDigitLower))
        {
            return Invalid<MaterialDetail>("'sha256' must be 64 lowercase hexadecimal characters.");
        }

        var categoryCode = ReadCode(category) ?? string.Empty;
        var max = settings.CategoryMaxBytes.TryGetValue(categoryCode, out var categoryMax) ? Math.Min(categoryMax, settings.MaxBytes) : settings.MaxBytes;
        if (session.Length <= 0 || session.Length > max)
        {
            return Invalid<MaterialDetail>($"'{fileName}' has {session.Length} bytes; category '{categoryCode}' allows at most {max} bytes.");
        }

        var path = session.Blob ?? string.Empty;
        var files = mediaFileManager.FileSystem;
        if (!UploadSessionSignature.IsValidPath(path) || !path.StartsWith(settings.PathPrefix, StringComparison.Ordinal) || !files.FileExists(path))
        {
            return Invalid<MaterialDetail>($"The file of the upload session is not in the media storage under '{settings.PathPrefix}'.");
        }

        if (files.GetSize(path) != session.Length)
        {
            return Invalid<MaterialDetail>("The size of the stored file differs from the verified size.");
        }

        // Outside the write lock: for a large file this takes a while (R5: about 28 s for 10 GB) and needs no lock.
        if (settings.VerifySha256)
        {
            using var stream = files.OpenFile(path);
            if (!string.Equals(System.Convert.ToHexStringLower(SHA256.HashData(stream)), sha256, StringComparison.Ordinal))
            {
                return Invalid<MaterialDetail>("The SHA-256 of the stored file differs from the verified SHA-256.");
            }
        }

        // One file is one element: under the media and content write locks a retry finds the element it created before,
        // and a second material cannot claim the same file. Media first, the order in which add_elements saves.
        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.MediaTree, Constants.Locks.ContentTree);

        content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, "add elements") is { } notAllowedNow)
        {
            return notAllowedNow;
        }

        var url = files.GetUrl(path);
        var picked = PickedMedia(content);
        if (mediaService.GetMediaByPath(url) is { } existing)
        {
            if (picked.Contains(existing.Key) && !existing.Trashed)
            {
                scope.Complete();
                return AgentResult.Ok(ToDetail(content, caller));
            }

            return AgentResult.Fail<MaterialDetail>(AgentErrors.UploadSessionUsed, "The file of this upload session is already registered elsewhere.");
        }

        var media = mediaService.CreateMedia(fileName, ElementsFolder(content).Key, elementType.Alias);
        media.SetValue(Constants.Conventions.Media.File, url);
        if (SaveElement(media, fileName, session.Length, sha256, category) is { } notSaved)
        {
            return notSaved;
        }

        picked.Add(media.Key);
        if (SavePicked(content, picked) is { } materialNotSaved)
        {
            return materialNotSaved;
        }

        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <summary>Checks the file name and category of an element; null when both are valid.</summary>
    private string? ElementError(string? rawName, string? rawCategory, IPropertyType? categoryType, out string fileName, out string? category)
    {
        fileName = Path.GetFileName(rawName?.Trim() ?? string.Empty);
        category = null;
        if (fileName.Length is 0 or > MaxNameLength || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return $"File name '{rawName}' is not valid.";
        }

        category = categoryType is null ? null : Convert(categoryType, rawCategory?.Trim() ?? string.Empty);
        return categoryType is not null && category is null
            ? $"Category '{rawCategory}' is not allowed. Allowed: {string.Join(", ", AllowedValues(categoryType) ?? [])}."
            : null;
    }

    /// <summary>Sets extension, size, checksum and category of a new element and saves it; null when saved.</summary>
    private AgentResult<MaterialDetail>? SaveElement(IMedia media, string fileName, long size, string sha256, string? category)
    {
        SetIfExists(media, Constants.Conventions.Media.Extension, Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant());
        SetIfExists(media, Constants.Conventions.Media.Bytes, size.ToString(CultureInfo.InvariantCulture));
        SetIfExists(media, Model.Sha256Property, sha256);
        if (category is not null)
        {
            media.SetValue(Model.CategoryProperty, category);
        }

        var saved = mediaService.Save(media);
        return saved.Success
            ? null
            : AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.Result?.EventMessages, $"The content layer refused to store '{fileName}'."));
    }

    /// <summary>Writes the element picker of a material and saves it; null when saved.</summary>
    private AgentResult<MaterialDetail>? SavePicked(IContent content, List<Guid> picked)
    {
        content.SetValue(Model.ElementsProperty, JsonSerializer.Serialize(picked.Select(key => new { key = Guid.NewGuid(), mediaKey = key }), Json));
        var saved = contentService.Save(content);
        return saved.Success
            ? null
            : AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.EventMessages, "The content layer refused to update the material."));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> RemoveElement(AgentCaller caller, Guid materialKey, Guid elementKey, string? version)
    {
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return Invalid<MaterialDetail>("'version' is required: send the version of the material as you last read it.");
        }

        // Content and media write locks before the read, as in UpdateMaterial and AddElementFromSession: the version check,
        // the new element list and the move to the recycle bin are one step.
        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree, Constants.Locks.MediaTree);

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, "remove an element") is { } notAllowed)
        {
            return notAllowed;
        }

        var current = VersionOf(content);
        if (!string.Equals(version.Trim(), current, StringComparison.Ordinal))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.VersionConflict,
                $"The material changed after version '{version.Trim()}'; the current version is '{current}'. Read it again, show the differences to the user and send the confirmed change with the current version.");
        }

        // An element of another material, a trashed one and an unknown key are the same not_found.
        var picked = PickedMedia(content);
        var media = picked.Contains(elementKey) ? mediaService.GetById(elementKey) : null;
        if (media is null || media.Trashed)
        {
            return NotFound<MaterialDetail>();
        }

        picked.RemoveAll(key => key == elementKey);
        if (SavePicked(content, picked) is { } materialNotSaved)
        {
            return materialNotSaved;
        }

        // Recycle bin, not delete: the Compliance Pack audits the move (person, channel, issuer) and its deletion block applies.
        var moved = mediaService.MoveToRecycleBin(media);
        if (!moved.Success)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(moved.Result?.EventMessages, $"The content layer refused to remove '{media.Name}'."));
        }

        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> UpdateMaterial(AgentCaller caller, Guid materialKey, UpdateMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            return Invalid<MaterialDetail>("'version' is required: send the version of the material as you last read it.");
        }

        if (request.Unknown is { Count: > 0 })
        {
            return Invalid<MaterialDetail>($"{string.Join(", ", request.Unknown.Keys.Select(k => $"'{k}'"))} cannot be set through an update.");
        }

        // The content write lock is taken before the material is read, so the version check and the save are one step:
        // a concurrent update of any material waits here and then reads the version the other one wrote. Eager, because
        // WriteLock waits for the first database access and the read below can come from the cache.
        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, "change a material") is { } notAllowed)
        {
            return notAllowed;
        }

        var current = VersionOf(content);
        if (!string.Equals(request.Version.Trim(), current, StringComparison.Ordinal))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.VersionConflict,
                $"The material changed after version '{request.Version.Trim()}'; the current version is '{current}'. Read it again, show the differences to the user and send the confirmed changes with the current version.");
        }

        var title = request.Title?.Trim();
        if (title is { Length: 0 or > MaxTitleLength })
        {
            return Invalid<MaterialDetail>($"Title cannot be empty and is at most {MaxTitleLength} characters.");
        }

        // Kind and link are checked as a pair, on the values the material will have after the update.
        var currentKind = KindOf(content);
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? currentKind : request.Kind.Trim().ToLowerInvariant();
        var linkUrl = request.LinkUrl is null ? content.GetValue<string>(Model.LinkUrlProperty) : request.LinkUrl.Trim();
        if (LinkRuleViolation(kind, linkUrl, request.Kind) is { } linkError)
        {
            return Invalid<MaterialDetail>(linkError);
        }

        if (kind == KindLink && currentKind != KindLink && PickedMedia(content).Count > 0)
        {
            return Invalid<MaterialDetail>("A material with elements cannot become a link material.");
        }

        if ((request.OriginalDate is not null || request.Provenance is not null || request.Metadata?.Keys.Any(ImportMetadataRules.IsImportField) == true) && !caller.User.IsInRole(AgentRoles.FunctionalManager))
        {
            return AgentResult.Fail<MaterialDetail>("import_metadata_manager_required", "import_metadata_manager_required: Only a functional manager may change import metadata.");
        }
        if (ImportMetadataRules.Validate(request.OriginalDate ?? request.Metadata?.GetValueOrDefault(ImportMetadataRules.OriginalDate), request.Provenance ?? request.Metadata?.GetValueOrDefault(ImportMetadataRules.Provenance), timeProvider) is { } importError)
        {
            return AgentResult.Fail<MaterialDetail>(importError.Split(':')[0], importError);
        }

        var changes = new List<(string Alias, string? Value)>();
        void Change(string alias, string? value)
        {
            if (value is not null)
            {
                changes.Add((alias, value));
            }
        }

        Change(Model.TitleProperty, title);
        Change(Model.DescriptionProperty, request.Description);
        Change(Model.LicenseProperty, request.License);
        Change(Model.SharingLevelProperty, request.SharingLevel);
        Change(Model.SourceProperty, request.Source);
        Change(ImportMetadataRules.OriginalDate, request.OriginalDate);
        Change(ImportMetadataRules.Provenance, request.Provenance);
        Change(Model.KindProperty, kind == currentKind ? null : kind);
        Change(Model.LinkUrlProperty, request.LinkUrl is null ? null : linkUrl);
        foreach (var (alias, value) in request.Metadata ?? new Dictionary<string, string>())
        {
            if ((alias == ImportMetadataRules.OriginalDate && request.OriginalDate is not null)
                || (alias == ImportMetadataRules.Provenance && request.Provenance is not null))
            {
                return Invalid<MaterialDetail>($"Send '{alias}' either as a named argument or through metadata, not both.");
            }
            if (Target(alias) != FieldTargets.Metadata)
            {
                return Invalid<MaterialDetail>($"Field '{alias}' cannot be set through metadata.");
            }

            changes.Add((alias, value ?? string.Empty));
        }

        var type = contentTypeService.Get(content.ContentType.Key)!;
        var states = fieldStates.Stored();
        var changed = 0;
        foreach (var (alias, value) in changes)
        {
            var propertyType = type.CompositionPropertyTypes.FirstOrDefault(p => p.Alias == alias);
            if (propertyType is null)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                return Invalid<MaterialDetail>($"Material type '{type.Alias}' has no field '{alias}'.");
            }

            // An empty value clears the field.
            string? stored = null;
            if (!string.IsNullOrWhiteSpace(value) && (stored = Convert(propertyType, value.Trim())) is null)
            {
                return Invalid<MaterialDetail>($"Value '{value}' is not allowed for field '{alias}'. Allowed: {AllowedText(propertyType)}.");
            }

            var old = content.GetValue(alias)?.ToString();
            if (string.Equals(string.IsNullOrEmpty(old) ? null : old, stored, StringComparison.Ordinal))
            {
                continue;
            }

            // Only a changed value counts: what the material holds already stays valid, whatever happened to the field since.
            if (fieldStates.CheckChange(type, alias, old, stored, states) is { } violation)
            {
                return StateRefused<MaterialDetail>(violation);
            }

            content.SetValue(alias, stored);
            changed++;
        }

        // Nothing changed: no save (so no audit entry), the version stays the same.
        if (changed == 0)
        {
            scope.Complete();
            return AgentResult.Ok(ToDetail(content, caller));
        }

        if (title is not null)
        {
            content.Name = Truncate(title, MaxNameLength);
        }

        var saved = contentService.Save(content);
        if (!saved.Success)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.EventMessages, "The content layer refused to update the material."));
        }

        // The Compliance Pack's audit handlers log one update entry per changed field for this save.
        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> SubmitMaterial(AgentCaller caller, Guid materialKey, SubmitMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);
        var confirmed = Confirmed(caller, materialKey, request.Version, "submit a material", mustBeEditable: true);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Cast<MaterialDetail>();
        }

        // The same required-field check as the Compliance Pack's submission endpoint and save guard .
        var content = confirmed.Value!;
        var english = CultureInfo.GetCultureInfo("en");
        var missing = submissionGuard.Check(content, english);
        if (missing.Count > 0)
        {
            // Text for a person plus the fields as data (alias and a label per language), so a consumer need not read the text.
            return AgentResult.Fail<MaterialDetail>(
                AgentErrors.Invalid,
                SubmissionMessages.MissingFields(missing, english),
                new Dictionary<string, object?> { ["missingFields"] = missing });
        }

        LifecycleStatuses.Write(content, Model.StatusProperty, LifecycleStatuses.Submitted);
        var saved = contentService.Save(content);
        if (!saved.Success)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.EventMessages, "The content layer refused to submit the material."));
        }

        scope.Complete();
        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> SetOwners(AgentCaller caller, Guid materialKey, SetOwnersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            return Invalid<MaterialDetail>("'version' is required: send the version of the material the user confirmed.");
        }

        var mode = request.Mode?.Trim().ToLowerInvariant();
        if (mode is not (OwnerModes.Replace or OwnerModes.Add))
        {
            return Invalid<MaterialDetail>($"'mode' is required: '{OwnerModes.Replace}' (these people become the owners) or '{OwnerModes.Add}' (these people are added).");
        }

        var requested = new List<string>();
        foreach (var raw in request.Owners ?? [])
        {
            var key = raw?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > MaxOwnerKeyLength || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return Invalid<MaterialDetail>($"An owner is a person key without spaces, at most {MaxOwnerKeyLength} characters; '{raw}' is not.");
            }

            if (!requested.Contains(key, StringComparer.Ordinal))
            {
                requested.Add(key);
            }
        }

        if (requested.Count == 0)
        {
            return Invalid<MaterialDetail>("'owners' needs at least one person key; a material always keeps an owner.");
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);
        var confirmed = Confirmed(caller, materialKey, request.Version, "change the owners of a material", mustBeEditable: false);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Cast<MaterialDetail>();
        }

        var content = confirmed.Value!;
        if (!content.HasProperty(Model.OwnersProperty))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotConfigured, "Owners are not configured in this content layer.");
        }

        if (IsArchived(content))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotEditable, "The material is archived; the owners of an archived material cannot be changed.");
        }

        if (content.Published && !caller.User.IsInRole(AgentRoles.FunctionalManager))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Forbidden, "Only a functional manager can change the owners of a published material.");
        }

        var current = Owners(content);
        var next = mode == OwnerModes.Replace ? requested : current.Concat(requested.Except(current, StringComparer.Ordinal)).ToList();
        if (next.SequenceEqual(current, StringComparer.Ordinal))
        {
            // Nothing changed: no save (so no audit entry), the version stays the same.
            scope.Complete();
            return AgentResult.Ok(ToDetail(content, caller));
        }

        content.SetValue(Model.OwnersProperty, JsonSerializer.Serialize(next));
        var saved = contentService.Save(content);
        if (!saved.Success)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.EventMessages, "The content layer refused to change the owners."));
        }

        // The Compliance Pack's audit handlers log the owners field with old and new value, person, channel and issuer.
        scope.Complete();
        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> ArchiveMaterial(AgentCaller caller, Guid materialKey, ArchiveMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        if (request.Reason is { Length: > ArchiveService.MaxReasonLength })
        {
            return Invalid<MaterialDetail>($"'reason' is at most {ArchiveService.MaxReasonLength} characters.");
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);
        var confirmed = Confirmed(caller, materialKey, request.Version, "archive a material", mustBeEditable: false);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Cast<MaterialDetail>();
        }

        // The Compliance Pack checks owner and state again and writes status, date and unpublish as one save, with the
        // person of this request as acting user.
        var archived = archiveService.Archive(materialKey, actingUserAccessor.Current, caller.User.IsInRole(AgentRoles.FunctionalManager), request.Reason);
        var failure = archived.Status switch
        {
            ArchiveOperationStatus.Success => null,
            ArchiveOperationStatus.NotPublished => AgentResult.Fail<MaterialDetail>(AgentErrors.NotArchivable, "Only a published material can be archived; this material is not published."),
            ArchiveOperationStatus.AlreadyArchived => AgentResult.Fail<MaterialDetail>(AgentErrors.NotArchivable, "The material is archived already."),
            ArchiveOperationStatus.NotOwner or ArchiveOperationStatus.ActingUserRequired =>
                AgentResult.Fail<MaterialDetail>(AgentErrors.Forbidden, "Only an owner with the developer role or a functional manager can archive a material."),
            ArchiveOperationStatus.NotSupported => Invalid<MaterialDetail>("This material type has no lifecycle that can be archived."),
            ArchiveOperationStatus.DocumentNotFound => NotFound<MaterialDetail>(),
            _ => AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, "The content layer refused to archive the material."),
        };
        if (failure is not null)
        {
            return failure;
        }

        scope.Complete();
        return Material(materialKey) is { } after ? AgentResult.Ok(ToDetail(after, caller)) : NotFound<MaterialDetail>();
    }

    /// <summary>
    /// The checks a confirmed state change shares, under the content write lock: visible (else not_found), an owner with
    /// the developer role or a functional manager, editable when <paramref name="mustBeEditable"/>, and still at the
    /// version the person confirmed.
    /// </summary>
    private AgentResult<IContent> Confirmed(AgentCaller caller, Guid materialKey, string? version, string what, bool mustBeEditable)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Invalid<IContent>("'version' is required: send the version of the material the user confirmed.");
        }

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<IContent>();
        }

        var refusal = mustBeEditable ? ChangeRefused(caller, content, what) : RoleRefused(caller, content, what);
        if (refusal is not null)
        {
            return refusal.Cast<IContent>();
        }

        var current = VersionOf(content);
        return string.Equals(version.Trim(), current, StringComparison.Ordinal)
            ? AgentResult.Ok(content)
            : AgentResult.Fail<IContent>(AgentErrors.VersionConflict,
                $"The material changed after version '{version.Trim()}'; the current version is '{current}'. Read it again and ask the user to confirm the current version.");
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> GetMaterial(AgentCaller caller, Guid materialKey)
    {
        var content = Material(materialKey);
        return content is not null && CanSee(caller, content) ? AgentResult.Ok(ToDetail(content, caller)) : NotFound<MaterialDetail>();
    }

    /// <inheritdoc />
    public AgentResult<DetailLink> GetDetailLink(AgentCaller caller, Guid materialKey)
    {
        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<DetailLink>();
        }

        return LinkFor(content.Key) is { } url
            ? AgentResult.Ok(new DetailLink(content.Key, url))
            : AgentResult.Fail<DetailLink>(AgentErrors.NotConfigured, "No detail page address is configured for this content layer.");
    }

    /// <inheritdoc />
    public AgentResult<IReadOnlyList<GroupSummary>> ListGroups(AgentCaller caller)
    {
        if (caller.User.Roles.Count == 0)
        {
            return NoRole<IReadOnlyList<GroupSummary>>();
        }

        return AgentResult.Ok<IReadOnlyList<GroupSummary>>(AllGroups()
            .Select(group => new GroupSummary(group.Key, GroupTitle(group)))
            .OrderBy(group => group.Title, StringComparer.CurrentCulture)
            .ToList());
    }

    /// <inheritdoc />
    public AgentResult<MaterialDetail> AddToGroup(AgentCaller caller, Guid materialKey, Guid groupKey) =>
        ChangeGroup(caller, materialKey, groupKey, add: true);

    /// <inheritdoc />
    public AgentResult<MaterialDetail> RemoveFromGroup(AgentCaller caller, Guid materialKey, Guid groupKey) =>
        ChangeGroup(caller, materialKey, groupKey, add: false);

    /// <inheritdoc />
    public AgentResult<MaterialDetail> AddRelation(AgentCaller caller, Guid materialKey, RelationRequest request) =>
        ChangeRelation(caller, materialKey, request, add: true);

    /// <inheritdoc />
    public AgentResult<MaterialDetail> RemoveRelation(AgentCaller caller, Guid materialKey, RelationRequest request) =>
        ChangeRelation(caller, materialKey, request, add: false);

    // ---- groups and relations ----

    /// <summary>
    /// Group membership is a property of the group document, so the change is one save of the group: Umbraco's
    /// notifications fire for it, the Compliance Pack audits the members field and the host sends a
    /// <c>ContentSaved</c> event for the group, which the platform fans out to old and new members.
    /// </summary>
    private AgentResult<MaterialDetail> ChangeGroup(AgentCaller caller, Guid materialKey, Guid groupKey, bool add)
    {
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        // The members are read and written under the content write lock, so two changes to one group cannot overwrite each other.
        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, add ? "add a material to a group" : "remove a material from a group") is { } notAllowed)
        {
            return notAllowed;
        }

        var group = Group(groupKey);
        if (group is null)
        {
            return NotFound<MaterialDetail>();
        }

        if (!group.HasProperty(Model.GroupMembersProperty))
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotConfigured, "Groups are not configured in this content layer.");
        }

        var members = Members(group);
        var isMember = members.Contains(content.Key);
        if (add == isMember)
        {
            scope.Complete();
            return AgentResult.Ok(ToDetail(content, caller));
        }

        if (add)
        {
            members.Add(content.Key);
        }
        else
        {
            members.RemoveAll(key => key == content.Key);
        }

        group.SetValue(Model.GroupMembersProperty, members.Count == 0 ? null : string.Join(",", members.Select(key => Udi.Create(Constants.UdiEntityType.Document, key).ToString())));
        var saved = contentService.Save(group);
        if (!saved.Success)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.Cancelled, CancelMessage(saved.EventMessages, "The content layer refused to save the group."));
        }

        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    /// <summary>
    /// A typed link is an Umbraco relation. Relate and delete go through <see cref="IRelationService"/>, so its
    /// notifications fire: the Compliance Pack audits them and the host sends a relation event for both materials.
    /// </summary>
    private AgentResult<MaterialDetail> ChangeRelation(AgentCaller caller, Guid materialKey, RelationRequest request, bool add)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialDetail>();
        }

        var alias = request.Type?.Trim() ?? string.Empty;
        if (!options.Value.DetailPage.RelationTypes.Contains(alias, StringComparer.Ordinal))
        {
            return Invalid<MaterialDetail>($"Relation type '{request.Type}' is not allowed. Allowed: {string.Join(", ", options.Value.DetailPage.RelationTypes)}.");
        }

        if (request.TargetKey == materialKey)
        {
            return Invalid<MaterialDetail>("A material cannot be linked to itself.");
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<MaterialDetail>();
        }

        if (ChangeRefused(caller, content, add ? "link a material" : "remove a link") is { } notAllowed)
        {
            return notAllowed;
        }

        // The other material only has to be visible: linking to a published material is the usual case. A material
        // the caller may not see gives the same answer as one that does not exist, so a link reveals nothing about it.
        var target = Material(request.TargetKey);
        if (target is null || !CanSee(caller, target))
        {
            return NotFound<MaterialDetail>();
        }

        var relationType = relationService.GetRelationTypeByAlias(alias);
        if (relationType is null)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotConfigured, $"Relation type '{alias}' does not exist in this content layer.");
        }

        var (parent, child) = request.Inverse ? (content, target) : (target, content);
        var existing = relationService.GetByParentOrChildId(content.Id, alias)
            .Where(r => (r.ParentId == parent.Id && r.ChildId == child.Id)
                || (relationType.IsBidirectional && r.ParentId == child.Id && r.ChildId == parent.Id))
            .ToList();

        if (add && existing.Count == 0)
        {
            relationService.Relate(parent, child, relationType);
        }
        else if (!add)
        {
            foreach (var relation in existing)
            {
                relationService.Delete(relation);
            }
        }

        scope.Complete();

        return AgentResult.Ok(ToDetail(content, caller));
    }

    private IContent? Group(Guid key)
    {
        if (string.IsNullOrEmpty(Model.GroupDocumentTypeAlias))
        {
            return null;
        }

        var group = contentService.GetById(key);
        return group is { Trashed: false } && group.ContentType.Alias == Model.GroupDocumentTypeAlias ? group : null;
    }

    private List<IContent> AllGroups()
    {
        var type = string.IsNullOrEmpty(Model.GroupDocumentTypeAlias) ? null : contentTypeService.Get(Model.GroupDocumentTypeAlias);
        var result = new List<IContent>();
        if (type is null)
        {
            return result;
        }

        const int pageSize = 500;
        long page = 0, total;
        do
        {
            result.AddRange(contentService.GetPagedOfType(type.Id, page, pageSize, out total, filter: null!).Where(c => !c.Trashed));
            page++;
        }
        while (page * pageSize < total);

        return result;
    }

    private string GroupTitle(IContent group) => group.GetValue<string>(Model.TitleProperty) is { Length: > 0 } title ? title : group.Name ?? string.Empty;

    /// <summary>Member keys in group order, from the picker's stored comma-separated document UDIs; anything else is skipped.</summary>
    private List<Guid> Members(IContent group)
    {
        var raw = group.GetValue<string>(Model.GroupMembersProperty);
        var keys = new List<Guid>();
        foreach (var part in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (UdiParser.TryParse(part, out GuidUdi? udi) && udi.EntityType == Constants.UdiEntityType.Document && !keys.Contains(udi.Guid))
            {
                keys.Add(udi.Guid);
            }
        }

        return keys;
    }

    // ---- rights ----

    /// <summary>State changes need the person's own delegated token.</summary>
    private static AgentResult<object>? WriteRefused(AgentCaller caller) => caller.User.IsDelegated
        ? null
        : AgentResult.Fail<object>(AgentErrors.DelegatedTokenRequired, "Changes need the signed-in person's own token; team and service credentials are read-only.");

    /// <summary>
    /// Visibility per role preset (union of roles): functional managers see everything, owners
    /// their own materials, reviewers submitted/inReview ones, every role publications within the published audience.
    /// Updates and new elements go through this check first, so a material a caller cannot see is not_found there too.
    /// </summary>
    private bool CanSee(AgentCaller caller, IContent content)
    {
        var user = caller.User;
        if (user.Roles.Count == 0 || content.Trashed)
        {
            return false;
        }

        if (CanSeeDraft(caller, content))
        {
            return true;
        }

        // Access and returned values must use the same published snapshot. Draft edits to the audience cannot
        // expose a restricted publication or hide a publication before the next approval and publish.
        if (!content.Published)
        {
            return false;
        }

        return ReadCode(content.GetValue<string>(Model.SharingLevelProperty, published: true)) switch
        {
            "public" => true,
            "groups" => ReadIds(content.GetValue<string>(Model.SharingGroupsProperty, published: true)).Any(user.Groups.Contains),
            _ => false, // Missing/unknown level, missing/malformed groups and token overage all fail closed.
        };
    }

    private bool CanSeeDraft(AgentCaller caller, IContent content) =>
        caller.User.IsInRole(AgentRoles.FunctionalManager)
        || Owners(content).Contains(caller.User.PersonId, StringComparer.Ordinal)
        || (caller.User.IsInRole(AgentRoles.Reviewer) && ReviewStatuses.Contains(Status(content), StringComparer.Ordinal));

    /// <summary>
    /// Changes to an existing material (elements, metadata): an owner with the developer role or a functional manager,
    /// and only while the material is editable. Null when the change may go ahead.
    /// </summary>
    private AgentResult<MaterialDetail>? ChangeRefused(AgentCaller caller, IContent content, string what)
    {
        if (RoleRefused(caller, content, what) is { } forbidden)
        {
            return forbidden;
        }

        if (content.Published)
        {
            return AgentResult.Fail<MaterialDetail>(AgentErrors.NotEditable, "The material is published; a published material cannot be changed.");
        }

        return IsEditable(content)
            ? null
            : AgentResult.Fail<MaterialDetail>(AgentErrors.NotEditable, $"The material has status '{Status(content)}'; only draft and rejected materials can be changed.");
    }

    /// <summary>An owner with the developer role or a functional manager; null when the caller is one of them.</summary>
    private AgentResult<MaterialDetail>? RoleRefused(AgentCaller caller, IContent content, string what) =>
        caller.User.IsInRole(AgentRoles.FunctionalManager)
        || (caller.User.IsInRole(AgentRoles.Developer) && Owners(content).Contains(caller.User.PersonId, StringComparer.Ordinal))
            ? null
            : AgentResult.Fail<MaterialDetail>(AgentErrors.Forbidden, $"Only an owner with the developer role or a functional manager can {what}.");

    /// <summary>
    /// Only unpublished draft and rejected materials change; archived ones (start of retention) never do. Published
    /// is checked apart from the status, because the stored status of a published material can still be draft.
    /// </summary>
    private bool IsEditable(IContent content) =>
        !content.Trashed
        && !content.Published
        && EditableStatuses.Contains(Status(content), StringComparer.Ordinal)
        && !IsArchived(content);

    private bool IsArchived(IContent content) =>
        content.HasProperty(Model.ArchivedAtProperty) && !string.IsNullOrEmpty(content.GetValue(Model.ArchivedAtProperty)?.ToString());

    // ---- model ----

    private List<IContentType> MaterialTypes() => contentTypeService.GetAll()
        .Where(type => !type.IsElement
            && !string.Equals(type.Alias, Model.MaterialCompositionAlias, StringComparison.Ordinal)
            && type.ContentTypeCompositionExists(Model.MaterialCompositionAlias))
        .OrderBy(type => type.Alias, StringComparer.Ordinal)
        .ToList();

    private IMediaType? ElementType() => mediaTypeService.Get(Model.ElementMediaTypeAlias);

    private List<string> AllowedIn(string collectionTypeAlias)
    {
        var collectionType = contentTypeService.Get(collectionTypeAlias);
        var allowedKeys = collectionType?.AllowedContentTypes?.Select(t => t.Key).ToHashSet() ?? [];
        return MaterialTypes().Where(t => allowedKeys.Contains(t.Key)).Select(t => t.Alias).ToList();
    }

    private List<CollectionInfo> Collections(AgentCaller caller, List<IContentType> materialTypes)
    {
        var canCreate = caller.User.IsDelegated && caller.User.IsInRole(AgentRoles.Developer);
        var result = new List<CollectionInfo>();
        foreach (var alias in Model.CollectionDocumentTypes)
        {
            var type = contentTypeService.Get(alias);
            if (type is null)
            {
                continue;
            }

            var allowed = AllowedIn(alias);
            const int pageSize = 500;
            long page = 0, total;
            do
            {
                foreach (var item in contentService.GetPagedOfType(type.Id, page, pageSize, out total, filter: null!).Where(c => !c.Trashed))
                {
                    result.Add(new CollectionInfo(item.Key, item.Name ?? string.Empty, PathOf(item), allowed, canCreate && allowed.Count > 0));
                }

                page++;
            }
            while (page * pageSize < total);
        }

        return result.OrderBy(c => c.Path, StringComparer.CurrentCulture).ToList();
    }

    private string PathOf(IContent item)
    {
        var ids = item.Path.Split(',').Skip(1).Select(id => int.Parse(id, CultureInfo.InvariantCulture)).ToArray();
        return string.Join(" / ", contentService.GetByIds(ids).OrderBy(c => c.Level).Select(c => c.Name));
    }

    private IContent? Material(Guid key)
    {
        var content = contentService.GetById(key);
        return content is not null && content.ContentType.Alias != Model.MaterialCompositionAlias
               && contentTypeService.Get(content.ContentType.Key)?.ContentTypeCompositionExists(Model.MaterialCompositionAlias) == true
            ? content
            : null;
    }

    private string Target(string alias)
    {
        if (ImportMetadataRules.IsImportField(alias)) { return FieldTargets.Metadata; }
        if (alias == Model.TitleProperty || alias == Model.DescriptionProperty || alias == Model.LicenseProperty
            || alias == Model.SharingLevelProperty || alias == Model.SourceProperty || alias == Model.KindProperty || alias == Model.LinkUrlProperty)
        {
            return alias;
        }

        return alias == Model.StatusProperty || alias == Model.OwnersProperty || alias == Model.ElementsProperty || alias == Model.ArchivedAtProperty
            ? FieldTargets.System
            : FieldTargets.Metadata;
    }

    private string AllowedText(IPropertyType propertyType)
    {
        if (propertyType.Alias == Model.SharingGroupsProperty)
        {
            return "a JSON array of group ids, for example [\"group-a\"]";
        }

        var deprecated = fieldStates.Stored().GetValueOrDefault(propertyType.Alias)?.DeprecatedValues ?? FieldStateInfo.None.DeprecatedValues;
        return string.Join(", ", (AllowedValues(propertyType) ?? []).Where(code => !deprecated.Contains(code, StringComparer.OrdinalIgnoreCase)));
    }

    private static AgentResult<T> StateRefused<T>(FieldStateViolation violation) =>
        AgentResult.Fail<T>(violation.Code == FieldStateErrorCodes.FieldArchived ? AgentErrors.FieldArchived : AgentErrors.ValueDeprecated,
            FieldStateMessages.Text(violation, CultureInfo.GetCultureInfo("en")));

    private List<string>? AllowedValues(IPropertyType? propertyType)
    {
        if (propertyType is null || propertyType.PropertyEditorAlias != Constants.PropertyEditors.Aliases.DropDownListFlexible)
        {
            return null;
        }

        return dataTypeService.GetAsync(propertyType.DataTypeKey).GetAwaiter().GetResult()?.ConfigurationObject is ValueListConfiguration list
            ? list.Items.ToList()
            : null;
    }

    /// <summary>Stored form of a value: list fields store a JSON array of codes and accept only allowed codes.</summary>
    private string? Convert(IPropertyType propertyType, string value)
    {
        // Sharing groups decide who reads a publication: store only a well-formed list, normalised like it is read.
        if (propertyType.Alias == Model.SharingGroupsProperty)
        {
            var ids = ReadIds(value);
            return ids.Count > 0 ? JsonSerializer.Serialize(ids) : null;
        }

        var allowed = AllowedValues(propertyType);
        if (allowed is null)
        {
            return propertyType.PropertyEditorAlias == Constants.PropertyEditors.Aliases.DropDownListFlexible ? null : value;
        }

        var code = allowed.FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        return code is null ? null : JsonSerializer.Serialize(new[] { code });
    }

    private IMedia ElementsFolder(IContent content)
    {
        // One media folder per material; later elements go into the folder of the first one.
        var first = PickedMedia(content).Select(mediaService.GetById).OfType<IMedia>().FirstOrDefault();
        if (first is not null && first.ParentId > 0 && mediaService.GetById(first.ParentId) is { } current)
        {
            return current;
        }

        var name = Truncate(content.Name ?? content.Key.ToString(), MaxNameLength);
        var folder = Model.ElementsRootKey is { } rootKey && mediaService.GetById(rootKey) is { } root
            ? mediaService.CreateMedia(name, root, Constants.Conventions.MediaTypes.Folder)
            : mediaService.CreateMedia(name, Constants.System.Root, Constants.Conventions.MediaTypes.Folder);
        mediaService.Save(folder);
        return folder;
    }

    // ---- reading ----

    private MaterialDetail ToDetail(IContent content, AgentCaller caller)
    {
        var published = !CanSeeDraft(caller, content);
        var kind = KindOf(content, published);
        var owners = Owners(content, published);
        var fields = content.Properties
            .Where(p => p.Alias != Model.OwnersProperty && p.Alias != Model.ElementsProperty)
            .ToDictionary(p => p.Alias, p => p.Alias == Model.SharingGroupsProperty
                ? p.GetValue(published: published)?.ToString()
                : ReadCode(p.GetValue(published: published)?.ToString()), StringComparer.Ordinal);
        var (relations, hidden) = Relations(caller, content);

        return new MaterialDetail
        {
            Key = content.Key,
            Title = content.GetValue<string>(Model.TitleProperty, published: published) ?? string.Empty,
            MaterialType = content.ContentType.Alias,
            CollectionKey = contentService.GetParent(content)?.Key,
            Status = Status(content, published),
            Published = content.Published,
            Kind = kind,
            LinkUrl = kind == KindLink ? content.GetValue<string>(Model.LinkUrlProperty, published: published) : null,
            Owners = owners,
            SharingGroups = ReadIds(content.GetValue<string>(Model.SharingGroupsProperty, published: published)),
            Fields = fields,
            Elements = PickedMedia(content, published).Select(mediaService.GetById).OfType<IMedia>()
                .Where(m => !m.Trashed && (m.GetValue<bool>("confidential") != true || CanSeeConfidential(caller, owners)))
                .Select(ToElement).ToList(),
            HiddenRelationCount = hidden,
            Relations = relations,
            Groups = GroupsOf(caller, content),
            DetailLink = LinkFor(content.Key),
            Version = published ? null : VersionOf(content),
            Review = published ? null : ReviewOf(content),
            ArchivedAt = published ? null : RetentionCalculator.ParseDate(content.GetValue(Model.ArchivedAtProperty)),
        };
    }

    /// <summary>The latest decision with its feedback, and whether it is about the current draft.</summary>
    private ReviewInfo? ReviewOf(IContent content) => approvalStore.GetAll(content.Key) is [var latest, ..]
        ? new ReviewInfo(latest.Decision, latest.Comment, latest.Findings, latest.ReviewerName,
            DateTime.SpecifyKind(latest.DecidedUtc, DateTimeKind.Utc),
            string.Equals(latest.VersionHash, versionHasher.ComputeHash(content), StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>Confidential elements: the owners and the functional manager, the same rule as for hidden drafts.</summary>
    internal static bool CanSeeConfidential(AgentCaller caller, IReadOnlyList<string> owners) =>
        caller.User.IsInRole(AgentRoles.FunctionalManager) || owners.Contains(caller.User.PersonId, StringComparer.Ordinal);

    /// <summary>
    /// The related materials this caller may see, and an anonymous count per relation type of the ones the caller cannot
    /// see. A trashed or missing material is neither listed nor counted: it no longer exists for anyone, so a
    /// count would only reveal that something was deleted. Another person's draft, an unpublished or restricted material
    /// is counted: the relation exists and is not accessible.
    /// </summary>
    private (List<RelationInfo> Visible, Dictionary<string, int> Hidden) Relations(AgentCaller caller, IContent content)
    {
        var visible = new List<RelationInfo>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relation in relationService.GetByParentOrChildId(content.Id))
        {
            var alias = relation.RelationType.Alias;
            if (!options.Value.DetailPage.RelationTypes.Contains(alias, StringComparer.Ordinal)) { continue; }
            var isParent = relation.ParentId == content.Id;
            var other = contentService.GetById(isParent ? relation.ChildId : relation.ParentId);
            if (other is null || other.Trashed) { continue; }
            if (CanSee(caller, other)) { visible.Add(new RelationInfo(alias, isParent, other.Key, TitleFor(caller, other))); }
            else { counts[alias] = counts.GetValueOrDefault(alias) + 1; }
        }
        return (visible, counts);
    }

    /// <summary>
    /// Groups that contain this material, with the members the caller may see in group order and an anonymous count of
    /// the others (the same rule as for relations).
    /// </summary>
    private List<GroupInfo> GroupsOf(AgentCaller caller, IContent content)
    {
        var groups = new List<GroupInfo>();
        foreach (var group in AllGroups())
        {
            var keys = Members(group);
            if (!keys.Contains(content.Key)) { continue; }
            var members = new List<GroupMember>();
            var hidden = 0;
            foreach (var key in keys)
            {
                var member = key == content.Key ? content : contentService.GetById(key);
                if (member is null || member.Trashed) { continue; }
                if (CanSee(caller, member)) { members.Add(new GroupMember(member.Key, TitleFor(caller, member))); }
                else { hidden++; }
            }
            groups.Add(new GroupInfo(group.Key, GroupTitle(group), members, hidden));
        }
        return groups.OrderBy(g => g.Title, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Title from the same version the caller sees of that material: the draft for owners, managers and active reviewers, else the published one.</summary>
    private string TitleFor(AgentCaller caller, IContent other) =>
        other.GetValue<string>(Model.TitleProperty, published: !CanSeeDraft(caller, other)) ?? string.Empty;

    private ElementInfo ToElement(IMedia media) => new(
        media.Key,
        media.Name ?? string.Empty,
        ReadCode(media.GetValue<string>(Model.CategoryProperty)),
        long.TryParse(media.GetValue(Constants.Conventions.Media.Bytes)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
        media.GetValue<string>(Model.Sha256Property), media.GetValue<string>("transcript"));

    /// <summary>A material without a stored kind (created before the kind property existed, or without it) is a file material.</summary>
    private string KindOf(IContent content, bool published = false) =>
        string.Equals(ReadCode(content.GetValue<string>(Model.KindProperty, published: published)), KindLink, StringComparison.OrdinalIgnoreCase) ? KindLink : KindFile;

    /// <summary>Why a kind and link pair is not allowed, or null: a link needs a web address, a file has none.</summary>
    private static string? LinkRuleViolation(string? kind, string? linkUrl, string? requestedKind)
    {
        if (kind is not (null or KindFile or KindLink))
        {
            return $"Kind '{requestedKind}' is not allowed. Allowed: {KindFile}, {KindLink}.";
        }

        if (kind == KindLink && !IsWebUrl(linkUrl))
        {
            return $"A link material needs 'linkUrl': an absolute http or https address of at most {MaxLinkUrlLength} characters.";
        }

        return kind != KindLink && !string.IsNullOrEmpty(linkUrl)
            ? $"'linkUrl' is only allowed for kind '{KindLink}'; send an empty 'linkUrl' to clear it."
            : null;
    }

    /// <summary>
    /// Concurrency token: the update date of the material, ISO-8601 in UTC with seven decimals. Not the version id:
    /// Umbraco keeps that while a draft is saved again.
    /// </summary>
    private static string VersionOf(IContent content)
    {
        var date = content.UpdateDate.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(content.UpdateDate, DateTimeKind.Utc) : content.UpdateDate.ToUniversalTime();
        return date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static bool IsWebUrl(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLinkUrlLength && !value.Any(char.IsWhiteSpace)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    private string Status(IContent content, bool published = false) => ReadCode(content.GetValue<string>(Model.StatusProperty, published: published)) ?? Draft;

    private List<string> Owners(IContent content, bool published = false) =>
        ReadIds(content.GetValue<string>(Model.OwnersProperty, published: published));

    /// <summary>Audience lists are JSON arrays of non-empty ids; malformed data never grants access.</summary>
    private static List<string> ReadIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return []; }
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(raw);
            return ids is null || ids.Any(string.IsNullOrWhiteSpace) ? [] : ids.Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return []; }
    }

    private List<Guid> PickedMedia(IContent content, bool published = false) =>
        ParsePickedMedia(content.GetValue<string>(Model.ElementsProperty, published: published));

    /// <summary>Media keys of a stored media picker value (a JSON array of <c>{ key, mediaKey }</c>); empty when unreadable.</summary>
    internal static List<Guid> ParsePickedMedia(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : document.RootElement.EnumerateArray()
                    .Select(item => item.TryGetProperty("mediaKey", out var key) && key.TryGetGuid(out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty)
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>List values are stored as a JSON array of codes; return the single code.</summary>
    private static string? ReadCode(string? raw)
    {
        if (raw is null || !raw.StartsWith('['))
        {
            return raw;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw)?.FirstOrDefault();
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private string? LinkFor(Guid key) => string.IsNullOrEmpty(options.Value.DetailLinkTemplate)
        ? null
        : options.Value.DetailLinkTemplate.Replace(AgentApiOptions.DetailLinkPlaceholder, key.ToString("D"), StringComparison.Ordinal);

    // ---- helpers ----

    private static void SetIfExists(IContentBase item, string alias, object value)
    {
        if (item.HasProperty(alias))
        {
            item.SetValue(alias, value);
        }
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return System.Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static string CancelMessage(EventMessages? messages, string fallback)
    {
        var text = messages?.GetAll().Select(m => m.Message).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return text is { Count: > 0 } ? string.Join(" ", text) : fallback;
    }

    private static AgentResult<T> NoRole<T>() =>
        AgentResult.Fail<T>(AgentErrors.Forbidden, "You have no role in the learning material bank.");

    private static AgentResult<T> NotFound<T>() =>
        AgentResult.Fail<T>(AgentErrors.NotFound, "Not found or no access.");

    private static AgentResult<T> Invalid<T>(string message) => AgentResult.Fail<T>(AgentErrors.Invalid, message);

    /// <summary>Targets of a field in <see cref="FieldInfo.Target"/> besides the named arguments.</summary>
    private static class FieldTargets
    {
        public const string Metadata = "metadata";
        public const string System = "system";
    }
}
