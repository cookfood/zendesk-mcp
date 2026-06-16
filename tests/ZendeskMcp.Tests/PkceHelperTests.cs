using ZendeskMcp.Server.Auth;

namespace ZendeskMcp.Tests;

public class PkceHelperTests
{
    [Fact]
    public void ComputeChallenge_RoundTrips_WithVerifier()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();
        var challenge = PkceHelper.ComputeCodeChallenge(verifier);

        Assert.True(PkceHelper.ValidateCodeChallenge(verifier, challenge));
    }

    [Fact]
    public void Validate_Fails_ForWrongVerifier()
    {
        var challenge = PkceHelper.ComputeCodeChallenge(PkceHelper.GenerateCodeVerifier());

        Assert.False(PkceHelper.ValidateCodeChallenge(PkceHelper.GenerateCodeVerifier(), challenge));
    }

    [Fact]
    public void Verifier_IsBase64Url_NoPadding()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();

        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
    }
}
