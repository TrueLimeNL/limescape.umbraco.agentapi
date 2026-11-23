extern alias AzureIdentity;
using System.Security.Cryptography;
using System.Text.Json;
using DefaultAzureCredential = AzureIdentity::Azure.Identity.DefaultAzureCredential;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Limescape.Umbraco.AgentApi.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace Limescape.Umbraco.AgentApi.Services;

public sealed record SignedDownload(string Url, DateTimeOffset ExpiresAt);
public sealed record DownloadFile(Stream Stream, string Name, string ContentType, bool Inline);

/// <summary>Signs only elements of visible materials. Local links bind person, material, element, disposition and expiry.</summary>
public sealed class SignedDownloadService(ILearningMaterialService materials, IMediaService media,
    MediaFileManager files, MediaUrlGeneratorCollection generators, IDataProtectionProvider protection,
    IOptions<AgentApiOptions> options, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Limescape.MaterialDownload.v1");
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private sealed record Ticket(Guid Material, Guid Element, string Person, long Expires, bool Inline);

    public async Task<AgentResult<SignedDownload>> CreateAsync(AgentCaller caller, Guid materialKey, Guid elementKey,
        bool inline, CancellationToken cancellationToken = default)
    {
        var allowed = Element(caller, materialKey, elementKey);
        if (!allowed.IsSuccess) { return allowed.Cast<SignedDownload>(); }
        var item = allowed.Value!;
        var settings = options.Value.DetailPage;
        var now = clock.GetUtcNow();
        var expiry = now.AddSeconds(settings.DownloadLifetimeSeconds);
        inline = inline && CanInline(item.FileName);
        if (!string.IsNullOrEmpty(settings.BlobServiceUri))
        {
            var stored = media.GetById(elementKey)!;
            if (!stored.TryGetMediaPath(Constants.Conventions.Media.File, generators, out var path) || string.IsNullOrEmpty(path))
            { return Missing<SignedDownload>(); }
            var service = new BlobServiceClient(new Uri(settings.BlobServiceUri), new DefaultAzureCredential());
            var blob = service.GetBlobContainerClient(settings.BlobContainer).GetBlobClient(BlobName(path, files.FileSystem.GetRelativePath(path), settings.BlobRootPath));
            var key = (await service.GetUserDelegationKeyAsync(now, expiry, cancellationToken)).Value;
            return AgentResult.Ok(new SignedDownload(CreateUserDelegationUrl(blob, key, now, expiry, item.FileName, inline), expiry));
        }
        var ticket = _protector.Protect(JsonSerializer.Serialize(new Ticket(materialKey, elementKey, caller.User.PersonId, expiry.ToUnixTimeSeconds(), inline)));
        return AgentResult.Ok(new SignedDownload($"/materiaal/download?ticket={Uri.EscapeDataString(ticket)}", expiry));
    }

    public AgentResult<DownloadFile> Open(AgentCaller caller, string ticket)
    {
        Ticket? value;
        try { value = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(ticket)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { return Missing<DownloadFile>(); }
        if (value is null || clock.GetUtcNow().ToUnixTimeSeconds() >= value.Expires || value.Person != caller.User.PersonId)
        { return Missing<DownloadFile>(); }
        var allowed = Element(caller, value.Material, value.Element);
        if (!allowed.IsSuccess) { return allowed.Cast<DownloadFile>(); }
        var stored = media.GetById(value.Element);
        if (stored is null || stored.Trashed) { return Missing<DownloadFile>(); }
        var stream = files.GetFile(stored, out _);
        return stream is null ? Missing<DownloadFile>() : AgentResult.Ok(new DownloadFile(stream, allowed.Value!.FileName,
            TypeOf(allowed.Value.FileName), value.Inline && CanInline(allowed.Value.FileName)));
    }

    private AgentResult<ElementInfo> Element(AgentCaller caller, Guid material, Guid element)
    {
        var visible = materials.GetMaterial(caller, material);
        if (!visible.IsSuccess) { return visible.Cast<ElementInfo>(); }
        var item = visible.Value!.Elements.FirstOrDefault(e => e.Key == element);
        var stored = media.GetById(element);
        // Confidentiality per element can be added through the schema without opening a download bypass.
        var confidential = stored?.GetValue<bool>("confidential") == true;
        return item is null || stored is null || stored.Trashed ||
            (confidential && !LearningMaterialService.CanSeeConfidential(caller, visible.Value.Owners))
            ? Missing<ElementInfo>() : AgentResult.Ok(item);
    }

    public static string TypeOf(string name) => name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ? "text/vtt" : ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";
    public static bool CanPreview(string name) => TypeOf(name) is "application/pdf" || TypeOf(name).StartsWith("audio/", StringComparison.Ordinal) || TypeOf(name).StartsWith("video/", StringComparison.Ordinal);
    private static bool CanInline(string name) => CanPreview(name) || TypeOf(name) == "text/vtt";

    /// <summary>WebVTT captions are elements named like video.nl.vtt/video.en.vtt, filtered by material visibility first.</summary>
    public static IEnumerable<(ElementInfo Element, string Language)> CaptionsFor(IReadOnlyList<ElementInfo> elements, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var language in new[] { "nl", "en" })
        {
            var caption = elements.FirstOrDefault(e => string.Equals(e.FileName, $"{stem}.{language}.vtt", StringComparison.OrdinalIgnoreCase));
            if (caption is not null) { yield return (caption, language); }
        }
    }
    public static string Disposition(string name, bool inline) => $"{(inline ? "inline" : "attachment")}; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(name))}";

    /// <summary>
    /// Blob name of a media file: <paramref name="rootPath"/> plus the file system's relative path, or, without a root
    /// path, the path of the media URL (the Umbraco Azure Blob provider stores files under its virtual path by default).
    /// </summary>
    public static string BlobName(string mediaUrl, string relativePath, string? rootPath)
    {
        if (!string.IsNullOrWhiteSpace(rootPath)) { return rootPath.Trim('/') + "/" + relativePath.TrimStart('/'); }
        var urlPath = Uri.TryCreate(mediaUrl, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : mediaUrl;
        return Uri.UnescapeDataString(urlPath.Split('?')[0]).TrimStart('/');
    }

    /// <summary>Read-only, HTTPS, one-blob user-delegation SAS. No account-key SAS is ever generated.</summary>
    public static string CreateUserDelegationUrl(BlobClient blob, UserDelegationKey key, DateTimeOffset starts,
        DateTimeOffset expires, string fileName, bool inline)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(key);
        if (expires <= starts || expires - starts > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(expires), "Downloads may live at most ten minutes.");
        }
        var sas = new BlobSasBuilder
        {
            BlobContainerName = blob.BlobContainerName, BlobName = blob.Name, Resource = "b",
            StartsOn = starts, ExpiresOn = expires, Protocol = SasProtocol.Https,
            ContentType = TypeOf(fileName), ContentDisposition = Disposition(fileName, inline && CanInline(fileName)),
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        return new BlobUriBuilder(blob.Uri) { Sas = sas.ToSasQueryParameters(key, blob.AccountName) }.ToUri().AbsoluteUri;
    }
    private static AgentResult<T> Missing<T>() => AgentResult.Fail<T>(AgentErrors.NotFound, "Not found or no access.");
}
