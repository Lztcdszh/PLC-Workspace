namespace MySecurityLibrary.Services;

/// <summary>密码哈希抽象。可替换为其它算法（如 bcrypt）而不影响上层。</summary>
public interface IPasswordHasher
{
    /// <summary>对明文密码生成随机盐与哈希（均为 Base64 字符串）。</summary>
    void Hash(string password, out string saltBase64, out string hashBase64);

    /// <summary>校验明文密码是否与已存储的盐+哈希匹配。</summary>
    bool Verify(string password, string saltBase64, string hashBase64);
}
