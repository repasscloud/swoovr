namespace Swoovr.Domain;

/// <summary>Issue 488 (D413). Gone: the provider no longer has the session. None: it has no document image. HostNotAllowed: an image's host
/// isn't in `Didit:IdImages:Hosts` (issue 535: the owner's configuration, not Didit). Refused: an image broke the type or size rules.</summary>
public enum ProviderImageResult { Available, Gone, Unavailable, None, Refused, HostNotAllowed }
/// <summary>One side of an ID document, held in memory for one response: never stored, cached or logged.</summary>
public sealed record ProviderImage(string Side, string ContentType, byte[] Data);
public sealed record ProviderImages(ProviderImageResult Result, IReadOnlyList<ProviderImage> Images)
{
    public ProviderImages(ProviderImageResult result) : this(result, []) { }
}

/// <summary>Reads a member's ID document images live from the provider, for an admin reviewing a date of birth (D412, D413).</summary>
public interface IIdImageSource
{
    bool Enabled { get; }
    Task<ProviderImages> ReadAsync(string sessionId, Guid accountId, CancellationToken cancellationToken);
}

public static class IdImagePolicy
{
    /// <summary>The image type from its first bytes, never from a header or file name; null for anything else.</summary>
    public static string? ContentType(ReadOnlySpan<byte> data) =>
        data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : data.Length > 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp" : null;
}
