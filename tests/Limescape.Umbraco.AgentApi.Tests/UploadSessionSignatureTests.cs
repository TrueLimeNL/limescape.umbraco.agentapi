using Limescape.Umbraco.AgentApi.Services;
using Xunit;

namespace Limescape.Umbraco.AgentApi.Tests;

/// <summary>
/// The signature format is a contract with the platform. The expected value was computed independently with Python's
/// <c>hmac</c> (see the package README), so a change on either side shows up here.
/// </summary>
public sealed class UploadSessionSignatureTests
{
    private const string Secret = "test-secret-of-at-least-32-characters!!";
    private static readonly Guid Material = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private static readonly UploadSessionRegistration Session = new()
    {
        SessionId = "6b1f0c2e-7d4a-4c55-9a51-0f4f6c3e2a10",
        Blob = "materials/7/6b1f0c2e-7d4a-4c55-9a51-0f4f6c3e2a10/lecture.mp4",
        Length = 104857600,
        Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
        Etag = "\"0x8DCE1F2A3B4C5D6\"",
        Category = "multimedia",
        FileName = "lecture.mp4",
        ConfirmationId = "c0ffee00-0000-4000-8000-000000000001",
        ExpiresAt = 1791547200,
        Signature = "ca25c02f388b0e554756c37f9d1438ed0a4fbe32823ddd1cd0533544a42adf7c",
    };

    [Fact]
    public void Matches_the_platform_test_vector()
    {
        Assert.Equal(Session.Signature, UploadSessionSignature.Sign(Secret, Material, Session));
        Assert.True(UploadSessionSignature.IsValid(Secret, Material, Session));
        Assert.True(UploadSessionSignature.IsValid(Secret, Material, Session with { Signature = Session.Signature!.ToUpperInvariant() }));
    }

    [Fact]
    public void Every_signed_field_the_material_and_the_secret_matter()
    {
        UploadSessionRegistration[] changed =
        [
            Session with { SessionId = "other" },
            Session with { Blob = "materials/7/other/lecture.mp4" },
            Session with { Length = Session.Length + 1 },
            Session with { Sha256 = new string('0', 64) },
            Session with { Etag = "\"other\"" },
            Session with { Category = "basic" },
            Session with { FileName = "other.mp4" },
            Session with { ConfirmationId = "other" },
            Session with { ExpiresAt = Session.ExpiresAt + 1 },
            Session with { Signature = null },
            Session with { Signature = "abc" },
        ];

        Assert.All(changed, session => Assert.False(UploadSessionSignature.IsValid(Secret, Material, session)));
        Assert.False(UploadSessionSignature.IsValid(Secret, Guid.NewGuid(), Session));
        Assert.False(UploadSessionSignature.IsValid(Secret + "x", Material, Session));
        Assert.False(UploadSessionSignature.IsValid(string.Empty, Material, Session));
    }

    [Theory]
    [InlineData("materials/7/id/file.mp4", true)]
    [InlineData("materials/7/id/my lecture (1).mp4", true)]
    [InlineData("/materials/7/id/file.mp4", false)]
    [InlineData("materials/7/../8/file.mp4", false)]
    [InlineData("materials/./file.mp4", false)]
    [InlineData("materials//file.mp4", false)]
    [InlineData("materials\\7\\file.mp4", false)]
    [InlineData("https://other.example/file.mp4", false)]
    [InlineData("materials/7/file.mp4?x=1", false)]
    [InlineData("", false)]
    public void Only_relative_paths_without_traversal_are_valid(string path, bool valid) =>
        Assert.Equal(valid, UploadSessionSignature.IsValidPath(path));
}
