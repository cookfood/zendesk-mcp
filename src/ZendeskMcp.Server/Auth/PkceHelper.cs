using System.Security.Cryptography;
using System.Text;

namespace ZendeskMcp.Server.Auth;

public static class PkceHelper
{
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static bool ValidateCodeChallenge(string codeVerifier, string codeChallenge)
    {
        return ComputeCodeChallenge(codeVerifier) == codeChallenge;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
