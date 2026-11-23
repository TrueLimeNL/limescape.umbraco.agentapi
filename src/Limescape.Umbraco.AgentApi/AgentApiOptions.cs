namespace Limescape.Umbraco.AgentApi;

/// <summary>Tenant configuration of the Agent API, bound from <c>Limescape:AgentApi</c>.</summary>
public sealed class AgentApiOptions
{
    public Services.DetailPageOptions DetailPage { get; set; } = new();
    public const string SectionName = "Limescape:AgentApi";

    internal const string DetailLinkPlaceholder = "{key}";

    public bool Enabled { get; set; }

    /// <summary>Path of the MCP endpoint (Streamable HTTP).</summary>
    public string EndpointPath { get; set; } = "/mcp";

    /// <summary>Base path of the REST endpoints over the same service layer.</summary>
    public string RestBasePath { get; set; } = "/api/learning-materials";

    /// <summary>Validation of the user's bearer token and the mapping of its claims to roles.</summary>
    public UserTokenOptions Authentication { get; set; } = new();

    /// <summary>
    /// Delegated person tokens signed by a trusted issuer (the platform), accepted next to identity-provider tokens
    ///. Off until an issuer is configured here or under <c>Compliance:ActingUser:TrustedIssuers</c>.
    /// </summary>
    public DelegatedTokenOptions UserToken { get; set; } = new();

    /// <summary>Which content types are learning materials and collections, and which property aliases the service writes.</summary>
    public ContentModelOptions Content { get; set; } = new();

    /// <summary>
    /// Absolute URL of a material's detail page under the Navigator domain, with <c>{key}</c> as placeholder for the
    /// material key, for example <c>https://navigator.example.org/materiaal/{key}</c>. Empty: no detail links.
    /// </summary>
    public string DetailLinkTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Largest element accepted inline (base64 in the request). Larger files go through the upload screen and its
    /// verified upload sessions. Default 20 MB.
    /// </summary>
    public long MaxInlineElementBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Registration of verified upload sessions as elements (<c>add_element_from_session</c>).</summary>
    public UploadSessionOptions UploadSessions { get; set; } = new();

    internal static bool IsValidPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith('/') &&
        path.Length > 1 &&
        path.Split('/').Skip(1).All(segment =>
            segment.Length > 0 &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));

    internal static bool IsValidDetailLinkTemplate(string template) =>
        string.IsNullOrEmpty(template) ||
        (template.Contains(DetailLinkPlaceholder, StringComparison.Ordinal) &&
         Uri.TryCreate(template.Replace(DetailLinkPlaceholder, Guid.Empty.ToString(), StringComparison.Ordinal), UriKind.Absolute, out var uri) &&
         (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)));
}

/// <summary>
/// Files above the inline limit are uploaded outside the content layer (resumable, block blob) and verified by the
/// platform; the platform signs the verified session and the content layer registers the existing file as an element.
/// </summary>
public sealed class UploadSessionOptions
{
    /// <summary>Smallest accepted secret length, in characters.</summary>
    internal const int MinSecretLength = 32;

    /// <summary>
    /// Shared secret for the HMAC-SHA256 signature of a session (at least 32 characters). Empty: registration is not
    /// configured and every call gives <c>not_configured</c>.
    /// </summary>
    public string SigningSecret { get; set; } = string.Empty;

    /// <summary>
    /// Path prefix in the media file system under which session files may be registered, for example <c>materials/</c>.
    /// A signed path outside it is refused, so a session can never point at another media file.
    /// </summary>
    public string PathPrefix { get; set; } = "materials/";

    /// <summary>Largest file that can be registered, whatever its category. Default 10 GB.</summary>
    public long MaxBytes { get; set; } = 10_000_000_000;

    /// <summary>Largest file per element category code, for example <c>basic</c>. A category without entry: <see cref="MaxBytes"/>.</summary>
    public IDictionary<string, long> CategoryMaxBytes { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Longest accepted time between now and the signed <c>expiresAt</c>. Default one hour.</summary>
    public int MaxSignatureLifetimeSeconds { get; set; } = 3600;

    /// <summary>
    /// Read the file once and compare its SHA-256 with the signed one before registering. Default true. Off: the
    /// platform's verification is trusted and only the size is checked against the stored file.
    /// </summary>
    public bool VerifySha256 { get; set; } = true;
}

/// <summary>
/// Accepted user tokens: OAuth2/OIDC access tokens of the tenant identity provider, issued to the signed-in person for
/// this content layer (delegated). Discovery and signing keys come from <see cref="Authority"/>.
/// </summary>
public sealed class UserTokenOptions
{
    /// <summary>OIDC authority (issuer base URL); its <c>/.well-known/openid-configuration</c> provides the signing keys. Empty: every request is refused.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Accepted <c>aud</c> values (the content layer's application id URI or client id). At least one is required.</summary>
    public IList<string> Audiences { get; } = [];

    /// <summary>Accepted <c>iss</c> values when they differ from the discovery document. Empty: the discovered issuer.</summary>
    public IList<string> ValidIssuers { get; } = [];

    /// <summary>Claim that holds the stable person id recorded in the audit and as owner. Default <c>sub</c>; Entra ID tenants usually use <c>oid</c>.</summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>Claim with the display name.</summary>
    public string NameClaim { get; set; } = "name";

    /// <summary>Claims read for role mapping: app roles and group ids.</summary>
    public IList<string> RoleClaims { get; } = ["roles", "groups"];

    /// <summary>
    /// Delegated scope a user token must carry (in <c>scp</c> or <c>scope</c>), for example <c>materials.readwrite</c>.
    /// Empty: any delegated scope. A token without delegated scope is an application (team or service) token.
    /// </summary>
    public string RequiredScope { get; set; } = string.Empty;

    /// <summary>
    /// Role mapping: Agent API role (<c>reader</c>, <c>developer</c>, <c>reviewer</c>, <c>functionalManager</c>) to the
    /// claim values (app role names or group ids) that grant it. A person can hold several roles.
    /// </summary>
    public IDictionary<string, List<string>> RoleMapping { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Delegated person tokens of a trusted issuer, bound from <c>Limescape:AgentApi:UserToken</c>. The contract
/// (platform item 900-02k): bearer JWT with an asymmetric <c>alg</c> (ES256 by default; HS* refused), exact <c>iss</c>
/// of a configured issuer, <c>aud</c> equal to the issuer's audience, <c>sub</c> (stable person id), <c>name</c>,
/// <c>email</c>, <c>roles</c> (the values the identity provider would send), <c>channel</c>, and <c>iat</c> and
/// <c>exp</c> at most <see cref="MaxLifetime"/> apart, valid now within <see cref="ClockSkew"/>.
/// </summary>
public sealed class DelegatedTokenOptions
{
    /// <summary>Default <c>aud</c> a delegated person token must carry.</summary>
    public const string DefaultAudience = "agent-api";

    /// <summary>Longest accepted <c>exp - iat</c> (600 seconds). Not configurable.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(600);

    /// <summary>Allowed clock difference between the issuer and this site (60 seconds). Not configurable.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Issuers whose person tokens are accepted: <c>iss</c> value, PEM public keys (EC or RSA 2048+) and optionally
    /// an <c>Audience</c> (default <see cref="Audience"/>). Empty: the Compliance Pack's
    /// <c>Compliance:ActingUser:TrustedIssuers</c> are used with <see cref="Audience"/>. Both empty: the route is off.
    /// </summary>
    public IList<Compliance.ActingUser.TrustedIssuerOptions> TrustedIssuers { get; } = [];

    /// <summary>Required <c>aud</c> for issuers without their own <c>Audience</c>. Default <c>agent-api</c>; required.</summary>
    public string Audience { get; set; } = DefaultAudience;

    internal bool IsValid() =>
        !string.IsNullOrWhiteSpace(Audience) &&
        TrustedIssuers.All(issuer => !string.IsNullOrWhiteSpace(issuer.Issuer) && issuer.PublicKeys.Count > 0 && issuer.PublicKeys.All(Compliance.ActingUser.TrustedIssuerTokenValidator.IsPublicKeyPem));
}

/// <summary>
/// How learning materials are represented in Umbraco. Defaults match the generic object model of the Limescape content
/// layer host (<c>lsLearningMaterialBase</c>, <c>lsElement</c>); another installation configures its own aliases.
/// </summary>
public sealed class ContentModelOptions
{
    /// <summary>Composition every material type is composed with. A document type with this composition is a material type.</summary>
    public string MaterialCompositionAlias { get; set; } = "lsLearningMaterialBase";

    /// <summary>Document types whose items are collections: the places where a material may be created.</summary>
    public IList<string> CollectionDocumentTypes { get; } = [];

    /// <summary>Media type of one element (file) of a material.</summary>
    public string ElementMediaTypeAlias { get; set; } = "lsElement";

    /// <summary>Media folder under which a folder per material is created. Empty: the media root.</summary>
    public Guid? ElementsRootKey { get; set; }

    public string TitleProperty { get; set; } = "title";

    public string DescriptionProperty { get; set; } = "description";

    /// <summary>Copy of the lifecycle status (draft, submitted, …). The service only ever writes draft.</summary>
    public string StatusProperty { get; set; } = "status";

    public string SharingLevelProperty { get; set; } = "sharingLevel";

    /// <summary>JSON array of group ids allowed to read a group-shared publication.</summary>
    public string SharingGroupsProperty { get; set; } = "sharingGroups";

    public string LicenseProperty { get; set; } = "license";

    public string SourceProperty { get; set; } = "source";

    /// <summary>Owners: JSON array of person ids. Written by the service only.</summary>
    public string OwnersProperty { get; set; } = "owners";

    /// <summary>Kind of material: dropdown with <c>file</c> or <c>link</c>. A missing value reads as <c>file</c>.</summary>
    public string KindProperty { get; set; } = "kind";

    /// <summary>URL of a link-only material (kind <c>link</c>).</summary>
    public string LinkUrlProperty { get; set; } = "linkUrl";

    /// <summary>Elements: media picker with the material's element media items.</summary>
    public string ElementsProperty { get; set; } = "elements";

    /// <summary>Element format category property on the element media type.</summary>
    public string CategoryProperty { get; set; } = "category";

    /// <summary>SHA-256 property on the element media type.</summary>
    public string Sha256Property { get; set; } = "sha256";

    /// <summary>Archive date property; a material with a value is archived.</summary>
    public string ArchivedAtProperty { get; set; } = "archivedAt";

    /// <summary>Document type of a group of materials. Empty: no groups.</summary>
    public string GroupDocumentTypeAlias { get; set; } = "lsMaterialGroup";

    /// <summary>Members of a group: an ordered multi-node tree picker of materials.</summary>
    public string GroupMembersProperty { get; set; } = "members";

    /// <summary>
    /// Labels and field types for the metadata context, per property alias. The object model stores localization
    /// keys and codes; this is where a tenant says what a person reads. Empty: every label is the alias.
    /// </summary>
    public IDictionary<string, FieldLabelOptions> Fields { get; } = new Dictionary<string, FieldLabelOptions>(StringComparer.Ordinal);

    /// <summary>Labels per material type alias. A type without entry is labelled with its alias.</summary>
    public IDictionary<string, LabelSetOptions> MaterialTypes { get; } = new Dictionary<string, LabelSetOptions>(StringComparer.Ordinal);
}

/// <summary>Labels per language (<c>nl</c>, <c>en</c>, …) of one object model element.</summary>
public sealed class LabelSetOptions
{
    /// <summary>Label per language code.</summary>
    public IDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Presentation of one field: its labels, its type for a form, and the labels of its codes.</summary>
public sealed class FieldLabelOptions
{
    /// <summary>Label per language code.</summary>
    public IDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Field type for a form: <c>string</c>, <c>text</c>, <c>list</c>, <c>number</c>, <c>date</c>, <c>url</c>, <c>boolean</c> or
    /// <c>other</c>. Empty: derived from the property editor.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>Labels per language of the allowed codes of a list field, by code.</summary>
    public IDictionary<string, Dictionary<string, string>> Values { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
}
