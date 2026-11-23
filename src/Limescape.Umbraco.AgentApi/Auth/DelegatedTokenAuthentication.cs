using System.Security.Claims;
using System.Text.Encodings.Web;
using Limescape.Umbraco.Compliance.ActingUser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Net.Http.Headers;

namespace Limescape.Umbraco.AgentApi.Auth;

/// <summary>
/// Second route into the Agent API: a delegated person token signed by a trusted issuer, typically the Limescape
/// platform acting for a signed-in person who has no identity-provider token for this content layer (local slice, tenant
/// without on-behalf-of). Same key model as the Compliance Pack's acting-user header: asymmetric signature, exact
/// <c>iss</c>, required <c>aud</c>, <c>iat</c> and <c>exp</c> with a lifetime of at most
/// <see cref="DelegatedTokenOptions.MaxLifetime"/>. The person (<c>sub</c>, <c>name</c>) and the roles (<c>roles</c>, the
/// same values the identity provider would send) map to the same <see cref="AgentUser"/> as an identity-provider token,
/// so owner and role rules are identical. Off by default: without a configured issuer nothing changes.
/// </summary>
public static class DelegatedTokenAuthentication
{
    /// <summary>Scheme of trusted-issuer person tokens; the identity-provider scheme forwards to it by <c>iss</c>.</summary>
    public const string Scheme = "Limescape.AgentApi.Delegated";

    internal static IServiceCollection AddDelegatedTokenAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<DelegatedTokenIssuers>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DelegatedTokenAuthenticationHandler>(Scheme, _ => { });
        return services;
    }

    /// <summary>
    /// Forward target for the identity-provider scheme: this scheme when the bearer token names a configured trusted
    /// issuer, otherwise null (the identity-provider handler validates the token itself).
    /// </summary>
    internal static string? SelectScheme(HttpContext context)
    {
        var token = ReadBearerToken(context);
        return token is not null && context.RequestServices.GetRequiredService<DelegatedTokenIssuers>().NamesTrustedIssuer(token)
            ? Scheme
            : null;
    }

    internal static string? ReadBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}

/// <summary>
/// The effective trusted issuers for delegated person tokens: <c>Limescape:AgentApi:UserToken:TrustedIssuers</c>, or,
/// when that list is empty, the Compliance Pack's <c>Compliance:ActingUser:TrustedIssuers</c> (same keys, so the
/// integration that signs acting-user headers can also sign person tokens). Every issuer gets the audience of the
/// Agent API: its own <c>Audience</c> from the Agent API list, or <see cref="DelegatedTokenOptions.Audience"/>. The
/// audience of a Compliance Pack issuer belongs to the acting-user header and is not used here.
/// </summary>
internal sealed class DelegatedTokenIssuers(IOptionsMonitor<AgentApiOptions> agentApi, IOptionsMonitor<ActingUserOptions> actingUser)
{
    private readonly TrustedIssuerTokenValidator _validator = new(DelegatedTokenOptions.MaxLifetime);

    public TrustedIssuerTokenValidator Validator => _validator;

    public IReadOnlyList<TrustedIssuerOptions> Current
    {
        get
        {
            var options = agentApi.CurrentValue.UserToken;
            var own = options.TrustedIssuers.Count > 0;
            var source = own ? options.TrustedIssuers : actingUser.CurrentValue.TrustedIssuers;
            var issuers = new List<TrustedIssuerOptions>(source.Count);
            foreach (var issuer in source)
            {
                if (string.IsNullOrWhiteSpace(issuer.Issuer) || issuer.PublicKeys.Count == 0)
                {
                    continue;
                }

                var effective = new TrustedIssuerOptions
                {
                    Issuer = issuer.Issuer,
                    Audience = own && !string.IsNullOrWhiteSpace(issuer.Audience) ? issuer.Audience : options.Audience,
                };
                foreach (var key in issuer.PublicKeys)
                {
                    effective.PublicKeys.Add(key);
                }

                issuers.Add(effective);
            }

            return issuers;
        }
    }

    public bool NamesTrustedIssuer(string token)
    {
        var issuers = Current;
        return issuers.Count > 0 && _validator.NamesTrustedIssuer(token, issuers);
    }
}

/// <summary>Authenticates a bearer token of a trusted issuer and maps its claims to a principal of <see cref="DelegatedTokenAuthentication.Scheme"/>.</summary>
internal sealed partial class DelegatedTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DelegatedTokenIssuers issuers,
    IOptionsMonitor<AgentApiOptions> agentApi,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = DelegatedTokenAuthentication.ReadBearerToken(Context);
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var trusted = issuers.Current;
        if (trusted.Count == 0)
        {
            return AuthenticateResult.Fail("No trusted issuer of delegated person tokens is configured.");
        }

        var result = await issuers.Validator.ValidateAsync(token, trusted, DelegatedTokenOptions.ClockSkew, timeProvider.GetUtcNow()).ConfigureAwait(false);
        if (!result.IsValid)
        {
            TokenRefused(Logger, result.Error!);
            return AuthenticateResult.Fail($"Delegated person token refused: {result.Error}.");
        }

        var jwt = result.Token!;
        var error = ValidateClaims(jwt);
        if (error is not null)
        {
            TokenRefused(Logger, error);
            return AuthenticateResult.Fail($"Delegated person token refused: {error}.");
        }

        // Claims as issued (roles as repeated claims), like the identity-provider scheme with MapInboundClaims off.
        var identity = new ClaimsIdentity(jwt.Claims, DelegatedTokenAuthentication.Scheme, agentApi.CurrentValue.Authentication.NameClaim, "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), DelegatedTokenAuthentication.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer error=\"invalid_token\"");
        return Task.CompletedTask;
    }

    private static string? ValidateClaims(JsonWebToken jwt)
    {
        var subject = StringClaim(jwt, JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
        {
            return "sub is missing or too long";
        }

        if (StringClaim(jwt, JwtRegisteredClaimNames.Name) is { Length: > 255 })
        {
            return "name is too long";
        }

        if (StringClaim(jwt, JwtRegisteredClaimNames.Email) is { Length: > 255 })
        {
            return "email is too long";
        }

        var channel = StringClaim(jwt, "channel");
        if (channel is not null && !ActingUserTokenValidator.IsValidChannel(channel))
        {
            return "channel is invalid";
        }

        if (jwt.Claims.Any(claim => claim.Type == "roles" && (claim.Value.Length == 0 || claim.Value.Length > 255)))
        {
            return "roles contains an empty or too long value";
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delegated person token refused: {Reason}.")]
    private static partial void TokenRefused(ILogger logger, string reason);

    private static string? StringClaim(JsonWebToken jwt, string name) =>
        jwt.TryGetPayloadValue<string>(name, out var value) ? value : null;
}
