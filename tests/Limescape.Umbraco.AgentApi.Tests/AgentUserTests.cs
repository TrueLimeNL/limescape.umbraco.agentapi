using System.Security.Claims;
using Limescape.Umbraco.AgentApi.Auth;
using Xunit;

namespace Limescape.Umbraco.AgentApi.Tests;

public sealed class AgentUserTests
{
    private static UserTokenOptions Options(string requiredScope = "")
    {
        var options = new UserTokenOptions { RequiredScope = requiredScope };
        options.RoleMapping["developer"] = ["Materials.Developer"];
        options.RoleMapping["reviewer"] = ["Materials.Reviewer"];
        options.RoleMapping["functionalManager"] = ["4f1c0000-0000-0000-0000-00000000f00d"];
        options.RoleMapping["reader"] = ["Materials.Reader"];
        options.RoleMapping["unknownRole"] = ["Materials.Developer"];
        return options;
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public void Roles_come_from_app_roles_and_groups_and_unknown_roles_are_ignored()
    {
        var user = AgentUser.FromPrincipal(
            Principal(("sub", "p1"), ("iss", "https://idp"), ("scp", "materials.readwrite"), ("roles", "Materials.Developer"), ("groups", "4f1c0000-0000-0000-0000-00000000f00d")),
            Options());

        Assert.NotNull(user);
        Assert.Equal("p1", user.PersonId);
        Assert.Equal(["developer", "functionalManager"], user.Roles.Order());
        Assert.True(user.IsDelegated);
    }

    [Fact]
    public void No_mapped_claim_gives_no_role()
    {
        var user = AgentUser.FromPrincipal(Principal(("sub", "p1"), ("scp", "x"), ("roles", "Other")), Options());

        Assert.NotNull(user);
        Assert.Empty(user.Roles);
    }

    [Fact]
    public void Application_token_is_not_delegated_even_with_roles()
    {
        // Client-credentials token of a team or service: no scp, idtyp=app in Entra ID.
        var appToken = AgentUser.FromPrincipal(Principal(("sub", "svc"), ("roles", "Materials.Developer"), ("idtyp", "app")), Options());
        var noScope = AgentUser.FromPrincipal(Principal(("sub", "svc"), ("roles", "Materials.Developer")), Options());
        var appWithScope = AgentUser.FromPrincipal(Principal(("sub", "svc"), ("scp", "materials.readwrite"), ("idtyp", "app")), Options());

        Assert.False(appToken!.IsDelegated);
        Assert.False(noScope!.IsDelegated);
        Assert.False(appWithScope!.IsDelegated);
    }

    [Theory]
    [InlineData("materials.readwrite", true)]
    [InlineData("openid materials.readwrite profile", true)]
    [InlineData("materials.read", false)]
    public void Required_scope_must_be_present(string scopes, bool delegated)
    {
        var user = AgentUser.FromPrincipal(Principal(("sub", "p1"), ("scp", scopes)), Options("materials.readwrite"));

        Assert.Equal(delegated, user!.IsDelegated);
    }

    [Fact]
    public void Generic_oidc_scope_claim_counts_as_delegated()
    {
        var user = AgentUser.FromPrincipal(Principal(("sub", "p1"), ("scope", "materials.readwrite")), Options("materials.readwrite"));

        Assert.True(user!.IsDelegated);
    }

    [Fact]
    public void Configured_subject_claim_is_the_person_id()
    {
        var options = Options();
        options.SubjectClaim = "oid";

        var user = AgentUser.FromPrincipal(Principal(("sub", "pairwise"), ("oid", "object-id"), ("scp", "x")), options);

        Assert.Equal("object-id", user!.PersonId);
    }

    [Fact]
    public void Missing_person_id_or_anonymous_principal_gives_no_user()
    {
        Assert.Null(AgentUser.FromPrincipal(Principal(("scp", "x")), Options()));
        Assert.Null(AgentUser.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity()), Options()));
    }
}
