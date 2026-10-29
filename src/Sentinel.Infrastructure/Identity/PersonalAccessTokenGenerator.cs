using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Infrastructure.Identity;

public static class PersonalAccessTokenGenerator
{
    private const string Prefix = "sntl_";

    public static (string Token, string Hash) Generate()
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);

        string secret = Convert.ToHexString(randomBytes)
            .ToLowerInvariant();

        string token = Prefix + secret;

        string hash = ComputeHash(token);

        return (token, hash);
    }

    public static string ComputeHash(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);

        byte[] hashBytes = SHA256.HashData(bytes);

        return Convert.ToHexString(hashBytes)
            .ToLowerInvariant();
    }
}