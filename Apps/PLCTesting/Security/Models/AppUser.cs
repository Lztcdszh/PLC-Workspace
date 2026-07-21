using System;

namespace PLCTesting.Security.Models;

/// <summary>
/// 系统用户实体。注意：本类不持有明文密码，仅存储 PBKDF2 哈希与每用户随机盐。
/// 该类型与数据库表 Users 一一对应，可整体移植到任意 .NET 项目。
/// </summary>
public class AppUser
{
    public int Id { get; set; }

    /// <summary>登录用户名（唯一、区分大小写由存储层保证）。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>显示名（中文名 / 工号等）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色（管理员 / 普通用户）。</summary>
    public UserRole Role { get; set; } = UserRole.Normal;

    /// <summary>PBKDF2 密码哈希（Base64）。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>每用户随机盐（Base64），防止彩虹表攻击。</summary>
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>是否启用（禁用后无法登录）。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最近一次成功登录时间。</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>创建者用户名。</summary>
    public string CreatedBy { get; set; } = string.Empty;
}
