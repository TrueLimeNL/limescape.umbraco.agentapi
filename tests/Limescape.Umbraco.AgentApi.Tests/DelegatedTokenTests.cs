using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.AgentApi.Services;
using Limescape.Umbraco.Compliance.ActingUser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Limescape.Umbraco.AgentApi.Tests;

/// <summary>
/// Delegated person tokens of a trusted issuer through the real endpoint filter, without a CMS: the
/// MCP endpoint and the REST endpoints accept a token of the configured issuer next to identity-provider tokens, and
/// refuse everything the contract forbids. Every negative test calls the content layer directly.
/// </summary>
public sealed class DelegatedTokenTests
{
    private const string Section = "Limescape:AgentApi:UserToken:";

    private static readonly Dictionary<string, string?> PlatformIssuer = new()
    {
        [Section + "TrustedIssuers:0:Issuer"] = TestPlatform.Issuer,
        [Section + "TrustedIssuers:0:PublicKeys:0"] = TestPlatform.Shared.PublicKeyPem,
    };

    [Fact]
    public async Task Platform_token_lists_tools_over_mcp_and_resolves_the_person_over_rest()
    {
        await using var app = await StartAsync();
        using var client = Client(app, TestPlatform.Shared.Create(subject: "platform-person-7", name: "Pat Platform", roles: ["Materials.Developer"]));

        using var mcp = await McpEndpointTests.PostToolsListAsync(client);
        using var rest = await client.GetAsync("/api/learning-materials/collections", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        var caller = Assert.Single(await rest.Content.ReadFromJsonAsync<List<CallerEcho>>(TestContext.Current.CancellationToken) ?? []);
        Assert.Equal("platform-person-7", caller.Name);
        Assert.Equal(TestPlatform.Issuer, caller.Path);
        Assert.Equal(["developer"], caller.MaterialTypes);
        Assert.True(caller.CanCreate, "a person token of a trusted issuer is delegated; it needs no scope claim");
    }

    [Fact]
    public async Task Identity_provider_tokens_keep_working_next_to_the_trusted_issuer()
    {
        await using var app = await StartAsync();
        using var client = Client(app, TestTokens.Shared.Create(roles: ["Materials.Developer"]));

        using var mcp = await McpEndpointTests.PostToolsListAsync(client);
        using var rest = await client.GetAsync("/api/learning-materials/collections", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        var caller = Assert.Single(await rest.Content.ReadFromJsonAsync<List<CallerEcho>>(TestContext.Current.CancellationToken) ?? []);
        Assert.Equal(TestTokens.Issuer, caller.Path);
    }

    public static TheoryData<string, string> RefusedTokens
    {
        get
        {
            using var otherKey = new TestPlatform();
            return new TheoryData<string, string>
            {
                { "HS256", TestPlatform.Shared.Create(signingCredentials: TestPlatform.Hmac()) },
                { "unsigned", TestPlatform.Shared.Create(withoutSignature: true) },
                { "another key, trusted iss", otherKey.Create() },
                { "unknown issuer", TestPlatform.Shared.Create(issuer: "some-other-platform") },
                { "expired", TestPlatform.Shared.Create(issuedAt: DateTimeOffset.UtcNow.AddMinutes(-20), lifetime: TimeSpan.FromMinutes(5)) },
                { "lifetime 601 s", TestPlatform.Shared.Create(lifetime: TimeSpan.FromSeconds(601)) },
                { "issued 2 minutes in the future", TestPlatform.Shared.Create(issuedAt: DateTimeOffset.UtcNow.AddMinutes(2)) },
                { "wrong aud", TestPlatform.Shared.Create(audience: "content-layer") },
                { "no aud", TestPlatform.Shared.Create(omit: ["aud"]) },
                { "no iat", TestPlatform.Shared.Create(omit: ["iat"]) },
                { "no sub", TestPlatform.Shared.Create(omit: ["sub"]) },
                { "invalid channel", TestPlatform.Shared.Create(channel: "Navigator App") },
            };
        }
    }

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    public async Task Refused_platform_tokens_get_401_over_mcp_and_rest(string reason, string token)
    {
        await using var app = await StartAsync();
        using var client = Client(app, token);

        using var mcp = await McpEndpointTests.PostToolsListAsync(client);
        using var rest = await client.GetAsync("/api/learning-materials/collections", TestContext.Current.CancellationToken);
        using var write = await client.PostAsJsonAsync("/api/learning-materials", new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        Assert.Contains(mcp.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public async Task A_token_of_600_seconds_is_the_longest_accepted()
    {
        await using var app = await StartAsync();
        using var client = Client(app, TestPlatform.Shared.Create(lifetime: TimeSpan.FromSeconds(600)));

        using var mcp = await McpEndpointTests.PostToolsListAsync(client);

        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_issuer_a_platform_token_is_refused_and_nothing_else_changes()
    {
        await using var app = await StartAsync(settings: null);
        using var platform = Client(app, TestPlatform.Shared.Create(roles: ["Materials.Developer"]));
        using var idp = Client(app, TestTokens.Shared.Create(roles: ["Materials.Developer"]));

        using var platformMcp = await McpEndpointTests.PostToolsListAsync(platform);
        using var platformRest = await platform.GetAsync("/api/learning-materials/collections", TestContext.Current.CancellationToken);
        using var idpMcp = await McpEndpointTests.PostToolsListAsync(idp);

        Assert.Equal(HttpStatusCode.Unauthorized, platformMcp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, platformRest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, idpMcp.StatusCode);
    }

    [Fact]
    public async Task The_compliance_pack_issuers_are_the_fallback_with_the_agent_api_audience()
    {
        // The acting-user issuer of the Compliance Pack has its own audience for the header; a person token for the
        // Agent API must carry the Agent API audience, whatever the header's audience is.
        var compliance = new Dictionary<string, string?>
        {
            ["Compliance:ActingUser:TrustedIssuers:0:Issuer"] = TestPlatform.Issuer,
            ["Compliance:ActingUser:TrustedIssuers:0:PublicKeys:0"] = TestPlatform.Shared.PublicKeyPem,
            ["Compliance:ActingUser:TrustedIssuers:0:Audience"] = "content-layer-header",
        };
        await using var app = await StartAsync(compliance, bindCompliance: true);
        using var agentApiAudience = Client(app, TestPlatform.Shared.Create());
        using var headerAudience = Client(app, TestPlatform.Shared.Create(audience: "content-layer-header"));

        using var accepted = await McpEndpointTests.PostToolsListAsync(agentApiAudience);
        using var refused = await McpEndpointTests.PostToolsListAsync(headerAudience);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task The_audience_and_a_per_issuer_audience_are_configurable()
    {
        var issuers = new Dictionary<string, string?>(PlatformIssuer)
        {
            [Section + "Audience"] = "content-layer-agents",
            [Section + "TrustedIssuers:1:Issuer"] = "second-platform",
            [Section + "TrustedIssuers:1:PublicKeys:0"] = TestPlatform.Shared.PublicKeyPem,
            [Section + "TrustedIssuers:1:Audience"] = "second-audience",
        };
        await using var app = await StartAsync(issuers);
        using var defaultAudience = Client(app, TestPlatform.Shared.Create());
        using var configured = Client(app, TestPlatform.Shared.Create(audience: "content-layer-agents"));
        using var second = Client(app, TestPlatform.Shared.Create(issuer: "second-platform", audience: "second-audience"));
        using var secondWithDefault = Client(app, TestPlatform.Shared.Create(issuer: "second-platform", audience: "content-layer-agents"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await McpEndpointTests.PostToolsListAsync(defaultAudience)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await McpEndpointTests.PostToolsListAsync(configured)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await McpEndpointTests.PostToolsListAsync(second)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpEndpointTests.PostToolsListAsync(secondWithDefault)).StatusCode);
    }

    [Theory]
    [InlineData("TrustedIssuers:0:Issuer", "")]
    [InlineData("TrustedIssuers:0:PublicKeys:0", "")]
    [InlineData("Audience", "")]
    [InlineData("TrustedIssuers:0:PublicKeys:0", "not a key")]
    public async Task Incomplete_issuer_configuration_fails_at_startup(string key, string value)
    {
        var issuers = new Dictionary<string, string?>(PlatformIssuer) { [Section + key] = value };

        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(issuers));
    }

    private static HttpClient Client(WebApplication app, string token)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<WebApplication> StartAsync(IDictionary<string, string?>? settings, bool bindCompliance = false) =>
        McpEndpointTests.StartAsync(true, null, extraSettings: settings, configureServices: services =>
        {
            // No CMS here: the REST endpoints run against a service that echoes the resolved caller.
            services.AddScoped<ILearningMaterialService, CallerEchoService>();
            if (bindCompliance)
            {
                // What the Compliance Pack's composer does in a real host.
                services.AddOptions<ActingUserOptions>().Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(ActingUserOptions.SectionName).Bind(options));
            }
        });

    private static Task<WebApplication> StartAsync() => StartAsync(PlatformIssuer);

    private sealed record CallerEcho(string Name, string Path, string[] MaterialTypes, bool CanCreate);

    /// <summary>Echoes the caller as one collection: name = person id, path = issuer, material types = roles, canCreate = delegated.</summary>
    private sealed class CallerEchoService : ILearningMaterialService
    {
        public AgentResult<IReadOnlyList<CollectionInfo>> ListCollections(AgentCaller caller) =>
            AgentResult.Ok<IReadOnlyList<CollectionInfo>>([new CollectionInfo(Guid.Empty, caller.User.PersonId, caller.User.Issuer, caller.User.Roles.Order().ToList(), caller.User.IsDelegated)]);

        public AgentResult<MetadataContext> GetMetadataContext(AgentCaller caller, string? materialType = null) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> CreateMaterial(AgentCaller caller, CreateMaterialRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> AddElements(AgentCaller caller, Guid materialKey, IReadOnlyList<ElementUpload> elements) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> AddElementFromSession(AgentCaller caller, Guid materialKey, UploadSessionRegistration session) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> RemoveElement(AgentCaller caller, Guid materialKey, Guid elementKey, string? version) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> UpdateMaterial(AgentCaller caller, Guid materialKey, UpdateMaterialRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> SubmitMaterial(AgentCaller caller, Guid materialKey, SubmitMaterialRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> ArchiveMaterial(AgentCaller caller, Guid materialKey, ArchiveMaterialRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> SetOwners(AgentCaller caller, Guid materialKey, SetOwnersRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> GetMaterial(AgentCaller caller, Guid materialKey) => throw new NotSupportedException();
        public AgentResult<DetailLink> GetDetailLink(AgentCaller caller, Guid materialKey) => throw new NotSupportedException();
        public AgentResult<IReadOnlyList<GroupSummary>> ListGroups(AgentCaller caller) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> AddToGroup(AgentCaller caller, Guid materialKey, Guid groupKey) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> RemoveFromGroup(AgentCaller caller, Guid materialKey, Guid groupKey) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> AddRelation(AgentCaller caller, Guid materialKey, RelationRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialDetail> RemoveRelation(AgentCaller caller, Guid materialKey, RelationRequest request) => throw new NotSupportedException();
        public AgentResult<IReadOnlyList<ReviewQueueItem>> ListReviewQueue(AgentCaller caller) => throw new NotSupportedException();
        public AgentResult<ReviewDecision> ApproveMaterial(AgentCaller caller, Guid materialKey, ApproveMaterialRequest request) => throw new NotSupportedException();
        public AgentResult<ReviewDecision> RejectMaterial(AgentCaller caller, Guid materialKey, RejectMaterialRequest request) => throw new NotSupportedException();
        public AgentResult<RetentionOverview> ListRetention(AgentCaller caller, RetentionListRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialRetention> SetRetentionClass(AgentCaller caller, Guid materialKey, SetRetentionClassRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialRetention> SetLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request) => throw new NotSupportedException();
        public AgentResult<MaterialRetention> ClearLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request) => throw new NotSupportedException();
        public AgentResult<MetadataFieldState> SetMetadataFieldState(AgentCaller caller, string fieldAlias, SetFieldStateRequest request) => throw new NotSupportedException();
    }
}

/// <summary>Claims-to-principal mapping of a trusted-issuer token: same <see cref="AgentUser"/> as an identity-provider token.</summary>
public sealed class DelegatedAgentUserTests
{
    private static UserTokenOptions Options(string subjectClaim = "sub")
    {
        var options = new UserTokenOptions { SubjectClaim = subjectClaim, RequiredScope = "materials.readwrite" };
        options.RoleMapping["developer"] = ["Materials.Developer"];
        options.RoleMapping["functionalManager"] = ["Materials.Manager"];
        return options;
    }

    private static ClaimsPrincipal Delegated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), DelegatedTokenAuthentication.Scheme));

    [Fact]
    public void Person_id_is_sub_even_when_the_identity_provider_uses_another_claim_and_no_scope_is_needed()
    {
        var user = AgentUser.FromPrincipal(
            Delegated(("sub", "platform-1"), ("iss", TestPlatform.Issuer), ("name", "Pat"), ("email", "pat@example.test"), ("roles", "Materials.Developer"), ("roles", "Materials.Manager"), ("channel", "navigator")),
            Options(subjectClaim: "oid"));

        Assert.NotNull(user);
        Assert.Equal("platform-1", user.PersonId);
        Assert.Equal("Pat", user.Name);
        Assert.Equal(TestPlatform.Issuer, user.Issuer);
        Assert.Equal(["developer", "functionalManager"], user.Roles.Order());
        Assert.True(user.IsDelegated);
        Assert.Empty(user.Groups);
    }

    [Fact]
    public void Without_roles_the_person_has_no_role()
    {
        var user = AgentUser.FromPrincipal(Delegated(("sub", "platform-2"), ("iss", TestPlatform.Issuer)), Options());

        Assert.NotNull(user);
        Assert.Empty(user.Roles);
        Assert.True(user.IsDelegated);
    }

    [Fact]
    public void An_identity_provider_token_without_scope_is_still_not_delegated()
    {
        var idp = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "p1"), new Claim("roles", "Materials.Developer")], "Bearer"));

        var user = AgentUser.FromPrincipal(idp, Options(subjectClaim: "oid"));

        Assert.NotNull(user);
        Assert.False(user.IsDelegated);
    }
}
