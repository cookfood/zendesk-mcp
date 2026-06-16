using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class AttachmentValidatorTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    [Fact]
    public void Validate_Accepts_MatchingPng()
    {
        var mime = AttachmentValidator.Validate(PngHeader, "image/png");
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void Validate_Accepts_ContentTypeWithCharset()
    {
        var mime = AttachmentValidator.Validate(JpegHeader, "image/jpeg; charset=binary");
        Assert.Equal("image/jpeg", mime);
    }

    [Fact]
    public void Validate_Rejects_DisallowedType()
    {
        Assert.Throws<InvalidOperationException>(() => AttachmentValidator.Validate(PngHeader, "image/svg+xml"));
    }

    [Fact]
    public void Validate_Rejects_SpoofedContentType()
    {
        // Declared as PNG but bytes are JPEG.
        Assert.Throws<InvalidOperationException>(() => AttachmentValidator.Validate(JpegHeader, "image/png"));
    }

    [Fact]
    public void Validate_Rejects_OversizedAttachment()
    {
        var big = new byte[AttachmentValidator.MaxBytes + 1];
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;
        big[4] = 0x0D; big[5] = 0x0A; big[6] = 0x1A; big[7] = 0x0A;

        Assert.Throws<InvalidOperationException>(() => AttachmentValidator.Validate(big, "image/png"));
    }

    [Fact]
    public void Validate_Rejects_Empty()
    {
        Assert.Throws<InvalidOperationException>(() => AttachmentValidator.Validate([], "image/png"));
    }
}
