using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>A file the model wants attached to a comment, as supplied in the tool call.</summary>
public sealed class CommentAttachment
{
    [JsonPropertyName("fileName")]
    [Description("File name shown on the ticket, including the extension, e.g. 'orders.csv', 'error.log' or 'refunds.xlsx'.")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [Description("The file content: plain text for format 'text', base64 for 'base64', CSV (or tab-separated) text for 'xlsx'.")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    [Description(
        "How content is supplied. 'text' (default): logs, CSV, SQL, JSON and other text files, sent as UTF-8. " +
        "'xlsx': CSV or tab-separated text that the server converts into an Excel workbook (first row is the header). " +
        "'base64': any other binary file, base64-encoded.")]
    public string Format { get; set; } = "text";
}

/// <summary>An attachment decoded and ready to upload to Zendesk.</summary>
public sealed record PreparedAttachment(string FileName, byte[] Data, string ContentType);

/// <summary>
/// Turns <see cref="CommentAttachment"/>s from a tool call into bytes for the Zendesk
/// uploads API. Everything is validated before anything is uploaded, so a bad third
/// file does not leave the first two uploaded with no comment.
/// </summary>
public static class OutgoingAttachments
{
    public const int MaxBytes = 10 * 1024 * 1024; // 10 MB per file
    public const int MaxCount = 10;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".sql"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".xlsx"] = XlsxWriter.ContentType,
        [".xls"] = "application/vnd.ms-excel",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
    };

    public static IReadOnlyList<PreparedAttachment> PrepareAll(IReadOnlyList<CommentAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return [];
        if (attachments.Count > MaxCount)
            throw new ArgumentException($"{attachments.Count} attachments were supplied; the limit is {MaxCount} per comment.");

        return attachments.Select(Prepare).ToList();
    }

    public static PreparedAttachment Prepare(CommentAttachment attachment)
    {
        var fileName = CleanFileName(attachment.FileName);
        var format = string.IsNullOrWhiteSpace(attachment.Format) ? "text" : attachment.Format.Trim().ToLowerInvariant();
        var content = attachment.Content ?? string.Empty;

        if (content.Length == 0)
            throw new ArgumentException($"Attachment '{fileName}' has no content.");

        byte[] data;
        switch (format)
        {
            case "text":
                CheckSize(fileName, Encoding.UTF8.GetByteCount(content));
                data = EncodeText(fileName, content);
                break;

            case "base64":
                // Decoded size is about three quarters of the base64 length; check before allocating.
                CheckSize(fileName, content.Length / 4 * 3);
                try
                {
                    data = Convert.FromBase64String(content);
                }
                catch (FormatException)
                {
                    throw new ArgumentException($"Attachment '{fileName}' has format 'base64' but its content is not valid base64.");
                }
                break;

            case "xlsx":
                CheckSize(fileName, Encoding.UTF8.GetByteCount(content));
                fileName = Path.ChangeExtension(fileName, ".xlsx");
                data = XlsxWriter.FromCsv(content);
                break;

            default:
                throw new ArgumentException($"Attachment '{fileName}' has unknown format '{attachment.Format}'. Use 'text', 'xlsx' or 'base64'.");
        }

        CheckSize(fileName, data.Length);
        return new PreparedAttachment(fileName, data, ContentTypeFor(fileName, format));
    }

    /// <summary>
    /// CSV files get a UTF-8 byte order mark: without it Excel reads them as Windows-1252
    /// and mangles characters such as '£'. Other text files are plain UTF-8.
    /// </summary>
    private static byte[] EncodeText(string fileName, string content)
    {
        var needsBom = Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            && !content.StartsWith('﻿');
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: needsBom);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
    }

    private static string CleanFileName(string? fileName)
    {
        // Keep only the last path segment, whichever separator the caller used.
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        if (name.Length == 0)
            throw new ArgumentException("Every attachment needs a fileName, e.g. 'orders.csv'.");
        return name;
    }

    private static string ContentTypeFor(string fileName, string format) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type)
            ? type
            : format == "text" ? "text/plain" : "application/octet-stream";

    private static void CheckSize(string fileName, int bytes)
    {
        if (bytes > MaxBytes)
            throw new ArgumentException($"Attachment '{fileName}' is about {bytes} bytes, over the {MaxBytes} byte limit.");
    }
}
