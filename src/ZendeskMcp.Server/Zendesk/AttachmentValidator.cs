namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Validates downloaded attachments before they are returned to the model as
/// base64 image data. Ported from the original Python server: an allow-list of
/// image MIME types (no SVG, to avoid embedded scripts), a magic-byte check to
/// catch spoofed content types, and a hard size cap.
/// </summary>
public static class AttachmentValidator
{
    public const int MaxBytes = 10 * 1024 * 1024; // 10 MB

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
    };

    /// <summary>
    /// Returns the canonical MIME type if the data is a permitted image within the
    /// size limit and its magic bytes match <paramref name="declaredContentType"/>.
    /// Throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public static string Validate(byte[] data, string declaredContentType)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Attachment is empty.");

        if (data.Length > MaxBytes)
            throw new InvalidOperationException($"Attachment is {data.Length} bytes, exceeding the {MaxBytes} byte limit.");

        var declared = NormaliseMime(declaredContentType);
        if (!AllowedMimeTypes.Contains(declared))
            throw new InvalidOperationException($"Attachment type '{declared}' is not an allowed image type ({string.Join(", ", AllowedMimeTypes)}).");

        var detected = DetectImageType(data);
        if (detected is null)
            throw new InvalidOperationException("Attachment content does not match any allowed image signature.");

        if (!string.Equals(detected, declared, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Attachment content ({detected}) does not match declared type ({declared}).");

        return detected;
    }

    private static string NormaliseMime(string contentType)
    {
        var semicolon = contentType.IndexOf(';');
        var mime = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return mime.Trim();
    }

    private static string? DetectImageType(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return "image/png";

        if (data.Length >= 6 &&
            data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
            data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
            return "image/gif";

        // WEBP: "RIFF"...."WEBP"
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return "image/webp";

        return null;
    }
}
