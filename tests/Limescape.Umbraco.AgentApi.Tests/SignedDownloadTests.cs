using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Limescape.Umbraco.AgentApi.Services;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Limescape.Umbraco.AgentApi.Tests;

public sealed class SignedDownloadTests
{
    [Fact]
    public void Azure_url_is_read_only_https_one_blob_user_delegation_and_never_longer_than_ten_minutes()
    {
        var now = DateTimeOffset.UtcNow;
        var blob = new BlobClient(new Uri("https://storageexample.blob.core.windows.net/media/original/document.pdf"));
        var key = BlobsModelFactory.UserDelegationKey(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            now, now.AddMinutes(10), "b", "2023-11-03", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var url = new Uri(SignedDownloadService.CreateUserDelegationUrl(blob, key, now, now.AddMinutes(10), "document.pdf", false));
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("https", url.Scheme);
        Assert.Equal(blob.Uri.AbsolutePath, url.AbsolutePath);
        Assert.Equal("r", query["sp"].ToString());
        Assert.Equal("https", query["spr"].ToString());
        Assert.Equal("b", query["sr"].ToString());
        Assert.Equal(key.SignedObjectId, query["skoid"].ToString());
        Assert.Equal(key.SignedTenantId, query["sktid"].ToString());
        Assert.StartsWith("attachment;", query["rscd"].ToString());
        Assert.NotEmpty(query["sig"].ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => SignedDownloadService.CreateUserDelegationUrl(blob, key, now, now.AddMinutes(10).AddSeconds(1), "document.pdf", false));
    }

    [Theory]
    // The blob provider stores /media/hytlykoj/mat-004.odt as blob media/hytlykoj/mat-004.odt in container media.
    [InlineData("/media/hytlykoj/mat-004.odt", "hytlykoj/mat-004.odt", "", "media/hytlykoj/mat-004.odt")]
    [InlineData("https://cdn.example.test/media/a%20b/file.pdf", "a b/file.pdf", null, "media/a b/file.pdf")]
    [InlineData("/media/abc/file.pdf", "abc/file.pdf", "/tenant-files/", "tenant-files/abc/file.pdf")]
    public void Blob_name_follows_the_media_providers_root_path(string mediaUrl, string relative, string? root, string expected) =>
        Assert.Equal(expected, SignedDownloadService.BlobName(mediaUrl, relative, root));
}
