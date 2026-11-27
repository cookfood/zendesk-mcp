using System.Text;
using System.Web;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Decides how a downloaded ticket attachment is handed back to the model:
/// images as image content, text files and Excel workbooks as text, and anything
/// else (PDF, Word, zip) as base64 binary.
/// </summary>
public static class AttachmentContent
{
    public enum Kind { Image, Text, Binary }

    public sealed record Result(Kind Kind, string FileName, string MimeType, byte[] Data, string? Text, string? Note);

    private static readonly HashSet<string> TextMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/xml",
        "application/csv",
        "application/x-csv",
        "application/sql",
        "application/x-yaml",
        "application/yaml",
        "application/javascript",
        "image/svg+xml",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".json", ".xml", ".log", ".md", ".sql", ".yml", ".yaml", ".html", ".htm", ".svg", ".ini", ".cfg",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    /// <summary>
    /// Classifies and converts an attachment. Throws <see cref="InvalidOperationException"/>
    /// if it is empty, over <see cref="AttachmentValidator.MaxBytes"/>, or declared as an
    /// image whose content does not match.
    /// </summary>
    public static Result From(byte[] data, string declaredContentType, string contentUrl)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Attachment is empty.");
        if (data.Length > AttachmentValidator.MaxBytes)
            throw new InvalidOperationException($"Attachment is {data.Length} bytes, exceeding the {AttachmentValidator.MaxBytes} byte limit.");

        var declared = AttachmentValidator.NormaliseMime(declaredContentType);
        var fileName = FileNameFrom(contentUrl);
        var extension = Path.GetExtension(fileName);

        // A declared image type keeps the original check: the bytes must match it.
        if (AttachmentValidator.IsAllowedImageType(declared))
        {
            var mime = AttachmentValidator.Validate(data, declared);
            return new Result(Kind.Image, fileName, mime, data, null, null);
        }

        // Zendesk sometimes serves images as application/octet-stream; trust the signature.
        if (AttachmentValidator.DetectImageType(data) is { } detected)
            return new Result(Kind.Image, fileName, detected, data, null, null);

        if (XlsxReader.IsXlsx(data))
        {
            var text = XlsxReader.ToText(data);
            return new Result(Kind.Text, fileName, XlsxWriter.ContentType, data, text,
                "Converted from Excel: each sheet as CSV. Formula cells show their last calculated value; dates are yyyy-MM-dd.");
        }

        if (IsPdf(data))
            return new Result(Kind.Binary, fileName, "application/pdf", data, null,
                "PDF returned as base64 binary; text is not extracted on the server.");

        if (LooksLikeText(declared, extension) || !HasBinaryBytes(data))
        {
            if (TryDecode(data, out var text, out var encodingNote))
                return new Result(Kind.Text, fileName, declared, data, text, encodingNote);
        }

        return new Result(Kind.Binary, fileName, declared, data, null,
            "Binary file returned as base64; no text conversion is available for this type.");
    }

    /// <summary>The name query parameter Zendesk puts on content_url, or the last path segment.</summary>
    internal static string FileNameFrom(string contentUrl)
    {
        if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out var uri)) return "attachment";

        var name = HttpUtility.ParseQueryString(uri.Query)["name"];
        if (!string.IsNullOrWhiteSpace(name)) return name;

        var segment = uri.Segments.LastOrDefault(s => s.Trim('/').Length > 0)?.Trim('/');
        return string.IsNullOrEmpty(segment) ? "attachment" : Uri.UnescapeDataString(segment);
    }

    private static bool IsPdf(byte[] data) =>
        data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-';

    private static bool LooksLikeText(string mime, string extension) =>
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        TextMimeTypes.Contains(mime) ||
        mime.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
        mime.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
        TextExtensions.Contains(extension);

    /// <summary>NUL bytes in the first 8 KB mark a file as binary (the same test git uses).</summary>
    private static bool HasBinaryBytes(byte[] data) =>
        data.AsSpan(0, Math.Min(data.Length, 8192)).IndexOf((byte)0) >= 0;

    /// <summary>
    /// Decodes as UTF-8 (or UTF-16 with a byte order mark), falling back to Windows-1252
    /// for the legacy exports some tills and spreadsheets still produce.
    /// </summary>
    private static bool TryDecode(byte[] data, out string text, out string? note)
    {
        note = null;

        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
        {
            var encoding = data[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            text = encoding.GetString(data, 2, data.Length - 2);
            return true;
        }

        var offset = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        try
        {
            text = StrictUtf8.GetString(data, offset, data.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
        }

        if (HasBinaryBytes(data))
        {
            text = string.Empty;
            return false;
        }

        text = Windows1252.Value.GetString(data);
        note = "Not valid UTF-8; decoded as Windows-1252, so a few symbols may be wrong if the file used another encoding.";
        return true;
    }
}
