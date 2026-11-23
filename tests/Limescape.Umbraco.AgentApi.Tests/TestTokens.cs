using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Limescape.Umbraco.AgentApi.Tests;

/// <summary>
/// A stand-in for the tenant identity provider: an EC P-256 key generated per test run that signs access tokens the
/// way Entra ID or another OIDC provider would (delegated <c>scp</c>, app roles in <c>roles</c>).
/// </summary>
public sealed class TestTokens : IDisposable
{
    public const string Issuer = "https://login.example.test/tenant/v2.0";
    public const string Audience = "api://content-layer";
    public const string Scope = "materials.readwrite";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static TestTokens Shared { get; } = new();

    public SecurityKey SigningKey => new ECDsaSecurityKey(_key);

    /// <summary>A delegated user token. Pass <paramref name="scope"/> null for a token without delegated scope.</summary>
    public string Create(
        string subject = "person-1",
        string? name = "Test Person",
        string[]? roles = null,
        string? scope = Scope,
        string audience = Audience,
        string issuer = Issuer,
        TimeSpan? lifetime = null,
        string? idtyp = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(10));
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = subject,
            [JwtRegisteredClaimNames.Iss] = issuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Nbf] = Math.Min(now.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds()),
            [JwtRegisteredClaimNames.Exp] = expires.ToUnixTimeSeconds(),
        };
        if (name is not null)
        {
            claims[JwtRegisteredClaimNames.Name] = name;
        }

        if (scope is not null)
        {
            claims["scp"] = scope;
        }

        if (roles is { Length: > 0 })
        {
            claims["roles"] = roles;
        }

        if (idtyp is not null)
        {
            claims["idtyp"] = idtyp;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    public void Dispose() => _key.Dispose();
}
