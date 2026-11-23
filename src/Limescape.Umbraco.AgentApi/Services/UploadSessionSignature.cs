using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>
/// The signature of a verified upload session: HMAC-SHA256 with <see cref="UploadSessionOptions.SigningSecret"/> over
/// the material key and every field of <see cref="UploadSessionRegistration"/> except the signature, one per line in a
/// fixed order (see <see cref="Canonical"/>), as lowercase hex. The platform computes the same string after its worker
/// has verified the file; the content layer never trusts a field that is not covered by it.
/// </summary>
public static class UploadSessionSignature
{
    /// <summary>First line of the signed text; a new format gets a new version.</summary>
    public const string Version = "limescape-upload-session-v1";

    /// <summary>
    /// The signed text: <c>v1</c> marker, material key (lowercase, with hyphens), session id, blob path, length (decimal),
    /// sha256, etag, category, file name, confirmation id, expiresAt (Unix seconds), joined by <c>\n</c> without a final newline.
    /// </summary>
    public static string Canonical(Guid materialKey, UploadSessionRegistration session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return string.Join('\n',
            Version,
            materialKey.ToString("D"),
            session.SessionId ?? string.Empty,
            session.Blob ?? string.Empty,
            session.Length.ToString(CultureInfo.InvariantCulture),
            session.Sha256 ?? string.Empty,
            session.Etag ?? string.Empty,
            session.Category ?? string.Empty,
            session.FileName ?? string.Empty,
            session.ConfirmationId ?? string.Empty,
            session.ExpiresAt.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The signature for a session, lowercase hex.</summary>
    public static string Sign(string secret, Guid materialKey, UploadSessionRegistration session) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(Canonical(materialKey, session))));

    /// <summary>Whether the session carries the signature of <paramref name="secret"/>; compared in constant time.</summary>
    public static bool IsValid(string secret, Guid materialKey, UploadSessionRegistration session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(secret) || session.Signature is not { Length: 64 } signature)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(secret, materialKey, session)),
            Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    /// <summary>
    /// A relative path in the media file system: segments separated by <c>/</c>, no empty, <c>.</c> or <c>..</c> segment,
    /// no backslash, colon or control character.
    /// </summary>
    public static bool IsValidPath(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= 1024
        && !path.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#' or '%')
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
}
