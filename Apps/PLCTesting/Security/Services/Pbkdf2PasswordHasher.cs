using System;
using System.Security.Cryptography;

namespace PLCTesting.Security.Services;

/// <summary>
/// 基于 Rfc2898 (PBKDF2-HMAC-SHA256) 的密码哈希实现。
/// 仅依赖 .NET 基类库，无第三方依赖，可直接移植到其它项目。
/// </summary>
public class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    public void Hash(string password, out string saltBase64, out string hashBase64)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(HashBytes);
        saltBase64 = Convert.ToBase64String(salt);
        hashBase64 = Convert.ToBase64String(hash);
    }

    public bool Verify(string password, string saltBase64, string hashBase64)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(hashBase64))
            return false;
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            var hash = pbkdf2.GetBytes(HashBytes);
            var expected = Convert.FromBase64String(hashBase64);
            if (hash.Length != expected.Length) return false;
            // 常量时间比较，降低计时侧信道风险
            int diff = 0;
            for (int i = 0; i < hash.Length; i++)
                diff |= hash[i] ^ expected[i];
            return diff == 0;
        }
        catch
        {
            return false;
        }
    }
}
