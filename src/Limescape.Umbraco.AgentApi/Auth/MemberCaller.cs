using System.Security.Claims;

namespace Limescape.Umbraco.AgentApi.Auth;

/// <summary>Read-only caller from a validated member session, using the token API's role mapping.</summary>
public static class MemberCaller
{
    public static AgentCaller? FromPrincipal(ClaimsPrincipal principal, UserTokenOptions options)
    {
        var user = AgentUser.FromPrincipal(principal, options);
        return user is null ? null : new AgentCaller(user with { IsDelegated = false }, AgentChannels.App);
    }
}
