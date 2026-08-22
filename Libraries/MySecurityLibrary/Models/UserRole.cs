namespace MySecurityLibrary.Models;

/// <summary>系统角色（两档：管理员 / 普通用户）。</summary>
public enum UserRole
{
    /// <summary>管理员：拥有全部权限，包括用户管理。</summary>
    Admin,

    /// <summary>普通用户：可操作设备与查看，但不能管理用户。</summary>
    Normal
}

/// <summary>角色显示名扩展。</summary>
public static class UserRoleExtensions
{
    public static string DisplayName(this UserRole role) => role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Normal => "普通用户",
        _ => role.ToString()
    };
}
