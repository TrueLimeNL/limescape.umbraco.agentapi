using Limescape.Umbraco.AgentApi.Services;
using Limescape.Umbraco.Compliance.ActingUser;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Limescape.Umbraco.AgentApi.Auth;

/// <summary>
/// The person a call is made for and the channel it came through: <see cref="AgentChannels.Assistant"/> for MCP,
/// <see cref="AgentChannels.App"/> for REST.
/// </summary>
public sealed record AgentCaller(AgentUser User, string Channel);

/// <summary>Channels recorded in the audit for calls through this package (002 data model: app, api, assistant, job).</summary>
public static class AgentChannels
{
    /// <summary>A Limescape agent calling the MCP endpoint on behalf of the person.</summary>
    public const string Assistant = "assistant";

    /// <summary>The Navigator screens calling the REST endpoints on behalf of the person.</summary>
    public const string App = "app";
}

/// <summary>Resolves the caller of the current request.</summary>
public interface IAgentCallerAccessor
{
    /// <summary>
    /// Reads the validated user token of the current request and makes its person the acting user of the Compliance Pack
    /// for the rest of the request, so every audit entry and rule check is attributed to the real person .
    /// </summary>
    AgentResult<AgentCaller> Resolve(string channel);
}

/// <inheritdoc />
public sealed class AgentCallerAccessor(IHttpContextAccessor httpContextAccessor, IOptions<AgentApiOptions> options) : IAgentCallerAccessor
{
    /// <inheritdoc />
    public AgentResult<AgentCaller> Resolve(string channel)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var user = httpContext is null ? null : AgentUser.FromPrincipal(httpContext.User, options.Value.Authentication);
        if (httpContext is null || user is null)
        {
            return AgentResult.Fail<AgentCaller>(AgentErrors.Unauthenticated, "A valid user token of the tenant identity provider is required.");
        }

        // A trusted integration may also send an acting-user token (validated by the Compliance Pack middleware).
        // It must name the same person as the bearer token; otherwise nobody can say who acted.
        var existing = httpContext.Features.Get<ActingUserTokenFeature>()?.Token;
        if (existing is not null && !string.Equals(existing.Subject, user.PersonId, StringComparison.Ordinal))
        {
            return AgentResult.Fail<AgentCaller>(AgentErrors.ActingUserMismatch, "The acting-user header names a different person than the user token.");
        }

        var now = DateTimeOffset.UtcNow;
        httpContext.Features.Set(new ActingUserTokenFeature(
            new ActingUserToken(user.PersonId, user.Name, channel, existing?.CorrelationId, user.Issuer, now, now)));

        return AgentResult.Ok(new AgentCaller(user, channel));
    }
}
