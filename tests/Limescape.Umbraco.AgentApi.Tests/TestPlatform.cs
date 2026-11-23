using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Limescape.Umbraco.AgentApi.Tests;

/// <summary>
/// A stand-in for the platform as trusted issuer of delegated person tokens: an EC P-256
/// key generated per test run whose public key is configured as trusted issuer, and tokens signed the way the
/// platform signs them (ES256, <c>iss</c>, <c>aud</c>, <c>sub</c>, <c>email</c>, <c>name</c>, <c>roles</c>,
/// <c>channel</c>, <c>iat</c>, <c>exp</c>).
/// </summary>
public sealed class TestPlatform : IDisposable
{
    public const string Issuer = "lmb-test-platform";
    public const string Audience = "agent-api";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static TestPlatform Shared { get; } = new();

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    public SecurityKey SigningKey => new ECDsaSecurityKey(_key);

    /// <summary>A delegated person token. Pass <paramref name="omit"/> to leave claims out.</summary>
    public string Create(
        string subject = "person-1",
        string? name = "Test Person",
        string? email = "test.person@example.test",
        string[]? roles = null,
        string? channel = "navigator",
        string audience = Audience,
        string issuer = Issuer,
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        SigningCredentials? signingCredentials = null,
        bool withoutSignature = false,
        params string[] omit)
    {
        var iat = issuedAt ?? DateTimeOffset.UtcNow;
        var exp = iat + (lifetime ?? TimeSpan.FromMinutes(5));
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = subject,
            [JwtRegisteredClaimNames.Iss] = issuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Iat] = iat.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = exp.ToUnixTimeSeconds(),
        };
        if (name is not null)
        {
            claims[JwtRegisteredClaimNames.Name] = name;
        }

        if (email is not null)
        {
            claims[JwtRegisteredClaimNames.Email] = email;
        }

        if (roles is { Length: > 0 })
        {
            claims["roles"] = roles;
        }

        if (channel is not null)
        {
            claims["channel"] = channel;
        }

        foreach (var claim in omit)
        {
            claims.Remove(claim);
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = withoutSignature ? null : signingCredentials ?? new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>Symmetric credentials (HS256), which the contract forbids.</summary>
    public static SigningCredentials Hmac() =>
        new(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256);

    public void Dispose() => _key.Dispose();
}
