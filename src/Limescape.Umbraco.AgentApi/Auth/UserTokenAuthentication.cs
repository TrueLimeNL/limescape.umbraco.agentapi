using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Limescape.Umbraco.AgentApi.Auth;

/// <summary>
/// Authentication of the Agent API: the caller sends the signed-in person's own OAuth2/OIDC access token of the
/// tenant identity provider. The token is validated against the provider's discovery document (issuer, audience,
/// signature, lifetime); its claims are mapped to roles by <see cref="AgentUser.FromPrincipal"/>.
/// The scheme is registered under its own name and is never the default, so Umbraco's backoffice and member
/// authentication are untouched.
/// </summary>
public static class UserTokenAuthentication
{
    /// <summary>Authentication scheme of the Agent API endpoints.</summary>
    public const string Scheme = "Limescape.AgentApi";

    /// <summary>Authorization policy on the MCP and REST endpoints: an authenticated token of <see cref="Scheme"/>.</summary>
    public const string Policy = "Limescape.AgentApi.User";

    internal static IServiceCollection AddUserTokenAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddDelegatedTokenAuthentication();
        services.AddOptions<JwtBearerOptions>(Scheme).Configure<IOptions<AgentApiOptions>>((bearer, agentApi) =>
            Configure(bearer, agentApi.Value.Authentication));

        services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()));

        return services;
    }

    private static void Configure(JwtBearerOptions bearer, UserTokenOptions options)
    {
        // Keep claim names as issued (roles, groups, scp, oid), so the role mapping reads what the provider sends.
        bearer.MapInboundClaims = false;
        // A bearer token whose iss is a configured trusted issuer is handled by the delegated scheme; any other
        // token, and a request without one, stays with this identity-provider handler. Without configured issuers the
        // selector never forwards.
        bearer.ForwardDefaultSelector = DelegatedTokenAuthentication.SelectScheme;
        if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            bearer.Authority = options.Authority;
            bearer.RequireHttpsMetadata = !Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) || !authority.IsLoopback;
        }

        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = options.ValidIssuers.Count > 0 ? [.. options.ValidIssuers] : null,
            // Without a configured audience nothing validates: fail closed.
            ValidateAudience = true,
            ValidAudiences = [.. options.Audiences],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = options.NameClaim,
        };
    }
}

/// <summary>
/// The person a call is made for, as derived from a validated user token.
/// </summary>
/// <param name="PersonId">Stable person id (configured subject claim).</param>
/// <param name="Name">Display name, if the token has one.</param>
/// <param name="Issuer">Token issuer.</param>
/// <param name="Roles">Agent API roles (see <see cref="AgentRoles"/>).</param>
/// <param name="IsDelegated">
/// True for a delegated user token (it carries the required scope and is not an application token). Only delegated
/// tokens may change state; team-wide and service credentials may not.
/// </param>
public sealed record AgentUser(string PersonId, string? Name, string Issuer, IReadOnlySet<string> Roles, bool IsDelegated)
{
    /// <summary>Group ids from the validated groups claim, when enabled in RoleClaims. App roles never grant group membership.</summary>
    public IReadOnlySet<string> Groups { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsInRole(string role) => Roles.Contains(role);

    /// <summary>Maps a principal authenticated by <see cref="UserTokenAuthentication.Scheme"/>. Null when it has no person id.</summary>
    public static AgentUser? FromPrincipal(ClaimsPrincipal principal, UserTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // A delegated person token of a trusted issuer always carries the person id in sub, whatever claim the
        // identity provider uses; it is a person token by contract, so it counts as delegated without a scope.
        var trustedIssuer = string.Equals(principal.Identity.AuthenticationType, DelegatedTokenAuthentication.Scheme, StringComparison.Ordinal);
        var personId = principal.FindFirst(trustedIssuer ? "sub" : options.SubjectClaim)?.Value;
        if (string.IsNullOrWhiteSpace(personId) || personId.Length > 255)
        {
            return null;
        }

        var issuer = principal.FindFirst("iss")?.Value ?? string.Empty;
        var name = principal.FindFirst(options.NameClaim)?.Value ?? (trustedIssuer ? principal.FindFirst("name")?.Value : null);

        var values = principal.Claims
            .Where(claim => options.RoleClaims.Contains(claim.Type, StringComparer.Ordinal))
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        var roles = options.RoleMapping
            .Where(mapping => AgentRoles.All.Contains(mapping.Key, StringComparer.OrdinalIgnoreCase) && mapping.Value.Any(values.Contains))
            .Select(mapping => AgentRoles.All.First(role => string.Equals(role, mapping.Key, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.Ordinal);

        return new AgentUser(personId, name, issuer, roles, trustedIssuer || IsDelegatedToken(principal, options.RequiredScope))
        {
            Groups = options.RoleClaims.Contains("groups", StringComparer.Ordinal)
                ? principal.FindAll("groups").Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// A user token carries delegated scopes (<c>scp</c>, or <c>scope</c> in generic OIDC providers) and is not marked as
    /// an application token (<c>idtyp=app</c> in Entra ID). Client-credential tokens of a team or service have no
    /// delegated scope.
    /// </summary>
    private static bool IsDelegatedToken(ClaimsPrincipal principal, string requiredScope)
    {
        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scopes = principal.FindAll("scp").Concat(principal.FindAll("scope"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

        return string.IsNullOrWhiteSpace(requiredScope) ? scopes.Count > 0 : scopes.Contains(requiredScope);
    }
}

/// <summary>
/// Agent API roles. Visibility and rights follow the standard role presets of the Limescape Materialenbank app.
/// </summary>
public static class AgentRoles
{
    /// <summary>Reads published materials.</summary>
    public const string Reader = "reader";

    /// <summary>Creates materials and changes their own drafts.</summary>
    public const string Developer = "developer";

    /// <summary>Publication reviewer: sees submitted materials.</summary>
    public const string Reviewer = "reviewer";

    /// <summary>Sees and manages every material.</summary>
    public const string FunctionalManager = "functionalManager";

    public static readonly IReadOnlyList<string> All = [Reader, Developer, Reviewer, FunctionalManager];
}
