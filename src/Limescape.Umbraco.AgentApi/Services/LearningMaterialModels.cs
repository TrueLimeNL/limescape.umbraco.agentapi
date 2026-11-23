using System.Text.Json;
using System.Text.Json.Serialization;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>A place where materials can be created (<c>list_collections</c>).</summary>
/// <param name="Key">Content key of the collection.</param>
/// <param name="Name">Collection name.</param>
/// <param name="Path">Names from the content root to the collection, separated by " / ".</param>
/// <param name="MaterialTypes">Material type aliases allowed in this collection.</param>
/// <param name="CanCreate">True when the caller may create a material here.</param>
public sealed record CollectionInfo(Guid Key, string Name, string Path, IReadOnlyList<string> MaterialTypes, bool CanCreate);

/// <summary>
/// Everything an agent needs to propose metadata for a document: the material types with their fields and
/// allowed values, and the collections. The agent proposes; a person confirms before <c>create_material</c>.
/// </summary>
public sealed record MetadataContext(IReadOnlyList<MaterialTypeInfo> MaterialTypes, IReadOnlyList<CollectionInfo> Collections, ElementFieldInfo Element);

/// <param name="Alias">Document type alias, used as <c>materialType</c> in <c>create_material</c>.</param>
/// <param name="Name">Name or localization key of the type.</param>
/// <param name="Description">Description or localization key of the type.</param>
/// <param name="Fields">Fields of the type, core fields first.</param>
/// <param name="Labels">Label of the type per language (<c>nl</c>, <c>en</c>); the alias where the tenant configured none.</param>
public sealed record MaterialTypeInfo(string Alias, string Name, string? Description, IReadOnlyList<FieldInfo> Fields, IReadOnlyDictionary<string, string> Labels);

/// <param name="Alias">Property alias.</param>
/// <param name="Name">Name or localization key.</param>
/// <param name="Editor">Umbraco property editor alias.</param>
/// <param name="Mandatory">Mandatory in the Umbraco backoffice (Umbraco's own flag, enforced on publish). See <paramref name="Required"/> for the rule of the content layer.</param>
/// <param name="AllowedValues">Codes of a list field that can newly be chosen (deprecated codes left out); null for free text.</param>
/// <param name="Target">
/// Where the value goes in <c>create_material</c>: a named argument (<c>title</c>, <c>description</c>, <c>license</c>,
/// <c>sharingLevel</c>, <c>source</c>), <c>metadata</c> for other fields, or <c>system</c> for fields the content layer sets itself.
/// </param>
/// <param name="Type">Field type for a form: <c>string</c>, <c>text</c>, <c>list</c>, <c>number</c>, <c>date</c>, <c>url</c>, <c>boolean</c>, <c>system</c> or <c>other</c>.</param>
/// <param name="Required">
/// True when the field must be filled to submit a material of this type for review: mandatory in the object model, or required
/// by the Compliance Pack's submission rule. Not required for a draft, except the title.
/// </param>
/// <param name="Labels">Label of the field per language (<c>nl</c>, <c>en</c>); the alias where the tenant configured none.</param>
/// <param name="Values">Every code of a list field, deprecated ones included and flagged, with a label per language each; null for other fields.</param>
/// <param name="Archived">
/// The field is archived (in the archived property group of the type, or flagged through <c>set_metadata_field_state</c>):
/// its values stay on the materials and stay readable, but no new value can be written (<c>field_archived</c>), and a form
/// or a proposal leaves it out.
/// </param>
/// <param name="Proposable">False for system fields and import facts that AI must never invent.</param>
public sealed record FieldInfo(
    string Alias,
    string Name,
    string Editor,
    bool Mandatory,
    IReadOnlyList<string>? AllowedValues,
    string Target,
    string Type,
    bool Required,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<ValueInfo>? Values,
    bool Archived = false,
    bool Proposable = true);

/// <summary>One code of a list field.</summary>
/// <param name="Value">The code as stored.</param>
/// <param name="Labels">Label per language (<c>nl</c>, <c>en</c>); the code where the tenant configured none.</param>
/// <param name="Deprecated">
/// The code can no longer be chosen: a material that holds it keeps it, any other material is refused it
/// (<c>value_deprecated</c>). A form or a proposal leaves it out.
/// </param>
public sealed record ValueInfo(string Value, IReadOnlyDictionary<string, string> Labels, bool Deprecated = false);

/// <summary>
/// The state of one metadata field, set by a functional manager: archived or not, and the deprecated codes.
/// Existing values on materials are never touched. Send <see cref="Version"/> from an earlier answer to refuse the change
/// when somebody else changed the state in the meantime; leave it out the first time.
/// </summary>
public sealed record SetFieldStateRequest
{
    /// <summary><see cref="MetadataFieldState.Version"/> of the state the person saw; optional.</summary>
    public string? Version { get; init; }

    /// <summary>Archive the field (<c>true</c>) or restore it (<c>false</c>).</summary>
    public bool Archived { get; init; }

    /// <summary>All codes that are deprecated after this change (the list replaces the earlier one); empty for none. Codes of a list field only.</summary>
    public IReadOnlyList<string>? DeprecatedValues { get; init; }
}

/// <summary>The state of a metadata field after <c>set_metadata_field_state</c>.</summary>
/// <param name="Alias">Property alias.</param>
/// <param name="Archived">Archived by flag or by the archived property group.</param>
/// <param name="ArchivedByGroup">The property sits in the archived group; restoring it then means moving it out of that group.</param>
/// <param name="DeprecatedValues">The deprecated codes.</param>
/// <param name="Version">State version, to send with the next change.</param>
public sealed record MetadataFieldState(string Alias, bool Archived, bool ArchivedByGroup, IReadOnlyList<string> DeprecatedValues, string Version);

/// <summary>Element (file) fields: the allowed categories and the inline size limit.</summary>
public sealed record ElementFieldInfo(IReadOnlyList<string> Categories, long MaxInlineBytes);

/// <summary>Confirmed values for a new material. Unconfirmed AI suggestions must not be sent.</summary>
public sealed record CreateMaterialRequest
{
    public Guid CollectionKey { get; init; }

    public string MaterialType { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? License { get; init; }

    public string? SharingLevel { get; init; }

    public string? Source { get; init; }

    /// <summary>Optional source date, yyyy-MM-dd (UTC today at the latest); empty clears on update.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>Optional import provenance, at most 500 characters; only a functional manager may change it after creation.</summary>
    public string? Provenance { get; init; }

    /// <summary><c>file</c> (default) or <c>link</c>. Left out with a <see cref="LinkUrl"/>, the material is a link.</summary>
    public string? Kind { get; init; }

    /// <summary>Absolute http(s) URL of a link-only material. Required for kind <c>link</c>, refused for kind <c>file</c>.</summary>
    public string? LinkUrl { get; init; }

    /// <summary>Other fields of the material type by alias. Reserved and system fields are refused.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Confirmed changes to a material's metadata. A field that is left out (null) stays as it is; an empty string clears it
/// (not the title). Status, owners, elements and the archive date are set by the content layer, never by an update.
/// </summary>
public sealed record UpdateMaterialRequest
{
    /// <summary><see cref="MaterialDetail.Version"/> of the material as the caller last read it. Required.</summary>
    public string Version { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? License { get; init; }

    public string? SharingLevel { get; init; }

    public string? Source { get; init; }

    /// <summary>Optional source date, yyyy-MM-dd (UTC today at the latest); empty clears on update.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>Optional import provenance, at most 500 characters; only a functional manager may change it after creation.</summary>
    public string? Provenance { get; init; }

    /// <summary><c>file</c> or <c>link</c>. A material with elements cannot become a link.</summary>
    public string? Kind { get; init; }

    /// <summary>Absolute http(s) URL; only for kind <c>link</c>. Send an empty string to clear it when changing to <c>file</c>.</summary>
    public string? LinkUrl { get; init; }

    /// <summary>Other fields of the material type by alias. Reserved and system fields are refused.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Members the request does not know, such as <c>status</c> or <c>owners</c>; any value is refused.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>A confirmed submission for review of a draft or rejected material.</summary>
public sealed record SubmitMaterialRequest
{
    /// <summary><see cref="MaterialDetail.Version"/> of the material the person confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;
}

/// <summary>How <see cref="SetOwnersRequest.Owners"/> relates to the current owners.</summary>
public static class OwnerModes
{
    /// <summary>The listed people become the owners; everyone else stops being an owner.</summary>
    public const string Replace = "replace";

    /// <summary>The listed people are added to the current owners; nobody is removed.</summary>
    public const string Add = "add";
}

/// <summary>Confirmed change of the owners of a material (co-owners, a new developer, the move to another person key).</summary>
public sealed record SetOwnersRequest
{
    /// <summary><see cref="MaterialDetail.Version"/> of the material the person confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary><see cref="OwnerModes.Replace"/> or <see cref="OwnerModes.Add"/>. Required, there is no default.</summary>
    public string? Mode { get; init; }

    /// <summary>Person keys (the id the person's token carries: the e-mail address of a platform token, the object id of an Entra token). At least one.</summary>
    public IReadOnlyList<string>? Owners { get; init; }
}

/// <summary>A confirmed archive of a published material.</summary>
public sealed record ArchiveMaterialRequest
{
    /// <summary><see cref="MaterialDetail.Version"/> of the material the person confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Optional reason, written to the audit log (max 500 characters).</summary>
    public string? Reason { get; init; }
}

/// <summary>One file to add to a material, sent inline.</summary>
/// <param name="FileName">Original file name.</param>
/// <param name="Category">Format category code.</param>
/// <param name="ContentBase64">File content, base64.</param>
public sealed record ElementUpload(string FileName, string Category, string ContentBase64);

/// <summary>
/// A verified upload session, signed by the platform (<see cref="UploadSessionSignature"/>): the file is already in
/// the media storage and is registered as an element without being sent again.
/// </summary>
public sealed record UploadSessionRegistration
{
    /// <summary>Id of the upload session in the platform; also in the signature.</summary>
    public string? SessionId { get; init; }

    /// <summary>Path of the committed file, relative to the media file system (for Azure: to the media container root).</summary>
    public string? Blob { get; init; }

    /// <summary>Size in bytes, as verified.</summary>
    public long Length { get; init; }

    /// <summary>SHA-256 of the file as verified, 64 lowercase hex characters.</summary>
    public string? Sha256 { get; init; }

    /// <summary>ETag of the blob when it was verified. Signed and kept in the signature only; the media file system has no ETag.</summary>
    public string? Etag { get; init; }

    /// <summary>Format category code of the element.</summary>
    public string? Category { get; init; }

    /// <summary>File name shown for the element.</summary>
    public string? FileName { get; init; }

    /// <summary>Id of the recorded human confirmation of the save in the platform.</summary>
    public string? ConfirmationId { get; init; }

    /// <summary>End of validity of the signature, Unix seconds.</summary>
    public long ExpiresAt { get; init; }

    /// <summary>HMAC-SHA256 signature, lowercase hex.</summary>
    public string? Signature { get; init; }
}

/// <summary>A material as the caller may see it.</summary>
public sealed record MaterialDetail
{
    public Guid Key { get; init; }

    public string Title { get; init; } = string.Empty;

    public string MaterialType { get; init; } = string.Empty;

    public Guid? CollectionKey { get; init; }

    /// <summary>Lifecycle status code; a new material is always <c>draft</c>.</summary>
    public string Status { get; init; } = string.Empty;

    public bool Published { get; init; }

    /// <summary><c>file</c> or <c>link</c>; a material without a stored kind is a file material.</summary>
    public string Kind { get; init; } = "file";

    /// <summary>URL of a link-only material; null for a file material.</summary>
    public string? LinkUrl { get; init; }

    public IReadOnlyList<string> Owners { get; init; } = [];

    /// <summary>Sharing-group ids from the same version as the other returned values.</summary>
    public IReadOnlyList<string> SharingGroups { get; init; } = [];

    /// <summary>Field values by alias (list fields as their code).</summary>
    public IReadOnlyDictionary<string, string?> Fields { get; init; } = new Dictionary<string, string?>();

    public IReadOnlyList<ElementInfo> Elements { get; init; } = [];

    /// <summary>
    /// Related materials the caller may not see, as an anonymous count per relation type alias. Only types shown on
    /// the detail page are counted, and only existing, non-deleted materials; never a key, title or other trait of the
    /// hidden material.
    /// </summary>
    public IReadOnlyDictionary<string, int> HiddenRelationCount { get; init; } = new Dictionary<string, int>();

    /// <summary>Typed links to related materials the caller may see, read in both directions.</summary>
    public IReadOnlyList<RelationInfo> Relations { get; init; } = [];

    /// <summary>Groups this material belongs to, with the members the caller may see in group order.</summary>
    public IReadOnlyList<GroupInfo> Groups { get; init; } = [];

    /// <summary>Detail page under the Navigator domain, when configured.</summary>
    public string? DetailLink { get; init; }

    /// <summary>
    /// Concurrency token: the update date of the material as an ISO-8601 string. Opaque to the caller; send it back
    /// unchanged in <see cref="UpdateMaterialRequest.Version"/>. A different value gives <c>version_conflict</c>.
    /// Null for a caller who only sees the published values: they cannot update, and the token belongs to the draft.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// The latest review decision about the material, with the reviewer's feedback: what an owner has to change after a
    /// rejection. Only for callers who see the draft; null when nobody decided yet.
    /// </summary>
    public ReviewInfo? Review { get; init; }

    /// <summary>Archive date (UTC); null when the material is not archived. Only for callers who see the draft.</summary>
    public DateTime? ArchivedAt { get; init; }
}

/// <summary>A review decision as the owner sees it.</summary>
/// <param name="Decision"><c>approved</c> or <c>rejected</c>.</param>
/// <param name="Comment">Short comment of the reviewer.</param>
/// <param name="Findings">Findings of the quality check; for a rejection in Umbraco Workflow, the reviewer's comment.</param>
/// <param name="ReviewerName">Display name of the reviewer.</param>
/// <param name="DecidedUtc">Moment of the decision (UTC).</param>
/// <param name="CurrentVersion">True when the decision is about the current draft; false after the owner changed it.</param>
public sealed record ReviewInfo(string Decision, string? Comment, string? Findings, string? ReviewerName, DateTime DecidedUtc, bool CurrentVersion);

/// <param name="Key">Media key of the element.</param>
/// <param name="FileName">File name.</param>
/// <param name="Category">Format category code.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">SHA-256 of the stored content, lowercase hexadecimal.</param>
/// <param name="Transcript">Optional plain-text transcript from the element schema.</param>
public sealed record ElementInfo(Guid Key, string FileName, string? Category, long Size, string? Sha256, string? Transcript = null);

/// <summary>
/// One typed link seen from a material. Without <see cref="Inverse"/> it reads "this material &lt;type&gt; the other"
/// (this material is the child, for example part of or a version of the other); with <see cref="Inverse"/> it reads
/// "the other &lt;type&gt; this material" (this material contains or has the other as version).
/// </summary>
/// <param name="Type">Relation type alias, for example <c>lsRelated</c>, <c>lsPartOf</c> or <c>lsVersionOf</c>.</param>
/// <param name="Inverse">True when this material is the parent of the relation.</param>
/// <param name="MaterialKey">Key of the other material.</param>
/// <param name="Title">Title of the other material, from the version the caller may see.</param>
public sealed record RelationInfo(string Type, bool Inverse, Guid MaterialKey, string Title);

/// <summary>A group of materials (ordered), as the caller may see it.</summary>
/// <param name="Key">Content key of the group.</param>
/// <param name="Title">Group title.</param>
/// <param name="Members">Members the caller may see, in group order.</param>
/// <param name="HiddenMemberCount">Members the caller may not see; a count only, never a key or title.</param>
public sealed record GroupInfo(Guid Key, string Title, IReadOnlyList<GroupMember> Members, int HiddenMemberCount);

/// <param name="MaterialKey">Key of the member material.</param>
/// <param name="Title">Title from the version the caller may see.</param>
public sealed record GroupMember(Guid MaterialKey, string Title);

/// <summary>A group a material can be added to (<c>list_groups</c>).</summary>
public sealed record GroupSummary(Guid Key, string Title);

/// <summary>
/// A typed link to create or remove, seen from the material in the route. Confirmed by the person before it is sent.
/// </summary>
public sealed record RelationRequest
{
    /// <summary>Relation type alias; one of the configured relation types.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Key of the other material.</summary>
    public Guid TargetKey { get; init; }

    /// <summary>
    /// False (default): "this material &lt;type&gt; target", for example this material is part of the target.
    /// True: "target &lt;type&gt; this material", for example the target is part of this material. Ignored for a
    /// bidirectional type such as <c>lsRelated</c>.
    /// </summary>
    public bool Inverse { get; init; }
}

/// <summary>Link to a material's detail page under the Navigator domain.</summary>
public sealed record DetailLink(Guid MaterialKey, string Url);

/// <summary>Error codes returned by the service; the same for MCP and REST.</summary>
public static class AgentErrors
{
    /// <summary>No valid user token, or the token has no person id.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>The caller lacks the role or the relation to the material.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>State changes need the person's own delegated token; team or service credentials are refused.</summary>
    public const string DelegatedTokenRequired = "delegated_token_required";

    /// <summary>The acting-user header names someone other than the bearer token.</summary>
    public const string ActingUserMismatch = "acting_user_mismatch";

    /// <summary>Not found or no access; deliberately the same answer for both.</summary>
    public const string NotFound = "not_found";

    /// <summary>The request is invalid; the message says which field.</summary>
    public const string Invalid = "invalid";

    /// <summary>The material is not in an editable state (only draft and rejected materials can be changed).</summary>
    public const string NotEditable = "material_not_editable";

    /// <summary>Only a published material that is not archived yet can be archived.</summary>
    public const string NotArchivable = "material_not_archivable";

    /// <summary>Only a submitted (or in-review) material can be approved or rejected.</summary>
    public const string NotReviewable = "material_not_reviewable";

    /// <summary>Four eyes: an owner cannot approve or reject their own material.</summary>
    public const string OwnMaterial = "own_material";

    /// <summary>A new or changed value for an archived field; existing values stay.</summary>
    public const string FieldArchived = "field_archived";

    /// <summary>A deprecated code that the material did not hold already.</summary>
    public const string ValueDeprecated = "value_deprecated";

    /// <summary>The material changed after the caller read it: the version sent is not the current one.</summary>
    public const string VersionConflict = "version_conflict";

    /// <summary>Umbraco or a Compliance Pack rule cancelled the operation.</summary>
    public const string Cancelled = "operation_cancelled";

    /// <summary>The feature is not configured (for example no detail link template).</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>An upload session without a valid signature, or whose signature has expired.</summary>
    public const string UploadSessionInvalid = "upload_session_invalid";

    /// <summary>The file of an upload session is already registered as an element of another material.</summary>
    public const string UploadSessionUsed = "upload_session_used";
}

/// <summary>Creates <see cref="AgentResult{T}"/> values.</summary>
public static class AgentResult
{
    public static AgentResult<T> Ok<T>(T value) => new(value, null, null, null);

    public static AgentResult<T> Fail<T>(string error, string message) => new(default, error, message, null);

    /// <summary>A failure that also carries structured details for the problem response (for example <c>missingFields</c>).</summary>
    public static AgentResult<T> Fail<T>(string error, string message, IReadOnlyDictionary<string, object?> details) => new(default, error, message, details);
}

/// <summary>Outcome of a service call: a value or an error code with a message that is safe to show.</summary>
public sealed record AgentResult<T>
{
    internal AgentResult(T? value, string? error, string? message, IReadOnlyDictionary<string, object?>? details)
    {
        Value = value;
        Error = error;
        Message = message;
        Details = details;
    }

    public T? Value { get; }

    public string? Error { get; }

    public string? Message { get; }

    /// <summary>Structured details of the error, added to the REST problem next to <c>code</c>; <c>null</c> for most errors.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; }

    public bool IsSuccess => Error is null;

    /// <summary>The same error for another value type.</summary>
    public AgentResult<TOther> Cast<TOther>() => new(default, Error ?? AgentErrors.Invalid, Message ?? string.Empty, Details);
}
